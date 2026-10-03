using Microsoft.CodeAnalysis;

namespace McpIt.Generator;

public static class Diagnostics
{
    public static readonly DiagnosticDescriptor MissingDescription = new(
        id: "MCPGEN001",
        title: "MCP tool has no description",
        messageFormat: "The MCP tool '{0}' has no description; add an XML <summary> or [Description] so the model knows when to call it",
        category: "McpIt",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DestructiveOperation = new(
        id: "MCPGEN002",
        title: "Destructive operation exposed as MCP tool",
        messageFormat: "destructive operation '{0}' is exposed as an MCP tool; set [McpTool(AllowDestructive = true)] to acknowledge",
        category: "McpIt",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnresolvedApiVersion = new(
        id: "MCPGEN003",
        title: "Unresolved API version in route",
        messageFormat: "the route for MCP tool '{0}' contains a {{version:apiVersion}} token but no [ApiVersion]/[MapToApiVersion] was found, so the loopback call will 404; add a version attribute to the action or controller",
        category: "McpIt",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // Discoverability lints are Info, not Warning: they are advice for better tool selection by
    // the model, and must not break consumers that build with TreatWarningsAsErrors.
    public static readonly DiagnosticDescriptor ShortDescription = new(
        id: "MCPGEN004",
        title: "MCP tool description is too short to be discoverable",
        messageFormat: "The description of MCP tool '{0}' is too short to be discoverable; describe what the tool does and when to use it (at least a short sentence that is more than the tool's name or title)",
        category: "McpIt",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UndescribedParameter = new(
        id: "MCPGEN005",
        title: "MCP tool parameter has no description",
        messageFormat: "MCP tool '{0}' has parameters without a description: {1}; add an XML <param> or [Description] so the model knows what to pass",
        category: "McpIt",
        defaultSeverity: DiagnosticSeverity.Info,
        isEnabledByDefault: true);
}
