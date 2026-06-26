using McpIt.Generator.Internal;

namespace McpIt.Generator;

public enum ParameterSource { Route, Query, Body }

public sealed record ParameterModel(
    string Name,
    string TypeFullyQualified,
    ParameterSource Source,
    string? Description = null,
    // Compact constraint spec derived from DataAnnotation attributes on the original
    // controller-action parameter. Format: pipe-separated entries in stable order
    // (req | range:MIN:MAX | strlen:MIN:MAX | minlen:N | maxlen:N | regex:PATTERN)
    // where regex is always last (patterns can contain '|').
    // Null when no supported DataAnnotation attributes are present.
    string? Constraints = null);

public sealed record EndpointModel(
    string Namespace,
    string GeneratedClassName,
    string ToolName,
    string? Description,
    string HttpMethod,
    string RouteTemplate,
    EquatableArray<ParameterModel> Parameters,
    bool ReadOnly,
    bool Destructive,
    bool Idempotent,
    bool AllowDestructive,
    int? OutputMaxLength,
    EquatableArray<string> OutputFields,
    int? OutputMaxItems,
    string? Title,
    LocationInfo? Location,
    string? RequiredScope = null)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public bool HasOutputShaping => OutputMaxLength.HasValue || OutputFields.Count > 0 || OutputMaxItems.HasValue;
}
