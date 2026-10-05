using FullStackLauncher.Models;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FullStackLauncher.Services;

public static partial class ApiEndpointDiscovery
{
    private static readonly Dictionary<string, string> MinimalVerbMethods = new(StringComparer.Ordinal)
    {
        ["MapGet"] = "GET", ["MapPost"] = "POST", ["MapPut"] = "PUT", ["MapPatch"] = "PATCH",
        ["MapDelete"] = "DELETE"
    };
    private static readonly HashSet<string> GroupConventions = new(StringComparer.Ordinal)
    {
        "WithTags", "WithMetadata", "WithGroupName", "RequireAuthorization", "AllowAnonymous", "RequireCors",
        "RequireRateLimiting", "DisableRateLimiting", "CacheOutput", "AddEndpointFilter", "AddEndpointFilterFactory",
        "ExcludeFromDescription", "WithOpenApi", "WithDescription", "WithSummary", "WithName"
    };
    private static readonly HashSet<string> MiddlewareConventions = new(StringComparer.Ordinal)
        { "UseRouting", "UseAuthentication", "UseAuthorization", "UseCors", "UseRateLimiter" };

    private static void AnalyzeMinimalApis(IReadOnlyList<SyntaxTree> trees, CSharpCompilation compilation,
        InventoryBuilder builder, CancellationToken token) => new MinimalSourceScanner(trees, compilation, builder, token).Analyze();

    // ASP.NET references are intentionally not loaded. Only known source types, builder factories and
    // stable local aliases establish receiver provenance; an arbitrary method named MapGet is not enough.
    private sealed class MinimalSourceScanner
    {
        private readonly CSharpCompilation _compilation;
        private readonly InventoryBuilder _inventory;
        private readonly CancellationToken _token;
        private readonly InvocationExpressionSyntax[] _calls;
        private readonly Dictionary<string, InvocationExpressionSyntax[]> _callsByName;
        private readonly Dictionary<string, MethodDeclarationSyntax[]> _methodsByName;
        private readonly HashSet<string> _sourceTypeNames;
        private readonly HashSet<string> _sourceTypeFullNames;
        private readonly HashSet<ISymbol> _writtenSymbols = new(SymbolEqualityComparer.Default);
        private readonly Dictionary<ISymbol, Receiver?> _receiverCache = new(SymbolEqualityComparer.Default);
        private readonly HashSet<string> _handlers = new(StringComparer.Ordinal);
        private readonly Dictionary<SyntaxTree, SemanticModel> _models = [];

        private enum ReceiverKind { ApplicationBuilder, MiddlewareBuilder, Application, EndpointBuilder, Group }
        private sealed record Receiver(ReceiverKind Kind, IReadOnlyList<string> Prefixes, bool UnknownPrefix = false)
        {
            public bool IsEndpoint => Kind is ReceiverKind.Application or ReceiverKind.EndpointBuilder or ReceiverKind.Group;
        }
        private sealed record MappingCall(string Name, ExpressionSyntax Receiver, IReadOnlyList<ArgumentSyntax> Arguments, bool ExplicitFramework);

        public MinimalSourceScanner(IReadOnlyList<SyntaxTree> trees, CSharpCompilation compilation,
            InventoryBuilder inventory, CancellationToken token)
        {
            _compilation = compilation; _inventory = inventory; _token = token;
            var roots = trees.Select(tree => tree.GetRoot(token)).ToArray();
            _calls = roots.SelectMany(root => root.DescendantNodes().OfType<InvocationExpressionSyntax>()).ToArray();
            _callsByName = _calls.GroupBy(CallName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            _methodsByName = roots.SelectMany(root => root.DescendantNodes().OfType<MethodDeclarationSyntax>())
                .GroupBy(method => method.Identifier.ValueText, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var sourceTypes = roots.SelectMany(root => root.DescendantNodes().Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax)).ToArray();
            _sourceTypeNames = sourceTypes.Select(SourceTypeName).ToHashSet(StringComparer.Ordinal);
            _sourceTypeFullNames = sourceTypes
                .Select(type => string.Join(".", type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(item => item.Name.ToString()).Append(SourceTypeName(type))))
                .ToHashSet(StringComparer.Ordinal);
            foreach (var root in roots)
            {
                token.ThrowIfCancellationRequested();
                foreach (var assignment in root.DescendantNodes().OfType<AssignmentExpressionSyntax>()) RecordWrite(assignment.Left);
                foreach (var unary in root.DescendantNodes().OfType<PrefixUnaryExpressionSyntax>().Where(node => node.IsKind(SyntaxKind.PreIncrementExpression) || node.IsKind(SyntaxKind.PreDecrementExpression))) RecordWrite(unary.Operand);
                foreach (var unary in root.DescendantNodes().OfType<PostfixUnaryExpressionSyntax>().Where(node => node.IsKind(SyntaxKind.PostIncrementExpression) || node.IsKind(SyntaxKind.PostDecrementExpression))) RecordWrite(unary.Operand);
                foreach (var argument in root.DescendantNodes().OfType<ArgumentSyntax>().Where(argument => argument.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || argument.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))) RecordWrite(argument.Expression);
            }
        }

        public void Analyze()
        {
            var registrations = 0;
            foreach (var call in _calls)
            {
                _token.ThrowIfCancellationRequested();
                var name = CallName(call);
                var standard = MinimalVerbMethods.ContainsKey(name) || name is "MapMethods" or "Map";
                if (!standard && !name.StartsWith("Map", StringComparison.Ordinal)) continue;
                var mapping = GetMappingCall(call);
                if (mapping is null) continue;
                var receiver = ResolveReceiver(mapping.Receiver, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0);
                if (receiver is null || !receiver.IsEndpoint)
                {
                    if (standard || name == "MapGroup")
                        Warn(call, "mapping receiver is not a statically identified ASP.NET endpoint builder; operation omitted.");
                    continue;
                }
                if (name == "MapGroup")
                {
                    if (ResolveReceiver(call, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0) is { UnknownPrefix: true })
                        Warn(call, "MapGroup prefix is unresolved; registrations in this group require a resolvable literal or constant prefix.");
                    continue;
                }
                if (!standard)
                {
                    if (name is not "MapControllers" and not "MapControllerRoute" and not "MapDefaultControllerRoute" and not "MapAreaControllerRoute")
                        Warn(call, "custom/dynamic mapping is not expanded. Only recognized registration declarations in scanned source are counted.");
                    continue;
                }
                if (!_inventory.TryInspectSelector()) break;
                if (IsSourceMappingOverride(call, name) && !mapping.ExplicitFramework)
                { Warn(call, "a source-defined mapping method may replace the standard ASP.NET registration; operation omitted."); continue; }
                var routeArgument = FindArgument(mapping.Arguments, 0, "pattern");
                var handlerArgument = FindArgument(mapping.Arguments, name == "MapMethods" ? 2 : 1, "handler", "requestDelegate");
                if (routeArgument is null || handlerArgument is null)
                { Warn(call, "unsupported registration arguments; operation omitted."); continue; }
                if (name == "Map" && !mapping.ExplicitFramework && receiver.Kind == ReceiverKind.Application && !IsRequestDelegate(handlerArgument.Expression))
                { Warn(call, "Map may create a middleware branch. An explicit endpoint Map call or an identifiable HttpContext handler is required; operation omitted."); continue; }
                registrations++;
                var handlerIdentity = ReadHandlerIdentity(handlerArgument.Expression);
                if (handlerIdentity is not null) _handlers.Add(handlerIdentity);
                else Warn(call, "handler declaration is unresolved; registration counts, but the distinct handler count excludes it.");
                if (call.Ancestors().Any(node => node is BaseMethodDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
                    _inventory.Warn("Minimal API helper/lambda bodies are scanned as declarations. Calls, execution order, conditions and runtime reachability are not evaluated; an uncalled helper may contribute registrations.");
                var route = ReadString(routeArgument.Expression, Model(call), _token);
                if (route is null || !IsMinimalPattern(route))
                { Warn(call, "route is not a supported literal/constant template; operation omitted."); continue; }
                var verbs = ReadVerbs(mapping, call);
                if (verbs is null) continue;
                if (receiver.UnknownPrefix)
                    Warn(call, "receiver/group prefix is unresolved for one or more callers; those operations are omitted.");
                var origin = new ApiEndpointOrigin(call.SyntaxTree.FilePath, call.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                    "", HandlerLabel(handlerArgument.Expression, name), DeclarationOffset: call.SpanStart,
                    OriginKind: "Minimal", HandlerIdentity: handlerIdentity);
                foreach (var prefix in receiver.Prefixes)
                {
                    var combined = CombineMinimalPatterns(prefix, route);
                    if (combined is null) { Warn(call, "combined group/route template is unsupported; operation omitted."); continue; }
                    foreach (var verb in verbs) _inventory.Add(verb, combined, origin);
                }
            }
            _inventory.MinimalEndpointCount = registrations;
            _inventory.MinimalHandlerCount = _handlers.Count;
        }

        private IReadOnlyList<string>? ReadVerbs(MappingCall mapping, InvocationExpressionSyntax call)
        {
            if (MinimalVerbMethods.TryGetValue(mapping.Name, out var verb)) return [verb];
            if (mapping.Name == "Map") return ["ALL"];
            var argument = FindArgument(mapping.Arguments, 1, "httpMethods");
            var expressions = argument is null ? null : ReadMethodList(argument.Expression, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0);
            if (expressions is null || expressions.Count is 0 or > 128)
            { Warn(call, "MapMethods HTTP methods are unresolved or exceed the 128-method limit; operation omitted."); return null; }
            var methods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var expression in expressions)
            {
                var value = ReadString(expression, Model(expression), _token) ?? ReadFrameworkHttpMethod(expression);
                if (value is null || !IsHttpToken(value))
                { Warn(call, "MapMethods contains an unresolved/invalid HTTP method; operation omitted."); return null; }
                methods.Add(value.ToUpperInvariant());
            }
            return methods.ToArray();
        }

        private IReadOnlyList<ExpressionSyntax>? ReadMethodList(ExpressionSyntax expression, HashSet<ISymbol> active, int depth)
        {
            _token.ThrowIfCancellationRequested();
            if (depth > 24) return null;
            expression = Unwrap(expression);
            switch (expression)
            {
                case ArrayCreationExpressionSyntax { Initializer: not null } array: return array.Initializer.Expressions.ToArray();
                case ImplicitArrayCreationExpressionSyntax array: return array.Initializer.Expressions.ToArray();
                case CollectionExpressionSyntax collection when collection.Elements.All(element => element is ExpressionElementSyntax):
                    return collection.Elements.OfType<ExpressionElementSyntax>().Select(element => element.Expression).ToArray();
                case IdentifierNameSyntax:
                    var symbol = Model(expression).GetSymbolInfo(expression, _token).Symbol;
                    if (symbol is not ILocalSymbol || _writtenSymbols.Contains(symbol) || !active.Add(symbol)) return null;
                    try
                    {
                        var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_token) as VariableDeclaratorSyntax;
                        // A local array can be modified through an alias or element write; accept only an immediate literal collection.
                        if (declaration?.Initializer is null || CollectionIsMutated(symbol, declaration)) return null;
                        return ReadMethodList(declaration.Initializer.Value, active, depth + 1);
                    }
                    finally { active.Remove(symbol); }
                default: return null;
            }
        }

        private bool CollectionIsMutated(ISymbol symbol, VariableDeclaratorSyntax declaration)
        {
            var scope = declaration.Ancestors().FirstOrDefault(node => node is BlockSyntax or CompilationUnitSyntax);
            if (scope is null) return true;
            foreach (var reference in scope.DescendantNodes().OfType<IdentifierNameSyntax>())
            {
                if (!SymbolEqualityComparer.Default.Equals(Model(reference).GetSymbolInfo(reference, _token).Symbol, symbol)) continue;
                // Restrict collection references to MapMethods arguments. Passing it elsewhere or aliasing is unsafe.
                var argument = reference.Parent as ArgumentSyntax;
                if (argument?.Parent?.Parent is not InvocationExpressionSyntax call || CallName(call) != "MapMethods") return true;
                var mapping = GetMappingCall(call);
                if (mapping is null || ResolveReceiver(mapping.Receiver, new HashSet<ISymbol>(SymbolEqualityComparer.Default), 0)?.IsEndpoint != true ||
                    IsSourceMappingOverride(call, "MapMethods") && !mapping.ExplicitFramework) return true;
            }
            return false;
        }

        private Receiver? ResolveReceiver(ExpressionSyntax expression, HashSet<ISymbol> active, int depth)
        {
            _token.ThrowIfCancellationRequested();
            if (depth > 24) return null;
            expression = Unwrap(expression);
            if (expression is InvocationExpressionSyntax invocation)
            {
                var name = CallName(invocation);
                if (name is "CreateBuilder" or "CreateSlimBuilder" or "CreateEmptyBuilder" && invocation.Expression is MemberAccessExpressionSyntax factory && IsFrameworkType(factory.Expression, "WebApplication", "Microsoft.AspNetCore.Builder"))
                    return new Receiver(ReceiverKind.ApplicationBuilder, []);
                if (name is "Create" && invocation.Expression is MemberAccessExpressionSyntax create && IsFrameworkType(create.Expression, "WebApplication", "Microsoft.AspNetCore.Builder"))
                    return new Receiver(ReceiverKind.Application, [""]);
                if (name == "Build" && invocation.Expression is MemberAccessExpressionSyntax build && invocation.ArgumentList.Arguments.Count == 0 &&
                    ResolveReceiver(build.Expression, active, depth + 1)?.Kind == ReceiverKind.ApplicationBuilder)
                    return new Receiver(ReceiverKind.Application, [""]);
                var mapping = GetMappingCall(invocation);
                if (mapping is null) return null;
                var receiver = ResolveReceiver(mapping.Receiver, active, depth + 1);
                if (receiver is null) return null;
                if (MiddlewareConventions.Contains(name) && receiver.Kind is ReceiverKind.MiddlewareBuilder or ReceiverKind.Application && !IsSourceMappingOverride(invocation, name)) return receiver;
                if (!receiver.IsEndpoint) return null;
                if (name == "MapGroup")
                {
                    if (IsSourceMappingOverride(invocation, name) && !mapping.ExplicitFramework) return new Receiver(ReceiverKind.Group, [], true);
                    var argument = FindArgument(mapping.Arguments, 0, "prefix");
                    var route = argument is null ? null : ReadString(argument.Expression, Model(invocation), _token);
                    if (route is null || !IsMinimalPattern(route)) return new Receiver(ReceiverKind.Group, [], true);
                    var prefixes = receiver.Prefixes.Select(prefix => CombineMinimalPatterns(prefix, route)).ToArray();
                    return new Receiver(ReceiverKind.Group, prefixes.OfType<string>().ToArray(), receiver.UnknownPrefix || prefixes.Any(prefix => prefix is null));
                }
                if (GroupConventions.Contains(name) && receiver.Kind == ReceiverKind.Group && !IsSourceMappingOverride(invocation, name)) return receiver;
                return null;
            }
            var symbol = Model(expression).GetSymbolInfo(expression, _token).Symbol;
            if (symbol is null || _writtenSymbols.Contains(symbol)) return null;
            if (_receiverCache.TryGetValue(symbol, out var cached)) return cached;
            if (!active.Add(symbol)) return null;
            try
            {
                Receiver? receiver = null;
                var syntax = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_token);
                if (syntax is VariableDeclaratorSyntax variable && symbol is ILocalSymbol)
                {
                    var declared = DeclaredReceiver((variable.Parent as VariableDeclarationSyntax)?.Type);
                    if (variable.Initializer is not null) receiver = ResolveReceiver(variable.Initializer.Value, active, depth + 1);
                    if (receiver is not null && declared is not null)
                        receiver = new Receiver(declared.Kind, receiver.Prefixes, receiver.UnknownPrefix);
                    receiver ??= declared;
                }
                else if (syntax is ParameterSyntax parameter)
                {
                    var declared = DeclaredReceiver(parameter.Type);
                    receiver = ResolveEndpointLambda(parameter, active, depth + 1);
                    var anonymousParameter = parameter.Parent is AnonymousFunctionExpressionSyntax || parameter.Parent?.Parent is AnonymousFunctionExpressionSyntax;
                    if (receiver is null && !anonymousParameter)
                    {
                        if (declared?.Kind is ReceiverKind.Application or ReceiverKind.ApplicationBuilder or ReceiverKind.MiddlewareBuilder) receiver = declared;
                        else if (declared is not null) receiver = ResolveParameterCallers(parameter, declared.Kind, active, depth + 1);
                    }
                }
                _receiverCache[symbol] = receiver;
                return receiver;
            }
            finally { active.Remove(symbol); }
        }

        private Receiver? DeclaredReceiver(TypeSyntax? type)
        {
            if (type is null) return null;
            if (IsFrameworkType(type, "WebApplication", "Microsoft.AspNetCore.Builder")) return new Receiver(ReceiverKind.Application, [""]);
            if (IsFrameworkType(type, "WebApplicationBuilder", "Microsoft.AspNetCore.Builder")) return new Receiver(ReceiverKind.ApplicationBuilder, []);
            if (IsFrameworkType(type, "IApplicationBuilder", "Microsoft.AspNetCore.Builder")) return new Receiver(ReceiverKind.MiddlewareBuilder, []);
            if (IsFrameworkType(type, "IEndpointRouteBuilder", "Microsoft.AspNetCore.Routing")) return new Receiver(ReceiverKind.EndpointBuilder, [], true);
            if (IsFrameworkType(type, "RouteGroupBuilder", "Microsoft.AspNetCore.Routing")) return new Receiver(ReceiverKind.Group, [], true);
            return null;
        }

        private Receiver? ResolveEndpointLambda(ParameterSyntax parameter, HashSet<ISymbol> active, int depth)
        {
            var lambda = parameter.Ancestors().OfType<LambdaExpressionSyntax>().FirstOrDefault();
            if (lambda?.Parent is not ArgumentSyntax argument || argument.Parent?.Parent is not InvocationExpressionSyntax call || CallName(call) != "UseEndpoints") return null;
            var parameters = lambda switch
            {
                SimpleLambdaExpressionSyntax simple => new[] { simple.Parameter },
                ParenthesizedLambdaExpressionSyntax parenthesized => parenthesized.ParameterList.Parameters.ToArray(),
                _ => []
            };
            if (parameters.Length != 1 || parameters[0] != parameter || IsSourceMappingOverride(call, "UseEndpoints")) return null;
            var mapping = GetMappingCall(call);
            if (mapping is null) return null;
            var receiver = ResolveReceiver(mapping.Receiver, active, depth + 1);
            return receiver?.Kind is ReceiverKind.MiddlewareBuilder or ReceiverKind.Application ? new Receiver(ReceiverKind.EndpointBuilder, [""]) : null;
        }

        private Receiver ResolveParameterCallers(ParameterSyntax parameter, ReceiverKind kind, HashSet<ISymbol> active, int depth)
        {
            if (parameter.Parent?.Parent is not MethodDeclarationSyntax method || !_callsByName.TryGetValue(method.Identifier.ValueText, out var calls))
                return new Receiver(kind, [], true);
            var declaredSymbol = Model(method).GetDeclaredSymbol(method, _token);
            var parameterIndex = method.ParameterList.Parameters.IndexOf(parameter);
            var prefixes = new HashSet<string>(StringComparer.Ordinal);
            var unknown = false;
            var matched = false;
            foreach (var call in calls)
            {
                _token.ThrowIfCancellationRequested();
                if (!MatchesHelper(call, method, declaredSymbol)) continue;
                matched = true;
                var reduced = method.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true &&
                    call.Expression is MemberAccessExpressionSyntax member && !IsContainingType(member.Expression, method);
                ExpressionSyntax? argument;
                if (reduced && parameterIndex == 0) argument = ((MemberAccessExpressionSyntax)call.Expression).Expression;
                else argument = FindArgument(call.ArgumentList.Arguments.ToArray(), parameterIndex - (reduced ? 1 : 0), parameter.Identifier.ValueText)?.Expression;
                var receiver = argument is null ? null : ResolveReceiver(argument, active, depth + 1);
                if (receiver is null || !receiver.IsEndpoint) { unknown = true; continue; }
                unknown |= receiver.UnknownPrefix;
                foreach (var prefix in receiver.Prefixes)
                {
                    if (prefixes.Count >= 128)
                    { unknown = true; _inventory.Warn("The 128-prefix limit for a minimal API helper was reached; additional caller routes were omitted."); break; }
                    prefixes.Add(prefix);
                }
            }
            return new Receiver(kind, prefixes.ToArray(), unknown || !matched);
        }

        private bool MatchesHelper(InvocationExpressionSyntax call, MethodDeclarationSyntax method, IMethodSymbol? declared)
        {
            var bound = Model(call).GetSymbolInfo(call, _token).Symbol as IMethodSymbol;
            if (bound is not null && declared is not null)
                return SymbolEqualityComparer.Default.Equals((bound.ReducedFrom ?? bound).OriginalDefinition, declared.OriginalDefinition);
            // With missing ASP.NET references, extension binding may fail. Resolve only a unique static
            // source helper with the matching name and compatible argument names/count.
            if (!_methodsByName.TryGetValue(method.Identifier.ValueText, out var candidates) || candidates.Length != 1 || !method.Modifiers.Any(SyntaxKind.StaticKeyword)) return false;
            var extension = method.ParameterList.Parameters.FirstOrDefault()?.Modifiers.Any(SyntaxKind.ThisKeyword) == true;
            var reduced = extension && call.Expression is MemberAccessExpressionSyntax member && !IsContainingType(member.Expression, method);
            if (!reduced && call.Expression is MemberAccessExpressionSyntax owner && !IsContainingType(owner.Expression, method)) return false;
            var parameters = method.ParameterList.Parameters.Skip(reduced ? 1 : 0).ToArray();
            var arguments = call.ArgumentList.Arguments;
            if (arguments.Count > parameters.Length || arguments.Count < parameters.Count(item => item.Default is null && !item.Modifiers.Any(SyntaxKind.ParamsKeyword))) return false;
            return arguments.All(argument => argument.NameColon is null || parameters.Any(item => item.Identifier.ValueText == argument.NameColon.Name.Identifier.ValueText));
        }

        private bool IsContainingType(ExpressionSyntax expression, MethodDeclarationSyntax method)
        {
            var type = method.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
            return type is not null && LastName(ExpandName(expression)) == type.Identifier.ValueText;
        }

        private MappingCall? GetMappingCall(InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax member) return null;
            var name = member.Name.Identifier.ValueText;
            if (IsFrameworkType(member.Expression, "EndpointRouteBuilderExtensions", "Microsoft.AspNetCore.Builder") ||
                IsFrameworkType(member.Expression, "RequestDelegateRouteBuilderExtensions", "Microsoft.AspNetCore.Builder") ||
                IsFrameworkType(member.Expression, "EndpointRoutingApplicationBuilderExtensions", "Microsoft.AspNetCore.Builder"))
            {
                var arguments = invocation.ArgumentList.Arguments.ToArray();
                var receiver = FindArgument(arguments, 0, "endpoints", "app", "builder");
                if (receiver is null) return null;
                return new MappingCall(name, receiver.Expression, arguments.Where(argument => argument != receiver).ToArray(), true);
            }
            return new MappingCall(name, member.Expression, invocation.ArgumentList.Arguments.ToArray(), false);
        }

        private bool IsSourceMappingOverride(InvocationExpressionSyntax call, string name)
        {
            var symbol = Model(call).GetSymbolInfo(call, _token).Symbol as IMethodSymbol;
            if (symbol?.DeclaringSyntaxReferences.Length > 0) return true;
            return _methodsByName.TryGetValue(name, out var methods) && methods.Any(method =>
                method.ParameterList.Parameters.FirstOrDefault() is { } parameter && parameter.Modifiers.Any(SyntaxKind.ThisKeyword) && DeclaredReceiver(parameter.Type) is not null);
        }

        private bool IsRequestDelegate(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            if (expression is CastExpressionSyntax cast && IsFrameworkType(cast.Type, "RequestDelegate", "Microsoft.AspNetCore.Http")) return true;
            if (expression is ParenthesizedLambdaExpressionSyntax lambda)
                return lambda.ParameterList.Parameters.Count == 1 && IsFrameworkType(lambda.ParameterList.Parameters[0].Type, "HttpContext", "Microsoft.AspNetCore.Http");
            var symbol = Model(expression).GetSymbolInfo(expression, _token).Symbol;
            if (symbol is IMethodSymbol method && method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_token) is MethodDeclarationSyntax declaration)
                return declaration.ParameterList.Parameters.Count == 1 && IsFrameworkType(declaration.ParameterList.Parameters[0].Type, "HttpContext", "Microsoft.AspNetCore.Http") &&
                    LastName(declaration.ReturnType.ToString()) is "Task" or "ValueTask";
            if (symbol is ILocalSymbol local && local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_token) is VariableDeclaratorSyntax variable)
                return IsFrameworkType((variable.Parent as VariableDeclarationSyntax)?.Type, "RequestDelegate", "Microsoft.AspNetCore.Http");
            return false;
        }

        private string? ReadHandlerIdentity(ExpressionSyntax expression)
        {
            expression = Unwrap(expression);
            while (expression is CastExpressionSyntax cast) expression = Unwrap(cast.Expression);
            if (expression is AnonymousFunctionExpressionSyntax)
                return expression.SyntaxTree.FilePath + ":" + expression.SpanStart;
            var symbol = Model(expression).GetSymbolInfo(expression, _token).Symbol;
            if (symbol is not IMethodSymbol method) return null;
            var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(_token);
            return syntax is null ? null : syntax.SyntaxTree.FilePath + ":" + syntax.SpanStart;
        }

        private string? ReadFrameworkHttpMethod(ExpressionSyntax expression)
        {
            if (expression is not MemberAccessExpressionSyntax member || !IsFrameworkType(member.Expression, "HttpMethods", "Microsoft.AspNetCore.Http")) return null;
            return member.Name.Identifier.ValueText switch
            {
                "Get" => "GET", "Post" => "POST", "Put" => "PUT", "Patch" => "PATCH", "Delete" => "DELETE",
                "Head" => "HEAD", "Options" => "OPTIONS", "Trace" => "TRACE", "Connect" => "CONNECT", _ => null
            };
        }

        private bool IsFrameworkType(SyntaxNode? syntax, string name, string space)
        {
            if (syntax is null) return false;
            var resolved = Model(syntax).GetSymbolInfo(syntax, _token).Symbol;
            if (resolved is not null && resolved is not INamedTypeSymbol && resolved is not IAliasSymbol) return false;
            var text = ExpandName(syntax).Replace("global::", "", StringComparison.Ordinal);
            if (text == space + "." + name)
                return !_sourceTypeFullNames.Contains(text);
            return text == name && !_sourceTypeNames.Contains(name);
        }

        private string ExpandName(SyntaxNode syntax)
        {
            var text = syntax.ToString();
            var first = text.Split('.')[0];
            var localUsings = syntax.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().SelectMany(space => space.Usings);
            var rootUsings = (syntax.SyntaxTree.GetRoot(_token) as CompilationUnitSyntax)?.Usings ?? default;
            var alias = localUsings.Concat(rootUsings).FirstOrDefault(item => item.Alias?.Name.Identifier.ValueText == first);
            return alias?.Name is null ? text : alias.Name + text[first.Length..];
        }

        private void RecordWrite(ExpressionSyntax expression)
        {
            var symbol = Model(expression).GetSymbolInfo(expression, _token).Symbol;
            if (symbol is ILocalSymbol or IParameterSymbol) _writtenSymbols.Add(symbol);
        }
        private SemanticModel Model(SyntaxNode node)
        {
            if (!_models.TryGetValue(node.SyntaxTree, out var model)) _models[node.SyntaxTree] = model = _compilation.GetSemanticModel(node.SyntaxTree);
            return model;
        }
        private void Warn(InvocationExpressionSyntax call, string message) =>
            _inventory.Warn($"{SafeFile(call.SyntaxTree.FilePath)}:{call.GetLocation().GetLineSpan().StartLinePosition.Line + 1}: {CallName(call)}: {message}");
        private static string HandlerLabel(ExpressionSyntax expression, string method) => expression is AnonymousFunctionExpressionSyntax
            ? method + " handler" : method + " " + (expression is IdentifierNameSyntax identifier ? identifier.Identifier.ValueText : expression is MemberAccessExpressionSyntax member ? member.Name.Identifier.ValueText : "handler");
        private static string CallName(InvocationExpressionSyntax call) => call.Expression switch
        { MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText, IdentifierNameSyntax identifier => identifier.Identifier.ValueText, GenericNameSyntax generic => generic.Identifier.ValueText, _ => "" };
        private static string SourceTypeName(SyntaxNode type) => type switch
        { BaseTypeDeclarationSyntax declaration => declaration.Identifier.ValueText, DelegateDeclarationSyntax declaration => declaration.Identifier.ValueText, _ => "" };
        private static ExpressionSyntax Unwrap(ExpressionSyntax expression)
        { while (expression is ParenthesizedExpressionSyntax parenthesized) expression = parenthesized.Expression; return expression; }
        private static ArgumentSyntax? FindArgument(IReadOnlyList<ArgumentSyntax> arguments, int position, params string[] names) =>
            arguments.FirstOrDefault(argument => argument.NameColon is not null && names.Contains(argument.NameColon.Name.Identifier.ValueText, StringComparer.Ordinal)) ??
            (position >= 0 && position < arguments.Count && arguments[position].NameColon is null ? arguments[position] : null);
    }

    private static bool IsMinimalPattern(string route)
    {
        if (route.Length > 8192 || route.StartsWith('~') || route.Contains("//", StringComparison.Ordinal)) return false;
        var depth = 0;
        foreach (var character in route)
        {
            if (character == '{') depth++;
            else if (character == '}' && --depth < 0) return false;
            else if (depth == 0 && character is '?' or '#') return false;
            if (char.IsControl(character)) return false;
        }
        return depth == 0;
    }

    // Minimal group prefixes always append, including when the child pattern starts with '/'. MVC's
    // absolute-route override does not apply. Canonical slash handling matches controller operation keys.
    private static string? CombineMinimalPatterns(string prefix, string route)
    {
        if (!IsMinimalPattern(prefix) || !IsMinimalPattern(route)) return null;
        var left = prefix.Trim('/');
        var right = route.Trim('/');
        var combined = "/" + (left.Length == 0 ? right : right.Length == 0 ? left : left + "/" + right);
        return IsMinimalPattern(combined) ? combined : null;
    }
}
