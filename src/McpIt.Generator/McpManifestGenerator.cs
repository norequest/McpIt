using System.Collections.Generic;
using System.Globalization;
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
/// P3 fingerprint coverage (controller-action scenario):
///   1. Tool name: explicit [McpTool(Name=...)] used verbatim; otherwise camelCase(method)
///      prefixed by class-level [McpTool(NamePrefix=...)] when present; suffix _vMAJOR[_MINOR]
///      appended when BOTH explicitName AND namePrefix are absent and an [ApiVersion] /
///      [MapToApiVersion] attribute can be resolved (mirrors ModelBuilder.Build naming exactly).
///   2. HTTP verb: read from [HttpGet]/[HttpPost]/[HttpPut]/[HttpPatch]/[HttpDelete] on the
///      method; empty string when no verb attribute is found.
///   3. Route: combined class-level [Route] + method-level verb route arg, following the same
///      CombineRoutes logic as ModelBuilder (slash-trim and join with "/").
///
/// Remaining limitations:
///   Minimal-API handler methods: verb and route are determined by the surrounding MapGet(...)
///   call-syntax, which is NOT accessible via the attribute-driven pipeline; those handlers
///   are fingerprinted with empty verb/route strings.
///   API-version route-token substitution ({version:apiVersion} -> concrete segment) is NOT
///   applied to the manifest route; the raw template is stored.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class McpManifestGenerator : IIncrementalGenerator
{
    private const string McpToolAttributeFqn = "McpIt.McpToolAttribute";
    private const string DescriptionAttributeFqn = "System.ComponentModel.DescriptionAttribute";
    private const string CancellationTokenFqn = "System.Threading.CancellationToken";
    private const string RouteAttributeFqn = "Microsoft.AspNetCore.Mvc.RouteAttribute";

    // Mirrors ModelBuilder.VerbAttributes. Duplicated here because ModelBuilder is owned by the
    // main generator pipeline and must not be called from this generator directly. Unify in a
    // future refactor once both generators share a common model-building library.
    private static readonly (string Attr, string Verb)[] VerbAttributes =
    [
        ("Microsoft.AspNetCore.Mvc.HttpGetAttribute",    "GET"),
        ("Microsoft.AspNetCore.Mvc.HttpPostAttribute",   "POST"),
        ("Microsoft.AspNetCore.Mvc.HttpPutAttribute",    "PUT"),
        ("Microsoft.AspNetCore.Mvc.HttpDeleteAttribute", "DELETE"),
        ("Microsoft.AspNetCore.Mvc.HttpPatchAttribute",  "PATCH"),
    ];

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

        // -----------------------------------------------------------------------
        // Tool-name derivation: mirrors ModelBuilder.Build naming exactly.
        // The logic is duplicated here because ModelBuilder is part of the main
        // generator pipeline and should not be called from this manifest pipeline.
        // TODO: unify in a future refactor (extract shared naming helper).
        // -----------------------------------------------------------------------
        var explicitName = mcpAttr?.NamedArguments
            .FirstOrDefault(static kv => kv.Key == "Name").Value.Value as string;

        var classMcpAttr = GetClassMcpAttribute(method.ContainingType);
        var namePrefix = classMcpAttr?.NamedArguments
            .FirstOrDefault(static kv => kv.Key == "NamePrefix").Value.Value as string;

        var apiVersion = ResolveApiVersion(method);

        var toolName = string.IsNullOrWhiteSpace(explicitName)
            ? (string.IsNullOrWhiteSpace(namePrefix) ? string.Empty : namePrefix!) + ToCamelCase(method.Name)
            : explicitName!;

        // Only suffix a fully derived name; an explicit Name or NamePrefix means the author is
        // taking control of naming (and disambiguation) themselves (mirrors ModelBuilder line 55).
        if (string.IsNullOrWhiteSpace(explicitName) && string.IsNullOrWhiteSpace(namePrefix) && apiVersion is not null)
            toolName += "_v" + FormatVersionSegment(apiVersion).Replace('.', '_');

        var description = GetXmlSummary(method) ?? GetDescriptionAttribute(method) ?? string.Empty;

        // -----------------------------------------------------------------------
        // HTTP verb and route: read controller-action attributes.
        // Mirrors ModelBuilder.GetVerbAndRoute + GetClassRoute + CombineRoutes.
        // Minimal-API handler methods carry no verb/route attributes; they receive
        // empty strings (documented limitation above).
        // -----------------------------------------------------------------------
        var (httpVerb, methodRoute) = GetVerbAndRoute(method);
        var classRoute = GetClassRoute(method.ContainingType);
        var route = CombineRoutes(classRoute, methodRoute);

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
            HttpVerb: httpVerb,
            Route: route,
            Parameters: new EquatableArray<ManifestParamEntry>(paramEntries));
    }

    // ---------------------------------------------------------------------------
    // HTTP verb + route helpers (mirror ModelBuilder's controller-action logic;
    // duplicated here and should be unified in a future refactor).
    // ---------------------------------------------------------------------------

    private static (string Verb, string Route) GetVerbAndRoute(IMethodSymbol method)
    {
        foreach (var attr in method.GetAttributes())
        {
            var name = attr.AttributeClass?.ToDisplayString();
            var match = VerbAttributes.FirstOrDefault(v => v.Attr == name);
            if (match.Attr is not null)
            {
                var route = attr.ConstructorArguments.Length > 0
                    ? attr.ConstructorArguments[0].Value as string ?? string.Empty
                    : string.Empty;
                return (match.Verb, route);
            }
        }
        // No verb attribute found: empty strings indicate undetermined verb/route
        // (common for minimal-API handlers; documented limitation).
        return (string.Empty, string.Empty);
    }

    private static string GetClassRoute(INamedTypeSymbol type)
    {
        var routeAttr = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == RouteAttributeFqn);
        if (routeAttr is { ConstructorArguments.Length: > 0 })
            return routeAttr.ConstructorArguments[0].Value as string ?? string.Empty;
        return string.Empty;
    }

    // Mirrors ModelBuilder.CombineRoutes exactly (that method is internal to ModelBuilder;
    // duplicated here and should be unified in a future refactor).
    private static string CombineRoutes(string prefix, string suffix)
    {
        prefix = prefix.Trim('/');
        suffix = suffix.Trim('/');
        if (prefix.Length == 0) return suffix;
        if (suffix.Length == 0) return prefix;
        return $"{prefix}/{suffix}";
    }

    // ---------------------------------------------------------------------------
    // API-version helpers (mirror ModelBuilder's private methods; duplicated here
    // and should be unified in a future refactor).
    //
    // Priority: method [MapToApiVersion] > method [ApiVersion] > controller [ApiVersion].
    // Matched by simple type name so it works for both the modern Asp.Versioning package
    // and the legacy Microsoft.AspNetCore.Mvc.Versioning one.
    // ---------------------------------------------------------------------------

    private static string? ResolveApiVersion(IMethodSymbol method)
        => HighestVersion(method, "MapToApiVersionAttribute")
        ?? HighestVersion(method, "ApiVersionAttribute")
        ?? HighestVersion(method.ContainingType, "ApiVersionAttribute");

    private static string? HighestVersion(ISymbol symbol, string attrSimpleName)
    {
        string? best = null;
        foreach (var attr in symbol.GetAttributes())
        {
            if (attr.AttributeClass?.Name != attrSimpleName) continue;
            var v = ReadVersionFromAttribute(attr);
            if (v is null) continue;
            if (best is null || CompareVersions(v, best) > 0) best = v;
        }
        return best;
    }

    // Handles the common ApiVersion constructor shapes: ("1.0"), (1.0), (1, 0[, status]).
    private static string? ReadVersionFromAttribute(AttributeData attr)
    {
        var args = attr.ConstructorArguments;
        if (args.Length == 0 || args[0].IsNull) return null;
        switch (args[0].Value)
        {
            case string s:
                return string.IsNullOrWhiteSpace(s) ? null : s;
            case double d:
                return d.ToString("0.0###", CultureInfo.InvariantCulture);
            case int major when args.Length >= 2 && args[1].Value is int minor:
                return $"{major}.{minor}";
            case int major:
                return major.ToString(CultureInfo.InvariantCulture);
            default:
                return null;
        }
    }

    // URL-segment convention: major-only when minor is zero (v1 not v1.0);
    // non-zero minor is preserved (v2.1). Mirrors ModelBuilder.FormatVersionSegment.
    private static string FormatVersionSegment(string version)
    {
        var dot = version.IndexOf('.');
        if (dot < 0) return version;
        var minorPart = version.Substring(dot + 1);
        if (double.TryParse(minorPart, NumberStyles.Any, CultureInfo.InvariantCulture, out var minor) && minor == 0)
            return version.Substring(0, dot);
        return version;
    }

    private static int CompareVersions(string a, string b)
    {
        var (am, an) = ParseVersion(a);
        var (bm, bn) = ParseVersion(b);
        if (am != bm) return am.CompareTo(bm);
        if (an != bn) return an.CompareTo(bn);
        return string.CompareOrdinal(a, b);
    }

    private static (double Major, double Minor) ParseVersion(string v)
    {
        var dot = v.IndexOf('.');
        double major = 0, minor = 0;
        if (dot < 0)
            double.TryParse(v, NumberStyles.Any, CultureInfo.InvariantCulture, out major);
        else
        {
            double.TryParse(v.Substring(0, dot), NumberStyles.Any, CultureInfo.InvariantCulture, out major);
            double.TryParse(v.Substring(dot + 1), NumberStyles.Any, CultureInfo.InvariantCulture, out minor);
        }
        return (major, minor);
    }

    // ---------------------------------------------------------------------------
    // Attribute helpers.
    // ---------------------------------------------------------------------------

    private static AttributeData? GetClassMcpAttribute(INamedTypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == McpToolAttributeFqn);

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
