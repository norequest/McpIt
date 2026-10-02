; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
MCPGEN001 | McpIt | Warning | MCP tool has no description; add an XML <summary> or [Description].
MCPGEN002 | McpIt | Warning | Destructive operation exposed as an MCP tool without [McpTool(AllowDestructive = true)].
MCPGEN003 | McpIt | Warning | Route contains an unresolved {version:apiVersion} token; add [ApiVersion]/[MapToApiVersion].
MCPGEN004 | McpIt | Info | MCP tool description is too short (under 4 words or just the name/title) to be discoverable.
MCPGEN005 | McpIt | Info | MCP tool parameters have no XML <param> or [Description].
