using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace McpIt.Generator;

public static class ParameterClassifier
{
    // Fully-qualified format WITHOUT special-type keywords (so int -> global::System.Int32),
    // but keeping nullable reference type annotations (so string? stays string?).
    private static readonly SymbolDisplayFormat TypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
            (SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions
                & ~SymbolDisplayMiscellaneousOptions.UseSpecialTypes)
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    public static ParameterModel Classify(IParameterSymbol p, string route)
    {
        var typeName = p.Type.ToDisplayString(TypeFormat);
        var source = DetermineSource(p, route);
        var constraints = ExtractConstraints(p);
        return new ParameterModel(p.Name, typeName, source, Constraints: constraints);
    }

    private static ParameterSource DetermineSource(IParameterSymbol p, string route)
    {
        if (route.Contains("{" + p.Name + "}"))
            return ParameterSource.Route;

        var hasFromBody = p.GetAttributes().Any(a =>
            a.AttributeClass?.ToDisplayString() == "Microsoft.AspNetCore.Mvc.FromBodyAttribute");
        if (hasFromBody) return ParameterSource.Body;

        if (IsComplex(p.Type)) return ParameterSource.Body;

        return ParameterSource.Query;
    }

    // ---------------------------------------------------------------------------
    // DataAnnotation constraint extraction.
    // Supported set: [Required], [Range], [StringLength], [MinLength], [MaxLength],
    // [RegularExpression]. All other attributes are silently ignored.
    //
    // The produced spec string has the form:
    //   req|range:MIN:MAX|strlen:MIN:MAX|minlen:N|maxlen:N|regex:PATTERN
    // where each segment is optional and they appear in the stable order above.
    // [regex:PATTERN] is always LAST because patterns can themselves contain '|'.
    // ---------------------------------------------------------------------------

    private static string? ExtractConstraints(IParameterSymbol p)
    {
        var reqEntries = new List<string>();
        var rangeEntries = new List<string>();
        var strlenEntries = new List<string>();
        var minlenEntries = new List<string>();
        var maxlenEntries = new List<string>();
        string? regexEntry = null;

        foreach (var attr in p.GetAttributes())
        {
            var fqn = attr.AttributeClass?.ToDisplayString();
            switch (fqn)
            {
                case "System.ComponentModel.DataAnnotations.RequiredAttribute":
                    reqEntries.Add("req");
                    break;

                case "System.ComponentModel.DataAnnotations.RangeAttribute":
                    // Only the (int,int) and (double,double) constructors are reconstructable;
                    // the (Type,string,string) form is skipped.
                    if (attr.ConstructorArguments.Length >= 2)
                    {
                        var minFmt = FormatNumericConstant(attr.ConstructorArguments[0]);
                        var maxFmt = FormatNumericConstant(attr.ConstructorArguments[1]);
                        if (minFmt is not null && maxFmt is not null)
                            rangeEntries.Add($"range:{minFmt}:{maxFmt}");
                    }
                    break;

                case "System.ComponentModel.DataAnnotations.StringLengthAttribute":
                    if (attr.ConstructorArguments.Length >= 1
                        && attr.ConstructorArguments[0].Value is int maxLen)
                    {
                        // MinimumLength is a named (property-set) argument.
                        var minLenRaw = attr.NamedArguments
                            .FirstOrDefault(kv => kv.Key == "MinimumLength").Value.Value;
                        var minLen = minLenRaw as int?;
                        var entry = (minLen.HasValue && minLen.Value > 0)
                            ? $"strlen:{minLen.Value}:{maxLen}"
                            : $"strlen::{maxLen}";
                        strlenEntries.Add(entry);
                    }
                    break;

                case "System.ComponentModel.DataAnnotations.MinLengthAttribute":
                    if (attr.ConstructorArguments.Length >= 1
                        && attr.ConstructorArguments[0].Value is int minLenVal)
                        minlenEntries.Add($"minlen:{minLenVal}");
                    break;

                case "System.ComponentModel.DataAnnotations.MaxLengthAttribute":
                    if (attr.ConstructorArguments.Length >= 1
                        && attr.ConstructorArguments[0].Value is int maxLenVal)
                        maxlenEntries.Add($"maxlen:{maxLenVal}");
                    break;

                case "System.ComponentModel.DataAnnotations.RegularExpressionAttribute":
                    // Stored last: regex pattern can contain '|'.
                    if (attr.ConstructorArguments.Length >= 1
                        && attr.ConstructorArguments[0].Value is string pattern
                        && !string.IsNullOrEmpty(pattern))
                        regexEntry = $"regex:{pattern}";
                    break;
            }
        }

        var parts = new List<string>();
        parts.AddRange(reqEntries);
        parts.AddRange(rangeEntries);
        parts.AddRange(strlenEntries);
        parts.AddRange(minlenEntries);
        parts.AddRange(maxlenEntries);
        if (regexEntry is not null) parts.Add(regexEntry);

        return parts.Count > 0 ? string.Join("|", parts) : null;
    }

    // Formats the numeric value inside a TypedConstant for storage in the spec string.
    // Integers are stored without a decimal point; doubles always include one so the
    // emitter can distinguish the constructor overload to use.
    private static string? FormatNumericConstant(TypedConstant tc)
    {
        return tc.Value switch
        {
            int i    => i.ToString(CultureInfo.InvariantCulture),
            long l   => l.ToString(CultureInfo.InvariantCulture),
            double d => FormatDouble(d),
            float f  => FormatDouble((double)f),
            _        => null
        };
    }

    private static string FormatDouble(double d)
    {
        var s = d.ToString("R", CultureInfo.InvariantCulture);
        // Ensure the stored value always contains a '.' so the emitter knows to use
        // the double constructor overload rather than the int one.
        return s.IndexOf('.') >= 0 ? s : s + ".0";
    }

    private static bool IsComplex(ITypeSymbol type)
    {
        if (type.SpecialType != SpecialType.None) return false; // string, int, bool, etc.
        if (type.TypeKind == TypeKind.Enum) return false;
        if (type is INamedTypeSymbol { IsGenericType: true } g &&
            g.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
            return false; // Nullable<primitive>
        return type.TypeKind is TypeKind.Class or TypeKind.Struct;
    }
}
