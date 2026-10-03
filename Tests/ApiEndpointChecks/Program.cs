using FullStackLauncher.Models;
using FullStackLauncher.Services;

// Metadata-only checks: no WPF, API process, HTTP, service settings or real business actions.
var checks = new (string Name, Func<Task> Run)[]
{
    ("Controller and action routes form a cartesian product", Sync(RouteCartesianProduct)),
    ("Templated verbs keep their own MVC route selectors", Sync(VerbSpecificSelectors)),
    ("Template-less verbs combine with Route selectors", Sync(TemplateLessVerbs)),
    ("Mixed template-less and templated verbs preserve separate selectors", Sync(MixedVerbSelectors)),
    ("AcceptVerbs and Route selectors exclude other templated verbs", Sync(MixedAcceptVerbsSelectors)),
    ("Absolute action routes override controller route prefixes", Sync(AbsoluteRoutes)),
    ("Controller/action tokens and ActionName are substituted", Sync(RouteTokens)),
    ("Literal const route expressions resolve without executing source", Sync(ConstantRoutes)),
    ("GET POST PUT PATCH DELETE HEAD OPTIONS TRACE and custom verbs count", Sync(AllVerbs)),
    ("AcceptVerbs preserves route and multiple verb operations", Sync(AcceptVerbsRoutes)),
    ("Unconstrained Route selectors stay in the ALL bucket", Sync(UnconstrainedRoutes)),
    ("Duplicate verb/route pairs count once and retain distinct source origins", Sync(DuplicateOperations)),
    ("Same-line overloads and same-named namespace controllers retain distinct origins", Sync(SourceOriginIdentity)),
    ("NonAction and ineligible method/controller declarations do not count", Sync(EligibleActionsOnly)),
    ("Routed ordinary Dispose counts while direct IDisposable implementations do not", Sync(DisposeActionEligibility)),
    ("Partial method declaration and implementation form one attributed action", Sync(PartialMethodAction)),
    ("Action Area metadata overrides the controller Area route token", Sync(ActionAreaOverride)),
    ("Controller attribute discovers a controller without the name suffix", Sync(ControllerAttribute)),
    ("Conventional unresolved actions make source counts partial", Sync(ConventionalRoutesArePartial)),
    ("Dynamic route expressions are omitted and make counts partial", Sync(DynamicRoutesArePartial)),
    ("Minimal API maps are omitted and reported as unsupported", Sync(MinimalApisArePartial)),
    ("Malformed source is reported as partial", Sync(MalformedSourceIsPartial)),
    ("Conditional compilation is explicitly marked partial", Sync(ConditionalCompilationIsPartial)),
    ("Source-defined familiar routing attributes cannot create guessed operations", Sync(CustomRoutingAttributesArePartial)),
    ("Supported MVC attribute aliases resolve statically", Sync(FrameworkRoutingAliases)),
    ("Empty source has successful zero counts", Sync(EmptySource)),
    ("Canceled source and OpenAPI parsing do not return inventories", Sync(CanceledParsing)),
    ("OpenAPI counts only HTTP operations and keeps controllers unknown", Sync(OpenApiOperations)),
    ("Duplicate OpenAPI pairs count once with warnings and separate origins", Sync(DuplicateOpenApiOperations)),
    ("Case-distinct route templates remain distinct declaration pairs", Sync(CaseDistinctRouteTemplates)),
    ("Swagger 2 includes its basePath", Sync(SwaggerBasePath)),
    ("OpenAPI local path refs resolve and remain route-specific", Sync(LocalOpenApiReferences)),
    ("External and cyclic OpenAPI refs make the inventory partial", Sync(UnresolvedOpenApiReferences)),
    ("Invalid and unsupported OpenAPI documents fail visibly", Sync(InvalidOpenApiDocuments)),
    ("Source refresh sees growth while build/dependency directories stay excluded", DirectoryRefresh),
    ("Empty source directories and missing folders have distinct outcomes", DirectoryEmptyAndMissing),
    ("Custom project source membership is warned or rejected explicitly", UnsupportedProjectMembership),
    ("Oversized/generated source files are bounded or excluded", SourceSizeLimits),
    ("Oversized OpenAPI metadata fails before parsing", Sync(OpenApiSizeLimit)),
    ("Local OpenAPI import is read-only and supports cancellation/errors", LocalOpenApiImport),
};

var failures = new List<string>();
foreach (var check in checks)
{
    try
    {
        await check.Run();
        Console.WriteLine($"PASS {check.Name}");
    }
    catch (Exception error)
    {
        failures.Add(check.Name);
        Console.Error.WriteLine($"FAIL {check.Name}: {error.Message}");
    }
}
Console.WriteLine($"{checks.Length - failures.Count}/{checks.Length} API endpoint checks passed.");
return failures.Count == 0 ? 0 : 1;

static Func<Task> Sync(Action action) => () => { action(); return Task.CompletedTask; };

static ApiEndpointInventory Source(string code, string file = "Controllers/WidgetController.cs") =>
    ApiEndpointDiscovery.AnalyzeSources(new Dictionary<string, string> { [file] = code });

static void RouteCartesianProduct()
{
    var inventory = Source("""
        [Route("api/widgets")]
        [Route("v2/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet("{id:int}")]
            [HttpGet("by-name/{name}")]
            public object Read() => throw new Exception("Must never execute");
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets/{id:int}", "GET /api/widgets/by-name/{name}",
        "GET /v2/widgets/{id:int}", "GET /v2/widgets/by-name/{name}");
    Check.Equal(1, inventory.ControllerCount, "One controller declaration.");
    Check.Equal(1, inventory.ControllerActionCount, "Multiple selectors do not multiply action count.");
    Check.WarningContains(inventory, "Static declarations", "Source results disclose that runtime conventions are not evaluated.");
}

static void VerbSpecificSelectors()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet("read")]
            [HttpPost("write")]
            public object Work() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets/read", "POST /api/widgets/write");
}

static void TemplateLessVerbs()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet]
            [HttpPost]
            [Route("first")]
            [Route("second")]
            public object Work() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets/first", "POST /api/widgets/first",
        "GET /api/widgets/second", "POST /api/widgets/second");
}

static void AbsoluteRoutes()
{
    var inventory = Source("""
        [Route("api/widgets")]
        [Route("v2/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet("/global/read")]
            [HttpPost("~/global/write")]
            public object Work() => null;
        }
        """);
    Check.Pairs(inventory, "GET /global/read", "POST /global/write");
}

static void MixedVerbSelectors()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet]
            [HttpPost("write")]
            public object Work() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets", "POST /api/widgets/write");
}

static void MixedAcceptVerbsSelectors()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [AcceptVerbs("GET", "HEAD")]
            [Route("read")]
            [HttpPost("write")]
            public object Work() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets/read", "HEAD /api/widgets/read", "POST /api/widgets/write");
}

static void RouteTokens()
{
    var inventory = Source("""
        [Route("api/[controller]/[action]")]
        public class WidgetController : ControllerBase
        {
            [HttpGet]
            [ActionName("Fetch")]
            public object Read() => null;
            [HttpDelete]
            public object Remove() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/Widget/Fetch", "DELETE /api/Widget/Remove");
}

static void ConstantRoutes()
{
    var inventory = Source("""
        [Route(Prefix)]
        public class WidgetController : ControllerBase
        {
            private const string Prefix = "api/" + "widgets";
            private const string Leaf = "read";
            [HttpGet(Leaf)]
            public object Read() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets/read");
    Check.False(inventory.Warnings.Any(warning => warning.Contains("unresolved", StringComparison.OrdinalIgnoreCase)),
        "Literal const expressions must not be reported as unresolved.");
}

static void AllVerbs()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet] public object Get() => null;
            [HttpPost] public object Post() => null;
            [HttpPut] public object Put() => null;
            [HttpPatch] public object Patch() => null;
            [HttpDelete] public object Delete() => null;
            [HttpHead] public object Head() => null;
            [HttpOptions] public object Options() => null;
            [AcceptVerbs("TRACE", "PROPFIND")] public object Other() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets", "POST /api/widgets", "PUT /api/widgets",
        "PATCH /api/widgets", "DELETE /api/widgets", "HEAD /api/widgets", "OPTIONS /api/widgets",
        "TRACE /api/widgets", "PROPFIND /api/widgets");
    Check.Equal(8, inventory.ControllerActionCount, "One AcceptVerbs action has multiple operations.");
    Check.Equal(9, inventory.HttpMethodCounts.Count, "Every discovered verb has its own bucket.");
    Check.True(inventory.HttpMethodCounts.All(count => count.Count == 1), "Buckets have one route each.");
}

static void AcceptVerbsRoutes()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [AcceptVerbs("GET", "POST", Route = "shared")]
            public object Work() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets/shared", "POST /api/widgets/shared");
}

static void UnconstrainedRoutes()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [Route("shared")]
            public object Work() => null;
        }
        """);
    Check.Pairs(inventory, "ALL /api/widgets/shared");
    Check.Equal(1, inventory.HttpMethodCounts.Single().Count, "ALL is one pair, not an invented list of verbs.");
}

static void DuplicateOperations()
{
    var inventory = ApiEndpointDiscovery.AnalyzeSources(new Dictionary<string, string>
    {
        ["Controllers/FirstController.cs"] = """
            [Route("api/shared")]
            public class FirstController : ControllerBase
            {
                [HttpGet("read")]
                [HttpGet("read")]
                public object Read() => null;
            }
            """,
        ["Controllers/SecondController.cs"] = """
            [Route("api/shared")]
            public class SecondController : ControllerBase
            {
                [HttpGet("read")]
                public object ReadAgain() => null;
            }
            """,
    });
    Check.Pairs(inventory, "GET /api/shared/read");
    Check.Equal(2, inventory.Operations.Single().Origins.Count, "Repeated attributes must not duplicate an origin.");
    Check.Equal(2, inventory.ControllerCount, "Controller count preserves both declarations.");
    Check.Equal(2, inventory.ControllerActionCount, "Action count preserves both methods.");
    Check.True(inventory.Operations.Single().Origins.All(origin => origin.Line > 0), "Source line attribution is present.");
}

static void EligibleActionsOnly()
{
    var inventory = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet] public object Read() => null;
            [NonAction][HttpPost] public object Helper() => null;
            [HttpPut] private object Hidden() => null;
            [HttpPatch] public static object Static() => null;
            [HttpDelete] public object Generic<T>() => null;
        }
        [NonController][Route("api/no")]
        public class DisabledController : ControllerBase { [HttpGet] public object Read() => null; }
        [Route("api/abstract")]
        public abstract class AbstractController : ControllerBase { [HttpGet] public object Read() => null; }
        [Route("api/generic")]
        public class GenericController<T> : ControllerBase { [HttpGet] public object Read() => null; }
        public class Container
        {
            [Route("api/nested")]
            public class NestedController : ControllerBase { [HttpGet] public object Read() => null; }
        }
        """);
    Check.Pairs(inventory, "GET /api/widgets");
    Check.Equal(1, inventory.ControllerCount, "Only eligible controllers count.");
    Check.Equal(1, inventory.ControllerActionCount, "Only eligible public instance methods count.");
}

static void SourceOriginIdentity()
{
    var inventory = Source("namespace First { [Route(\"api/shared\")] public class WidgetController : ControllerBase { [HttpGet(\"read\")] public object Read() => null; [HttpGet(\"read\")] public object Read(int id) => null; } } namespace Second { [Route(\"api/shared\")] public class WidgetController : ControllerBase { [HttpGet(\"read\")] public object Read() => null; } }", "Controllers/Shared.cs");
    Check.Pairs(inventory, "GET /api/shared/read");
    Check.Equal(2, inventory.ControllerCount, "Same class names in different namespaces remain distinct controllers.");
    Check.Equal(3, inventory.ControllerActionCount, "Same-line overloaded declarations remain distinct actions.");
    var origins = inventory.Operations.Single().Origins;
    Check.Equal(3, origins.Count, "Pair de-duplication retains every distinct method origin.");
    Check.Equal(3, origins.Select(origin => origin.DeclarationOffset).Distinct().Count(), "Source offsets distinguish same-line overloads.");
    Check.Equal(2, origins.Select(origin => origin.ControllerIdentity).Distinct(StringComparer.Ordinal).Count(), "Qualified controller identities distinguish namespaces.");
    Check.True(origins.All(origin => origin.Line == 1), "This fixture specifically verifies origins sharing one line number.");
}

static void ControllerAttribute()
{
    var inventory = Source("""
        [Controller][Route("api/special")]
        public class Special : ControllerBase { [HttpGet] public object Read() => null; }
        """);
    Check.Pairs(inventory, "GET /api/special");
    Check.Equal(1, inventory.ControllerCount, "ControllerAttribute identifies a non-suffixed controller.");
}

static void DisposeActionEligibility()
{
    var inventory = ApiEndpointDiscovery.AnalyzeSources(new Dictionary<string, string>
    {
        ["Controllers/DisposeControllers.cs"] = """
            [Route("api/plain")]
            public class PlainController : ControllerBase
            {
                [HttpPost("dispose")]
                public void Dispose() { }
            }
            [Route("api/disposable")]
            public class DisposableController : ControllerBase, System.IDisposable
            {
                [HttpPost("dispose")]
                public void Dispose() { }
                [HttpGet("read")]
                public object Read() => null;
            }
            [Route("api/partial")]
            public partial class PartialController : ControllerBase
            {
                [HttpPost("dispose")]
                public void Dispose() { }
                [HttpGet("read")]
                public object Read() => null;
            }
            """,
        ["Controllers/PartialController.Interface.cs"] = """
            public partial class PartialController : System.IDisposable { }
            """,
    });
    Check.Pairs(inventory, "POST /api/plain/dispose", "GET /api/disposable/read", "GET /api/partial/read");
    Check.Equal(3, inventory.ControllerCount, "Partial declarations form one controller.");
    Check.Equal(3, inventory.ControllerActionCount, "Ordinary Dispose is an action; actual direct interface implementations are excluded.");
}

static void PartialMethodAction()
{
    var inventory = ApiEndpointDiscovery.AnalyzeSources(new Dictionary<string, string>
    {
        ["Controllers/PartialController.Declaration.cs"] = """
            [Route("api/partial")]
            public partial class PartialController : ControllerBase
            {
                [HttpGet("read")]
                public partial object Work();
            }
            """,
        ["Controllers/PartialController.Implementation.cs"] = """
            public partial class PartialController
            {
                [HttpPost("write")]
                public partial object Work() => null;
            }
            """,
    });
    Check.Pairs(inventory, "GET /api/partial/read", "POST /api/partial/write");
    Check.Equal(1, inventory.ControllerCount, "Partial source parts form one controller.");
    Check.Equal(1, inventory.ControllerActionCount, "A partial method signature and implementation are one declared action.");
}

static void ActionAreaOverride()
{
    var inventory = Source("""
        [Area("parent")][Route("api/[area]/[controller]")]
        public class WidgetController : ControllerBase
        {
            [Area("child")][HttpGet("read")]
            public object Read() => null;
        }
        """);
    Check.Pairs(inventory, "GET /api/child/Widget/read");
}

static void ConventionalRoutesArePartial()
{
    var inventory = Source("""
        public class WidgetController : ControllerBase { public object Read() => null; }
        """);
    Check.Pairs(inventory);
    Check.Equal(1, inventory.ControllerActionCount, "Unresolved action still counts as a declared action.");
    Check.Partial(inventory, "An unknown conventional route must not be guessed.");
    Check.WarningContains(inventory, "conventional", "Conventional route coverage is explained.");
}

static void DynamicRoutesArePartial()
{
    var inventory = Source("""
        [Route(Prefix)]
        public class WidgetController : ControllerBase
        {
            private static string Prefix => BuildPrefix();
            [HttpGet("read")] public object Read() => null;
        }
        """);
    Check.Pairs(inventory);
    Check.Equal(1, inventory.ControllerActionCount, "An unresolved template does not erase the action.");
    Check.Partial(inventory, "Dynamic expressions require a partial warning.");
    Check.WarningContains(inventory, "unresolved", "Dynamic template coverage is explained.");
}

static void MinimalApisArePartial()
{
    var inventory = Source("""
        app.MapGet("/read", () => throw new Exception("Never execute"));
        app.MapPost("/write", BusinessMutation);
        app.MapMethods("/shared", new[] { "GET", "POST" }, Handler);
        """, "Program.cs");
    Check.Pairs(inventory);
    Check.Partial(inventory, "Minimal API maps are outside controller-source coverage.");
    Check.WarningContains(inventory, "Minimal API", "Minimal API coverage is explained.");
}

static void MalformedSourceIsPartial()
{
    var inventory = Source("[Route(\"api/widgets\")] public class WidgetController { [HttpGet(\"bad\") public object Read() { ");
    Check.Partial(inventory, "Syntax errors require a warning so a result does not claim completeness.");
    Check.WarningContains(inventory, "syntax errors", "Malformed file coverage is explained.");
}

static void EmptySource()
{
    var inventory = ApiEndpointDiscovery.AnalyzeSources(new Dictionary<string, string>());
    Check.Pairs(inventory);
    Check.Equal(0, inventory.ControllerCount, "No source controllers is an exact zero declaration count.");
    Check.Equal(0, inventory.ControllerActionCount, "No actions is an exact zero declaration count.");
    Check.WarningContains(inventory, "Static declarations", "Empty source still discloses the general source coverage boundary.");
}

static void ConditionalCompilationIsPartial()
{
    var inventory = Source("""
        #if FEATURE_ENABLED
        [Route("api/conditional")]
        public class ConditionalController : ControllerBase { [HttpGet] public object Read() => null; }
        #endif
        [Route("api/widgets")]
        public class WidgetController : ControllerBase { [HttpGet] public object Read() => null; }
        """);
    Check.Pairs(inventory, "GET /api/widgets");
    Check.Partial(inventory, "A source scan cannot claim runtime completeness without project compilation symbols.");
    Check.WarningContains(inventory, "Preprocessor", "Conditional coverage is explained distinctly from baseline source limits.");
}

static void CustomRoutingAttributesArePartial()
{
    var inventory = Source("""
        public class HttpGetAttribute : Attribute { public HttpGetAttribute(string route) { } }
        [Route("api/widgets")]
        public class WidgetController : ControllerBase { [HttpGet("read")] public object Read() => null; }
        """);
    Check.Pairs(inventory);
    Check.Partial(inventory, "Source-defined familiar routing names cannot be treated as the framework implementation.");
    Check.WarningContains(inventory, "custom/aliased", "The custom routing metadata boundary is explained.");
}

static void FrameworkRoutingAliases()
{
    var inventory = Source("""
        using Get = Microsoft.AspNetCore.Mvc.HttpGetAttribute;
        using Mvc = Microsoft.AspNetCore.Mvc;
        [Mvc.Route("api/widgets")]
        public class WidgetController : ControllerBase { [Get("read")] public object Read() => null; }
        """);
    Check.Pairs(inventory, "GET /api/widgets/read");
}

static void CanceledParsing()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    Check.Throws<OperationCanceledException>(() => ApiEndpointDiscovery.AnalyzeSources(
        new Dictionary<string, string> { ["Program.cs"] = "" }, cancellation.Token), "Canceled source scans must throw cancellation.");
    Check.Throws<OperationCanceledException>(() => ApiEndpointDiscovery.ImportOpenApiJson(
        "{\"openapi\":\"3.0.0\",\"paths\":{}}", "test", cancellation.Token), "Canceled imports must throw cancellation.");
}

static void OpenApiOperations()
{
    var inventory = ApiEndpointDiscovery.ImportOpenApiJson("""
        {
          "openapi": "3.1.0",
          "paths": {
            "/widgets": {
              "summary": "metadata", "description": "not an operation", "parameters": [],
              "get": {"operationId": "Read", "responses": {}},
              "post": {"operationId": "Create", "responses": {}},
              "put": {"responses": {}}, "patch": {"responses": {}}, "delete": {"responses": {}},
              "head": {"responses": {}}, "options": {"responses": {}}, "trace": {"responses": {}},
              "x-audit": {"delete": {"responses": {}}}
            }
          },
          "components": {"schemas": {"Ignored": {"get": {}}}}
        }
        """);
    Check.Pairs(inventory, "GET /widgets", "POST /widgets", "PUT /widgets", "PATCH /widgets", "DELETE /widgets",
        "HEAD /widgets", "OPTIONS /widgets", "TRACE /widgets");
    Check.Equal<int?>(null, inventory.ControllerCount, "OpenAPI does not prove a controller count.");
    Check.Equal<int?>(null, inventory.ControllerActionCount, "OpenAPI does not prove an action count.");
    Check.False(inventory.IsPartial, "Supported operation metadata resolves completely.");
}

static void SwaggerBasePath()
{
    var inventory = ApiEndpointDiscovery.ImportOpenApiJson("""
        {"swagger":"2.0","basePath":"/v1","paths":{"/widgets":{"get":{"responses":{}}}}}
        """);
    Check.Pairs(inventory, "GET /v1/widgets");
}

static void DuplicateOpenApiOperations()
{
    var inventory = ApiEndpointDiscovery.ImportOpenApiJson("""
        {
          "openapi":"3.1.0",
          "paths": {
            "/widgets": {"get":{"operationId":"First"},"get":{"operationId":"Second"}},
            "/widgets": {"get":{"operationId":"Third"}}
          }
        }
        """);
    Check.Pairs(inventory, "GET /widgets");
    Check.Equal(3, inventory.Operations.Single().Origins.Count, "Distinct documented origins stay reviewable after de-duplication.");
    Check.Partial(inventory, "Duplicate JSON keys require warning instead of implying a pristine document.");
    Check.WarningContains(inventory, "Duplicate JSON path", "Duplicate path keys are explained.");
    Check.WarningContains(inventory, "Duplicate JSON HTTP-operation", "Duplicate operation keys are explained.");
}

static void CaseDistinctRouteTemplates()
{
    var documented = ApiEndpointDiscovery.ImportOpenApiJson("""
        {"openapi":"3.1.0","paths":{"/Widgets":{"get":{}},"/widgets":{"get":{}}}}
        """);
    Check.Pairs(documented, "GET /Widgets", "GET /widgets");
    var declared = Source("""
        [Route("api/widgets")]
        public class WidgetController : ControllerBase
        {
            [HttpGet("Read")][HttpGet("read")]
            public object Read() => null;
        }
        """);
    Check.Pairs(declared, "GET /api/widgets/Read", "GET /api/widgets/read");
}

static void LocalOpenApiReferences()
{
    var inventory = ApiEndpointDiscovery.ImportOpenApiJson("""
        {
          "openapi":"3.0.3",
          "paths": {
            "/first": {"$ref":"#/components/pathItems/Shared"},
            "/second": {"$ref":"#/components/pathItems/Shared"}
          },
          "components": {"pathItems": {"Shared": {"get":{"responses":{}},"post":{"responses":{}}}}}
        }
        """);
    Check.Pairs(inventory, "GET /first", "POST /first", "GET /second", "POST /second");
    Check.False(inventory.IsPartial, "Local path refs resolve without contacting a server.");
}

static void UnresolvedOpenApiReferences()
{
    var inventory = ApiEndpointDiscovery.ImportOpenApiJson("""
        {
          "openapi":"3.1.0",
          "paths": {
            "/safe": {"get":{"responses":{}}},
            "/external": {"$ref":"https://example.invalid/unsafe.json"},
            "/cycle": {"$ref":"#/components/pathItems/Cycle"},
            "/missing": {"$ref":"#/components/pathItems/Missing"}
          },
          "components": {"pathItems": {"Cycle": {"$ref":"#/components/pathItems/Cycle"}}}
        }
        """);
    Check.Pairs(inventory, "GET /safe");
    Check.Partial(inventory, "Unsupported references must not silently imply zero operations.");
    Check.WarningContains(inventory, "External", "External reference coverage is explained.");
    Check.WarningContains(inventory, "cyclic", "Cyclic reference coverage is explained.");
    Check.WarningContains(inventory, "unresolved local", "Missing local reference coverage is explained.");
}

static void InvalidOpenApiDocuments()
{
    foreach (var document in new[] { "{", "{}", "[]", "{\"openapi\":\"4.0.0\",\"paths\":{}}",
        "{\"openapi\":\"3.2.0\",\"paths\":{\"/new\":{\"query\":{}}}}",
        "{\"openapi\":\"3.1.0\",\"paths\":[]}" })
        Check.Throws<ApiEndpointDiscoveryException>(() => ApiEndpointDiscovery.ImportOpenApiJson(document),
            "Malformed/unsupported OpenAPI must return an explicit failure.");
}

static async Task DirectoryRefresh()
{
    using var folder = new ScratchFolder();
    folder.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
    folder.Write("Controllers/FirstController.cs", "[Route(\"api/first\")] public class FirstController : ControllerBase { [HttpGet] public object Read() => null; }");
    foreach (var ignored in new[] { "bin", "obj", "node_modules", ".git" })
        folder.Write($"{ignored}/IgnoredController.cs", "[Route(\"api/ignored\")] public class IgnoredController : ControllerBase { [HttpDelete] public object Mutate() => null; }");
    var first = await ApiEndpointDiscovery.AnalyzeSourceAsync(folder.Path, CancellationToken.None);
    Check.Pairs(first, "GET /api/first");
    Check.Equal(1, first.ScannedFileCount, "Build/dependency/version-control files stay outside source scanning.");
    folder.Write("Controllers/SecondController.cs", "[Route(\"api/second\")] public class SecondController : ControllerBase { [HttpPost] public object Create() => null; }");
    var refreshed = await ApiEndpointDiscovery.AnalyzeSourceAsync(folder.Path, CancellationToken.None);
    Check.Pairs(refreshed, "GET /api/first", "POST /api/second");
    Check.Equal(2, refreshed.ScannedFileCount, "Explicit refresh sees newly added source files.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Check.ThrowsAsync<OperationCanceledException>(() => ApiEndpointDiscovery.AnalyzeSourceAsync(folder.Path, cancellation.Token),
        "Canceled folder reads must remain distinguishable from empty results.");
}

static async Task DirectoryEmptyAndMissing()
{
    using var folder = new ScratchFolder();
    folder.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
    var inventory = await ApiEndpointDiscovery.AnalyzeSourceAsync(folder.Path, CancellationToken.None);
    Check.Pairs(inventory);
    Check.Equal(0, inventory.ScannedFileCount, "An empty directory is a successful empty result.");
    await Check.ThrowsAsync<ApiEndpointDiscoveryException>(() => ApiEndpointDiscovery.AnalyzeSourceAsync(
        System.IO.Path.Combine(folder.Path, "missing"), CancellationToken.None), "A missing source folder is an explicit error.");
}

static async Task UnsupportedProjectMembership()
{
    using var folder = new ScratchFolder();
    folder.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><ItemGroup><Compile Remove=\"Controllers/ExcludedController.cs\" /></ItemGroup></Project>");
    folder.Write("Controllers/ExcludedController.cs", "[Route(\"api/declared\")] public class ExcludedController : ControllerBase { [HttpGet] public object Read() => null; }");
    var inventory = await ApiEndpointDiscovery.AnalyzeSourceAsync(folder.Path, CancellationToken.None);
    Check.WarningContains(inventory, "Compile", "Custom Compile membership must be disclosed distinctly from general source limits.");
    folder.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"><PropertyGroup><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup></Project>");
    await Check.ThrowsAsync<ApiEndpointDiscoveryException>(() => ApiEndpointDiscovery.AnalyzeSourceAsync(folder.Path, CancellationToken.None),
        "Disabled default source membership cannot be guessed without evaluating MSBuild.");
}

static async Task LocalOpenApiImport()
{
    using var folder = new ScratchFolder();
    const string json = "{\"openapi\":\"3.0.0\",\"paths\":{\"/read\":{\"get\":{\"responses\":{}}}}}";
    var file = folder.Write("openapi.json", json);
    var inventory = await ApiEndpointDiscovery.LoadOpenApiAsync(file, CancellationToken.None);
    Check.Pairs(inventory, "GET /read");
    Check.Equal(json, await File.ReadAllTextAsync(file), "Import must leave the chosen metadata file unchanged.");
    await Check.ThrowsAsync<ApiEndpointDiscoveryException>(() => ApiEndpointDiscovery.LoadOpenApiAsync(
        System.IO.Path.Combine(folder.Path, "missing.json"), CancellationToken.None), "A missing metadata file fails explicitly.");
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    await Check.ThrowsAsync<OperationCanceledException>(() => ApiEndpointDiscovery.LoadOpenApiAsync(file, cancellation.Token),
        "Canceled metadata reads must not return a stale/empty inventory.");
}

static async Task SourceSizeLimits()
{
    using var folder = new ScratchFolder();
    folder.Write("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\" />");
    folder.Write("HugeController.cs", new string('x', 4 * 1024 * 1024 + 1));
    folder.Write("GeneratedController.g.cs", "[Route(\"api/generated\")] public class GeneratedController : ControllerBase { [HttpGet] public object Read() => null; }");
    var inventory = await ApiEndpointDiscovery.AnalyzeSourceAsync(folder.Path, CancellationToken.None);
    Check.Pairs(inventory);
    Check.Equal(0, inventory.ScannedFileCount, "Oversized/generated files cannot consume unbounded source parsing work.");
    Check.WarningContains(inventory, "oversized", "Skipped oversized source files are explained.");
}

static void OpenApiSizeLimit() => Check.Throws<ApiEndpointDiscoveryException>(
    () => ApiEndpointDiscovery.ImportOpenApiJson(new string('x', 16 * 1024 * 1024 + 1)),
    "Oversized imported metadata fails with a supported size-limit error before unbounded parsing.");

sealed class ScratchFolder : IDisposable
{
    private static readonly string ScratchRoot = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "FullStackLauncherApiChecks"));
    public string Path { get; } = System.IO.Path.GetFullPath(
        System.IO.Path.Combine(ScratchRoot, Guid.NewGuid().ToString("N")));
    public ScratchFolder() => Directory.CreateDirectory(Path);
    public string Write(string relativePath, string content)
    {
        var file = System.IO.Path.Combine(Path, relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
        return file;
    }
    public void Dispose()
    {
        var resolved = System.IO.Path.GetFullPath(Path);
        var parent = System.IO.Path.GetDirectoryName(resolved);
        if (!string.Equals(parent, ScratchRoot, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(System.IO.Path.GetFileName(resolved), "N", out _))
            throw new InvalidOperationException("Refusing fixture cleanup outside the intended temporary test directory.");
        Directory.Delete(resolved, recursive: true);
    }
}

static class Check
{
    public static void True(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    public static void False(bool condition, string message) => True(!condition, message);
    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message} Expected {expected}; actual {actual}.");
    }
    public static void Pairs(ApiEndpointInventory inventory, params string[] expected)
    {
        var actual = inventory.Operations.Select(operation => $"{operation.HttpMethod} {operation.Route}")
            .Order(StringComparer.Ordinal).ToArray();
        var sortedExpected = expected.Order(StringComparer.Ordinal).ToArray();
        if (!sortedExpected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException($"Expected [{string.Join(", ", sortedExpected)}]; actual [{string.Join(", ", actual)}].");
        Equal(expected.Length, inventory.TotalOperationCount, "Total equals the distinct expected operation pairs.");
        Equal(expected.Length, inventory.HttpMethodCounts.Sum(count => count.Count), "Verb bucket totals equal the overall operation count.");
    }
    public static void Partial(ApiEndpointInventory inventory, string message)
    {
        True(inventory.IsPartial, message);
        True(inventory.Warnings.Count > 0, "Every partial inventory gives a useful warning.");
    }
    public static void WarningContains(ApiEndpointInventory inventory, string fragment, string message) =>
        True(inventory.Warnings.Any(warning => warning.Contains(fragment, StringComparison.OrdinalIgnoreCase)), message);
    public static void Throws<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
    public static async Task ThrowsAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
