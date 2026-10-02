using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace McpIt;

/// <summary>
/// Extension method that serves agent discovery documents (MCP Server Card, AI Catalog,
/// <c>llms.txt</c>) built from the tool catalog, so AI agents and crawlers can find the MCP
/// server and its tools without connecting first.
/// </summary>
public static class McpDiscoveryEndpoints
{
    /// <summary>
    /// Maps the agent discovery documents:
    /// <list type="bullet">
    /// <item><description>MCP Server Card at <c>{McpEndpointPath}/server-card</c> (default <c>/mcp/server-card</c>), <c>application/mcp-server-card+json</c>.</description></item>
    /// <item><description>AI Catalog at <c>/.well-known/ai-catalog.json</c> pointing at the card, <c>application/ai-catalog+json</c>.</description></item>
    /// <item><description><c>/llms.txt</c> listing the tools grouped by category, <c>text/plain; charset=utf-8</c>.</description></item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>
    /// The documents are built once, here, and served as constant bytes with a SHA-256
    /// <c>ETag</c>, <c>Cache-Control: public, max-age=...</c>, <c>If-None-Match</c> / 304 support,
    /// <c>HEAD</c>, and the open CORS headers the Server Card draft requires (the documents are
    /// public, read-only metadata). No runtime serialization or reflection is involved, so this
    /// is Native AOT compatible.
    /// </para>
    /// <para>
    /// The Server Card format is a draft MCP extension (SEP-2127) as of October 2026; see
    /// <see cref="McpDiscoveryDocuments"/>.
    /// </para>
    /// <para>
    /// No authorization is applied: discovery documents are meant to be public, and
    /// <c>RequireAuthorization</c> on the MCP endpoint itself still protects the tools. To
    /// restrict them anyway, call <c>.RequireAuthorization()</c> on the returned group. Pass the
    /// generated catalog:
    /// <code>
    /// app.MapMcp("/mcp");
    /// app.MapMcpDiscovery(McpIt.Generated.McpItToolCatalog.Tools, o =>
    /// {
    ///     o.ServerName = "com.example/orders";
    ///     o.ServerTitle = "Orders API";
    ///     o.Description = "Look up, create and cancel customer orders.";
    ///     o.PublicBaseUrl = new Uri("https://api.example.com/");
    /// });
    /// </code>
    /// </para>
    /// </remarks>
    /// <param name="endpoints">The <see cref="IEndpointRouteBuilder"/> to add the routes to.</param>
    /// <param name="tools">The tool catalog, normally <c>McpIt.Generated.McpItToolCatalog.Tools</c>.</param>
    /// <param name="configure">Optional configuration of <see cref="McpDiscoveryOptions"/>.</param>
    /// <returns>A <see cref="RouteGroupBuilder"/> containing all discovery endpoints, for further conventions.</returns>
    /// <exception cref="InvalidOperationException">
    /// The Server Card or AI Catalog is enabled but <see cref="McpDiscoveryOptions.ServerName"/> is missing or invalid.
    /// </exception>
    public static RouteGroupBuilder MapMcpDiscovery(
        this IEndpointRouteBuilder endpoints,
        IReadOnlyList<McpToolDescriptor> tools,
        Action<McpDiscoveryOptions>? configure = null)
    {
        if (endpoints is null) throw new ArgumentNullException(nameof(endpoints));
        if (tools is null) throw new ArgumentNullException(nameof(tools));

        var options = new McpDiscoveryOptions();
        configure?.Invoke(options);

        var maxAge = options.CacheMaxAge < TimeSpan.Zero ? TimeSpan.Zero : options.CacheMaxAge;
        var cacheControl = "public, max-age=" + ((long)maxAge.TotalSeconds).ToString(CultureInfo.InvariantCulture);

        // Build every document before mapping anything, so a configuration error leaves no
        // half-registered routes behind.
        var documents = new List<(string Path, StaticDocument Document)>(3);
        if (options.EnableServerCard)
        {
            documents.Add((McpDiscoveryDocuments.ServerCardPathOf(options),
                new StaticDocument(McpDiscoveryDocuments.BuildServerCard(tools, options), McpDiscoveryDocuments.ServerCardMediaType, cacheControl)));
            if (options.EnableAiCatalog)
            {
                documents.Add((McpDiscoveryDocuments.NormalizePath(options.AiCatalogPath, "/.well-known/ai-catalog.json"),
                    new StaticDocument(McpDiscoveryDocuments.BuildAiCatalog(options), McpDiscoveryDocuments.AiCatalogMediaType, cacheControl)));
            }
        }
        if (options.EnableLlmsTxt)
        {
            documents.Add((McpDiscoveryDocuments.NormalizePath(options.LlmsTxtPath, "/llms.txt"),
                new StaticDocument(McpDiscoveryDocuments.BuildLlmsTxt(tools, options), McpDiscoveryDocuments.LlmsTxtMediaType, cacheControl)));
        }

        var group = endpoints.MapGroup(string.Empty);
        foreach (var (path, document) in documents)
        {
            // Explicit RequestDelegate: bypasses the minimal-API parameter binder (reflection).
            group.MapMethods(path, Methods, document.HandleAsync);
        }
        return group;
    }

    private static readonly string[] Methods = { HttpMethods.Get, HttpMethods.Head, HttpMethods.Options };

    /// <summary>A precomputed document plus the headers it is served with.</summary>
    private sealed class StaticDocument
    {
        private readonly byte[] _body;
        private readonly string _contentType;
        private readonly string _cacheControl;
        private readonly EntityTagHeaderValue _etag;
        private readonly string _etagText;

        public StaticDocument(string content, string contentType, string cacheControl)
        {
            _body = Encoding.UTF8.GetBytes(content);
            _contentType = contentType;
            _cacheControl = cacheControl;
            // Strong validator: first 128 bits of SHA-256 over the exact bytes served.
            var hash = SHA256.HashData(_body);
            _etagText = "\"" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant() + "\"";
            _etag = new EntityTagHeaderValue(_etagText);
        }

        public Task HandleAsync(HttpContext ctx)
        {
            var response = ctx.Response;
            var headers = response.Headers;
            headers[HeaderNames.AccessControlAllowOrigin] = "*";
            headers[HeaderNames.AccessControlAllowMethods] = "GET, HEAD, OPTIONS";
            headers[HeaderNames.AccessControlAllowHeaders] = "Accept, Content-Type, If-None-Match";
            headers[HeaderNames.AccessControlExposeHeaders] = "ETag";

            if (HttpMethods.IsOptions(ctx.Request.Method))
            {
                headers[HeaderNames.Allow] = "GET, HEAD, OPTIONS";
                headers[HeaderNames.AccessControlMaxAge] = "86400";
                response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            }

            headers[HeaderNames.ETag] = _etagText;
            headers[HeaderNames.CacheControl] = _cacheControl;
            headers[HeaderNames.XContentTypeOptions] = "nosniff";

            if (MatchesIfNoneMatch(ctx.Request))
            {
                response.StatusCode = StatusCodes.Status304NotModified;
                return Task.CompletedTask;
            }

            response.StatusCode = StatusCodes.Status200OK;
            response.ContentType = _contentType;
            response.ContentLength = _body.Length;
            if (HttpMethods.IsHead(ctx.Request.Method)) return Task.CompletedTask;
            return response.Body.WriteAsync(_body, 0, _body.Length, ctx.RequestAborted);
        }

        private bool MatchesIfNoneMatch(HttpRequest request)
        {
            if (!request.Headers.ContainsKey(HeaderNames.IfNoneMatch)) return false;
            var tags = request.GetTypedHeaders().IfNoneMatch;
            foreach (var tag in tags)
            {
                // If-None-Match uses the weak comparison function (RFC 9110 13.1.2).
                if (tag.Equals(EntityTagHeaderValue.Any) || tag.Compare(_etag, useStrongComparison: false))
                    return true;
            }
            return false;
        }
    }
}
