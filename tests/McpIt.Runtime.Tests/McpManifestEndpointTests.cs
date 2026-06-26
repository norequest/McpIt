using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace McpIt.Runtime.Tests;

/// <summary>
/// Tests for McpManifestEndpoint.MapMcpManifest.
///
/// Uses a lightweight IEndpointRouteBuilder implementation to avoid a TestHost /
/// Mvc.Testing dependency. MapGet (RequestDelegate overload) stores the delegate directly in
/// a RouteEndpointDataSource and does NOT invoke NewApplicationBuilder, so a minimal service
/// provider with AddRoutingCore() is sufficient to build the endpoints.
/// </summary>
public class McpManifestEndpointTests
{
    // Minimal IEndpointRouteBuilder that captures endpoint data sources.
    // NewApplicationBuilder is not called by MapGet when the handler is a RequestDelegate.
    private sealed class SimpleRouteBuilder : IEndpointRouteBuilder
    {
        private readonly ServiceProvider _sp;

        public SimpleRouteBuilder()
        {
            var services = new ServiceCollection();
            services.AddRoutingCore();
            _sp = services.BuildServiceProvider();
        }

        public IServiceProvider ServiceProvider => _sp;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(_sp);
    }

    // Builds a route builder, registers MapMcpManifest, and returns the RequestDelegate.
    private static async Task<RequestDelegate> GetHandlerAsync(
        string manifestJson, string pattern = "/mcp/manifest")
    {
        var builder = new SimpleRouteBuilder();
        builder.MapMcpManifest(manifestJson, pattern);

        var handler = builder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .Select(e => e.RequestDelegate)
            .FirstOrDefault(rd => rd is not null);

        return handler ?? throw new InvalidOperationException("No endpoint was registered.");
    }

    private static DefaultHttpContext CreateContext()
    {
        var ctx = new DefaultHttpContext();
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Handler_sets_application_json_content_type()
    {
        const string json = "{\"aggregateHash\":\"abc\",\"tools\":[]}";
        var handler = await GetHandlerAsync(json);
        var ctx = CreateContext();

        await handler(ctx);

        Assert.Equal("application/json; charset=utf-8", ctx.Response.ContentType);
    }

    [Fact]
    public async Task Handler_writes_exact_manifest_json_as_response_body()
    {
        const string json =
            "{\"aggregateHash\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"tools\":[]}";
        var handler = await GetHandlerAsync(json);
        var ctx = CreateContext();

        await handler(ctx);

        ctx.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(ctx.Response.Body);
        Assert.Equal(json, await reader.ReadToEndAsync());
    }

    [Fact]
    public void MapMcpManifest_registers_endpoint_at_the_specified_pattern()
    {
        var builder = new SimpleRouteBuilder();
        const string pattern = "/api/tool-manifest";
        builder.MapMcpManifest("{\"aggregateHash\":\"x\",\"tools\":[]}", pattern);

        var routeEndpoint = builder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault();

        Assert.NotNull(routeEndpoint);
        Assert.Equal(pattern.TrimStart('/'), routeEndpoint!.RoutePattern.RawText?.TrimStart('/'));
    }

    [Fact]
    public void MapMcpManifest_uses_default_pattern_when_none_is_specified()
    {
        var builder = new SimpleRouteBuilder();
        builder.MapMcpManifest("{\"aggregateHash\":\"x\",\"tools\":[]}");

        var routeEndpoint = builder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .OfType<RouteEndpoint>()
            .FirstOrDefault();

        Assert.NotNull(routeEndpoint);
        Assert.Equal("mcp/manifest", routeEndpoint!.RoutePattern.RawText?.TrimStart('/'));
    }

    [Fact]
    public void MapMcpManifest_returns_IEndpointConventionBuilder_without_throwing()
    {
        var builder = new SimpleRouteBuilder();

        // Must not throw; must return a valid IEndpointConventionBuilder.
        var result = builder.MapMcpManifest("{\"aggregateHash\":\"x\",\"tools\":[]}");

        Assert.NotNull(result);
    }
}
