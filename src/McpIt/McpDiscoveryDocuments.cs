using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace McpIt;

/// <summary>
/// Builds the agent discovery documents that <see cref="McpDiscoveryEndpoints.MapMcpDiscovery"/>
/// serves: an MCP Server Card, an AI Catalog pointing at it, and an <c>llms.txt</c> tool index.
/// Public so the same documents can also be written to static files at build or deploy time.
/// </summary>
/// <remarks>
/// <para>
/// <b>Server Card status (October 2026): draft.</b> The shape follows the MCP Server Card
/// extension (SEP-2127, which superseded SEP-1649) as captured in
/// <c>modelcontextprotocol/experimental-ext-server-card</c> at snapshot <c>526201bb</c>. SEP-2127
/// is not yet merged into the specification, so field names may still change. Per that draft a
/// card describes identity and remote endpoints only; it does not list tools or capabilities
/// (see <see cref="McpDiscoveryOptions.IncludeToolsInServerCardMeta"/> for an opt-in vendor
/// extension). The card lives at <c>&lt;streamable-http-url&gt;/server-card</c> and is found from
/// the domain through an AI Catalog at <c>/.well-known/ai-catalog.json</c>; the earlier
/// <c>/.well-known/mcp/server-card.json</c> location from SEP-1649 is no longer recommended.
/// </para>
/// <para>
/// <c>llms.txt</c> follows the llmstxt.org proposal: an H1, a blockquote summary, and H2
/// sections of <c>- [name](url): notes</c> bullets.
/// </para>
/// <para>All JSON is written with <see cref="Utf8JsonWriter"/>, so building is AOT and trim safe.</para>
/// </remarks>
public static class McpDiscoveryDocuments
{
    /// <summary>The JSON Schema URI every Server Card declares in <c>$schema</c>.</summary>
    public const string ServerCardSchemaUri = "https://static.modelcontextprotocol.io/schemas/v1/server-card.schema.json";

    /// <summary>Media type of a Server Card.</summary>
    public const string ServerCardMediaType = "application/mcp-server-card+json";

    /// <summary>Media type of an AI Catalog.</summary>
    public const string AiCatalogMediaType = "application/ai-catalog+json";

    /// <summary>Media type <c>llms.txt</c> is served with.</summary>
    public const string LlmsTxtMediaType = "text/plain; charset=utf-8";

    /// <summary>
    /// Vendor <c>_meta</c> key under which the tool list is written into the Server Card when
    /// <see cref="McpDiscoveryOptions.IncludeToolsInServerCardMeta"/> is on.
    /// </summary>
    public const string ToolsMetaKey = "io.github.norequest.mcpit/tools";

    /// <summary>Name of the template variable used for the server origin when no public base URL is configured.</summary>
    public const string BaseUrlVariable = "base_url";

    private const int CardTextMaxLength = 100;

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        // The documents are served as JSON with nosniff, never embedded in HTML, so non-ASCII
        // text (for example Georgian descriptions) can stay readable instead of \u-escaped.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Builds the MCP Server Card JSON.</summary>
    /// <exception cref="InvalidOperationException"><see cref="McpDiscoveryOptions.ServerName"/> is missing or invalid.</exception>
    public static string BuildServerCard(IReadOnlyList<McpToolDescriptor> tools, McpDiscoveryOptions options)
    {
        if (tools is null) throw new ArgumentNullException(nameof(tools));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var name = RequireServerName(options);
        var included = FilterTools(tools, options);

        return WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteString("$schema", ServerCardSchemaUri);
            w.WriteString("name", name);
            w.WriteString("version", string.IsNullOrWhiteSpace(options.Version) ? "1.0.0" : options.Version.Trim());
            w.WriteString("description", Shorten(SingleLine(DescriptionOf(options, included.Count)), CardTextMaxLength));
            w.WriteString("title", Shorten(SingleLine(TitleOf(options)), CardTextMaxLength));
            if (options.WebsiteUrl is { IsAbsoluteUri: true } website)
                w.WriteString("websiteUrl", website.AbsoluteUri);

            w.WriteStartArray("remotes");
            w.WriteStartObject();
            w.WriteString("type", "streamable-http");
            if (options.PublicBaseUrl is null)
            {
                // The card schema only accepts absolute or {variable}-templated URLs, so without a
                // configured origin the endpoint is templated rather than guessed from Host.
                w.WriteString("url", "{" + BaseUrlVariable + "}" + NormalizePath(options.McpEndpointPath, "/mcp"));
                w.WriteStartObject("variables");
                w.WriteStartObject(BaseUrlVariable);
                w.WriteString("description", "Origin the server card was fetched from, for example https://api.example.com");
                w.WriteBoolean("isRequired", true);
                w.WriteString("format", "string");
                w.WriteString("placeholder", "https://api.example.com");
                w.WriteEndObject();
                w.WriteEndObject();
            }
            else
            {
                w.WriteString("url", McpEndpointUrl(options));
            }

            if (options.SupportedProtocolVersions is { Count: > 0 } versions)
            {
                w.WriteStartArray("supportedProtocolVersions");
                foreach (var v in versions)
                    if (!string.IsNullOrWhiteSpace(v)) w.WriteStringValue(v.Trim());
                w.WriteEndArray();
            }
            w.WriteEndObject();
            w.WriteEndArray();

            if (options.IncludeToolsInServerCardMeta)
            {
                w.WriteStartObject("_meta");
                w.WriteStartArray(ToolsMetaKey);
                foreach (var tool in included)
                {
                    w.WriteStartObject();
                    w.WriteString("name", tool.Name);
                    if (!string.IsNullOrWhiteSpace(tool.Title)) w.WriteString("title", tool.Title);
                    if (!string.IsNullOrWhiteSpace(tool.Description)) w.WriteString("description", tool.Description);
                    if (!string.IsNullOrWhiteSpace(tool.Category)) w.WriteString("category", tool.Category!.Trim());
                    w.WriteStartObject("annotations");
                    w.WriteBoolean("readOnlyHint", tool.ReadOnly);
                    w.WriteBoolean("destructiveHint", tool.Destructive);
                    w.WriteEndObject();
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }

            w.WriteEndObject();
        });
    }

    /// <summary>Builds the AI Catalog JSON with a single entry pointing at the Server Card.</summary>
    /// <exception cref="InvalidOperationException"><see cref="McpDiscoveryOptions.ServerName"/> is missing or invalid.</exception>
    public static string BuildAiCatalog(McpDiscoveryOptions options)
    {
        if (options is null) throw new ArgumentNullException(nameof(options));
        var name = RequireServerName(options);
        var identifier = string.IsNullOrWhiteSpace(options.AiCatalogIdentifier)
            ? CatalogIdentifierFor(name)
            : options.AiCatalogIdentifier!.Trim();

        return WriteJson(w =>
        {
            w.WriteStartObject();
            w.WriteString("specVersion", "1.0");
            w.WriteStartArray("entries");
            w.WriteStartObject();
            w.WriteString("identifier", identifier);
            w.WriteString("type", ServerCardMediaType);
            w.WriteString("url", ResolveUrl(options, ServerCardPathOf(options)));
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteEndObject();
        });
    }

    /// <summary>Builds the <c>llms.txt</c> markdown: tools grouped by category, plus how to connect.</summary>
    public static string BuildLlmsTxt(IReadOnlyList<McpToolDescriptor> tools, McpDiscoveryOptions options)
    {
        if (tools is null) throw new ArgumentNullException(nameof(tools));
        if (options is null) throw new ArgumentNullException(nameof(options));
        var included = FilterTools(tools, options);
        var mcpUrl = McpEndpointUrl(options);
        var linkUrl = EscapeLinkDestination(mcpUrl);

        var sb = new StringBuilder();
        sb.Append("# ").Append(EscapeMarkdown(SingleLine(TitleOf(options)))).Append('\n').Append('\n');

        foreach (var line in DescriptionOf(options, included.Count).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            sb.Append(trimmed.Length == 0 ? ">" : "> " + EscapeMarkdown(trimmed)).Append('\n');
        }
        sb.Append('\n');

        sb.Append("This API is an MCP (Model Context Protocol) server. Connect an MCP client to the Streamable HTTP endpoint at ")
          .Append(MarkdownLink(mcpUrl))
          .Append(" and call `tools/list` for the authoritative tool list and input schemas. ")
          .Append("Each tool below is invoked with `tools/call` on that endpoint.\n");
        if (options.EnableServerCard && !string.IsNullOrWhiteSpace(options.ServerName))
        {
            sb.Append("Connection metadata is published as an MCP Server Card at ")
              .Append(MarkdownLink(ResolveUrl(options, ServerCardPathOf(options)))).Append(".\n");
        }

        var groups = included
            .GroupBy(t => string.IsNullOrWhiteSpace(t.Category) ? null : SingleLine(t.Category!), StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key is null ? 1 : 0)
            .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var anyCategorized = groups.Any(g => g.Key is not null);

        foreach (var group in groups)
        {
            var heading = group.Key ?? (anyCategorized ? "Other tools" : "Tools");
            sb.Append('\n').Append("## ").Append(EscapeMarkdown(heading)).Append('\n').Append('\n');
            foreach (var tool in group.OrderByDescending(t => t.Priority).ThenBy(t => t.Name, StringComparer.Ordinal))
            {
                sb.Append("- [").Append(EscapeMarkdown(SingleLine(tool.Name))).Append("](").Append(linkUrl).Append(')');
                var notes = new List<string>(3);
                var description = string.IsNullOrWhiteSpace(tool.Description) ? tool.Title : tool.Description;
                if (!string.IsNullOrWhiteSpace(description)) notes.Add(EscapeMarkdown(SingleLine(description!)));
                var keywords = (tool.Keywords ?? Array.Empty<string>()).Where(k => !string.IsNullOrWhiteSpace(k)).Select(k => SingleLine(k)).ToList();
                if (keywords.Count > 0) notes.Add("(keywords: " + EscapeMarkdown(string.Join(", ", keywords)) + ")");
                if (tool.Destructive) notes.Add("[destructive]");
                else if (tool.ReadOnly) notes.Add("[read-only]");
                if (notes.Count > 0) sb.Append(": ").Append(string.Join(" ", notes));
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    // ---------------------------------------------------------------------------------------
    // Shared helpers (internal so the endpoint mapper and tests can reuse them)
    // ---------------------------------------------------------------------------------------

    internal static string ServerCardPathOf(McpDiscoveryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServerCardPath))
            return NormalizePath(options.ServerCardPath, "/mcp/server-card");
        var mcp = NormalizePath(options.McpEndpointPath, "/mcp");
        return (mcp == "/" ? string.Empty : mcp) + "/server-card";
    }

    internal static string NormalizePath(string? path, string fallback)
    {
        var p = string.IsNullOrWhiteSpace(path) ? fallback : path!.Trim();
        if (!p.StartsWith("/", StringComparison.Ordinal)) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        return p.Length == 0 ? "/" : p;
    }

    internal static string McpEndpointUrl(McpDiscoveryOptions options)
        => ResolveUrl(options, NormalizePath(options.McpEndpointPath, "/mcp"));

    /// <summary>Absolute URL when a public base is configured, otherwise the root-relative path.</summary>
    internal static string ResolveUrl(McpDiscoveryOptions options, string path)
    {
        if (options.PublicBaseUrl is not { IsAbsoluteUri: true } baseUrl) return path;
        var left = baseUrl.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return left + path;
    }

    /// <summary>
    /// True when <paramref name="name"/> is a valid Server Card name: reverse-DNS namespace, one
    /// slash, server name (<c>^[a-zA-Z0-9.-]+/[a-zA-Z0-9._-]+$</c>, 3 to 200 characters).
    /// </summary>
    public static bool IsValidServerName(string? name)
    {
        if (name is null || name.Length < 3 || name.Length > 200) return false;
        var slash = name.IndexOf('/');
        if (slash <= 0 || slash == name.Length - 1 || name.IndexOf('/', slash + 1) >= 0) return false;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i == slash) continue;
            var ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '.' || c == '-'
                     || (i > slash && c == '_');
            if (!ok) return false;
        }
        return true;
    }

    private static string RequireServerName(McpDiscoveryOptions options)
    {
        var name = options.ServerName?.Trim();
        if (!IsValidServerName(name))
        {
            throw new InvalidOperationException(
                "McpDiscoveryOptions.ServerName must be a reverse-DNS name with exactly one slash, for example " +
                "'com.example/orders' (letters, digits, '.', '-'; '_' also allowed after the slash; 3 to 200 characters). " +
                "It is required for the MCP Server Card and AI Catalog; set it, or disable EnableServerCard and EnableAiCatalog. " +
                $"Got: '{options.ServerName}'.");
        }
        return name!;
    }

    /// <summary><c>com.example/orders</c> becomes <c>urn:air:example.com:mcp:orders</c>.</summary>
    internal static string CatalogIdentifierFor(string serverName)
    {
        var slash = serverName.IndexOf('/');
        var labels = serverName.Substring(0, slash).Split('.');
        Array.Reverse(labels);
        return "urn:air:" + string.Join(".", labels).ToLowerInvariant() + ":mcp:" + serverName.Substring(slash + 1);
    }

    private static List<McpToolDescriptor> FilterTools(IReadOnlyList<McpToolDescriptor> tools, McpDiscoveryOptions options)
    {
        var list = new List<McpToolDescriptor>(tools.Count);
        foreach (var tool in tools)
        {
            if (tool is null || string.IsNullOrWhiteSpace(tool.Name)) continue;
            if (tool.Destructive && !options.IncludeDestructiveTools) continue;
            list.Add(tool);
        }
        return list;
    }

    private static string TitleOf(McpDiscoveryOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ServerTitle)) return options.ServerTitle!.Trim();
        var name = options.ServerName?.Trim();
        if (!string.IsNullOrEmpty(name))
        {
            var slash = name!.IndexOf('/');
            var tail = slash >= 0 ? name.Substring(slash + 1) : name;
            if (tail.Length > 0) return tail;
        }
        return "MCP Server";
    }

    private static string DescriptionOf(McpDiscoveryOptions options, int toolCount)
        => !string.IsNullOrWhiteSpace(options.Description)
            ? options.Description!.Trim()
            : $"MCP server exposing {toolCount} API {(toolCount == 1 ? "tool" : "tools")} to AI agents.";

    /// <summary>Collapses all whitespace runs (including newlines and tabs) into single spaces.</summary>
    internal static string SingleLine(string value)
    {
        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { pendingSpace = sb.Length > 0; continue; }
            if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string Shorten(string value, int max)
    {
        if (value.Length <= max) return value;
        var cut = value.LastIndexOf(' ', max - 1);
        if (cut < max / 2) cut = max - 1;
        // Never split a surrogate pair: Utf8JsonWriter rejects a lone high surrogate.
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1])) cut--;
        return value.Substring(0, cut).TrimEnd() + "…";
    }

    /// <summary>Backslash-escapes the markdown characters that could change a line's structure.</summary>
    internal static string EscapeMarkdown(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            if (c is '\\' or '[' or ']' or '*' or '`' or '<' or '>') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Percent-encodes characters that would terminate a markdown link destination.</summary>
    internal static string EscapeLinkDestination(string url)
        => url.Replace(" ", "%20").Replace("(", "%28").Replace(")", "%29").Replace("<", "%3C").Replace(">", "%3E");

    /// <summary><c>[url](url)</c>; unlike an autolink this also works for relative URLs.</summary>
    private static string MarkdownLink(string url)
        => "[" + EscapeMarkdown(url) + "](" + EscapeLinkDestination(url) + ")";

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, WriterOptions))
        {
            write(writer);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
