using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using McpIt.Generator.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace McpIt.Generator;

public static class ModelBuilder
{
    private static readonly (string Attr, string Verb)[] VerbAttributes =
    [
        ("Microsoft.AspNetCore.Mvc.HttpGetAttribute", "GET"),
        ("Microsoft.AspNetCore.Mvc.HttpPostAttribute", "POST"),
        ("Microsoft.AspNetCore.Mvc.HttpPutAttribute", "PUT"),
        ("Microsoft.AspNetCore.Mvc.HttpDeleteAttribute", "DELETE"),
        ("Microsoft.AspNetCore.Mvc.HttpPatchAttribute", "PATCH")
    ];

    public static EndpointModel? Build(GeneratorAttributeSyntaxContext ctx)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method) return null;

        // Controller pipeline: skip non-controller classes.
        // Minimal-API handler methods live in plain classes and are processed
        // by the separate Map*-invocation pipeline in McpToolGenerator.
        if (!InheritsFromControllerBase(method.ContainingType)) return null;

        var ns = method.ContainingType.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : method.ContainingType.ContainingNamespace.ToDisplayString();

        var className = $"{method.ContainingType.Name}_{method.Name}_Tool";

        var mcpAttr = ctx.Attributes.FirstOrDefault();
        var classMcpAttr = GetClassMcpAttribute(method.ContainingType);

        var explicitName = mcpAttr?.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Name").Value.Value as string;
        // Class-level NamePrefix only applies to the DERIVED name; an explicit
        // action Name fully overrides and is used verbatim with no prefix.
        var namePrefix = classMcpAttr?.NamedArguments
            .FirstOrDefault(kv => kv.Key == "NamePrefix").Value.Value as string;
        // Resolved here because a derived tool name folds in the API version to keep versioned
        // variants distinct: the same action in separate per-version controllers would otherwise
        // derive the same name and collide at the MCP layer.
        var apiVersion = ResolveApiVersion(method);
        var toolName = string.IsNullOrWhiteSpace(explicitName)
            ? (string.IsNullOrWhiteSpace(namePrefix) ? string.Empty : namePrefix!) + ToCamelCase(method.Name)
            : explicitName!;
        // Only suffix a fully derived name; an explicit Name or NamePrefix means the author is
        // taking control of naming (and disambiguation) themselves.
        if (string.IsNullOrWhiteSpace(explicitName) && string.IsNullOrWhiteSpace(namePrefix) && apiVersion is not null)
            toolName += "_v" + FormatVersionSegment(apiVersion).Replace('.', '_');

        var actionAllowDestructive = mcpAttr?.NamedArguments
            .FirstOrDefault(kv => kv.Key == "AllowDestructive").Value.Value is bool b && b;
        var classAllowDestructive = classMcpAttr?.NamedArguments
            .FirstOrDefault(kv => kv.Key == "AllowDestructive").Value.Value is bool cb && cb;
        var allowDestructive = actionAllowDestructive || classAllowDestructive;

        var (httpMethod, methodRoute) = GetVerbAndRoute(method);
        var classRoute = GetClassRoute(method.ContainingType);
        var route = CombineRoutes(classRoute, methodRoute);

        // Resolve URL-segment API versioning so {version:apiVersion} becomes a concrete
        // segment per endpoint, otherwise the loopback call 404s on the literal token.
        route = SubstituteApiVersionToken(route, apiVersion);

        var description = GetXmlSummary(method) ?? GetDescriptionAttribute(method);

        var (readOnly, destructive, idempotent) = DeriveSafety(httpMethod);

        var (outputMaxLength, outputFields, outputMaxItems) = GetOutputShaping(method);

        var explicitTitle = mcpAttr?.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Title").Value.Value as string;
        var title = string.IsNullOrWhiteSpace(explicitTitle)
            ? DeriveTitle(method.Name)
            : explicitTitle!;

        var requiredScope = mcpAttr?.NamedArguments
            .FirstOrDefault(kv => kv.Key == "RequiredScope").Value.Value as string;

        var paramDescriptions = GetXmlParamDescriptions(method);

        var cancellationTokenType = ctx.SemanticModel.Compilation
            .GetTypeByMetadataName("System.Threading.CancellationToken");

        var parameters = method.Parameters
            .Where(p => !IsCancellationToken(p.Type, cancellationTokenType))
            .Select(p =>
            {
                var model = ParameterClassifier.Classify(p, route);
                if (paramDescriptions.TryGetValue(p.Name, out var desc) && !string.IsNullOrWhiteSpace(desc))
                    model = model with { Description = desc };
                return model;
            })
            .ToArray();

        return new EndpointModel(
            Namespace: ns,
            GeneratedClassName: className,
            ToolName: toolName,
            Description: description,
            HttpMethod: httpMethod,
            RouteTemplate: route,
            Parameters: new EquatableArray<ParameterModel>(parameters),
            ReadOnly: readOnly,
            Destructive: destructive,
            Idempotent: idempotent,
            AllowDestructive: allowDestructive,
            OutputMaxLength: outputMaxLength,
            OutputFields: new EquatableArray<string>(outputFields),
            OutputMaxItems: outputMaxItems,
            Title: title,
            Location: LocationInfo.From(method.Locations.FirstOrDefault() ?? Location.None),
            RequiredScope: requiredScope);
    }

    private static bool IsCancellationToken(ITypeSymbol type, INamedTypeSymbol? cancellationTokenType)
    {
        // Unwrap Nullable<CancellationToken> so `CancellationToken?` is caught too.
        if (type is INamedTypeSymbol { IsGenericType: true } nullable &&
            nullable.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            type = nullable.TypeArguments[0];

        if (cancellationTokenType is not null)
            return SymbolEqualityComparer.Default.Equals(type, cancellationTokenType);

        // Fallback if the symbol cannot be resolved from the compilation.
        return type.ToDisplayString() == "System.Threading.CancellationToken";
    }

    private static (bool ReadOnly, bool Destructive, bool Idempotent) DeriveSafety(string verb) => verb switch
    {
        "GET" => (true, false, true),
        "HEAD" => (true, false, true),
        "POST" => (false, true, false),
        "PUT" => (false, true, true),
        "PATCH" => (false, true, false),
        "DELETE" => (false, true, true),
        _ => (false, false, false),
    };

    private static (int? MaxLength, string[] Fields, int? MaxItems) GetOutputShaping(IMethodSymbol method)
    {
        var attr = method.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "McpIt.McpToolOutputAttribute");
        if (attr is null)
            return (null, [], null);

        int? maxLength = null;
        var maxArg = attr.NamedArguments.FirstOrDefault(kv => kv.Key == "MaxLength");
        if (maxArg.Key == "MaxLength" && maxArg.Value.Value is int m && m > 0)
            maxLength = m;

        var fields = System.Array.Empty<string>();
        var fieldsArg = attr.NamedArguments.FirstOrDefault(kv => kv.Key == "Fields");
        if (fieldsArg.Key == "Fields" && !fieldsArg.Value.IsNull)
        {
            fields = fieldsArg.Value.Values
                .Select(v => v.Value as string)
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => s!)
                .ToArray();
        }

        int? maxItems = null;
        var maxItemsArg = attr.NamedArguments.FirstOrDefault(kv => kv.Key == "MaxItems");
        if (maxItemsArg.Key == "MaxItems" && maxItemsArg.Value.Value is int mi && mi > 0)
            maxItems = mi;

        return (maxLength, fields, maxItems);
    }

    // Parses <param name="x">description</param> entries from the method's XML doc comment.
    // Returns an empty dictionary on any parse failure (best-effort, null-safe).
    private static System.Collections.Generic.Dictionary<string, string> GetXmlParamDescriptions(IMethodSymbol method)
    {
        var result = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
        var xml = method.GetDocumentationCommentXml();
        if (string.IsNullOrWhiteSpace(xml)) return result;
        try
        {
            var doc = XDocument.Parse(xml);
            foreach (var paramEl in doc.Descendants("param"))
            {
                var name = paramEl.Attribute("name")?.Value;
                var text = paramEl.Value.Trim();
                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(text))
                    result[name!] = text;
            }
        }
        catch
        {
            // Best-effort: return whatever was collected so far.
        }
        return result;
    }

    // Converts a PascalCase method name to a human-friendly title by inserting spaces
    // before each uppercase letter that follows a lowercase letter or digit.
    // Example: "GetOrderById" -> "Get Order By Id".
    private static string DeriveTitle(string methodName)
    {
        if (string.IsNullOrEmpty(methodName)) return methodName;
        return Regex.Replace(methodName, @"(?<=[a-z0-9])([A-Z])", " $1");
    }

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
        return ("GET", string.Empty);
    }

    private static AttributeData? GetClassMcpAttribute(INamedTypeSymbol type) =>
        type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "McpIt.McpToolAttribute");

    private static string GetClassRoute(INamedTypeSymbol type)
    {
        var routeAttr = type.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "Microsoft.AspNetCore.Mvc.RouteAttribute");
        if (routeAttr is { ConstructorArguments.Length: > 0 })
            return routeAttr.ConstructorArguments[0].Value as string ?? string.Empty;
        return string.Empty;
    }

    // Internal so McpToolGenerator can reuse it for MapGroup prefix assembly.
    internal static string CombineRoutes(string prefix, string suffix)
    {
        prefix = prefix.Trim('/');
        suffix = suffix.Trim('/');
        if (prefix.Length == 0) return suffix;
        if (suffix.Length == 0) return prefix;
        return $"{prefix}/{suffix}";
    }

    // Effective API version for URL-segment versioning, read from attributes already on the code.
    // Priority: method [MapToApiVersion] > method [ApiVersion] > controller [ApiVersion].
    // Matched by simple type name so it works for both the modern Asp.Versioning package and the
    // legacy Microsoft.AspNetCore.Mvc.Versioning one. Returns null when nothing is declared.
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

    // Replaces a {version:apiVersion} style token with the resolved version. No-op when there
    // is no token (non-versioned apps) or nothing was resolved (a warning is raised elsewhere).
    private static string SubstituteApiVersionToken(string route, string? version)
    {
        if (version is null || route.IndexOf("apiVersion", StringComparison.OrdinalIgnoreCase) < 0)
            return route;
        return Regex.Replace(route, @"\{[^{}]*:apiVersion[^{}]*\}", FormatVersionSegment(version), RegexOptions.IgnoreCase);
    }

    // URL-segment convention is major-only when the minor is zero (/v1/ not /v1.0/); the apiVersion
    // route constraint accepts /v1/ for a "1.0" declaration. A non-zero minor is preserved (/v2.1/).
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
        catch
        {
            return null;
        }
    }

    private static string? GetDescriptionAttribute(IMethodSymbol method)
    {
        var attr = method.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute");
        if (attr is { ConstructorArguments.Length: > 0 })
            return attr.ConstructorArguments[0].Value as string;
        return null;
    }

    private static string ToCamelCase(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLowerInvariant(s[0]) + s.Substring(1);

    // Walks the base-type chain to check for ControllerBase inheritance.
    // Matched by fully-qualified name so the check works even when the MVC
    // assembly is not the exact same reference version loaded here.
    private static bool InheritsFromControllerBase(INamedTypeSymbol type)
    {
        var current = type.BaseType;
        while (current is not null)
        {
            if (current.ToDisplayString() == "Microsoft.AspNetCore.Mvc.ControllerBase")
                return true;
            current = current.BaseType;
        }
        return false;
    }

    // ---------------------------------------------------------------------------
    // Lambda-from-syntax path: builds an EndpointModel from an inline lambda
    // passed to a Delegate-typed Map* overload (the real ASP.NET Core shape).
    // Roslyn's GetSymbolInfo returns null for such lambdas, so we derive
    // everything from the lambda expression's own syntax and SemanticModel.
    //
    // Constraints:
    //   - Lambda must carry [McpTool] in its AttributeLists; otherwise null.
    //   - All lambda parameters must have explicit type declarations; a param
    //     without an explicit type causes the entire endpoint to be skipped
    //     (returns null) since the type cannot be classified.
    //   - ParameterClassifier.Classify is called unchanged for each resolved param.
    //   - Lambdas have no XML doc comments; description comes from [Description]
    //     attribute syntax only (empty otherwise, which triggers MCPGEN001).
    // ---------------------------------------------------------------------------
    // classNameOverride: caller-supplied name derived from verb + sanitized route +
    //   containing type; avoids relying on the compiler-generated lambda method name.
    // toolNameHint: route-derived fallback tool name used when no explicit
    //   McpTool(Name=...) is set.
    public static EndpointModel? BuildFromLambda(
        LambdaExpressionSyntax lambda,
        string verb,
        string route,
        SemanticModel semanticModel,
        Compilation compilation,
        string? classNameOverride = null,
        string? toolNameHint = null,
        System.Threading.CancellationToken ct = default)
    {
        // Locate [McpTool] in the lambda's AttributeLists; reject unmarked lambdas.
        var mcpAttrSyntax = FindMcpToolAttributeSyntax(lambda, semanticModel);
        if (mcpAttrSyntax is null) return null;

        // Read McpTool named args directly from syntax (no IMethodSymbol available here).
        var explicitName     = ReadAttrStringArg(mcpAttrSyntax, "Name");
        var explicitTitle    = ReadAttrStringArg(mcpAttrSyntax, "Title");
        var allowDestructive = ReadAttrBoolArg(mcpAttrSyntax, "AllowDestructive");
        var requiredScope    = ReadAttrStringArg(mcpAttrSyntax, "RequiredScope");

        // Resolve enclosing namespace through the semantic model.
        var ns = GetLambdaNamespace(lambda, semanticModel);

        // Class name: caller-supplied override (verb + route + type) is strongly preferred;
        // the fallback keeps the name valid when context is unavailable.
        var className = classNameOverride ?? $"MinApi_Lambda_{verb}_Tool";

        // Tool name: explicit Name wins; otherwise use the route-derived hint or bare verb.
        var toolName = string.IsNullOrWhiteSpace(explicitName)
            ? (toolNameHint ?? verb.ToLowerInvariant())
            : explicitName!;

        // Collect and resolve lambda parameters; bail out on any unresolvable param.
        var paramSyntaxes = GetLambdaParameterSyntaxes(lambda);
        var ctType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

        var parameters = new System.Collections.Generic.List<ParameterModel>();
        foreach (var paramSyntax in paramSyntaxes)
        {
            // No explicit type means we cannot determine the kind; skip the endpoint.
            if (paramSyntax.Type is null) return null;

            var paramSym = semanticModel.GetDeclaredSymbol(paramSyntax, ct) as IParameterSymbol;
            if (paramSym is null) return null;

            if (IsCancellationToken(paramSym.Type, ctType)) continue;

            parameters.Add(ParameterClassifier.Classify(paramSym, route));
        }

        // Description: no XML doc on lambdas; check [Description] attribute in syntax.
        var description = GetDescriptionFromLambdaAttrs(lambda, semanticModel);

        var title = string.IsNullOrWhiteSpace(explicitTitle)
            ? DeriveTitle(toolName)
            : explicitTitle!;

        var (readOnly, destructive, idempotent) = DeriveSafety(verb);

        return new EndpointModel(
            Namespace: ns,
            GeneratedClassName: className,
            ToolName: toolName,
            Description: description,
            HttpMethod: verb,
            RouteTemplate: route,
            Parameters: new EquatableArray<ParameterModel>(parameters.ToArray()),
            ReadOnly: readOnly,
            Destructive: destructive,
            Idempotent: idempotent,
            AllowDestructive: allowDestructive,
            OutputMaxLength: null,
            OutputFields: new EquatableArray<string>(Array.Empty<string>()),
            OutputMaxItems: null,
            Title: title,
            Location: LocationInfo.From(lambda.GetLocation()),
            RequiredScope: requiredScope);
    }

    // Finds the [McpTool] attribute in a lambda's AttributeLists.
    // Uses the semantic model for accurate type-based matching; falls back to a name check
    // when binding is incomplete (partial compilation).
    private static AttributeSyntax? FindMcpToolAttributeSyntax(
        LambdaExpressionSyntax lambda,
        SemanticModel semanticModel)
    {
        foreach (var attrList in lambda.AttributeLists)
        foreach (var attr in attrList.Attributes)
        {
            // Primary: semantic model verification.
            var symInfo = semanticModel.GetSymbolInfo(attr);
            var ctor = (symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
            if (ctor?.ContainingType?.ToDisplayString() == "McpIt.McpToolAttribute")
                return attr;

            // Fallback: attribute name when symbol resolution is unavailable.
            var attrName = attr.Name.ToString();
            if (attrName is "McpTool" or "McpToolAttribute"
                         or "McpIt.McpTool" or "McpIt.McpToolAttribute")
                return attr;
        }
        return null;
    }

    // Reads a string literal named argument value from an attribute's argument list.
    // Returns null when the argument is absent or is not a string literal.
    private static string? ReadAttrStringArg(AttributeSyntax attr, string argName)
    {
        if (attr.ArgumentList is null) return null;
        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals?.Name.Identifier.Text == argName &&
                arg.Expression is LiteralExpressionSyntax lit)
                return lit.Token.ValueText;
        }
        return null;
    }

    // Reads a bool literal named argument value from an attribute's argument list.
    // Returns false when the argument is absent or not a recognizable bool literal.
    private static bool ReadAttrBoolArg(AttributeSyntax attr, string argName)
    {
        if (attr.ArgumentList is null) return false;
        foreach (var arg in attr.ArgumentList.Arguments)
        {
            if (arg.NameEquals?.Name.Identifier.Text == argName &&
                arg.Expression is LiteralExpressionSyntax lit)
                return lit.Token.ValueText == "true";
        }
        return false;
    }

    // Returns the fully-qualified namespace of the lambda's enclosing type via the
    // semantic model, or empty string for the global namespace.
    private static string GetLambdaNamespace(LambdaExpressionSyntax lambda, SemanticModel semanticModel)
    {
        var parent = lambda.Parent;
        while (parent is not null)
        {
            if (parent is TypeDeclarationSyntax typeDecl)
            {
                var typeSymbol = semanticModel.GetDeclaredSymbol(typeDecl) as INamedTypeSymbol;
                if (typeSymbol is not null)
                {
                    return typeSymbol.ContainingNamespace.IsGlobalNamespace
                        ? string.Empty
                        : typeSymbol.ContainingNamespace.ToDisplayString();
                }
            }
            parent = parent.Parent;
        }
        return string.Empty;
    }

    // Returns the ParameterSyntax list for parenthesized or simple lambda forms.
    private static System.Collections.Generic.IReadOnlyList<ParameterSyntax> GetLambdaParameterSyntaxes(
        LambdaExpressionSyntax lambda)
    {
        if (lambda is ParenthesizedLambdaExpressionSyntax p) return p.ParameterList.Parameters;
        if (lambda is SimpleLambdaExpressionSyntax s) return new[] { s.Parameter };
        return Array.Empty<ParameterSyntax>();
    }

    // Reads a [System.ComponentModel.DescriptionAttribute("...")] value from the lambda's
    // attribute lists. Lambdas carry no XML doc comments so this is the only way to supply
    // a description without using McpTool(Name=...) / McpTool(Title=...).
    private static string? GetDescriptionFromLambdaAttrs(
        LambdaExpressionSyntax lambda,
        SemanticModel semanticModel)
    {
        foreach (var attrList in lambda.AttributeLists)
        foreach (var attr in attrList.Attributes)
        {
            var symInfo = semanticModel.GetSymbolInfo(attr);
            var ctor = (symInfo.Symbol ?? symInfo.CandidateSymbols.FirstOrDefault()) as IMethodSymbol;
            if (ctor?.ContainingType?.ToDisplayString() == "System.ComponentModel.DescriptionAttribute" &&
                attr.ArgumentList?.Arguments.Count > 0 &&
                attr.ArgumentList.Arguments[0].Expression is LiteralExpressionSyntax lit)
            {
                return lit.Token.ValueText;
            }
        }
        return null;
    }

    // ---------------------------------------------------------------------------
    // Minimal-API path: builds a model from a handler method whose verb and route
    // come from the surrounding MapGet/MapPost/... invocation, not from controller
    // attributes. No class-level route combining or API-version substitution is
    // performed; the caller supplies the complete route string verbatim.
    // The handler MUST carry [McpIt.McpToolAttribute]; callers should verify this
    // before calling, but the method also checks and returns null if absent.
    // ---------------------------------------------------------------------------
    // classNameOverride: when provided (e.g. for lambda handlers with invalid compiler-generated
    //   names), this value is used verbatim instead of the auto-derived "MinApi_Type_Method_Tool".
    // toolNameHint: when provided and no explicit McpTool(Name=...) is set, this value is used
    //   as the MCP tool name instead of the auto-derived camelCase method name. Useful for lambda
    //   handlers whose compiler-generated names are not meaningful.
    public static EndpointModel? BuildFromHandler(
        IMethodSymbol handler,
        string verb,
        string route,
        Compilation compilation,
        string? classNameOverride = null,
        string? toolNameHint = null)
    {
        var mcpAttr = handler.GetAttributes().FirstOrDefault(a =>
            a.AttributeClass?.ToDisplayString() == "McpIt.McpToolAttribute");
        if (mcpAttr is null) return null;

        var ns = handler.ContainingType.ContainingNamespace.IsGlobalNamespace
            ? string.Empty
            : handler.ContainingType.ContainingNamespace.ToDisplayString();

        // classNameOverride is used for lambda handlers (compiler-generated names are not valid
        // C# identifiers). For method-group handlers the name is derived from the type and method.
        var className = classNameOverride ?? $"MinApi_{handler.ContainingType.Name}_{handler.Name}_Tool";

        var explicitName = mcpAttr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Name").Value.Value as string;
        // toolNameHint is the route-derived fallback for lambda handlers; it is only used when
        // no explicit Name is provided.
        var toolName = string.IsNullOrWhiteSpace(explicitName)
            ? (toolNameHint ?? ToCamelCase(handler.Name))
            : explicitName!;

        var allowDestructive = mcpAttr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "AllowDestructive").Value.Value is bool b && b;

        var description = GetXmlSummary(handler) ?? GetDescriptionAttribute(handler);
        var (readOnly, destructive, idempotent) = DeriveSafety(verb);
        var (outputMaxLength, outputFields, outputMaxItems) = GetOutputShaping(handler);

        var explicitTitle = mcpAttr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "Title").Value.Value as string;
        var title = string.IsNullOrWhiteSpace(explicitTitle)
            ? DeriveTitle(handler.Name)
            : explicitTitle!;

        var requiredScope = mcpAttr.NamedArguments
            .FirstOrDefault(kv => kv.Key == "RequiredScope").Value.Value as string;

        var paramDescriptions = GetXmlParamDescriptions(handler);
        var cancellationTokenType = compilation.GetTypeByMetadataName("System.Threading.CancellationToken");

        var parameters = handler.Parameters
            .Where(p => !IsCancellationToken(p.Type, cancellationTokenType))
            .Select(p =>
            {
                var model = ParameterClassifier.Classify(p, route);
                if (paramDescriptions.TryGetValue(p.Name, out var desc) && !string.IsNullOrWhiteSpace(desc))
                    model = model with { Description = desc };
                return model;
            })
            .ToArray();

        return new EndpointModel(
            Namespace: ns,
            GeneratedClassName: className,
            ToolName: toolName,
            Description: description,
            HttpMethod: verb,
            RouteTemplate: route,
            Parameters: new EquatableArray<ParameterModel>(parameters),
            ReadOnly: readOnly,
            Destructive: destructive,
            Idempotent: idempotent,
            AllowDestructive: allowDestructive,
            OutputMaxLength: outputMaxLength,
            OutputFields: new EquatableArray<string>(outputFields),
            OutputMaxItems: outputMaxItems,
            Title: title,
            Location: LocationInfo.From(handler.Locations.FirstOrDefault() ?? Location.None),
            RequiredScope: requiredScope);
    }
}
