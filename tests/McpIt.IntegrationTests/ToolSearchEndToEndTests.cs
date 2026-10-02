using System.Text.Json;
using McpIt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpIt.IntegrationTests;

// Hosts a real MCP server (Streamable HTTP at /mcp, in-memory TestServer) with only the
// search_tools meta-tool registered, and talks to it with the SDK's own MCP client, so
// tools/list and tools/call go through the full JSON-RPC pipeline.
public sealed class ToolSearchEndToEndTests : IAsyncLifetime
{
    private static readonly McpToolDescriptor[] Catalog =
    [
        new("listOrders", "List Orders", "Lists orders, newest first.", "GET", "/api/orders", "orders", [], 0, ["status"], true, false),
        new("getOrder", "Get Order by ID", "Gets one order.", "GET", "/api/orders/{id}", "orders", [], 0, ["id"], true, false),
        new("cancelOrder", "Cancel Order", "Cancels an order that has not shipped.", "POST", "/api/orders/{id}/cancel", "orders", [], 0, ["id", "reason"], false, true),
        new("deleteOrder", "Delete Order", "Permanently deletes an order.", "DELETE", "/api/orders/{id}", "orders", [], 0, ["id"], false, true),
        new("listCustomers", "List Customers", "Lists customers.", "GET", "/api/customers", "customers", [], 0, ["page"], true, false),
        new("refundInvoice", "Refund Invoice", "Refunds a paid invoice.", "POST", "/api/invoices/{id}/refund", "billing", ["money back"], 0, ["id", "amount"], false, true),
    ];

    private WebApplication? _app;
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolSearch(Catalog, o => o.MaxResults = 3);

        _app = builder.Build();
        _app.MapMcp("/mcp");
        await _app.StartAsync();

        var http = _app.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
            },
            http,
            loggerFactory: null,
            ownsHttpClient: false);

        _client = await McpClient.CreateAsync(transport);
    }

    public async Task DisposeAsync()
    {
        if (_client is not null) await _client.DisposeAsync();
        if (_app is not null) await _app.DisposeAsync();
    }

    [Fact]
    public async Task Tools_list_exposes_search_tools_with_schema_and_annotations()
    {
        var tools = await _client!.ListToolsAsync();

        var search = Assert.Single(tools, t => t.Name == "search_tools");
        var protocol = search.ProtocolTool;
        Assert.Contains("FIRST", protocol.Description);
        Assert.Equal("string", protocol.InputSchema.GetProperty("properties").GetProperty("query").GetProperty("type").GetString());
        Assert.True(protocol.Annotations?.ReadOnlyHint);
        Assert.False(protocol.Annotations?.DestructiveHint);
        Assert.True(protocol.Annotations?.IdempotentHint);
        Assert.False(protocol.Annotations?.OpenWorldHint);
    }

    [Fact]
    public async Task Tools_call_returns_ranked_results()
    {
        var result = await _client!.CallToolAsync(
            "search_tools",
            new Dictionary<string, object?> { ["query"] = "cancel my order" });

        Assert.NotEqual(true, result.IsError);
        using var doc = JsonDocument.Parse(TextOf(result));
        var results = doc.RootElement.GetProperty("results");

        Assert.InRange(results.GetArrayLength(), 1, 3); // capped by MaxResults = 3
        Assert.Equal("cancelOrder", results[0].GetProperty("name").GetString());
        Assert.True(results[0].GetProperty("destructive").GetBoolean());
    }

    [Fact]
    public async Task Tools_call_honours_limit_and_keywords()
    {
        var result = await _client!.CallToolAsync(
            "search_tools",
            new Dictionary<string, object?> { ["query"] = "give the money back", ["limit"] = 1 });

        using var doc = JsonDocument.Parse(TextOf(result));
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(1, results.GetArrayLength());
        Assert.Equal("refundInvoice", results[0].GetProperty("name").GetString());
    }

    [Fact]
    public async Task Tools_call_without_query_is_a_tool_error()
    {
        var result = await _client!.CallToolAsync("search_tools", new Dictionary<string, object?>());

        Assert.True(result.IsError);
        Assert.Contains("query", TextOf(result));
    }

    private static string TextOf(CallToolResult result) =>
        Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
}
