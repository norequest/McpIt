namespace McpIt;

/// <summary>
/// Options for the <c>search_tools</c> meta-tool registered by
/// <see cref="McpToolSearchExtensions.WithToolSearch"/>.
/// </summary>
public sealed class McpToolSearchOptions
{
    /// <summary>The hard upper bound for <see cref="MaxResults"/>.</summary>
    public const int MaxResultsCap = 25;

    /// <summary>
    /// Name of the meta-tool in <c>tools/list</c>. Defaults to <c>search_tools</c>. Must not
    /// collide with one of your own tool names.
    /// </summary>
    public string ToolName { get; set; } = "search_tools";

    /// <summary>
    /// Maximum number of results a single call returns, and the count returned when the caller
    /// omits <c>limit</c>. Defaults to 5; must be between 1 and <see cref="MaxResultsCap"/>.
    /// Small values keep each search response cheap in tokens.
    /// </summary>
    public int MaxResults { get; set; } = 5;

    /// <summary>
    /// Replaces the built-in tool description shown to the model. Leave null to use the default,
    /// which tells the model to call this tool first whenever it is unsure which tool fits.
    /// </summary>
    public string? Description { get; set; }
}
