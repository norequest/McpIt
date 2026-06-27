using System;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;

namespace McpIt.Runtime.Tests;

// Minimal sample DTO for body serialization tests. Must be at namespace level so the
// STJ source generator can produce the JsonSerializerContext implementation.
public sealed record OrderNote(string Note, int Priority);

// Source-generated JsonSerializerContext for AOT-clean path tests. Declared at namespace
// level (not nested inside the test class) because all containing types must be partial
// for STJ source generation to run (SYSLIB1032 / CS0534).
[JsonSerializable(typeof(OrderNote))]
internal sealed partial class BodyTestContext : JsonSerializerContext { }

// Tests for the object+Type InvokeAsync overload introduced to move body serialization
// out of generated code and into the runtime invoker.
public class BodySerializationTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;
        public string ResponseBody = "OK";

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Last = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(ResponseBody),
            };
        }
    }

    private static HttpClientMcpEndpointInvoker MakeInvoker(
        HttpClient http, McpEndpointsOptions? options = null)
    {
        var opts = options ?? new McpEndpointsOptions { BaseAddress = new Uri("http://localhost/") };
        if (opts.BaseAddress is null)
            opts.BaseAddress = new Uri("http://localhost/");
        return new HttpClientMcpEndpointInvoker(http, new HttpContextAccessor(), opts);
    }

    [Fact]
    public async Task Null_body_sends_no_request_content()
    {
        var handler = new CapturingHandler();
        var invoker = MakeInvoker(new HttpClient(handler));

        await invoker.InvokeAsync("GET", "orders/1", null, (object?)null, (Type?)null, CancellationToken.None);

        Assert.Null(handler.Last!.Content);
    }

    [Fact]
    public async Task Null_body_type_sends_no_content_even_when_body_provided()
    {
        var handler = new CapturingHandler();
        var invoker = MakeInvoker(new HttpClient(handler));

        // body is supplied but bodyType is null: treated as no-body per spec.
        await invoker.InvokeAsync("POST", "notes", null, (object?)new OrderNote("hi", 1), (Type?)null, CancellationToken.None);

        Assert.Null(handler.Last!.Content);
    }

    [Fact]
    public async Task Reflective_path_serializes_body_when_no_serializer_options()
    {
        var handler = new CapturingHandler();
        var invoker = MakeInvoker(new HttpClient(handler));
        var note = new OrderNote("urgent note", 5);

        await invoker.InvokeAsync("POST", "notes", null, (object?)note, typeof(OrderNote), CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("urgent note", handler.LastBody!);
        Assert.Contains("5", handler.LastBody!);
    }

    [Fact]
    public async Task Reflective_path_sets_content_type_to_application_json()
    {
        var handler = new CapturingHandler();
        var invoker = MakeInvoker(new HttpClient(handler));

        await invoker.InvokeAsync("POST", "notes", null, (object?)new OrderNote("x", 1), typeof(OrderNote), CancellationToken.None);

        var mediaType = handler.Last!.Content!.Headers.ContentType?.MediaType;
        Assert.Equal("application/json", mediaType);
    }

    [Fact]
    public async Task Options_path_serializes_body_using_context_backed_options()
    {
        var handler = new CapturingHandler();
        var serOpts = new JsonSerializerOptions { TypeInfoResolver = BodyTestContext.Default };
        var options = new McpEndpointsOptions
        {
            BaseAddress = new Uri("http://localhost/"),
            SerializerOptions = serOpts,
        };
        var invoker = MakeInvoker(new HttpClient(handler), options);
        var note = new OrderNote("context note", 9);

        await invoker.InvokeAsync("POST", "notes", null, (object?)note, typeof(OrderNote), CancellationToken.None);

        Assert.NotNull(handler.LastBody);
        Assert.Contains("context note", handler.LastBody!);
        Assert.Contains("9", handler.LastBody!);
    }

    [Fact]
    public async Task Options_path_produces_same_json_as_reflective_path()
    {
        var note = new OrderNote("compare note", 3);

        // Reflective path.
        var reflHandler = new CapturingHandler();
        var reflInvoker = MakeInvoker(new HttpClient(reflHandler));
        await reflInvoker.InvokeAsync("POST", "n", null, (object?)note, typeof(OrderNote), CancellationToken.None);

        // Options path.
        var ctxHandler = new CapturingHandler();
        var ctxOpts = new McpEndpointsOptions
        {
            BaseAddress = new Uri("http://localhost/"),
            SerializerOptions = new JsonSerializerOptions { TypeInfoResolver = BodyTestContext.Default },
        };
        var ctxInvoker = MakeInvoker(new HttpClient(ctxHandler), ctxOpts);
        await ctxInvoker.InvokeAsync("POST", "n", null, (object?)note, typeof(OrderNote), CancellationToken.None);

        Assert.Equal(reflHandler.LastBody, ctxHandler.LastBody);
    }

    [Fact]
    public async Task String_body_overload_still_works_unchanged()
    {
        // Verifies the pre-existing string-body InvokeAsync is unaffected by the refactor.
        var handler = new CapturingHandler();
        var invoker = MakeInvoker(new HttpClient(handler));

        await invoker.InvokeAsync("POST", "orders", null, "{\"note\":\"raw\"}", CancellationToken.None);

        Assert.Equal("{\"note\":\"raw\"}", handler.LastBody);
    }
}
