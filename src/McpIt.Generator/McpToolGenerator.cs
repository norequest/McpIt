using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace McpIt.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class McpToolGenerator : IIncrementalGenerator
{
    private const string AttributeMetadataName = "McpIt.McpToolAttribute";

    // Recognized Map* method names for the minimal-API pipeline.
    private static readonly System.Collections.Generic.HashSet<string> MapMethodNames =
        new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
        { "MapGet", "MapPost", "MapPut", "MapPatch", "MapDelete" };

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // -------------------------------------------------------------------
        // Pipeline 1: controller actions annotated with [McpTool] directly.
        // Existing behavior; unchanged.
        // -------------------------------------------------------------------
        var controllerModels = context.SyntaxProvider.ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: AttributeMetadataName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => ModelBuilder.Build(ctx))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(controllerModels, static (spc, model) =>
        {
            if (!model.HasDescription && model.Location is { } loc)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.MissingDescription, loc.ToLocation(), model.ToolName));
            }

            if (model.Destructive && !model.AllowDestructive && model.Location is { } dloc)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.DestructiveOperation, dloc.ToLocation(), model.ToolName));
            }

            // A leftover apiVersion token after substitution means no version attribute resolved.
            if (model.RouteTemplate.IndexOf("apiVersion", System.StringComparison.OrdinalIgnoreCase) >= 0
                && model.Location is { } vloc)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.UnresolvedApiVersion, vloc.ToLocation(), model.ToolName));
            }

            // Qualify the hint name with the namespace: the same class+action name can recur in
            // separate per-version controllers (e.g. V1.AccountController and V2.AccountController),
            // and AddSource requires a unique hint name per generator.
            var hint = string.IsNullOrEmpty(model.Namespace)
                ? $"{model.GeneratedClassName}.g.cs"
                : $"{model.Namespace}.{model.GeneratedClassName}.g.cs";
            spc.AddSource(hint, Emitter.Emit(model));
        });

        // -------------------------------------------------------------------
        // Pipeline 2: minimal-API MapGet/MapPost/MapPut/MapPatch/MapDelete calls
        // where the handler method symbol carries [McpTool].
        //
        // Supported shapes (Phase 2):
        //   - Method-group: app.MapGet("/route", Handlers.GetItem)
        //   - Lambda:       app.MapGet("/route", [McpTool] (int id) => ...)
        //       Requires the Map* stub to use an unconstrained generic THandler (not Delegate)
        //       so Roslyn's GetSymbolInfo resolves the lambda to an IMethodSymbol. The real
        //       ASP.NET Core Map* APIs use Delegate and Roslyn cannot resolve the symbol there;
        //       that case remains unsupported (GetSymbolInfo returns null, filtered silently).
        //   - MapGroup prefix (direct chain): app.MapGroup("/api").MapGet("/x", H)
        //   - MapGroup prefix (variable):     var g = app.MapGroup("/api"); g.MapGet("/x", H)
        //   - Overloaded method groups: first candidate picked deterministically.
        // -------------------------------------------------------------------
        var minimalApiModels = context.SyntaxProvider
            .CreateSyntaxProvider(
                predicate: static (node, _) => IsMapMethodInvocation(node),
                transform: static (ctx, ct) => BuildMinimalApiModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        context.RegisterSourceOutput(minimalApiModels, static (spc, model) =>
        {
            if (!model.HasDescription && model.Location is { } loc)
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.MissingDescription, loc.ToLocation(), model.ToolName));

            if (model.Destructive && !model.AllowDestructive && model.Location is { } dloc)
                spc.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.DestructiveOperation, dloc.ToLocation(), model.ToolName));

            var hint = string.IsNullOrEmpty(model.Namespace)
                ? $"{model.GeneratedClassName}.g.cs"
                : $"{model.Namespace}.{model.GeneratedClassName}.g.cs";
            spc.AddSource(hint, Emitter.Emit(model));
        });
    }

    // Predicate: cheap syntactic filter to select potential MapGet/... call sites.
    // Requires a member-access expression so we have a receiver (no bare MapGet(...)).
    private static bool IsMapMethodInvocation(SyntaxNode node)
    {
        if (node is not InvocationExpressionSyntax invoc) return false;
        if (invoc.ArgumentList.Arguments.Count < 2) return false;
        if (invoc.Expression is not MemberAccessExpressionSyntax ma) return false;
        return MapMethodNames.Contains(ma.Name.Identifier.Text);
    }

    // Transform: resolve the route, verb, and handler method symbol, then build a model.
    // Returns null (filtered by .Where) when this invocation should not produce a tool.
    private static EndpointModel? BuildMinimalApiModel(
        GeneratorSyntaxContext ctx,
        System.Threading.CancellationToken ct)
    {
        var invoc = (InvocationExpressionSyntax)ctx.Node;
        var ma = (MemberAccessExpressionSyntax)invoc.Expression;

        var verb = ma.Name.Identifier.Text switch
        {
            "MapGet" => "GET",
            "MapPost" => "POST",
            "MapPut" => "PUT",
            "MapPatch" => "PATCH",
            "MapDelete" => "DELETE",
            _ => null
        };
        if (verb is null) return null;

        // First argument must be a compile-time string literal (the route template).
        var firstArg = invoc.ArgumentList.Arguments[0].Expression;
        if (firstArg is not LiteralExpressionSyntax routeLiteral) return null;
        var route = routeLiteral.Token.ValueText;

        // Resolve any MapGroup prefix by walking the receiver expression chain.
        var groupPrefix = ResolveGroupPrefixes(invoc, ctx.SemanticModel, ct);
        var fullRoute = string.IsNullOrEmpty(groupPrefix)
            ? route
            : ModelBuilder.CombineRoutes(groupPrefix, route);

        // Resolve the handler method symbol via GetSymbolInfo.
        var handlerArgExpr = invoc.ArgumentList.Arguments[1].Expression;
        var symbolInfo = ctx.SemanticModel.GetSymbolInfo(handlerArgExpr, ct);

        // Use the unambiguous symbol first; fall back to first candidate for overloaded groups.
        var handlerMethod = (symbolInfo.Symbol
            ?? symbolInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;

        if (handlerMethod is null)
        {
            // GetSymbolInfo returned null. The most common cause is that the Map* overload
            // uses System.Delegate as the handler parameter type (the real ASP.NET Core
            // shape), which prevents Roslyn from resolving the lambda to an IMethodSymbol.
            // Attempt to build the model directly from the lambda syntax instead.
            LambdaExpressionSyntax? lambdaSyntax = handlerArgExpr switch
            {
                ParenthesizedLambdaExpressionSyntax p => p,
                SimpleLambdaExpressionSyntax s => s,
                _ => null
            };
            if (lambdaSyntax is null) return null;

            var sanitized = SanitizeForIdentifier(fullRoute);
            var containingName = GetContainingTypeNameFromSyntax(lambdaSyntax);
            var classNameOverride = sanitized.Length > 0
                ? $"MinApi_{verb}_{sanitized}_{containingName}_Tool"
                : $"MinApi_{verb}_{containingName}_Tool";
            var toolNameHint = sanitized.Length > 0
                ? $"{verb.ToLowerInvariant()}_{sanitized}"
                : verb.ToLowerInvariant();

            return ModelBuilder.BuildFromLambda(
                lambdaSyntax, verb, fullRoute, ctx.SemanticModel, ctx.SemanticModel.Compilation,
                classNameOverride, toolNameHint, ct);
        }

        // Opt-in: the handler must carry [McpTool]; skip anything unmarked.
        var hasMcpTool = handlerMethod.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "McpIt.McpToolAttribute");
        if (!hasMcpTool) return null;

        // Lambda handler: the compiler-generated name (e.g. "<Register>b__0") contains angle
        // brackets which are not valid C# identifiers. Derive the class name and tool name hint
        // from the verb + sanitized route + containing type instead.
        if (handlerMethod.MethodKind == MethodKind.AnonymousFunction)
        {
            var sanitized = SanitizeForIdentifier(fullRoute);
            var containingName = handlerMethod.ContainingType?.Name ?? "Lambda";
            var classNameOverride = sanitized.Length > 0
                ? $"MinApi_{verb}_{sanitized}_{containingName}_Tool"
                : $"MinApi_{verb}_{containingName}_Tool";

            // Tool name hint: lowercase verb + sanitized route, used when no explicit Name set.
            var toolNameHint = sanitized.Length > 0
                ? $"{verb.ToLowerInvariant()}_{sanitized}"
                : verb.ToLowerInvariant();

            return ModelBuilder.BuildFromHandler(
                handlerMethod, verb, fullRoute, ctx.SemanticModel.Compilation,
                classNameOverride, toolNameHint);
        }

        // Named method-group handler (the common case).
        return ModelBuilder.BuildFromHandler(handlerMethod, verb, fullRoute, ctx.SemanticModel.Compilation);
    }

    // ---------------------------------------------------------------------------
    // MapGroup prefix resolution: walks the receiver expression chain of a Map*
    // invocation to accumulate any MapGroup("/prefix") calls, returning the
    // combined prefix string (e.g. "api/v1"). Returns empty string when no
    // MapGroup is found. Non-literal prefixes (computed values) stop the walk
    // silently without crashing.
    //
    // Handled shapes:
    //   Direct chain: app.MapGroup("/api").MapGet("/x", H)  -> prefix = "api"
    //   Variable:     var g = app.MapGroup("/api"); g.MapGet("/x", H) -> prefix = "api"
    //   Nested:       app.MapGroup("/a").MapGroup("/b").MapGet("/x", H) -> prefix = "a/b"
    // ---------------------------------------------------------------------------
    private static string ResolveGroupPrefixes(
        InvocationExpressionSyntax mapInvoc,
        SemanticModel semanticModel,
        System.Threading.CancellationToken ct)
    {
        var ma = (MemberAccessExpressionSyntax)mapInvoc.Expression;
        var prefixes = new System.Collections.Generic.List<string>();
        CollectGroupPrefixes(ma.Expression, semanticModel, ct, prefixes);

        if (prefixes.Count == 0) return string.Empty;

        // Prefixes are collected innermost-first (closest to the Map* call first),
        // so reverse to get outermost-first for correct CombineRoutes assembly.
        prefixes.Reverse();

        var result = string.Empty;
        foreach (var prefix in prefixes)
            result = ModelBuilder.CombineRoutes(result, prefix);
        return result;
    }

    private static void CollectGroupPrefixes(
        ExpressionSyntax expr,
        SemanticModel semanticModel,
        System.Threading.CancellationToken ct,
        System.Collections.Generic.List<string> prefixes)
    {
        // Direct chain: someExpr.MapGroup("/prefix")
        if (expr is InvocationExpressionSyntax innerInvoc &&
            innerInvoc.Expression is MemberAccessExpressionSyntax innerMa &&
            innerMa.Name.Identifier.Text == "MapGroup")
        {
            if (innerInvoc.ArgumentList.Arguments.Count >= 1 &&
                innerInvoc.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax prefixLit)
            {
                prefixes.Add(prefixLit.Token.ValueText);
                // Recurse: the receiver of this MapGroup call may itself be another MapGroup.
                CollectGroupPrefixes(innerMa.Expression, semanticModel, ct, prefixes);
            }
            // Non-literal prefix: stop walking; do not crash.
            return;
        }

        // Variable reference: look up the local's initializer expression.
        if (expr is IdentifierNameSyntax identifier)
        {
            var sym = semanticModel.GetSymbolInfo(identifier, ct).Symbol;
            if (sym is ILocalSymbol local)
            {
                var declRef = local.DeclaringSyntaxReferences.FirstOrDefault();
                if (declRef?.GetSyntax(ct) is VariableDeclaratorSyntax declarator &&
                    declarator.Initializer?.Value is ExpressionSyntax initExpr)
                {
                    CollectGroupPrefixes(initExpr, semanticModel, ct, prefixes);
                }
            }
            // Not a resolvable local or no initializer: stop.
            return;
        }

        // Other expression types (parameters, properties, etc.): stop walking.
    }

    // Replaces every run of non-alphanumeric characters with a single '_', then trims
    // leading and trailing underscores. Used to turn a route template into a valid C#
    // identifier segment for the lambda-handler generated class name.
    // Example: "/items/{id}" -> "items_id"
    private static string SanitizeForIdentifier(string s)
    {
        var replaced = System.Text.RegularExpressions.Regex.Replace(s, @"[^a-zA-Z0-9]+", "_");
        return replaced.Trim('_');
    }

    // Walks the syntax tree upward from the given node to locate the first enclosing type
    // declaration (class, struct, or record). Returns its simple name, or "Lambda" when no
    // enclosing type is found. Used to produce a meaningful generated class name for inline
    // lambda handlers that have no method name.
    private static string GetContainingTypeNameFromSyntax(SyntaxNode node)
    {
        var parent = node.Parent;
        while (parent is not null)
        {
            if (parent is TypeDeclarationSyntax typeDecl)
                return typeDecl.Identifier.Text;
            parent = parent.Parent;
        }
        return "Lambda";
    }
}
