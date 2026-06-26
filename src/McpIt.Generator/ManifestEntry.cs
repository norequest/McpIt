using McpIt.Generator.Internal;

namespace McpIt.Generator;

/// <summary>
/// The name and fully-qualified type of a single parameter captured for manifest fingerprinting.
/// Value equality is automatic (readonly record struct).
/// </summary>
public readonly record struct ManifestParamEntry(string Name, string TypeFullyQualified);

/// <summary>
/// The model-facing surface of a single [McpTool]-annotated method, collected at build time
/// by <see cref="McpManifestGenerator"/> for emission into McpItManifest.
/// Implements full value equality (via record + EquatableArray) for Roslyn incremental caching.
/// </summary>
public sealed record ManifestEntry(
    string ToolName,
    string Description,
    string HttpVerb,
    string Route,
    EquatableArray<ManifestParamEntry> Parameters);
