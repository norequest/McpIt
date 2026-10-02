using System.Collections.Generic;

namespace McpIt;

/// <summary>
/// Build-time description of one generated MCP tool. The source generator emits one per
/// <c>[McpTool]</c> into <c>McpIt.Generated.McpItToolCatalog.Tools</c>; the runtime uses the
/// catalog for tool ranking, the <c>search_tools</c> meta-tool and agent discovery documents.
/// Constructed by generated code only, so no reflection is involved.
/// </summary>
/// <param name="Name">The MCP tool name as exposed in <c>tools/list</c>.</param>
/// <param name="Title">Human-friendly display title (may be empty for inline lambdas).</param>
/// <param name="Description">Tool description, or null when none was declared.</param>
/// <param name="HttpMethod">HTTP verb of the underlying endpoint (GET, POST, ...).</param>
/// <param name="Route">Route template of the underlying endpoint.</param>
/// <param name="Category">Optional category from <c>[McpTool(Category = ...)]</c>.</param>
/// <param name="Keywords">Search keywords from <c>[McpTool(Keywords = ...)]</c>; never null.</param>
/// <param name="Priority">Ranking boost from <c>[McpTool(Priority = ...)]</c>.</param>
/// <param name="Parameters">Names of the tool's input parameters; never null.</param>
/// <param name="ReadOnly">True when the tool does not change state.</param>
/// <param name="Destructive">True when the tool may change or delete state.</param>
public sealed record McpToolDescriptor(
    string Name,
    string Title,
    string? Description,
    string HttpMethod,
    string Route,
    string? Category,
    IReadOnlyList<string> Keywords,
    int Priority,
    IReadOnlyList<string> Parameters,
    bool ReadOnly,
    bool Destructive);
