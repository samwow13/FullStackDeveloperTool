using System.Security.Cryptography;
using System.Text.Json;
using FullStackLauncher.AgentBridge;
using FullStackLauncher.Models;
using FullStackLauncher.Services;

namespace FullStackLauncher;

public partial class MainWindow
{
    private sealed record AgentConsoleEntry(DateTime Timestamp, string Source, string Severity,
        bool IsStandardError, bool IsErrorContext, string Message);

    // Invoked on the dispatcher, where the same bounded collection is used by the console window.
    // This never drains pending output, clears lines, or changes the console's state.
    private object AgentServiceConsole(AgentBridgeRequest request)
    {
        if (request.IncludeSensitiveOutput != true)
            throw new ArgumentException("Explicit acknowledgment of potentially sensitive console output is required.");
        var project = RequireProject(request.ProjectId);
        var service = RequireService(project, request.ServiceId);
        var mode = request.ConsoleMode ?? "errors";
        if (mode is not ("errors" or "all")) throw new ArgumentException("Console mode must be errors or all.");
        var limit = request.Limit ?? 20;
        var offset = request.Offset ?? 0;
        if (limit is < 1 or > 25 || offset is < 0 or > 600)
            throw new ArgumentException("Console page limit or offset is outside the supported range.");
        if (offset > 0 && string.IsNullOrEmpty(request.SnapshotId))
            throw new ArgumentException("A snapshot ID is required for a subsequent console page.");

        var retained = service.ConsoleLines.ToArray();
        var included = new bool[retained.Length];
        if (mode == "all") Array.Fill(included, true);
        else
        {
            for (var i = 0; i < retained.Length; i++)
            {
                if (retained[i].Kind != ServiceLogKind.Error) continue;
                for (var context = Math.Max(0, i - 10); context <= i; context++) included[context] = true;
            }
        }

        // Producer-side API configuration redaction happens before its own truncation. Apply the
        // common redactor again before the tighter bridge limit, without claiming it catches all
        // secrets that an arbitrary application might write. Do not expose configured command entries.
        var entries = retained.Select((line, index) => (line, index))
            .Where(item => included[item.index] && item.line.Kind != ServiceLogKind.Command)
            .Select(item => new AgentConsoleEntry(item.line.Timestamp,
                BoundedAgentConsoleText(item.line.Source, 128), item.line.Kind.ToString(),
                item.line.IsStandardError, mode == "errors" && item.line.Kind != ServiceLogKind.Error,
                BoundedAgentConsoleText(item.line.Message, 1024)))
            .ToArray();
        var fingerprint = JsonSerializer.SerializeToUtf8Bytes(new
        {
            instanceId = _agentCoordination.InstanceId, projectId = project.Id,
            serviceId = service.Profile.Id, mode, entries
        }, AgentBridgeProtocol.Json);
        var snapshotId = Convert.ToHexString(SHA256.HashData(fingerprint));
        if (offset > 0 && !string.Equals(request.SnapshotId, snapshotId, StringComparison.Ordinal))
            throw new InvalidOperationException("Console output changed since the previous page. Restart from offset zero.");
        if (offset > entries.Length) throw new ArgumentException("Console offset exceeds the current snapshot.");

        // Pages are requested newest first, while entries within each page remain in capture order.
        var count = Math.Min(limit, entries.Length - offset);
        var start = entries.Length - offset - count;
        var next = offset + count;
        return new
        {
            instanceId = _agentCoordination.InstanceId, projectId = project.Id,
            serviceId = service.Profile.Id, observedUtc = DateTime.UtcNow, mode,
            warning = "Best-effort redaction only; arbitrary application output may contain sensitive information.",
            scope = "In-memory retained console entries; configured command entries omitted; output may be incomplete or cleared.",
            snapshotId, offset, nextOffset = next < entries.Length ? (int?)next : null,
            totalEntries = entries.Length, entries = entries.Skip(start).Take(count).ToArray()
        };
    }

    private static string BoundedAgentConsoleText(string text, int limit)
    {
        var safe = SensitiveDataProtection.Redact(text);
        return safe.Length > limit ? safe[..limit] + " … [truncated]" : safe;
    }
}
