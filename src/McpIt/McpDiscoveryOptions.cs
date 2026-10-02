using System;
using System.Collections.Generic;

namespace McpIt;

/// <summary>
/// Options for <see cref="McpDiscoveryEndpoints.MapMcpDiscovery"/> and
/// <see cref="McpDiscoveryDocuments"/>: what the agent discovery documents say about the server
/// and where they are served.
/// </summary>
/// <remarks>
/// Every document is built once, when the endpoints are mapped, from these options and the
/// tool catalog. Changing the options afterwards has no effect on the served documents.
/// </remarks>
public sealed class McpDiscoveryOptions
{
    /// <summary>
    /// Server name in reverse-DNS form with exactly one slash, for example
    /// <c>com.example/orders</c> (pattern <c>^[a-zA-Z0-9.-]+/[a-zA-Z0-9._-]+$</c>, 3 to 200
    /// characters). This is the Server Card <c>name</c> and is also used to derive the AI Catalog
    /// identifier. Required when <see cref="EnableServerCard"/> or <see cref="EnableAiCatalog"/>
    /// is on; mapping throws <see cref="InvalidOperationException"/> when it is missing or invalid.
    /// </summary>
    public string? ServerName { get; set; }

    /// <summary>
    /// Human-readable display name. Used as the <c>llms.txt</c> H1 and the Server Card
    /// <c>title</c> (truncated to 100 characters there). Defaults to the part of
    /// <see cref="ServerName"/> after the slash, or <c>MCP Server</c>.
    /// </summary>
    public string? ServerTitle { get; set; }

    /// <summary>
    /// What the server does, focused on capabilities. Used in full as the <c>llms.txt</c>
    /// blockquote summary; the Server Card <c>description</c> is limited to 100 characters by its
    /// schema, so it is shortened there on a word boundary. Defaults to a generated sentence.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>Server version for the Server Card. Should be a semantic version. Defaults to <c>1.0.0</c>.</summary>
    public string Version { get; set; } = "1.0.0";

    /// <summary>
    /// Path of the Streamable HTTP MCP endpoint, as passed to <c>MapMcp</c>. Defaults to
    /// <c>/mcp</c>. Used for the advertised connection URL and for the default Server Card path.
    /// </summary>
    public string McpEndpointPath { get; set; } = "/mcp";

    /// <summary>
    /// The public origin (plus any path base) the API is reachable at, for example
    /// <c>https://api.example.com/</c>. When set, documents carry absolute URLs.
    /// When null, the documents carry relative URLs (and the Server Card, whose schema requires
    /// an absolute or templated URL, uses a <c>{base_url}</c> template variable instead).
    /// </summary>
    /// <remarks>
    /// The base URL is deliberately never derived from the request's <c>Host</c> header: that
    /// header is client-controlled, and the documents are cached and served to everyone, so a
    /// spoofed host would poison the advertised endpoint. Set this explicitly in production.
    /// </remarks>
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>Optional homepage or documentation URL, emitted as the Server Card <c>websiteUrl</c>.</summary>
    public Uri? WebsiteUrl { get; set; }

    /// <summary>
    /// Optional MCP protocol versions the endpoint supports (for example <c>2025-06-18</c>),
    /// emitted as <c>remotes[].supportedProtocolVersions</c>. Omitted when null or empty.
    /// </summary>
    public IList<string>? SupportedProtocolVersions { get; set; }

    /// <summary>Serve the MCP Server Card. Defaults to true.</summary>
    public bool EnableServerCard { get; set; } = true;

    /// <summary>
    /// Path of the Server Card. Defaults to <c>{McpEndpointPath}/server-card</c>, the location
    /// the Server Card draft reserves (<c>&lt;streamable-http-url&gt;/server-card</c>).
    /// </summary>
    public string? ServerCardPath { get; set; }

    /// <summary>
    /// Serve an AI Catalog that points at the Server Card, which is how the Server Card draft
    /// makes a server discoverable from the domain. Defaults to true. Only served when
    /// <see cref="EnableServerCard"/> is also on. Turn it off if the site publishes its own
    /// catalog, and add the Server Card entry there.
    /// </summary>
    public bool EnableAiCatalog { get; set; } = true;

    /// <summary>Path of the AI Catalog. Defaults to <c>/.well-known/ai-catalog.json</c>.</summary>
    public string AiCatalogPath { get; set; } = "/.well-known/ai-catalog.json";

    /// <summary>
    /// The AI Catalog entry identifier. Defaults to <c>urn:air:{publisher}:mcp:{name}</c> derived
    /// from <see cref="ServerName"/> (<c>com.example/orders</c> becomes
    /// <c>urn:air:example.com:mcp:orders</c>).
    /// </summary>
    public string? AiCatalogIdentifier { get; set; }

    /// <summary>Serve <c>llms.txt</c>. Defaults to true.</summary>
    public bool EnableLlmsTxt { get; set; } = true;

    /// <summary>Path of <c>llms.txt</c>. Defaults to <c>/llms.txt</c>.</summary>
    public string LlmsTxtPath { get; set; } = "/llms.txt";

    /// <summary>
    /// List destructive tools in the documents. Defaults to true; they are flagged
    /// <c>[destructive]</c> in <c>llms.txt</c>. Set to false to leave them out entirely.
    /// </summary>
    public bool IncludeDestructiveTools { get; set; } = true;

    /// <summary>
    /// Also list the tools (name, title, description, category, annotations) inside the Server
    /// Card under the vendor <c>_meta</c> key <see cref="McpDiscoveryDocuments.ToolsMetaKey"/>.
    /// Defaults to false, because the Server Card draft deliberately leaves primitives out of the
    /// card (clients must call <c>tools/list</c> for the authoritative list); <c>llms.txt</c>
    /// always lists the tools.
    /// </summary>
    public bool IncludeToolsInServerCardMeta { get; set; }

    /// <summary>
    /// How long clients and caches may reuse a document (<c>Cache-Control: public, max-age</c>).
    /// Defaults to one hour, as the Server Card draft recommends. Responses always carry an
    /// <c>ETag</c> and honor <c>If-None-Match</c>.
    /// </summary>
    public TimeSpan CacheMaxAge { get; set; } = TimeSpan.FromHours(1);
}
