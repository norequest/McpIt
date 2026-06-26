using System;
using System.Collections.Generic;

namespace McpIt;

public sealed class McpEndpointsOptions
{
    /// <summary>
    /// Optional explicit base address for the loopback calls the generated tools make
    /// (e.g. <c>https://localhost:5001/</c>).
    /// <para>
    /// When <c>null</c> (the default), the base address is detected automatically from the
    /// incoming MCP request (its scheme + host), so no configuration is needed. Set this only
    /// when auto-detection is not appropriate, for example behind a proxy that rewrites the host
    /// or when tools are invoked outside of an HTTP request.
    /// </para>
    /// </summary>
    public Uri? BaseAddress { get; set; }

    /// <summary>
    /// When <c>true</c>, the incoming MCP request's <c>Authorization</c> header is copied onto the
    /// loopback call so a protected endpoint (Basic, Bearer, etc.) still authenticates. Convenience
    /// shortcut for adding <c>"Authorization"</c> to <see cref="ForwardedHeaders"/>.
    /// <para>
    /// Default <c>false</c>: no headers are forwarded, matching pre-1.2.0 behavior. Forwarding only
    /// works when the tool is invoked inside an HTTP request (so there is an incoming request to copy
    /// from) and the MCP client supplied the credentials.
    /// </para>
    /// </summary>
    public bool ForwardAuthorization { get; set; }

    /// <summary>
    /// Allowlist of header names copied verbatim from the incoming MCP request to each loopback call.
    /// Matching is case-insensitive. Use for API keys, cookies, tenant headers, or any auth scheme
    /// (<c>options.ForwardedHeaders.Add("X-Api-Key")</c>). Empty by default.
    /// </summary>
    public ICollection<string> ForwardedHeaders { get; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When <c>true</c>, a loopback call that returns a non-2xx status throws
    /// <see cref="McpEndpointInvocationException"/> (carrying the status code and response body)
    /// instead of silently returning the error body as if it were a successful result.
    /// <para>
    /// Default <c>false</c> to preserve pre-1.2.0 pass-through behavior. Turn on so an agent gets a
    /// real error (e.g. a 401 from a protected endpoint) rather than an error page as the "result".
    /// </para>
    /// </summary>
    public bool ThrowOnUnsuccessfulResponse { get; set; }

    /// <summary>
    /// When set (typically <c>new JsonSerializerOptions { TypeInfoResolver = MyJsonContext.Default }</c>),
    /// loopback request bodies are serialized through this options instance, making the body path
    /// reflection-free and Native-AOT / trim safe. When <c>null</c> (the default), a reflection-based
    /// serializer is used, which is zero-config but not AOT clean.
    /// </summary>
    /// <remarks>
    /// The request body is the only place McpIt serializes user data outbound. All other runtime paths
    /// (output shaping, query string building) are already reflection-free.
    /// </remarks>
    public System.Text.Json.JsonSerializerOptions? SerializerOptions { get; set; }
}
