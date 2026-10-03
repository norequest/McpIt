namespace McpIt;

/// <summary>
/// One ranked result from <see cref="McpToolRanker"/> or <see cref="McpToolIndex"/>.
/// </summary>
/// <param name="Tool">The matching tool.</param>
/// <param name="Score">
/// Relevance score; higher is better. Only meaningful relative to other scores from the same
/// query and catalog. It is <c>0</c> for every result of an empty query, which returns tools in
/// priority order instead of by relevance.
/// </param>
public sealed record McpToolMatch(McpToolDescriptor Tool, double Score);
