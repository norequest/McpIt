using System.Text.Json;

namespace McpIt.Runtime.Tests;

/// <summary>Content tests for the discovery documents built by <see cref="McpDiscoveryDocuments"/>.</summary>
public class McpDiscoveryDocumentsTests
{
    internal static McpToolDescriptor Tool(
        string name,
        string? description = null,
        string? category = null,
        string[]? keywords = null,
        int priority = 0,
        bool readOnly = false,
        bool destructive = false,
        string title = "")
        => new(name)
        {
            Title = title, Description = description, HttpMethod = readOnly ? "GET" : "POST", Route = "/" + name,
            Category = category, Keywords = keywords ?? Array.Empty<string>(), Priority = priority,
            ReadOnly = readOnly, Destructive = destructive,
        };

    internal static IReadOnlyList<McpToolDescriptor> SampleTools() => new[]
    {
        Tool("orders_get", "Gets an order by id.", "Orders", new[] { "order", "lookup" }, readOnly: true, title: "Get order"),
        Tool("orders_cancel", "Cancels an order. Cannot be undone.", "Orders", new[] { "order", "cancel" }, destructive: true, title: "Cancel order"),
        Tool("health_ping", "Checks that the API is up.", null, readOnly: true),
    };

    private static McpDiscoveryOptions Options(Action<McpDiscoveryOptions>? configure = null)
    {
        var o = new McpDiscoveryOptions
        {
            ServerName = "com.example/orders",
            ServerTitle = "Orders API",
            Description = "Look up and cancel customer orders.",
        };
        configure?.Invoke(o);
        return o;
    }

    // ---------------------------------------------------------------- Server Card

    [Fact]
    public void ServerCard_has_draft_schema_identity_and_streamable_http_remote()
    {
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o =>
        {
            o.Version = "2.3.0";
            o.PublicBaseUrl = new Uri("https://api.example.com/");
            o.SupportedProtocolVersions = new[] { "2025-06-18", "2025-11-25" };
            o.WebsiteUrl = new Uri("https://example.com/docs");
        }));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(McpDiscoveryDocuments.ServerCardSchemaUri, root.GetProperty("$schema").GetString());
        Assert.Equal("com.example/orders", root.GetProperty("name").GetString());
        Assert.Equal("2.3.0", root.GetProperty("version").GetString());
        Assert.Equal("Orders API", root.GetProperty("title").GetString());
        Assert.Equal("Look up and cancel customer orders.", root.GetProperty("description").GetString());
        Assert.Equal("https://example.com/docs", root.GetProperty("websiteUrl").GetString());

        var remote = Assert.Single(root.GetProperty("remotes").EnumerateArray());
        Assert.Equal("streamable-http", remote.GetProperty("type").GetString());
        Assert.Equal("https://api.example.com/mcp", remote.GetProperty("url").GetString());
        Assert.False(remote.TryGetProperty("variables", out _));
        Assert.Equal(new[] { "2025-06-18", "2025-11-25" },
            remote.GetProperty("supportedProtocolVersions").EnumerateArray().Select(e => e.GetString()));

        // The draft excludes primitives from the card by default.
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("_meta", out _));
    }

    [Fact]
    public void ServerCard_without_public_base_url_uses_templated_url_never_a_host()
    {
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o => o.McpEndpointPath = "api/mcp/"));

        using var doc = JsonDocument.Parse(json);
        var remote = doc.RootElement.GetProperty("remotes")[0];
        Assert.Equal("{base_url}/api/mcp", remote.GetProperty("url").GetString());
        var variable = remote.GetProperty("variables").GetProperty("base_url");
        Assert.True(variable.GetProperty("isRequired").GetBoolean());
    }

    [Fact]
    public void ServerCard_public_base_url_keeps_path_base()
    {
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o => o.PublicBaseUrl = new Uri("https://example.com/tenant-a")));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("https://example.com/tenant-a/mcp", doc.RootElement.GetProperty("remotes")[0].GetProperty("url").GetString());
    }

    [Fact]
    public void ServerCard_escapes_quotes_newlines_and_keeps_unicode_readable()
    {
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o =>
        {
            o.ServerTitle = "The \"Best\" API";
            o.Description = "Line one\nline \\two\t ლოტი";
            o.IncludeToolsInServerCardMeta = true;
        }));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("The \"Best\" API", root.GetProperty("title").GetString());
        // Card description is a single line (whitespace collapsed); backslash survives round trip.
        Assert.Equal("Line one line \\two ლოტი", root.GetProperty("description").GetString());
        Assert.Contains("ლოტი", json);
        Assert.Contains("\\\"Best\\\"", json);
    }

    [Fact]
    public void ServerCard_shortens_long_description_and_title_to_schema_limit()
    {
        var longText = string.Join(" ", Enumerable.Repeat("word", 60));
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o =>
        {
            o.Description = longText;
            o.ServerTitle = new string('x', 150);
        }));

        using var doc = JsonDocument.Parse(json);
        var description = doc.RootElement.GetProperty("description").GetString()!;
        Assert.True(description.Length <= 100, $"length {description.Length}");
        Assert.EndsWith("\u2026", description);
        Assert.True(doc.RootElement.GetProperty("title").GetString()!.Length <= 100);
    }

    [Fact]
    public void ServerCard_default_description_counts_tools()
    {
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), new McpDiscoveryOptions { ServerName = "com.example/orders" });

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("MCP server exposing 3 API tools to AI agents.", doc.RootElement.GetProperty("description").GetString());
        Assert.Equal("orders", doc.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public void ServerCard_meta_lists_tools_with_annotations_when_opted_in()
    {
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o => o.IncludeToolsInServerCardMeta = true));

        using var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.GetProperty("_meta").GetProperty(McpDiscoveryDocuments.ToolsMetaKey).EnumerateArray().ToList();
        Assert.Equal(3, tools.Count);
        var cancel = tools.Single(t => t.GetProperty("name").GetString() == "orders_cancel");
        Assert.Equal("Cancel order", cancel.GetProperty("title").GetString());
        Assert.Equal("Orders", cancel.GetProperty("category").GetString());
        Assert.True(cancel.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean());
        Assert.False(cancel.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
        var ping = tools.Single(t => t.GetProperty("name").GetString() == "health_ping");
        Assert.False(ping.TryGetProperty("title", out _));
        Assert.True(ping.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean());
    }

    [Fact]
    public void ServerCard_meta_respects_IncludeDestructiveTools()
    {
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o =>
        {
            o.IncludeToolsInServerCardMeta = true;
            o.IncludeDestructiveTools = false;
        }));

        Assert.DoesNotContain("orders_cancel", json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("orders")]
    [InlineData("com.example/orders/v2")]
    [InlineData("/orders")]
    [InlineData("com.example/")]
    [InlineData("com_example/orders")]
    [InlineData("com.example/or ders")]
    public void ServerCard_rejects_invalid_server_name(string? name)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => McpDiscoveryDocuments.BuildServerCard(SampleTools(), new McpDiscoveryOptions { ServerName = name }));
        Assert.Contains("ServerName", ex.Message);
    }

    [Theory]
    [InlineData("com.example/orders")]
    [InlineData("io.github.norequest/mcp_it-server.v2")]
    [InlineData("a/b")]
    public void ServerName_validation_accepts_reverse_dns_names(string name)
        => Assert.True(McpDiscoveryDocuments.IsValidServerName(name));

    // ---------------------------------------------------------------- AI Catalog

    [Fact]
    public void AiCatalog_points_at_server_card_with_derived_identifier()
    {
        var json = McpDiscoveryDocuments.BuildAiCatalog(Options(o => o.PublicBaseUrl = new Uri("https://api.example.com")));

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("1.0", doc.RootElement.GetProperty("specVersion").GetString());
        var entry = Assert.Single(doc.RootElement.GetProperty("entries").EnumerateArray());
        Assert.Equal("urn:air:example.com:mcp:orders", entry.GetProperty("identifier").GetString());
        Assert.Equal(McpDiscoveryDocuments.ServerCardMediaType, entry.GetProperty("type").GetString());
        Assert.Equal("https://api.example.com/mcp/server-card", entry.GetProperty("url").GetString());
    }

    [Fact]
    public void AiCatalog_uses_relative_url_without_base_and_honors_overrides()
    {
        var json = McpDiscoveryDocuments.BuildAiCatalog(Options(o =>
        {
            o.ServerCardPath = "/discovery/card.json";
            o.AiCatalogIdentifier = "urn:air:example.org:mcp:custom";
        }));

        using var doc = JsonDocument.Parse(json);
        var entry = doc.RootElement.GetProperty("entries")[0];
        Assert.Equal("/discovery/card.json", entry.GetProperty("url").GetString());
        Assert.Equal("urn:air:example.org:mcp:custom", entry.GetProperty("identifier").GetString());
    }

    // ---------------------------------------------------------------- llms.txt

    [Fact]
    public void LlmsTxt_has_h1_blockquote_connect_section_and_category_groups()
    {
        var text = McpDiscoveryDocuments.BuildLlmsTxt(SampleTools(), Options());
        var lines = text.Split('\n');

        Assert.Equal("# Orders API", lines[0]);
        Assert.Equal("> Look up and cancel customer orders.", lines[2]);
        Assert.Contains("Streamable HTTP endpoint at [/mcp](/mcp)", text);
        Assert.Contains("MCP Server Card at [/mcp/server-card](/mcp/server-card)", text);

        // Named categories first (alphabetical), uncategorized last under "Other tools".
        var orders = Array.IndexOf(lines, "## Orders");
        var other = Array.IndexOf(lines, "## Other tools");
        Assert.True(orders > 0 && other > orders);

        Assert.Contains("- [orders_get](/mcp): Gets an order by id. (keywords: order, lookup) [read-only]", lines);
        Assert.Contains("- [orders_cancel](/mcp): Cancels an order. Cannot be undone. (keywords: order, cancel) [destructive]", lines);
        Assert.Contains("- [health_ping](/mcp): Checks that the API is up. [read-only]", lines);
        Assert.Equal('\n', text[^1]);
    }

    [Fact]
    public void LlmsTxt_single_group_is_called_tools_and_orders_by_priority_then_name()
    {
        var tools = new[]
        {
            Tool("b_tool", "B"),
            Tool("a_tool", "A"),
            Tool("z_tool", "Z", priority: 5),
        };
        var lines = McpDiscoveryDocuments.BuildLlmsTxt(tools, Options()).Split('\n');

        var heading = Array.IndexOf(lines, "## Tools");
        Assert.True(heading > 0);
        Assert.StartsWith("- [z_tool]", lines[heading + 2]);
        Assert.StartsWith("- [a_tool]", lines[heading + 3]);
        Assert.StartsWith("- [b_tool]", lines[heading + 4]);
    }

    [Fact]
    public void LlmsTxt_groups_categories_case_insensitively()
    {
        var tools = new[] { Tool("one", "1", "Billing"), Tool("two", "2", "billing ") };
        var text = McpDiscoveryDocuments.BuildLlmsTxt(tools, Options());

        Assert.Single(text.Split('\n'), l => l.StartsWith("## ", StringComparison.Ordinal));
    }

    [Fact]
    public void LlmsTxt_escapes_markdown_and_keeps_each_tool_on_one_line()
    {
        var tools = new[]
        {
            Tool("weird", "Line one\nline two\r\n# not a heading [link](x) *bold* <b>`code`",
                "Cat\nGory", new[] { "a]b" }),
        };
        var text = McpDiscoveryDocuments.BuildLlmsTxt(tools, Options(o =>
        {
            o.ServerTitle = "Title\nwith *stars*";
            o.Description = "First line\n\nThird [line]";
        }));
        var lines = text.Split('\n');

        Assert.Equal("# Title with \\*stars\\*", lines[0]);
        Assert.Equal("> First line", lines[2]);
        Assert.Equal(">", lines[3]);
        Assert.Equal("> Third \\[line\\]", lines[4]);
        Assert.Contains("## Cat Gory", lines);
        var bullet = Assert.Single(lines, l => l.StartsWith("- [weird]", StringComparison.Ordinal));
        Assert.Equal(
            "- [weird](/mcp): Line one line two # not a heading \\[link\\](x) \\*bold\\* \\<b\\>\\`code\\` (keywords: a\\]b)",
            bullet);
    }

    [Fact]
    public void LlmsTxt_uses_absolute_urls_with_public_base_url_and_encodes_link_destination()
    {
        var text = McpDiscoveryDocuments.BuildLlmsTxt(SampleTools(), Options(o =>
        {
            o.PublicBaseUrl = new Uri("https://api.example.com/");
            o.McpEndpointPath = "/mcp (beta)";
        }));

        Assert.Contains("- [orders_get](https://api.example.com/mcp%20%28beta%29):", text);
        Assert.Contains("MCP Server Card at [https://api.example.com/mcp (beta)/server-card](https://api.example.com/mcp%20%28beta%29/server-card)", text);
    }

    [Fact]
    public void LlmsTxt_can_exclude_destructive_tools()
    {
        var text = McpDiscoveryDocuments.BuildLlmsTxt(SampleTools(), Options(o => o.IncludeDestructiveTools = false));

        Assert.DoesNotContain("orders_cancel", text);
        Assert.DoesNotContain("[destructive]", text);
        Assert.Contains("orders_get", text);
    }

    [Fact]
    public void LlmsTxt_omits_server_card_line_when_card_disabled_and_works_without_server_name()
    {
        var text = McpDiscoveryDocuments.BuildLlmsTxt(SampleTools(), new McpDiscoveryOptions { EnableServerCard = false });

        Assert.StartsWith("# MCP Server\n", text);
        Assert.DoesNotContain("Server Card", text);
    }

    [Fact]
    public void LlmsTxt_falls_back_to_title_when_description_missing()
    {
        // Nameless tools cannot exist: the McpToolDescriptor constructor rejects them.
        var tools = new[] { Tool("titled", null, title: "Has a title") };
        var text = McpDiscoveryDocuments.BuildLlmsTxt(tools, Options());

        Assert.Contains("- [titled](/mcp): Has a title\n", text);
    }

    [Fact]
    public void ServerCard_shortening_never_splits_a_surrogate_pair()
    {
        // 99 chars then an astral emoji straddling the 100-char cut, no spaces to break on.
        var text = new string('a', 98) + "\U0001F600" + new string('b', 20);
        var json = McpDiscoveryDocuments.BuildServerCard(SampleTools(), Options(o => o.Description = text));

        using var doc = JsonDocument.Parse(json);
        var description = doc.RootElement.GetProperty("description").GetString()!;
        Assert.True(description.Length <= 100, $"length {description.Length}");
        Assert.False(char.IsHighSurrogate(description[^2]));
    }
}
