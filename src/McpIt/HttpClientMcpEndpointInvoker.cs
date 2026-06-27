using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text;
using System.Text.Json;
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

    /// <inheritdoc/>
    public Task<string> InvokeAsync(
        string httpMethod,
        string relativePath,
        string? queryString,
        string? jsonBody,
        CancellationToken cancellationToken = default)
        => SendCoreAsync(httpMethod, relativePath, queryString, jsonBody, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// When <see cref="McpEndpointsOptions.SerializerOptions"/> is non-null the body is serialized
    /// via <c>options.GetTypeInfo(bodyType)</c> + <c>JsonSerializer.Serialize(body, typeInfo)</c>,
    /// a path that is free of <see cref="RequiresUnreferencedCodeAttribute"/> and
    /// <see cref="RequiresDynamicCodeAttribute"/>. When <see cref="McpEndpointsOptions.SerializerOptions"/>
    /// is null the reflective fallback is used. This override is annotated to satisfy IL2046
    /// (the interface member carries <see cref="RequiresUnreferencedCodeAttribute"/>).
    /// </remarks>
    [RequiresUnreferencedCode(
        "Body serialization falls back to JsonSerializer.Serialize(object, Type) when " +
        "McpEndpointsOptions.SerializerOptions is null. Set SerializerOptions with a source-generated " +
        "JsonSerializerContext to use the reflection-free code path.")]
    [RequiresDynamicCode(
        "Body serialization falls back to JsonSerializer.Serialize(object, Type) when " +
        "McpEndpointsOptions.SerializerOptions is null. Set SerializerOptions with a source-generated " +
        "JsonSerializerContext to use the reflection-free code path.")]
    public async Task<string> InvokeAsync(
        string httpMethod,
        string relativePath,
        string? queryString,
        object? body,
        Type? bodyType,
        CancellationToken cancellationToken = default)
    {
        string? jsonBody = null;

        if (body is not null && bodyType is not null)
        {
            if (_options.SerializerOptions is { } serOpts)
            {
                // AOT-clean path: user supplied a JsonSerializerContext-backed options instance.
                // JsonSerializerOptions.GetTypeInfo and JsonSerializer.Serialize(object?, JsonTypeInfo)
                // carry no [RequiresUnreferencedCode] / [RequiresDynamicCode] attributes.
                jsonBody = SerializeBodyWithOptions(body, bodyType, serOpts);
            }
            else
            {
                // Reflective fallback: no options supplied. Isolated in a separate annotated method
                // so the rest of this class (and the McpIt project with IsAotCompatible=true)
                // stays analyzer-clean. The outer method is also annotated (IL2046 compliance),
                // so calling the annotated helper here produces no additional diagnostic.
                jsonBody = SerializeBodyReflective(body, bodyType);
            }
        }

        return await SendCoreAsync(httpMethod, relativePath, queryString, jsonBody, cancellationToken);
    }

    /// <summary>
    /// AOT-clean serialization path. Uses <see cref="JsonSerializerOptions.GetTypeInfo"/> to
    /// retrieve a <see cref="System.Text.Json.Serialization.Metadata.JsonTypeInfo"/> from the
    /// user-supplied (typically source-generated) resolver, then serializes without reflection.
    /// Neither <c>GetTypeInfo</c> nor <c>JsonSerializer.Serialize(object?, JsonTypeInfo)</c>
    /// carries <see cref="RequiresUnreferencedCodeAttribute"/> or
    /// <see cref="RequiresDynamicCodeAttribute"/>.
    /// </summary>
    private static string SerializeBodyWithOptions(object body, Type bodyType, JsonSerializerOptions serOpts)
    {
        var typeInfo = serOpts.GetTypeInfo(bodyType);
        return JsonSerializer.Serialize(body, typeInfo);
    }

    /// <summary>
    /// Reflective serialization fallback used when <see cref="McpEndpointsOptions.SerializerOptions"/>
    /// is not configured. Isolated here so the enclosing class and all other methods stay
    /// analyzer-clean under IsAotCompatible=true. This method is only reached at runtime when the
    /// consumer has not supplied a source-generated <c>JsonSerializerContext</c>.
    /// </summary>
    [RequiresUnreferencedCode(
        "Reflective JsonSerializer.Serialize(object, Type) may fail under trimming. " +
        "Set McpEndpointsOptions.SerializerOptions with a source-generated JsonSerializerContext " +
        "to eliminate this code path entirely.")]
    [RequiresDynamicCode(
        "Reflective JsonSerializer.Serialize(object, Type) requires dynamic code generation. " +
        "Set McpEndpointsOptions.SerializerOptions with a source-generated JsonSerializerContext " +
        "to eliminate this code path entirely.")]
    private static string SerializeBodyReflective(object body, Type bodyType)
        => JsonSerializer.Serialize(body, bodyType);

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

    /// <summary>
    /// Shared HTTP dispatch core. Both public <c>InvokeAsync</c> overloads funnel here after
    /// body serialization. Preserves the Wave-2 OpenTelemetry activity instrumentation.
    /// </summary>
    private async Task<string> SendCoreAsync(
        string httpMethod,
        string relativePath,
        string? queryString,
        string? jsonBody,
        CancellationToken cancellationToken)
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
}
