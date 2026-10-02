using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace McpIt.Runtime.Tests;

/// <summary>
/// End-to-end tests for <see cref="McpDiscoveryEndpoints.MapMcpDiscovery"/>: a real in-process
/// <see cref="WebApplication"/> on a loopback Kestrel port (no TestHost package needed).
/// </summary>
public class McpDiscoveryEndpointsTests
{
    private static async Task<(WebApplication App, HttpClient Client)> StartAsync(Action<McpDiscoveryOptions>? configure = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var app = builder.Build();
        app.MapMcpDiscovery(McpDiscoveryDocumentsTests.SampleTools(), o =>
        {
            o.ServerName = "com.example/orders";
            o.ServerTitle = "Orders API";
            configure?.Invoke(o);
        });
        await app.StartAsync();
        var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };
        return (app, client);
    }

    [Fact]
    public async Task Serves_server_card_with_media_type_cache_and_cors_headers()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var response = await client.GetAsync("/mcp/server-card");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(McpDiscoveryDocuments.ServerCardMediaType, response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("*", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("ETag", response.Headers.GetValues("Access-Control-Expose-Headers").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal(TimeSpan.FromHours(1), response.Headers.CacheControl.MaxAge);
        Assert.NotNull(response.Headers.ETag);
        Assert.False(response.Headers.ETag!.IsWeak);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("com.example/orders", doc.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Server_card_does_not_echo_spoofed_host_header()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var request = new HttpRequestMessage(HttpMethod.Get, "/mcp/server-card");
        request.Headers.Host = "evil.example";
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("evil.example", body);
        Assert.Contains("{base_url}/mcp", body);
    }

    [Fact]
    public async Task Serves_llms_txt_as_utf8_text()
    {
        var (app, client) = await StartAsync(o => o.PublicBaseUrl = new Uri("https://api.example.com"));
        await using var _ = app;
        using var __ = client;

        using var response = await client.GetAsync("/llms.txt");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/plain", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("utf-8", response.Content.Headers.ContentType.CharSet);
        Assert.StartsWith("# Orders API\n", body);
        Assert.Contains("## Orders", body);
        Assert.Contains("- [orders_get](https://api.example.com/mcp):", body);
    }

    [Fact]
    public async Task Serves_ai_catalog_at_well_known_path()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var response = await client.GetAsync("/.well-known/ai-catalog.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(McpDiscoveryDocuments.AiCatalogMediaType, response.Content.Headers.ContentType!.MediaType);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("/mcp/server-card", doc.RootElement.GetProperty("entries")[0].GetProperty("url").GetString());
    }

    [Fact]
    public async Task Returns_304_for_matching_if_none_match_and_200_otherwise()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var first = await client.GetAsync("/llms.txt");
        var etag = first.Headers.ETag!;

        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/llms.txt");
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"other\""));
        conditional.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag.Tag, isWeak: true));
        using var notModified = await client.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Equal(etag, notModified.Headers.ETag);
        Assert.Empty(await notModified.Content.ReadAsByteArrayAsync());

        using var stale = new HttpRequestMessage(HttpMethod.Get, "/llms.txt");
        stale.Headers.IfNoneMatch.Add(new EntityTagHeaderValue("\"stale\""));
        using var fresh = await client.SendAsync(stale);
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
    }

    [Fact]
    public async Task Etag_is_stable_per_content_and_differs_between_documents()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var a1 = await client.GetAsync("/llms.txt");
        using var a2 = await client.GetAsync("/llms.txt");
        using var b = await client.GetAsync("/mcp/server-card");

        Assert.Equal(a1.Headers.ETag, a2.Headers.ETag);
        Assert.NotEqual(a1.Headers.ETag, b.Headers.ETag);
    }

    [Fact]
    public async Task Head_returns_headers_without_body_and_options_answers_preflight()
    {
        var (app, client) = await StartAsync();
        await using var _ = app;
        using var __ = client;

        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, "/mcp/server-card"));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.True(head.Content.Headers.ContentLength > 0);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var options = await client.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/mcp/server-card"));
        Assert.Equal(HttpStatusCode.NoContent, options.StatusCode);
        Assert.Contains("If-None-Match", options.Headers.GetValues("Access-Control-Allow-Headers").Single());
    }

    [Fact]
    public async Task Toggles_and_custom_paths_control_which_routes_exist()
    {
        var (app, client) = await StartAsync(o =>
        {
            o.EnableAiCatalog = false;
            o.ServerCardPath = "/discovery/card";
            o.LlmsTxtPath = "docs/llms.txt";
        });
        await using var _ = app;
        using var __ = client;

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/discovery/card")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/docs/llms.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mcp/server-card")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/llms.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/ai-catalog.json")).StatusCode);
    }

    [Fact]
    public async Task Disabling_server_card_also_drops_ai_catalog_and_needs_no_server_name()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        app.MapMcpDiscovery(McpDiscoveryDocumentsTests.SampleTools(), o => o.EnableServerCard = false);
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(app.Urls.First()) };

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/llms.txt")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/mcp/server-card")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.well-known/ai-catalog.json")).StatusCode);
    }

    [Fact]
    public void Missing_server_name_throws_at_map_time_and_registers_nothing()
    {
        var app = WebApplication.CreateBuilder().Build();
        var routes = (IEndpointRouteBuilder)app;

        Assert.Throws<InvalidOperationException>(() => app.MapMcpDiscovery(McpDiscoveryDocumentsTests.SampleTools()));
        Assert.Empty(routes.DataSources.SelectMany(d => d.Endpoints));
    }

    [Fact]
    public async Task Returned_group_accepts_conventions_for_all_discovery_endpoints()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var group = app.MapMcpDiscovery(McpDiscoveryDocumentsTests.SampleTools(), o => o.ServerName = "com.example/orders");
        group.WithMetadata("discovery-marker");

        var routes = (IEndpointRouteBuilder)app;
        var endpoints = routes.DataSources.SelectMany(d => d.Endpoints).OfType<RouteEndpoint>().ToList();

        Assert.Equal(3, endpoints.Count);
        Assert.All(endpoints, e => Assert.Contains("discovery-marker", e.Metadata.OfType<string>()));
        Assert.Equal(
            new[] { "/.well-known/ai-catalog.json", "/llms.txt", "/mcp/server-card" },
            endpoints.Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/')).OrderBy(p => p, StringComparer.Ordinal));
    }
}
