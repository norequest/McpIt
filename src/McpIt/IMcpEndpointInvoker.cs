using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace McpIt;

/// <summary>
/// Loopback invoker contract. The generated tool classes depend on this interface so they
/// remain decoupled from the <see cref="HttpClientMcpEndpointInvoker"/> implementation.
/// </summary>
public interface IMcpEndpointInvoker
{
    /// <summary>Invokes the loopback endpoint with a pre-serialized JSON body string.</summary>
    Task<string> InvokeAsync(
        string httpMethod,
        string relativePath,
        string? queryString,
        string? jsonBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Invokes the loopback endpoint, serializing <paramref name="body"/> to JSON before sending.
    /// </summary>
    /// <param name="httpMethod">The HTTP verb (GET, POST, etc.).</param>
    /// <param name="relativePath">The path relative to the base address.</param>
    /// <param name="queryString">Optional query string (without the leading <c>?</c>).</param>
    /// <param name="body">The body object to serialize, or <c>null</c> when there is no body.</param>
    /// <param name="bodyType">The declared <see cref="Type"/> of <paramref name="body"/>. When <c>null</c>, no body is sent.</param>
    /// <param name="cancellationToken">Propagated cancellation token.</param>
    /// <remarks>
    /// The default interface implementation serializes the body reflectively via
    /// <see cref="JsonSerializer.Serialize(object?,Type,JsonSerializerOptions?)"/>, which is not
    /// Native-AOT or trim safe. Supply a <see cref="McpEndpointsOptions.SerializerOptions"/> instance
    /// backed by a source-generated <c>JsonSerializerContext</c> to use the reflection-free code path
    /// in <see cref="HttpClientMcpEndpointInvoker"/>. Custom <see cref="IMcpEndpointInvoker"/>
    /// implementations that override this member and avoid reflection are exempt from the warning.
    /// </remarks>
    [RequiresUnreferencedCode(
        "The default body-serialization path uses JsonSerializer.Serialize(object, Type) which is " +
        "not trim safe. Set McpEndpointsOptions.SerializerOptions with a source-generated " +
        "JsonSerializerContext to use the reflection-free path in HttpClientMcpEndpointInvoker.")]
    [RequiresDynamicCode(
        "The default body-serialization path uses JsonSerializer.Serialize(object, Type) which " +
        "requires dynamic code generation. Set McpEndpointsOptions.SerializerOptions with a " +
        "source-generated JsonSerializerContext to use the reflection-free path in HttpClientMcpEndpointInvoker.")]
    Task<string> InvokeAsync(
        string httpMethod,
        string relativePath,
        string? queryString,
        object? body,
        Type? bodyType,
        CancellationToken cancellationToken = default)
    {
        string? jsonBody = (body is null || bodyType is null)
            ? null
            : JsonSerializer.Serialize(body, bodyType);
        return InvokeAsync(httpMethod, relativePath, queryString, jsonBody, cancellationToken);
    }
}
