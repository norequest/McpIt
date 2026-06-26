using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace McpIt;

public sealed class HttpClientMcpEndpointInvoker : IMcpEndpointInvoker
{
    private readonly HttpClient _http;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly McpEndpointsOptions _options;

    public HttpClientMcpEndpointInvoker(
        HttpClient http,
        IHttpContextAccessor httpContextAccessor,
        McpEndpointsOptions options)
    {
        _http = http;
        _httpContextAccessor = httpContextAccessor;
        _options = options;
    }

    public async Task<string> InvokeAsync(
        string httpMethod,
        string relativePath,
        string? queryString,
        string? jsonBody,
        CancellationToken cancellationToken = default)
    {
        var baseUrl = ResolveBaseUrl();
        var path = relativePath.TrimStart('/');
        var url = $"{baseUrl}/{path}";
        if (!string.IsNullOrEmpty(queryString))
            url += "?" + queryString;

        // Start an activity around the loopback call. StartActivity returns null when no
        // listener is attached, so this path is zero-cost in production when tracing is off.
        using var activity = McpItActivitySource.Source.StartActivity(
            "mcpit.endpoint.invoke", ActivityKind.Client);

        if (activity is not null)
        {
            activity.SetTag("http.request.method", httpMethod);
            // Include only the path (not query string or base URL) to avoid exposing secrets.
            activity.SetTag("url.path", "/" + path);
        }

        using var request = new HttpRequestMessage(new HttpMethod(httpMethod), url);
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        ForwardHeaders(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        activity?.SetTag("http.response.status_code", (int)response.StatusCode);

        if (_options.ThrowOnUnsuccessfulResponse && !response.IsSuccessStatusCode)
        {
            activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");
            throw new McpEndpointInvocationException(httpMethod, url, (int)response.StatusCode, content);
        }

        return content;
    }

    /// <summary>
    /// Copies the configured allowlist of headers (plus <c>Authorization</c> when
    /// <see cref="McpEndpointsOptions.ForwardAuthorization"/> is set) from the in-flight MCP request
    /// onto the loopback request, so protected endpoints still authenticate. No-op when there is no
    /// active HttpContext or nothing is configured to forward.
    /// </summary>
    private void ForwardHeaders(HttpRequestMessage request)
    {
        if (!_options.ForwardAuthorization && _options.ForwardedHeaders.Count == 0)
            return;

        var incoming = _httpContextAccessor.HttpContext?.Request.Headers;
        if (incoming is null)
            return;

        foreach (var name in _options.ForwardedHeaders)
            CopyHeader(incoming, request, name);

        // ForwardAuthorization is shorthand for adding "Authorization" to the allowlist; skip it if the
        // allowlist already covers it (ForwardedHeaders is case-insensitive) so it is not forwarded twice.
        if (_options.ForwardAuthorization && !_options.ForwardedHeaders.Contains("Authorization"))
            CopyHeader(incoming, request, "Authorization");
    }

    private static void CopyHeader(IHeaderDictionary incoming, HttpRequestMessage request, string name)
    {
        if (incoming.TryGetValue(name, out var values) && values.Count > 0)
            request.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
    }

    /// <summary>
    /// Resolves the absolute base URL for loopback calls: the explicitly configured
    /// <see cref="McpEndpointsOptions.BaseAddress"/> when set, otherwise the scheme + host of the
    /// in-flight MCP request (auto-detection).
    /// </summary>
    private string ResolveBaseUrl()
    {
        if (_options.BaseAddress is not null)
            return _options.BaseAddress.ToString().TrimEnd('/');

        var req = _httpContextAccessor.HttpContext?.Request
            ?? throw new InvalidOperationException(
                "McpIt could not determine the host automatically because there is no active " +
                "HttpContext (the tool was invoked outside of an HTTP request). Set " +
                "McpEndpointsOptions.BaseAddress explicitly in AddMcpEndpoints(...).");

        return $"{req.Scheme}://{req.Host.Value}{req.PathBase.Value}";
    }
}
