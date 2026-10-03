using System;
using System.Collections.Generic;

namespace McpIt;

/// <summary>
/// Build-time description of one generated MCP tool. The source generator emits one per
/// <c>[McpTool]</c> into <c>McpIt.Generated.McpItToolCatalog.Tools</c>; the runtime uses the
/// catalog for tool ranking, the <c>search_tools</c> meta-tool and agent discovery documents.
/// Constructed by generated code with an object initializer, so new properties can be added
/// in later versions without breaking previously generated code. No reflection is involved.
/// </summary>
public sealed class McpToolDescriptor
{
    private readonly IReadOnlyList<string> _keywords = Array.Empty<string>();
    private readonly IReadOnlyList<string> _parameters = Array.Empty<string>();

    /// <summary>Creates a descriptor for the tool with the given MCP name.</summary>
    /// <exception cref="ArgumentException"><paramref name="name"/> is null or whitespace.</exception>
    public McpToolDescriptor(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Tool name must not be empty.", nameof(name));
        Name = name;
    }

    /// <summary>The MCP tool name as exposed in <c>tools/list</c>.</summary>
    public string Name { get; }

    /// <summary>Human-friendly display title (may be empty for inline lambdas).</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>Tool description, or null when none was declared.</summary>
    public string? Description { get; init; }

    /// <summary>HTTP verb of the underlying endpoint (GET, POST, ...).</summary>
    public string HttpMethod { get; init; } = string.Empty;

    /// <summary>Route template of the underlying endpoint.</summary>
    public string Route { get; init; } = string.Empty;

    /// <summary>Optional category from <c>[McpTool(Category = ...)]</c>.</summary>
    public string? Category { get; init; }

    /// <summary>Search keywords from <c>[McpTool(Keywords = ...)]</c>; never null.</summary>
    public IReadOnlyList<string> Keywords
    {
        get => _keywords;
        init => _keywords = value ?? Array.Empty<string>();
    }

    /// <summary>Ranking boost from <c>[McpTool(Priority = ...)]</c>.</summary>
    public int Priority { get; init; }

    /// <summary>Names of the tool's input parameters; never null.</summary>
    public IReadOnlyList<string> Parameters
    {
        get => _parameters;
        init => _parameters = value ?? Array.Empty<string>();
    }

    /// <summary>True when the tool does not change state.</summary>
    public bool ReadOnly { get; init; }

    /// <summary>True when the tool may change or delete state.</summary>
    public bool Destructive { get; init; }

    /// <inheritdoc />
    public override string ToString() => $"{HttpMethod} {Route} -> {Name}";
}
