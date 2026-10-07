using System.ComponentModel;
using System.IO;
using System.Text.Json;

namespace FullStackLauncher.ProjectTasks;

/// <summary>
/// Converts protocol failures to fixed, value-free diagnostics. Provider messages,
/// command arguments, paths, and arbitrary server payloads are never returned.
/// </summary>
internal static class CodexFailureDetails
{
    private const int MaximumDepth = 4;
    private const int MaximumValues = 64;
    private const int MaximumStringCharacters = 4096;

    internal static string Describe(JsonElement error)
    {
        var evidence = new FailureEvidence();
        Inspect(error, evidence, 0);

        if (evidence.FileInUse && (evidence.SandboxRuntime || evidence.SetupRefreshFailed || evidence.HelperUnknownError))
            return "Windows sandbox runtime files are in use (os error 32). Wait for the owning process to release those files, then explicitly retry. No command startup was confirmed.";
        if (evidence.SetupRefreshFailed)
            return "Windows sandbox setup refresh failed before command startup. Check Codex sandbox setup, then explicitly retry after resolving it. No command ran.";
        if (evidence.HelperUnknownError)
            return "Codex Windows command helper failed (helper_unknown_error). Review this exact chat and its sandbox setup before explicitly retrying. No command startup was confirmed.";
        if (evidence.Category is { } category)
            return CategoryMessage(category)!;
        return evidence.RpcCode switch
        {
            -32601 => "Codex does not support the requested protocol method (-32601). Update Codex and Launcher, then explicitly retry after reviewing any retained task IDs.",
            -32602 => "Codex rejected the request parameters (-32602). Review the requested model, access mode, and protocol compatibility before explicitly retrying.",
            -32001 => "Codex is busy (-32001). Wait briefly and explicitly try again.",
            _ => "Codex reported an error without a recognized safe failure category. Open the exact chat to review details; retain its task and turn IDs before any new attempt."
        };
    }

    internal static string Describe(Exception exception)
    {
        // These exception classes contain only Launcher-authored, fixed messages.
        if (exception is CodexInteractionRequiredException or CodexRequestException or CodexCommandAccessException)
            return exception.Message;
        if (exception.InnerException is { } inner)
            return Describe(inner);
        if (exception is InvalidOperationException && exception.Message is
            CodexAppServerConnection.UnavailableMessage or CodexAppServerConnection.IncompatibleMessage)
            return exception.Message;

        return exception switch
        {
            Win32Exception native => $"The Codex process could not start (Windows error {native.NativeErrorCode}). Check installation and executable access before explicitly retrying.",
            UnauthorizedAccessException or System.Security.SecurityException =>
                "Launcher could not access the Codex executable or assigned project folder. Check local permissions before explicitly retrying.",
            JsonException =>
                "Codex returned malformed protocol data. Review retained task and turn IDs before any new attempt.",
            OperationCanceledException =>
                "The Codex operation timed out or was canceled. Review retained task and turn IDs before any new attempt.",
            IOException =>
                "The Codex connection closed or local storage was unavailable. Review retained task and turn IDs before any new attempt.",
            ArgumentException or NotSupportedException =>
                "The Codex executable, project folder, or request parameters could not be used. Check local setup before explicitly retrying.",
            _ => "The Codex operation failed. Open the exact chat and review retained task and turn IDs before any new attempt."
        };
    }

    private static void Inspect(JsonElement value, FailureEvidence evidence, int depth)
    {
        if (depth > MaximumDepth || ++evidence.Values > MaximumValues) return;
        if (value.ValueKind == JsonValueKind.String)
        {
            InspectMarkers(value.GetString(), evidence);
            return;
        }
        if (value.ValueKind != JsonValueKind.Object) return;

        foreach (var property in value.EnumerateObject())
        {
            if (++evidence.Values > MaximumValues) return;
            if (property.Name == "code" && property.Value.ValueKind == JsonValueKind.Number
                && property.Value.TryGetInt32(out var code))
                evidence.RpcCode ??= code;
            else if (property.Name == "codexErrorInfo")
            {
                if (property.Value.ValueKind == JsonValueKind.String)
                    SetCategory(property.Value.GetString(), evidence);
                else if (property.Value.ValueKind == JsonValueKind.Object)
                {
                    // Parameterized enum variants use their property name as the
                    // category. The associated provider data remains private.
                    foreach (var variant in property.Value.EnumerateObject())
                    {
                        if (++evidence.Values > MaximumValues) return;
                        SetCategory(variant.Name, evidence);
                    }
                }
            }
            else if (property.Name is "message" or "additionalDetails" or "error" or "data"
                or "detail" or "details" or "cause")
                Inspect(property.Value, evidence, depth + 1);
        }
    }

    private static void InspectMarkers(string? text, FailureEvidence evidence)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var bounded = text.AsSpan(0, Math.Min(text.Length, MaximumStringCharacters));
        var helper = Contains(bounded, "helper_unknown_error");
        var setup = Contains(bounded, "setup refresh had errors") || Contains(bounded, "setup refresh failed");
        var runtime = Contains(bounded, "sandbox-runtime") || Contains(bounded, "windows-sandbox")
            || Contains(bounded, "sandbox runtime") || (Contains(bounded, "sandbox") && Contains(bounded, "runtime"));
        var fileInUse = Contains(bounded, "os error 32") || Contains(bounded, "being used by another process")
            || Contains(bounded, "sharing violation");
        evidence.HelperUnknownError |= helper;
        evidence.SetupRefreshFailed |= setup;
        evidence.SandboxRuntime |= runtime;
        evidence.FileInUse |= fileInUse;
    }

    private static bool Contains(ReadOnlySpan<char> text, ReadOnlySpan<char> marker) =>
        text.IndexOf(marker, StringComparison.OrdinalIgnoreCase) >= 0;

    private static void SetCategory(string? category, FailureEvidence evidence)
    {
        if (category is not null && CategoryMessage(category) is not null)
            evidence.Category ??= category;
    }

    private static string? CategoryMessage(string category) => category switch
    {
        "sandboxError" => "Codex could not run a command in the selected sandbox (sandboxError). Review this exact chat and its access mode before explicitly retrying.",
        "threadRollbackFailed" => "Codex could not update the chat's saved history (threadRollbackFailed). Review the exact chat before sending again.",
        "contextWindowExceeded" => "The Codex context limit was reached (contextWindowExceeded). Review the exact chat and shorten the next task before explicitly retrying.",
        "usageLimitExceeded" => "Codex reported an account usage limit (usageLimitExceeded). Check usage in Codex before explicitly retrying.",
        "serverOverloaded" => "The Codex provider is overloaded (serverOverloaded). Wait briefly and explicitly retry after reviewing this attempt.",
        "httpConnectionFailed" => "Codex could not connect to its provider (httpConnectionFailed). Check network and Codex sign-in before explicitly retrying.",
        "responseStreamConnectionFailed" => "Codex could not open the provider response stream (responseStreamConnectionFailed). Review this attempt before explicitly retrying.",
        "responseStreamDisconnected" => "The Codex provider response stream disconnected (responseStreamDisconnected). Review retained task and turn IDs before any new attempt.",
        "responseTooManyFailedAttempts" => "The Codex provider response exhausted its retries (responseTooManyFailedAttempts). Review this attempt before explicitly retrying.",
        "internalServerError" => "The Codex provider reported an internal error (internalServerError). Review this attempt before explicitly retrying.",
        "unauthorized" => "Codex provider authorization failed (unauthorized). Check sign-in and model access in Codex before explicitly retrying.",
        "badRequest" => "Codex rejected the task request (badRequest). Review model access and the saved task before explicitly retrying.",
        "cyberPolicy" => "Codex reported a policy restriction (cyberPolicy). Open the exact chat to review it before any new attempt.",
        _ => null
    };

    private sealed class FailureEvidence
    {
        internal int Values;
        internal int? RpcCode;
        internal string? Category;
        internal bool HelperUnknownError;
        internal bool SetupRefreshFailed;
        internal bool SandboxRuntime;
        internal bool FileInUse;
    }
}

/// <summary>A fixed, Launcher-authored command-access diagnostic, safe to retain in a receipt.</summary>
internal sealed class CodexCommandAccessException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException) { }
