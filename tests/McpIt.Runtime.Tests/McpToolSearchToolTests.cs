using System.Text.Json;
using McpIt;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpIt.Runtime.Tests;

public class McpToolSearchToolTests
{
    private static McpToolSearchTool Create(Action<McpToolSearchOptions>? configure = null)
    {
        var options = new McpToolSearchOptions();
        configure?.Invoke(options);
        return new McpToolSearchTool(new McpToolIndex(McpToolRankerTests.Catalog), options);
    }

    [Fact]
    public void Protocol_tool_has_name_schema_and_safe_annotations()
    {
        var tool = Create().ProtocolTool;

        Assert.Equal("search_tools", tool.Name);
        Assert.Contains("FIRST", tool.Description);
        Assert.Contains("15 tools", tool.Description);

        var schema = tool.InputSchema;
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.Equal("string", schema.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        var limit = schema.GetProperty("properties").GetProperty("limit");
        Assert.Equal("integer", limit.GetProperty("type").GetString());
        Assert.Equal(5, limit.GetProperty("maximum").GetInt32());
        Assert.Equal(["query"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));

        Assert.NotNull(tool.Annotations);
        Assert.True(tool.Annotations!.ReadOnlyHint);
        Assert.False(tool.Annotations.DestructiveHint);
        Assert.True(tool.Annotations.IdempotentHint);
        Assert.False(tool.Annotations.OpenWorldHint);
    }

    [Fact]
    public void Options_rename_and_redescribe_the_tool()
    {
        var tool = Create(o =>
        {
            o.ToolName = "find_tool";
            o.Description = "Custom.";
            o.MaxResults = 10;
        }).ProtocolTool;

        Assert.Equal("find_tool", tool.Name);
        Assert.Equal("Custom.", tool.Description);
        Assert.Equal(10, tool.InputSchema.GetProperty("properties").GetProperty("limit").GetProperty("maximum").GetInt32());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(26)]
    public void Max_results_outside_the_cap_throws(int maxResults)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Create(o => o.MaxResults = maxResults));
    }

    [Fact]
    public void Blank_tool_name_throws()
    {
        Assert.Throws<ArgumentException>(() => Create(o => o.ToolName = " "));
    }

    [Fact]
    public void Search_returns_compact_ranked_json()
    {
        var json = Create().Search("cancel my order");

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("cancel my order", root.GetProperty("query").GetString());

        var results = root.GetProperty("results");
        Assert.InRange(results.GetArrayLength(), 1, 5);

        var first = results[0];
        Assert.Equal("cancelOrder", first.GetProperty("name").GetString());
        Assert.Equal("Cancel Order", first.GetProperty("title").GetString());
        Assert.Equal("Cancels an order that has not shipped yet.", first.GetProperty("description").GetString());
        Assert.False(first.GetProperty("readOnly").GetBoolean());
        Assert.True(first.GetProperty("destructive").GetBoolean());
        Assert.Equal(["id", "reason"], first.GetProperty("parameters").EnumerateArray().Select(e => e.GetString()));

        var score = first.GetProperty("score").GetDouble();
        Assert.True(score > 0);
        Assert.Equal(Math.Round(score, 3), score);

        // Best first.
        var scores = results.EnumerateArray().Select(r => r.GetProperty("score").GetDouble()).ToList();
        Assert.Equal(scores.OrderByDescending(s => s), scores);
    }

    [Fact]
    public void Search_writes_null_description_and_keeps_non_ascii_readable()
    {
        var index = new McpToolIndex(
        [
            new McpToolDescriptor("listLots", "აუქციონი", null, "GET", "/lots", null, [], 0, [], true, false),
        ]);
        var json = new McpToolSearchTool(index, new McpToolSearchOptions()).Search("lots");

        Assert.Contains("\"description\":null", json);
        Assert.Contains("აუქციონი", json); // not \u-escaped
    }

    [Theory]
    [InlineData(null, 5)]
    [InlineData(2, 2)]
    [InlineData(0, 1)]
    [InlineData(100, 5)]
    public void Limit_is_clamped_to_max_results(int? limit, int expected)
    {
        var json = Create().Search("order", limit);

        using var doc = JsonDocument.Parse(json);
        Assert.Equal(expected, doc.RootElement.GetProperty("results").GetArrayLength());
    }

    [Fact]
    public void No_match_returns_an_empty_results_array()
    {
        var json = Create().Search("xylophone");
        Assert.Equal("""{"query":"xylophone","results":[]}""", json);
    }

    // InvokeAsync needs a live McpServer for its RequestContext, so argument parsing and the
    // error path are covered end to end in McpIt.IntegrationTests/ToolSearchEndToEndTests.

    [Fact]
    public void WithToolSearch_registers_the_tool_in_DI()
    {
        var services = new ServiceCollection();
        services.AddMcpServer().WithToolSearch(McpToolRankerTests.Catalog, o => o.ToolName = "find");

        using var provider = services.BuildServiceProvider();
        var tools = provider.GetServices<McpServerTool>().ToList();

        var tool = Assert.Single(tools);
        Assert.IsType<McpToolSearchTool>(tool);
        Assert.Equal("find", tool.ProtocolTool.Name);
    }

    [Fact]
    public void WithToolSearch_validates_arguments()
    {
        var builder = new ServiceCollection().AddMcpServer();
        Assert.Throws<ArgumentNullException>(() => builder.WithToolSearch(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithToolSearch([], o => o.MaxResults = 99));
    }
}
