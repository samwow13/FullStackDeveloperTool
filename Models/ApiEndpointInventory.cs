namespace FullStackLauncher.Models;

/// <summary>Read-only, session-only API metadata. No source bodies or configuration values are retained.</summary>
public sealed record ApiEndpointOrigin(string File, int Line, string Controller, string Action, string? Tag = null,
    int DeclarationOffset = 0, string? ControllerIdentity = null, string OriginKind = "Controller", string? HandlerIdentity = null)
{
    public string Display => string.IsNullOrEmpty(Controller)
        ? (string.IsNullOrEmpty(Action) ? File : $"{Action} — {File}{(Line > 0 ? $":{Line}" : "")}")
        : $"{Controller}.{Action} — {File}:{Line}";
}

public sealed record ApiEndpointOperation(string HttpMethod, string Route, IReadOnlyList<ApiEndpointOrigin> Origins)
{
    public string OriginSummary => string.Join(Environment.NewLine, Origins.Select(origin => origin.Display));
}

public sealed record ApiHttpMethodCount(string HttpMethod, int Count);

public sealed class ApiEndpointInventory
{
    public string SourceKind { get; init; } = "Source";
    public string SourceLabel { get; init; } = "Controllers + minimal APIs (static declarations)";
    public string SourcePath { get; init; } = "";
    public int ScannedFileCount { get; init; }
    public int? ControllerCount { get; init; }
    /// <summary>Eligible declared public instance methods, including methods whose routes cannot be resolved.</summary>
    public int? ControllerActionCount { get; init; }
    /// <summary>Recognized minimal API registration declarations, including unresolved routes. A multi-verb registration counts once.</summary>
    public int? MinimalEndpointCount { get; init; }
    /// <summary>Distinct statically identifiable handlers in recognized registrations. Dynamic delegates are excluded.</summary>
    public int? MinimalHandlerCount { get; init; }
    public IReadOnlyList<ApiEndpointOperation> Operations { get; init; } = [];
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public bool IsPartial { get; init; }
    public int TotalOperationCount => Operations.Count;
    public IReadOnlyList<ApiHttpMethodCount> HttpMethodCounts => Operations
        .GroupBy(operation => operation.HttpMethod, StringComparer.OrdinalIgnoreCase)
        .OrderBy(group => MethodOrder(group.Key)).ThenBy(group => group.Key, StringComparer.Ordinal)
        .Select(group => new ApiHttpMethodCount(group.Key, group.Count())).ToArray();

    public const string CountingSemantics = "Operations are distinct HTTP verb + route template pairs. Multiple routes or verbs can produce several operations for one action. Repeated pairs count once and retain all source origins. ALL means an unconstrained HTTP method, not one operation for each known verb. Route parameters and constraints remain templates; runtime availability is not verified.";
    public const string ActionCountingSemantics = "Controller actions are eligible public instance methods declared in discovered source controllers, including unresolved/conventional actions. Inherited actions are excluded. OpenAPI does not establish controller or action counts.";
    public const string MinimalEndpointCountingSemantics = "Minimal API registrations are recognized MapGet/Post/Put/Patch/Delete/Methods/Map declarations on statically identified endpoint builders. A multi-verb registration counts once, including declarations with unresolved routes. Handlers count distinct identifiable lambdas or method declarations; dynamic delegates are excluded. Static helper declarations do not prove the helper is called at runtime. OpenAPI does not establish registration or handler counts.";

    private static int MethodOrder(string method) => method.ToUpperInvariant() switch
    {
        "GET" => 0, "POST" => 1, "PUT" => 2, "PATCH" => 3, "DELETE" => 4,
        "HEAD" => 5, "OPTIONS" => 6, "TRACE" => 7, "CONNECT" => 8, "ALL" => 10, _ => 9
    };
}

public sealed class ApiEndpointDiscoveryException(string message) : Exception(message);
