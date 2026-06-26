using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using McpIt.Generator.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace McpIt.Generator;

/// <summary>
/// Incremental source generator that emits McpIt.Generated.McpItManifest at build time.
/// The class exposes a SHA-256 aggregate hash over all [McpTool]-annotated methods so CI
/// pipelines or MCP clients can verify tool-surface integrity against an approved snapshot.
///
/// Roslyn auto-discovers all IIncrementalGenerator implementations in the analyzer assembly;
/// this generator coexists with McpToolGenerator with no registration changes required.
///
/// V1 tool-name derivation rule (mirrors ModelBuilder for common cases):
///   If [McpTool(Name = "explicit")] is present the explicit value is used verbatim.
///   Otherwise the method name is camel-cased (GetOrder -> getOrder).
/// NamePrefix, API-version suffixes, and minimal-API route-level overrides are NOT applied
/// in V1; those methods are fingerprinted by their direct method-level surface only.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class McpManifestGenerator : IIncrementalGenerator
{
    private const string McpToolAttributeFqn = "McpIt.McpToolAttribute";
    private const string DescriptionAttributeFqn = "System.ComponentModel.DescriptionAttribute";
    private const string CancellationTokenFqn = "System.Threading.CancellationToken";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Discover every method that carries [McpTool] (controller actions AND minimal-API handlers).
        var toolEntries = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                fullyQualifiedMetadataName: McpToolAttributeFqn,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, _) => ExtractManifestEntry(ctx))
            .Where(static e => e is not null)
            .Select(static (e, _) => e!)
            .Collect();

        // Emit one McpItManifest.g.cs file covering all discovered tools.
        context.RegisterSourceOutput(toolEntries, static (spc, entries) =>
        {
            // Do not emit a manifest for a tool-less assembly. Baking an empty McpItManifest
            // into every assembly with no [McpTool] methods (including McpIt.dll itself) would
            // collide with the consumer's own generated McpItManifest (CS0436).
            if (entries.IsDefaultOrEmpty) return;
            spc.AddSource("McpItManifest.g.cs", ManifestEmitter.Emit(entries));
        });
    }

    // ---------------------------------------------------------------------------
    // Extraction: build a ManifestEntry from a [McpTool]-annotated method symbol.
    // ---------------------------------------------------------------------------

    private static ManifestEntry? ExtractManifestEntry(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method) return null;

        var mcpAttr = ctx.Attributes.FirstOrDefault();

        // V1 name rule: explicit Name arg wins; otherwise camelCase of the method name.
        // NamePrefix and version-suffix logic from ModelBuilder is intentionally omitted in V1.
        var explicitName = mcpAttr?.NamedArguments
            .FirstOrDefault(static kv => kv.Key == "Name").Value.Value as string;
        var toolName = string.IsNullOrWhiteSpace(explicitName)
            ? ToCamelCase(method.Name)
            : explicitName!;

        var description = GetXmlSummary(method) ?? GetDescriptionAttribute(method) ?? string.Empty;

        // Collect parameter surface, mirroring ModelBuilder's CancellationToken filter.
        var paramEntries = new List<ManifestParamEntry>();
        foreach (var p in method.Parameters)
        {
            if (IsCancellationToken(p.Type)) continue;
            paramEntries.Add(new ManifestParamEntry(p.Name, p.Type.ToDisplayString()));
        }

        return new ManifestEntry(
            ToolName: toolName,
            Description: description,
            Parameters: new EquatableArray<ManifestParamEntry>(paramEntries));
    }

    // Filters out both CancellationToken and CancellationToken? to match ModelBuilder's behavior.
    private static bool IsCancellationToken(ITypeSymbol type)
    {
        // Unwrap Nullable<CancellationToken> (handles CancellationToken? in source).
        if (type is INamedTypeSymbol { IsGenericType: true } named &&
            named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            type = named.TypeArguments[0];

        return type.ToDisplayString() == CancellationTokenFqn;
    }

    private static string ToCamelCase(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    private static string? GetXmlSummary(IMethodSymbol method)
    {
        var xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            var summary = XDocument.Parse(xml).Descendants("summary").FirstOrDefault();
            var text = summary?.Value.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch { return null; }
    }

    private static string? GetDescriptionAttribute(IMethodSymbol method)
    {
        var attr = method.GetAttributes().FirstOrDefault(static a =>
            a.AttributeClass?.ToDisplayString() == DescriptionAttributeFqn);
        return attr is { ConstructorArguments.Length: > 0 }
            ? attr.ConstructorArguments[0].Value as string
            : null;
    }
}
