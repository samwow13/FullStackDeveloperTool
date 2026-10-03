using System.IO;
using System.Reflection.Metadata;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using FullStackLauncher.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FullStackLauncher.Services;

/// <summary>
/// Inspects source syntax or an explicitly chosen local OpenAPI file. Never loads an API assembly,
/// evaluates MSBuild, starts a host, fetches a URL, or invokes application methods.
/// </summary>
public static class ApiEndpointDiscovery
{
    private static unsafe AssemblyMetadata ReadRuntimeMetadata()
    {
        // CoreLib stays loaded for the process lifetime. Read its metadata without requiring
        // an assembly file path, which is unavailable in the single-file launcher.
        if (!typeof(object).Assembly.TryGetRawMetadata(out var blob, out var length))
            throw new ApiEndpointDiscoveryException("Runtime metadata is unavailable for source discovery. Choose a local OpenAPI JSON snapshot.");
        return AssemblyMetadata.Create(ModuleMetadata.CreateFromMetadata((IntPtr)blob, length));
    }

    private const int MaximumFiles = 4000;
    private const int MaximumDirectories = 8000;
    private const int MaximumFileBytes = 4 * 1024 * 1024;
    private const int MaximumSourceBytes = 64 * 1024 * 1024;
    private const int MaximumOpenApiBytes = 16 * 1024 * 1024;
    private const int MaximumOperations = 50000;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", ".vscode", "node_modules", "packages", "artifacts",
        "publish", "portable", "TestResults", "Tests", "test", "tests", "monitor-runtime", "api-runtime",
        "api-secrets", "database-connections"
    };
    private static readonly Dictionary<string, string> VerbAttributes = new(StringComparer.Ordinal)
    {
        ["HttpGet"] = "GET", ["HttpPost"] = "POST", ["HttpPut"] = "PUT", ["HttpPatch"] = "PATCH",
        ["HttpDelete"] = "DELETE", ["HttpHead"] = "HEAD", ["HttpOptions"] = "OPTIONS"
    };
    private static readonly HashSet<string> OpenApiMethods = new(StringComparer.OrdinalIgnoreCase)
        { "get", "post", "put", "patch", "delete", "head", "options", "trace" };
    private static readonly HashSet<string> MapMethods = new(StringComparer.Ordinal)
        { "MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete", "MapMethods", "Map", "MapGroup", "MapFallback", "MapFallbackToFile" };
    private static readonly HashSet<string> KnownNonRoutingAttributes = new(StringComparer.Ordinal)
    {
        "ApiController", "Controller", "NonController", "NonAction", "ActionName", "Area", "Authorize", "AllowAnonymous",
        "Produces", "ProducesResponseType", "ProducesDefaultResponseType", "Consumes", "ApiExplorerSettings", "ApiConventionType",
        "ApiConventionMethod", "ResponseCache", "ValidateAntiForgeryToken", "AutoValidateAntiforgeryToken", "IgnoreAntiforgeryToken",
        "RequestSizeLimit", "DisableRequestSizeLimit", "RequestFormLimits", "EnableCors", "DisableCors", "EnableRateLimiting",
        "DisableRateLimiting", "OutputCache", "Obsolete", "ExcludeFromDescription", "Tags", "EndpointDescription", "EndpointSummary",
        "ServiceFilter", "TypeFilter", "FromServices", "JsonIgnore", "SuppressMessage", "GeneratedCode", "DebuggerStepThrough"
    };

    public static Task<ApiEndpointInventory> AnalyzeSourceAsync(string workingDirectory, CancellationToken cancellationToken = default) =>
        Task.Run(() => AnalyzeDirectory(workingDirectory, cancellationToken), cancellationToken);

    public static Task<ApiEndpointInventory> LoadOpenApiAsync(string filePath, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fullPath = GetFullPath(filePath, "Choose a readable local OpenAPI JSON file.");
            try
            {
                if (!File.Exists(fullPath) || IsFileSystemLink(fullPath))
                    throw new ApiEndpointDiscoveryException("The selected OpenAPI file is unavailable or is a filesystem link.");
                var json = ReadBoundedText(fullPath, MaximumOpenApiBytes, cancellationToken);
                return ImportOpenApiJson(json, fullPath, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (ApiEndpointDiscoveryException) { throw; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new ApiEndpointDiscoveryException("The selected OpenAPI file could not be read."); }
        }, cancellationToken);

    /// <summary>Pure syntax entrypoint used for focused fixtures; no file or network I/O.</summary>
    public static ApiEndpointInventory AnalyzeSources(IReadOnlyDictionary<string, string> sources, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var builder = new InventoryBuilder("Source", "Attributed source (static declarations)", "Source text");
        return AnalyzeSourcesCore(sources, builder, cancellationToken);
    }

    private static ApiEndpointInventory AnalyzeDirectory(string workingDirectory, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var root = GetFullPath(workingDirectory, "The API working folder is unavailable.");
        if (!Directory.Exists(root) || IsFileSystemLink(root))
            throw new ApiEndpointDiscoveryException("The API working folder is unavailable or is a filesystem link.");
        var builder = new InventoryBuilder("Source", "Attributed source (static declarations)", root);
        try
        {
            var projects = Directory.EnumerateFiles(root, "*.csproj", SearchOption.TopDirectoryOnly).Take(2).ToArray();
            if (projects.Length != 1)
                throw new ApiEndpointDiscoveryException("Source counting requires a service working folder containing exactly one .csproj. Choose a local OpenAPI JSON snapshot for other layouts.");
            InspectProject(projects[0], builder, token);
            var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>();
            stack.Push(root);
            long bytes = 0;
            var directories = 0;
            var entries = 0;
            var exceeded = false;
            while (stack.Count > 0 && !exceeded)
            {
                token.ThrowIfCancellationRequested();
                if (++directories > MaximumDirectories)
                { builder.Warn("The directory limit was reached; the source inventory is partial."); break; }
                var directory = stack.Pop();
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (++entries > 60000)
                        { builder.Warn("The filesystem-entry limit was reached; the source inventory is partial."); exceeded = true; break; }
                        var attributes = File.GetAttributes(entry);
                        if (IsFileSystemLink(entry, attributes))
                        { builder.Warn("Filesystem links were skipped; linked source is outside this scan."); continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            var name = Path.GetFileName(entry);
                            if (!ExcludedDirectories.Contains(name) && !name.StartsWith('.'))
                            {
                                if (Directory.EnumerateFiles(entry, "*.csproj", SearchOption.TopDirectoryOnly).Any())
                                    builder.Warn("Nested project folders were skipped; this scan is scoped to the selected service project folder.");
                                else stack.Push(entry);
                            }
                            continue;
                        }
                        if (!entry.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || entry.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase)) continue;
                        if (sources.Count >= MaximumFiles)
                        { builder.Warn("The 4,000-file limit was reached; the source inventory is partial."); exceeded = true; break; }
                        var length = new FileInfo(entry).Length;
                        if (length > MaximumFileBytes)
                        { builder.Warn("An oversized source file was skipped (4 MiB file limit)."); continue; }
                        bytes += length;
                        if (bytes > MaximumSourceBytes)
                        { builder.Warn("The 64 MiB source limit was reached; the source inventory is partial."); exceeded = true; break; }
                        try { sources[Path.GetRelativePath(root, entry)] = ReadBoundedText(entry, MaximumFileBytes, token); }
                        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                        { builder.Warn("One or more source files could not be read; the source inventory is partial."); }
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { builder.Warn("One or more source folders could not be read; the source inventory is partial."); }
            }
            return AnalyzeSourcesCore(sources, builder, token);
        }
        catch (OperationCanceledException) { throw; }
        catch (ApiEndpointDiscoveryException) { throw; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or XmlException)
        { throw new ApiEndpointDiscoveryException("The API project source metadata could not be read."); }
    }

    private static void InspectProject(string path, InventoryBuilder builder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (IsFileSystemLink(path)) throw new ApiEndpointDiscoveryException("The API project file is a filesystem link and cannot be scanned.");
        using var reader = XmlReader.Create(new StringReader(ReadBoundedText(path, MaximumFileBytes, token)), new XmlReaderSettings
        { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaximumFileBytes });
        var document = XDocument.Load(reader);
        if (document.Root?.Name.LocalName != "Project") throw new ApiEndpointDiscoveryException("The .csproj file is not a supported project document.");
        var elements = document.Descendants().ToArray();
        if (elements.Any(element => element.Name.LocalName == "EnableDefaultCompileItems" && !string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            throw new ApiEndpointDiscoveryException("This project disables default source inclusion. Import a local OpenAPI JSON snapshot; source membership cannot be determined without evaluating MSBuild.");
        if (elements.Any(element => element.Name.LocalName == "Compile" && element.Attributes().Any(attribute => attribute.Name.LocalName is "Include" or "Remove" or "Exclude")))
            builder.Warn("Custom Compile inclusion/exclusion was found. Counts describe source declarations in this folder; evaluated project membership is unknown.");
        if (elements.Any(element => element.Name.LocalName is "ProjectReference" or "Import"))
            builder.Warn("Referenced projects/imports are outside this folder scan; controllers supplied by them are not included.");
        if (elements.Any(element => element.Name.LocalName is "DefineConstants" or "TargetFrameworks"))
            builder.Warn("Build symbols/target frameworks were not evaluated; conditional declarations may differ from the running API.");
    }

    private static ApiEndpointInventory AnalyzeSourcesCore(IReadOnlyDictionary<string, string> sources, InventoryBuilder builder, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        builder.Warn("Static declarations only: MSBuild, application parts, generated code, inherited actions and runtime registration/conventions are not evaluated. This is not a verified inventory of the running API.");
        var trees = new List<SyntaxTree>();
        long length = 0;
        foreach (var source in sources.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            if (trees.Count >= MaximumFiles || (length += source.Value.Length * 2L) > MaximumSourceBytes)
            { builder.Warn("The source-size/file limit was reached; additional files were skipped."); break; }
            if (source.Value.Length > MaximumFileBytes)
            { builder.Warn("An oversized source text was skipped."); continue; }
            var tree = CSharpSyntaxTree.ParseText(source.Value, new CSharpParseOptions(LanguageVersion.Latest), source.Key, cancellationToken: token);
            if (tree.GetDiagnostics(token).Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
            { builder.Warn($"{SafeFile(source.Key)}: syntax errors; this file's declarations were skipped."); continue; }
            if (tree.GetRoot(token).DescendantTrivia().Any(trivia => trivia.IsDirective))
                builder.Warn("Preprocessor directives were found; only the default parsed branch is inspected, not the project's build symbols.");
            trees.Add(tree);
        }
        builder.ScannedFileCount = trees.Count;
        using var runtimeMetadata = ReadRuntimeMetadata();
        var compilation = CSharpCompilation.Create("ApiMetadataInspection", trees,
            [runtimeMetadata.GetReference()],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var classes = trees.SelectMany(tree => tree.GetRoot(token).DescendantNodes().OfType<ClassDeclarationSyntax>()).ToArray();
        var shadowedAttributes = classes.Select(type => TrimAttribute(type.Identifier.ValueText)).Where(IsRoutingAttribute).ToHashSet(StringComparer.Ordinal);
        var customRoutingAttributes = classes.Where(type => type.BaseList?.Types.Any(baseType => LastName(baseType.Type.ToString()) is "RouteAttribute" or "HttpMethodAttribute" or "AcceptVerbsAttribute" or "IRouteTemplateProvider" or "IActionHttpMethodProvider") == true)
            .Select(type => TrimAttribute(type.Identifier.ValueText)).ToHashSet(StringComparer.Ordinal);
        var tokenTransforms = false;
        var asyncNameChanged = false;
        foreach (var tree in trees)
        {
            token.ThrowIfCancellationRequested();
            var root = tree.GetRoot(token);
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = invocation.Expression switch { MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText, IdentifierNameSyntax identifier => identifier.Identifier.ValueText, _ => "" };
                if (MapMethods.Contains(name)) builder.Warn("Minimal API/dynamic mapping calls were found. Those operations are omitted from attributed-controller counts; import a generated local OpenAPI snapshot for broader coverage.");
                if (name is "MapControllerRoute" or "MapDefaultControllerRoute" or "MapAreaControllerRoute" or "UseMvc") builder.Warn("Conventional MVC routing was found; conventional route operations cannot be established from action attributes.");
                if (name is "AddApplicationPart" or "ConfigureApplicationPartManager") builder.Warn("Application-part customization was found; controllers from other assemblies are outside this scan.");
            }
            if (root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(node => node.Identifier.ValueText == "RouteTokenTransformerConvention")) tokenTransforms = true;
            if (root.DescendantNodes().OfType<IdentifierNameSyntax>().Any(node => node.Identifier.ValueText == "SuppressAsyncSuffixInActionNames")) asyncNameChanged = true;
        }
        if (tokenTransforms) builder.Warn("Route token transformers were found; routes requiring token substitution are omitted because transformed values are runtime metadata.");
        if (asyncNameChanged) builder.Warn("Custom Async action-name suppression was found; [action] routes for Async methods are omitted unless [ActionName] is explicit.");
        var controllerGroups = classes.Where(type => type.Parent is not TypeDeclarationSyntax)
            .GroupBy(FullTypeName, StringComparer.Ordinal);
        var controllers = 0;
        var actions = 0;
        foreach (var group in controllerGroups)
        {
            token.ThrowIfCancellationRequested();
            var parts = group.ToArray();
            var attributes = parts.SelectMany(part => part.AttributeLists.SelectMany(list => list.Attributes)).ToArray();
            var name = parts[0].Identifier.ValueText;
            if (!parts.Any(part => part.Modifiers.Any(SyntaxKind.PublicKeyword)) || parts.Any(part => part.Modifiers.Any(SyntaxKind.AbstractKeyword) || part.TypeParameterList is not null || part.Modifiers.Any(SyntaxKind.StaticKeyword))) continue;
            if (attributes.Any(attribute => AttributeName(attribute) == "NonController")) continue;
            if (!name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) && !attributes.Any(attribute => AttributeName(attribute) is "Controller" or "ApiController")) continue;
            controllers++;
            var controllerName = name.EndsWith("Controller", StringComparison.OrdinalIgnoreCase) ? name[..^10] : name;
            if (parts.Any(part => part.BaseList?.Types.Any(baseType => LastName(baseType.Type.ToString()) is not "ControllerBase" and not "Controller" and not "object") == true))
                builder.Warn($"{name}: custom base types/interfaces may supply inherited actions or routing; only declared methods are counted.");
            WarnAttributes(attributes, name, customRoutingAttributes, builder);
            var controllerRoutes = ReadRoutingAttributes(attributes, compilation, shadowedAttributes, customRoutingAttributes, builder, name, token);
            var controllerSelectors = CreateSelectors(controllerRoutes);
            var area = ReadNamedValue(attributes, "Area", compilation, builder, name, token);
            foreach (var method in parts.SelectMany(part => part.Members.OfType<MethodDeclarationSyntax>()))
            {
                token.ThrowIfCancellationRequested();
                var methodAttributes = method.AttributeLists.SelectMany(list => list.Attributes).ToArray();
                if (method.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    var symbol = compilation.GetSemanticModel(method.SyntaxTree).GetDeclaredSymbol(method, token);
                    if (symbol?.PartialImplementationPart is not null) continue;
                    if (symbol?.PartialDefinitionPart is not null)
                        methodAttributes = symbol.PartialDefinitionPart.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(token))
                            .OfType<MethodDeclarationSyntax>().SelectMany(definition => definition.AttributeLists.SelectMany(list => list.Attributes))
                            .Concat(methodAttributes).ToArray();
                }
                if (!IsAction(method, methodAttributes, compilation, token)) continue;
                actions++;
                var location = $"{name}.{method.Identifier.ValueText}";
                if (method.Modifiers.Any(SyntaxKind.OverrideKeyword))
                    builder.Warn($"{location}: inherited method attributes cannot be established; only this declaration's attributes are inspected.");
                WarnAttributes(methodAttributes, location, customRoutingAttributes, builder);
                var actionRoutes = ReadRoutingAttributes(methodAttributes, compilation, shadowedAttributes, customRoutingAttributes, builder, location, token);
                if (actionRoutes.Any(route => !route.Resolved))
                { builder.Warn($"{location}: unresolved route/verb metadata; this action's operations were omitted."); continue; }
                var actionName = ReadNamedValue(methodAttributes, "ActionName", compilation, builder, location, token);
                var explicitActionName = methodAttributes.Any(attribute => AttributeName(attribute) == "ActionName");
                if (explicitActionName && actionName is null)
                { builder.Warn($"{location}: unresolved ActionName; this action's operations were omitted."); continue; }
                actionName ??= method.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal) ? method.Identifier.ValueText[..^5] : method.Identifier.ValueText;
                var actionArea = methodAttributes.Any(attribute => AttributeName(attribute) == "Area")
                    ? ReadNamedValue(methodAttributes, "Area", compilation, builder, location, token) : area;
                var sourceOrigin = new ApiEndpointOrigin(method.SyntaxTree.FilePath, method.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    name, method.Identifier.ValueText, DeclarationOffset: method.SpanStart, ControllerIdentity: group.Key[..^2]);
                foreach (var actionSelector in CreateSelectors(actionRoutes))
                {
                    if (IsAbsolute(actionSelector.Template))
                        AddSelector(null, actionSelector, controllerRoutes, controllerName, actionName, actionArea, sourceOrigin, builder, tokenTransforms, asyncNameChanged && !explicitActionName && method.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal), token);
                    else if (controllerSelectors.Any(selector => selector.HasRoute))
                    {
                        foreach (var controllerSelector in controllerSelectors.Where(selector => selector.HasRoute))
                            AddSelector(controllerSelector, actionSelector, controllerRoutes, controllerName, actionName, actionArea, sourceOrigin, builder, tokenTransforms, asyncNameChanged && !explicitActionName && method.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal), token);
                    }
                    else
                        AddSelector(null, actionSelector, controllerRoutes, controllerName, actionName, actionArea, sourceOrigin, builder, tokenTransforms, asyncNameChanged && !explicitActionName && method.Identifier.ValueText.EndsWith("Async", StringComparison.Ordinal), token);
                }
            }
        }
        builder.ControllerCount = controllers;
        builder.ControllerActionCount = actions;
        return builder.Build();
    }

    private static bool IsAction(MethodDeclarationSyntax method, AttributeSyntax[] attributes, CSharpCompilation compilation, CancellationToken token) =>
        method.Modifiers.Any(SyntaxKind.PublicKeyword) && !method.Modifiers.Any(SyntaxKind.StaticKeyword) &&
        !method.Modifiers.Any(SyntaxKind.AbstractKeyword) && method.TypeParameterList is null && method.ExplicitInterfaceSpecifier is null &&
        !attributes.Any(attribute => AttributeName(attribute) == "NonAction") &&
        !(method.Modifiers.Any(SyntaxKind.OverrideKeyword) && method.Identifier.ValueText is "ToString" or "GetHashCode" or "Equals") &&
        !IsDisposeImplementation(method, compilation, token);

    private static bool IsDisposeImplementation(MethodDeclarationSyntax method, CSharpCompilation compilation, CancellationToken token)
    {
        if (method.Identifier.ValueText != "Dispose" || method.ParameterList.Parameters.Count != 0) return false;
        var symbol = compilation.GetSemanticModel(method.SyntaxTree).GetDeclaredSymbol(method, token);
        if (symbol is null) return false;
        foreach (var contract in symbol.ContainingType.AllInterfaces.Where(contract => contract.ToDisplayString() == "System.IDisposable"))
            foreach (var member in contract.GetMembers("Dispose"))
                if (SymbolEqualityComparer.Default.Equals(symbol.ContainingType.FindImplementationForInterfaceMember(member), symbol)) return true;
        return false;
    }

    private sealed record RoutingAttribute(string Name, string? Template, IReadOnlyList<string> Methods, bool HasRoute, bool Resolved);
    private sealed record Selector(string? Template, IReadOnlyList<string> Methods, bool HasRoute, bool Resolved);

    private static RoutingAttribute[] ReadRoutingAttributes(AttributeSyntax[] attributes, CSharpCompilation compilation,
        HashSet<string> shadowed, HashSet<string> custom, InventoryBuilder builder, string location, CancellationToken token)
    {
        var result = new List<RoutingAttribute>();
        foreach (var attribute in attributes)
        {
            token.ThrowIfCancellationRequested();
            if (result.Count >= 128)
            { builder.Warn($"{location}: the route-attribute limit was reached; this declaration's operations were omitted."); result.Add(new RoutingAttribute("", null, [], true, false)); break; }
            var name = AttributeName(attribute);
            if (!IsRoutingAttribute(name) && !custom.Contains(name)) continue;
            var model = compilation.GetSemanticModel(attribute.SyntaxTree);
            var symbol = model.GetSymbolInfo(attribute, token).Symbol as IMethodSymbol;
            if (custom.Contains(name) || shadowed.Contains(name) || (symbol is not null && symbol.ContainingType.ContainingNamespace.ToDisplayString() != "Microsoft.AspNetCore.Mvc") || !IsFrameworkAttribute(attribute))
            {
                builder.Warn($"{location}: custom/aliased routing attribute cannot be verified; this action's route metadata is unresolved.");
                result.Add(new RoutingAttribute(name, null, [], true, false)); continue;
            }
            var args = attribute.ArgumentList?.Arguments.ToArray() ?? [];
            var positional = args.Where(argument => argument.NameEquals is null && argument.NameColon is null).ToArray();
            var routeArg = args.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == "Route" || argument.NameColon?.Name.Identifier.ValueText == "template")?.Expression;
            if (name != "AcceptVerbs") routeArg ??= positional.FirstOrDefault()?.Expression;
            var route = routeArg is null ? null : ReadString(routeArg, model, token);
            var valid = routeArg is null || route is not null;
            var hasRoute = route is not null || args.Any(argument => argument.NameEquals?.Name.Identifier.ValueText is "Name" or "Order");
            if (name == "Route") { hasRoute = true; valid &= route is not null; }
            var verbs = new List<string>();
            if (VerbAttributes.TryGetValue(name, out var verb)) verbs.Add(verb);
            else if (name == "AcceptVerbs")
            {
                var verbArgs = args.Where(argument => argument.NameEquals is null && argument.NameColon?.Name.Identifier.ValueText != "template").ToArray();
                foreach (var argument in verbArgs)
                {
                    if (argument.Expression is ArrayCreationExpressionSyntax { Initializer: not null } array)
                        foreach (var expression in array.Initializer.Expressions) AddVerb(expression);
                    else if (argument.Expression is ImplicitArrayCreationExpressionSyntax implicitArray)
                        foreach (var expression in implicitArray.Initializer.Expressions) AddVerb(expression);
                    else AddVerb(argument.Expression);
                }
                valid &= verbs.Count > 0;
            }
            result.Add(new RoutingAttribute(name, route, verbs.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), hasRoute, valid));
            void AddVerb(ExpressionSyntax expression)
            {
                var value = ReadString(expression, model, token);
                if (value is null || !IsHttpToken(value)) valid = false;
                else verbs.Add(value.ToUpperInvariant());
            }
        }
        return result.ToArray();
    }

    // Mirrors the standard MVC selector split; a template-bound verb does not leak to other routes.
    private static Selector[] CreateSelectors(RoutingAttribute[] attributes)
    {
        var routeProviders = attributes.Where(attribute => attribute.HasRoute).ToArray();
        var silent = attributes.Where(attribute => !attribute.HasRoute).ToArray();
        var createSilent = silent.Length > 0 && !routeProviders.Any(attribute => attribute.Methods.Count == 0);
        if (routeProviders.Length == 0 && !createSilent) return [new Selector(null, [], false, attributes.All(attribute => attribute.Resolved))];
        var selectors = routeProviders.Select(route => new Selector(route.Template,
            route.Methods.Count > 0 ? route.Methods : silent.SelectMany(attribute => attribute.Methods).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            true, route.Resolved && (route.Methods.Count > 0 || silent.All(attribute => attribute.Resolved)))).ToList();
        if (createSilent) selectors.Add(new Selector(null, silent.SelectMany(attribute => attribute.Methods).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), false, silent.All(attribute => attribute.Resolved)));
        return selectors.ToArray();
    }

    private static void AddSelector(Selector? controller, Selector action, RoutingAttribute[] controllerAttributes,
        string controllerName, string actionName, string? area, ApiEndpointOrigin origin, InventoryBuilder builder,
        bool tokenTransforms, bool asyncNameChanged, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!builder.TryInspectSelector()) return;
        if (!action.Resolved || controller is { Resolved: false } || (controller is null && controllerAttributes.Any(attribute => !attribute.Resolved)))
        { builder.Warn($"{origin.Controller}.{origin.Action}: unresolved controller route metadata; operation omitted."); return; }
        var route = CombineTemplates(controller?.Template, action.Template);
        if (route is null)
        { builder.Warn($"{origin.Controller}.{origin.Action}: conventional/unresolved route; operation omitted."); return; }
        if ((tokenTransforms && (route.Contains('[') || route.Contains(']'))) || (asyncNameChanged && route.Contains("[action]", StringComparison.OrdinalIgnoreCase)))
        { builder.Warn($"{origin.Controller}.{origin.Action}: customized route tokens cannot be resolved; operation omitted."); return; }
        if (!TryReplaceTokens(route, controllerName, actionName, area, out route))
        { builder.Warn($"{origin.Controller}.{origin.Action}: unknown/unresolved route token; operation omitted."); return; }
        if (route == "//" || route.Contains("//", StringComparison.Ordinal) || !HasBalancedBraces(route))
        { builder.Warn($"{origin.Controller}.{origin.Action}: invalid route template; operation omitted."); return; }
        // Controller and action HTTP constraints intersect, rather than adding unrelated verbs.
        var controllerMethods = controller?.Methods ?? controllerAttributes.SelectMany(attribute => attribute.Methods).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var methods = action.Methods.Count == 0 ? controllerMethods : controllerMethods.Count == 0 ? action.Methods : action.Methods.Intersect(controllerMethods, StringComparer.OrdinalIgnoreCase).ToArray();
        if (action.Methods.Count > 0 && controllerMethods.Count > 0 && methods.Count == 0)
        { builder.Warn($"{origin.Controller}.{origin.Action}: incompatible controller/action HTTP constraints; operation omitted."); return; }
        if (methods.Count == 0) methods = ["ALL"];
        foreach (var method in methods) builder.Add(method, "/" + route, origin);
    }

    private static string? CombineTemplates(string? left, string? right)
    {
        if (left is null && right is null) return null;
        string combined;
        if (right is null) combined = left!;
        else if (string.IsNullOrEmpty(left) || left is "/" or "~/" || IsAbsolute(right)) combined = right;
        else combined = left.EndsWith('/') ? left + right : left + "/" + right;
        if (combined == "//") return combined;
        if (combined.StartsWith("~/", StringComparison.Ordinal)) combined = combined[2..];
        else if (combined.StartsWith('/')) combined = combined[1..];
        if (combined.EndsWith('/') && combined.Length > 0) combined = combined[..^1];
        return combined;
    }

    private static bool TryReplaceTokens(string template, string controller, string action, string? area, out string route)
    {
        var result = new StringBuilder();
        for (var index = 0; index < template.Length; index++)
        {
            var character = template[index];
            if (character is '[' or ']')
            {
                if (index + 1 < template.Length && template[index + 1] == character)
                { result.Append(character); index++; continue; }
                if (character == ']') { route = ""; return false; }
                var end = template.IndexOf(']', index + 1);
                if (end < 0) { route = ""; return false; }
                var key = template[(index + 1)..end];
                var value = key.ToLowerInvariant() switch { "controller" => controller, "action" => action, "area" => area, _ => null };
                if (value is null) { route = ""; return false; }
                result.Append(value); index = end;
            }
            else result.Append(character);
        }
        route = result.ToString(); return true;
    }

    private static string? ReadNamedValue(AttributeSyntax[] attributes, string name, CSharpCompilation compilation, InventoryBuilder builder, string location, CancellationToken token)
    {
        var attribute = attributes.FirstOrDefault(attribute => AttributeName(attribute) == name);
        if (attribute is null) return null;
        var expression = attribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
        var value = expression is null ? null : ReadString(expression, compilation.GetSemanticModel(attribute.SyntaxTree), token);
        if (value is null) builder.Warn($"{location}: {name} is not a resolvable constant string.");
        return value;
    }

    private static string? ReadString(ExpressionSyntax expression, SemanticModel model, CancellationToken token)
    {
        var value = model.GetConstantValue(expression, token);
        return value.HasValue ? value.Value as string : null;
    }

    private static void WarnAttributes(AttributeSyntax[] attributes, string location, HashSet<string> custom, InventoryBuilder builder)
    {
        foreach (var attribute in attributes)
        {
            var name = AttributeName(attribute);
            if (!IsRoutingAttribute(name) && !KnownNonRoutingAttributes.Contains(name))
                builder.Warn($"{location}: custom/unknown attributes may alter routing or action selection; only standard MVC route declarations are counted.");
            if (custom.Contains(name)) builder.Warn($"{location}: custom routing attribute is unsupported; use an OpenAPI snapshot for runtime metadata.");
        }
    }

    private static bool IsRoutingAttribute(string name) => name is "Route" or "AcceptVerbs" || VerbAttributes.ContainsKey(name);
    private static bool IsFrameworkAttribute(AttributeSyntax attribute)
    {
        var text = ExpandAttributeAlias(attribute).Replace("global::", "", StringComparison.Ordinal);
        var separator = text.LastIndexOf('.');
        return separator < 0 || text[..separator] == "Microsoft.AspNetCore.Mvc";
    }

    private static string AttributeName(AttributeSyntax attribute)
    {
        return TrimAttribute(LastName(ExpandAttributeAlias(attribute)));
    }
    private static string ExpandAttributeAlias(AttributeSyntax attribute)
    {
        var text = attribute.Name.ToString();
        var first = text.Split('.')[0];
        var localUsings = attribute.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(space => space.Usings);
        var rootUsings = (attribute.SyntaxTree.GetRoot() as CompilationUnitSyntax)?.Usings ?? default;
        var alias = localUsings.Concat(rootUsings).FirstOrDefault(usingDirective => usingDirective.Alias?.Name.Identifier.ValueText == first);
        if (alias?.Name is not null) text = alias.Name + text[first.Length..];
        return text;
    }
    private static string TrimAttribute(string name) => name.EndsWith("Attribute", StringComparison.Ordinal) ? name[..^9] : name;
    private static string LastName(string name) => name.Replace("global::", "", StringComparison.Ordinal).Split('.').Last();
    private static string FullTypeName(ClassDeclarationSyntax type) => string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(space => space.Name.ToString()).Append(type.Identifier.ValueText + "`" + (type.TypeParameterList?.Parameters.Count ?? 0)));
    private static bool IsAbsolute(string? template) => template?.StartsWith('/') == true || template?.StartsWith("~/", StringComparison.Ordinal) == true;
    private static bool HasBalancedBraces(string route)
    {
        var depth = 0;
        foreach (var character in route)
        { if (character == '{') depth++; else if (character == '}') depth--; if (depth < 0) return false; }
        return depth == 0;
    }
    private static bool IsHttpToken(string value) => value.Length is > 0 and <= 64 && value.All(character => char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character));
    private static string SafeFile(string file) => Path.GetFileName(file);

    /// <summary>Counts top-level documented paths, not callback/webhook operations or server alternatives.</summary>
    public static ApiEndpointInventory ImportOpenApiJson(string json, string sourceLabel = "OpenAPI JSON", CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (json.Length > MaximumOpenApiBytes) throw new ApiEndpointDiscoveryException("The OpenAPI JSON exceeds the 16 MiB limit.");
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 80 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ApiEndpointDiscoveryException("The selected file is not an OpenAPI JSON object.");
            var swagger = StringProperty(root, "swagger");
            var openApi = StringProperty(root, "openapi");
            if (swagger != "2.0" && (openApi is null || (!openApi.StartsWith("3.0.", StringComparison.Ordinal) && !openApi.StartsWith("3.1.", StringComparison.Ordinal))))
                throw new ApiEndpointDiscoveryException("Supported snapshots are OpenAPI 3.0/3.1 or Swagger 2.0 JSON.");
            var builder = new InventoryBuilder("OpenAPI", swagger == "2.0" ? "Swagger 2.0 (local documented snapshot)" : $"OpenAPI {openApi} (local documented snapshot)", sourceLabel);
            if (!root.TryGetProperty("paths", out var paths) || paths.ValueKind != JsonValueKind.Object)
                throw new ApiEndpointDiscoveryException("The OpenAPI snapshot requires a paths object.");
            var basePath = swagger == "2.0" ? StringProperty(root, "basePath") ?? "" : "";
            if (basePath.Length > 0 && (!basePath.StartsWith('/') || basePath.Contains('?') || basePath.Contains('#')))
                throw new ApiEndpointDiscoveryException("The Swagger basePath is invalid.");
            if (root.TryGetProperty("webhooks", out _) || root.TryGetProperty("callbacks", out _))
                builder.Warn("Webhook/callback operations are outside the top-level paths count.");
            var pathNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var path in paths.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (path.Name.StartsWith("x-", StringComparison.OrdinalIgnoreCase)) continue;
                if (!path.Name.StartsWith('/')) { builder.Warn("A non-path entry was skipped in paths."); continue; }
                if (!pathNames.Add(path.Name)) builder.Warn("Duplicate JSON path keys were found; distinct operation pairs are counted once.");
                var item = ResolveLocalReference(path.Value, root, builder, cancellationToken);
                if (item is null || item.Value.ValueKind != JsonValueKind.Object)
                { builder.Warn("An invalid or unresolved path item was omitted."); continue; }
                var operationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in item.Value.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!OpenApiMethods.Contains(property.Name)) continue;
                    if (!operationNames.Add(property.Name)) builder.Warn("Duplicate JSON HTTP-operation keys were found; distinct operation pairs are counted once.");
                    if (property.Value.ValueKind != JsonValueKind.Object || property.Value.TryGetProperty("$ref", out _))
                    { builder.Warn("An invalid or referenced operation object was omitted; operation-level references are unsupported."); continue; }
                    if (property.Value.TryGetProperty("callbacks", out _)) builder.Warn("Callback operations are outside the top-level paths count.");
                    var operationId = StringProperty(property.Value, "operationId") ?? "";
                    var tag = property.Value.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
                        ? tags.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()).FirstOrDefault()
                        : null;
                    if (operationId.Length > 1024)
                    { operationId = ""; builder.Warn("An oversized operationId was omitted from display metadata."); }
                    if (tag?.Length > 256)
                    { tag = null; builder.Warn("An oversized first tag was omitted from display metadata."); }
                    var route = (basePath.TrimEnd('/') + path.Name);
                    builder.Add(property.Name.ToUpperInvariant(), route, new ApiEndpointOrigin(sourceLabel, 0, "", operationId, tag));
                }
            }
            return builder.Build();
        }
        catch (JsonException) { throw new ApiEndpointDiscoveryException("The selected file is not valid supported OpenAPI JSON."); }
    }

    private static JsonElement? ResolveLocalReference(JsonElement value, JsonElement root, InventoryBuilder builder, CancellationToken token)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 32; depth++)
        {
            token.ThrowIfCancellationRequested();
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("$ref", out var reference)) return value;
            if (reference.ValueKind != JsonValueKind.String) { builder.Warn("An invalid path-item reference was omitted."); return null; }
            var pointer = reference.GetString()!;
            if (!pointer.StartsWith("#/", StringComparison.Ordinal))
            { builder.Warn("External path-item references were omitted; no files or URLs are fetched."); return null; }
            if (!visited.Add(pointer)) { builder.Warn("A cyclic path-item reference was omitted."); return null; }
            if (value.EnumerateObject().Any(property => property.Name != "$ref" && !property.Name.StartsWith("x-", StringComparison.Ordinal)))
            { builder.Warn("A path item mixes $ref with sibling fields; its ambiguous operations were omitted."); return null; }
            value = root;
            foreach (var segment in pointer[2..].Split('/'))
            {
                var name = Uri.UnescapeDataString(segment).Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out value))
                { builder.Warn("An unresolved local path-item reference was omitted."); return null; }
            }
        }
        builder.Warn("The path-item reference depth limit was reached; referenced operations were omitted."); return null;
    }

    private static string? StringProperty(JsonElement element, string name) => element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    private static string GetFullPath(string path, string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
                throw new ApiEndpointDiscoveryException(message);
            return Path.GetFullPath(path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new ApiEndpointDiscoveryException(message); }
    }
    private static bool IsFileSystemLink(string path)
    {
        try { return IsFileSystemLink(path, File.GetAttributes(path)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return true; }
    }
    private static bool IsFileSystemLink(string path, FileAttributes attributes)
    {
        if ((attributes & FileAttributes.ReparsePoint) == 0) return false;
        // OneDrive cloud placeholders also carry ReparsePoint. LinkTarget identifies real symlinks/junctions
        // without rejecting ordinary configured OneDrive source files or following their link targets.
        FileSystemInfo info = (attributes & FileAttributes.Directory) != 0 ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget is not null;
    }
    private static string ReadBoundedText(string path, int limit, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > limit) throw new ApiEndpointDiscoveryException("A metadata file exceeds the supported size limit.");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var result = new StringBuilder();
        var buffer = new char[8192];
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            token.ThrowIfCancellationRequested();
            result.Append(buffer, 0, count);
            if (result.Length > limit) throw new ApiEndpointDiscoveryException("A metadata file exceeds the supported size limit.");
        }
        return result.ToString();
    }

    private sealed class InventoryBuilder(string sourceKind, string sourceLabel, string sourcePath)
    {
        private readonly Dictionary<string, (string Method, string Route, List<ApiEndpointOrigin> Origins)> _operations = new(StringComparer.Ordinal);
        private readonly List<string> _warnings = [];
        private readonly HashSet<string> _warningKeys = new(StringComparer.Ordinal);
        private int _inspectedSelectors;
        private long _retainedRouteCharacters;
        public int ScannedFileCount { get; set; }
        public int? ControllerCount { get; set; }
        public int? ControllerActionCount { get; set; }
        public bool TryInspectSelector()
        {
            if (++_inspectedSelectors <= 100000) return true;
            Warn("The 100,000-selector limit was reached; further source operations were omitted."); return false;
        }
        public void Warn(string text)
        {
            if (!_warningKeys.Add(text)) return;
            if (_warnings.Count < 100) _warnings.Add(text);
            else if (_warnings.Count == 100) _warnings.Add("Further discovery warnings were omitted (100-warning display limit).");
        }
        public void Add(string method, string route, ApiEndpointOrigin origin)
        {
            if (route.Length > 8192)
            { Warn("An oversized route template was omitted (8 KiB template limit)."); return; }
            var key = method.ToUpperInvariant() + "\n" + route;
            if (_operations.TryGetValue(key, out var existing))
            {
                if (!existing.Origins.Contains(origin))
                {
                    existing.Origins.Add(origin);
                    if (sourceKind == "Source") Warn("Repeated verb/route pairs have multiple source origins. They count once; runtime ambiguity or action constraints are not verified.");
                }
                return;
            }
            if (_operations.Count >= MaximumOperations)
            { Warn("The 50,000-operation limit was reached; further operations were omitted."); return; }
            if (_retainedRouteCharacters + route.Length > 8 * 1024 * 1024)
            { Warn("The retained route-metadata limit was reached; further operations were omitted."); return; }
            _retainedRouteCharacters += route.Length;
            _operations.Add(key, (method.ToUpperInvariant(), route, [origin]));
        }
        public ApiEndpointInventory Build() => new()
        {
            SourceKind = sourceKind, SourceLabel = sourceLabel, SourcePath = sourcePath,
            ScannedFileCount = ScannedFileCount, ControllerCount = ControllerCount, ControllerActionCount = ControllerActionCount,
            Operations = _operations.Values.OrderBy(operation => operation.Route, StringComparer.OrdinalIgnoreCase)
                .ThenBy(operation => operation.Method, StringComparer.Ordinal).Select(operation => new ApiEndpointOperation(operation.Method, operation.Route, operation.Origins.ToArray())).ToArray(),
            Warnings = _warnings.ToArray(), IsPartial = _warnings.Count > 0
        };
    }
}
