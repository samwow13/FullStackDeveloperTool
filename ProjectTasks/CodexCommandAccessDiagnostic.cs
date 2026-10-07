using System.IO;
using System.Text.Json;
using System.Windows;

namespace FullStackLauncher.ProjectTasks;

internal sealed record CodexCommandAccessResult(bool CommandAccess, string AccessMode, string Summary);

/// <summary>A bounded command diagnostic that never creates a chat or changes queue state.</summary>
internal static class CodexCommandAccessDiagnostic
{
    internal static async Task<CodexCommandAccessResult> CheckAsync(string folder,
        CancellationToken cancellationToken, bool workspaceOnly = false)
    {
        var mode = CodexAgentAccessMode.Workspace;
        try
        {
            if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
                return new(false, "", "Choose an existing absolute project folder. No task or prompt was submitted.");
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(35));
            await using var connection = await CodexAppServerConnection.StartAsync(folder, timeout.Token).ConfigureAwait(false);
            mode = workspaceOnly ? CodexAgentAccessMode.Workspace :
                await CodexAgentAccessPolicy.LoadConfiguredDefaultAsync(connection, folder, timeout.Token).ConfigureAwait(false);
            await CodexCommandAccessCheck.VerifyAsync(connection, folder, mode, timeout.Token).ConfigureAwait(false);
            return new(true, mode.ToString(), $"Command access verified ({mode}; approvals never). No task or prompt was submitted.");
        }
        catch (OperationCanceledException)
        {
            return new(false, mode.ToString(), "Command access check stopped or timed out. No task or prompt was submitted.");
        }
        catch (Exception ex)
        {
            return new(false, mode.ToString(), CodexFailureDetails.Describe(ex));
        }
    }

    internal static async void Open(string[] args)
    {
        var exitCode = 1;
        try
        {
            var index = Array.FindIndex(args, arg => arg.Equals("--folder", StringComparison.OrdinalIgnoreCase));
            var folder = index >= 0 && index + 1 < args.Length ? args[index + 1] : "";
            var result = await Task.Run(() => CheckAsync(folder, CancellationToken.None,
                args.Contains("--workspace", StringComparer.OrdinalIgnoreCase)));
            Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            exitCode = result.CommandAccess ? 0 : 2;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("The command access diagnostic could not finish.");
        }
        finally { Application.Current.Shutdown(exitCode); }
    }
}
