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
        // Only method-group handlers are resolved; lambda handlers are deferred.
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

        // Second argument is the handler. Try to resolve as a method group.
        // Lambda handlers are not yet supported (see findings in MinimalApiGeneratorTests.cs).
        var handlerArgExpr = invoc.ArgumentList.Arguments[1].Expression;
        var symbolInfo = ctx.SemanticModel.GetSymbolInfo(handlerArgExpr, ct);

        // Use the unambiguous symbol first; fall back to first candidate for non-overloaded groups.
        var handlerMethod = (symbolInfo.Symbol
            ?? symbolInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;

        if (handlerMethod is null) return null;

        // Skip anonymous-function symbols (lambdas).
        // Their compiler-generated names contain angle brackets (e.g. <Register>b__0)
        // which are not valid C# identifiers in the emitted class name.
        // Lambda-handler support is deferred to Phase 2.
        if (handlerMethod.MethodKind == MethodKind.AnonymousFunction) return null;

        // Opt-in: the handler must carry [McpTool]; skip anything unmarked.
        var hasMcpTool = handlerMethod.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "McpIt.McpToolAttribute");
        if (!hasMcpTool) return null;

        return ModelBuilder.BuildFromHandler(handlerMethod, verb, route, ctx.SemanticModel.Compilation);
    }
}
