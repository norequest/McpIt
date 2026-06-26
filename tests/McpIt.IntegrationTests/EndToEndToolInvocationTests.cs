using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using McpIt;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;

namespace McpIt.IntegrationTests;

// These tests exercise the generator end to end against the real SampleApi build:
// the source generator runs as an analyzer during SampleApi's compile, and we then
// reflect over the produced tool classes and invoke one for real through an in-memory
// test server. They are intentionally coupled to samples/SampleApi/Controllers/OrdersController.cs.
public class EndToEndToolInvocationTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    public EndToEndToolInvocationTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Generator_produced_tool_types_in_the_real_build()
    {
        var toolTypes = SampleAssembly()
            .GetTypes()
            .Where(t => t.GetCustomAttributes(inherit: false)
                .Any(a => a.GetType().Name == "McpServerToolTypeAttribute"))
            .ToList();

        // SampleApi annotates three actions ([McpTool] listOrders, getOrder, getOrderTracking),
        // so the generator must have emitted at least three tool types.
        Assert.True(toolTypes.Count >= 3, $"expected >= 3 generated tool types, got {toolTypes.Count}");
    }

    [Fact]
    public void Generated_tool_method_injects_invoker_and_exposes_route_param()
    {
        var invokeMethod = FindToolInvokeMethod("getOrder");
        var paramNames = invokeMethod.GetParameters().Select(p => p.Name).ToArray();

        Assert.Contains(invokeMethod.GetParameters(), p => p.ParameterType == typeof(IMcpEndpointInvoker));
        Assert.Contains("id", paramNames);
    }

    [Fact]
    public async Task Invoking_generated_tool_calls_the_real_endpoint()
    {
        // getOrder(1) loops back to GET /orders/1, which returns the full Order for Ada Lovelace.
        var result = await InvokeTool("getOrder", id: 1);

        Assert.Contains("Ada Lovelace", result);
    }

    [Fact]
    public async Task Output_shaping_trims_the_tool_response_to_the_declared_fields()
    {
        // getOrderTracking has [McpToolOutput(Fields = id,status,trackingNumber)]. The REST
        // endpoint returns the whole Order, but the generated tool keeps only those fields,
        // so the customer name must be dropped while the tracking number is kept.
        var result = await InvokeTool("getOrderTracking", id: 1);

        Assert.Contains("1Z-AAA-111", result);          // a kept field (trackingNumber)
        Assert.DoesNotContain("Ada Lovelace", result);  // a dropped field (customer)
    }

    private async Task<string> InvokeTool(string toolName, int id)
    {
        // Wire the invoker to the in-memory test server's client so the generated tool's
        // loopback call actually reaches the SampleApi endpoint.
        var client = _factory.CreateClient();
        var invoker = new HttpClientMcpEndpointInvoker(
            client, new HttpContextAccessor(), new McpEndpointsOptions { BaseAddress = client.BaseAddress });

        var invokeMethod = FindToolInvokeMethod(toolName);

        // Build arguments in declaration order: (IMcpEndpointInvoker, int id, CancellationToken=default).
        var args = invokeMethod.GetParameters().Select(p =>
            p.ParameterType == typeof(IMcpEndpointInvoker) ? (object)invoker :
            p.Name == "id" ? (object)id :
            p.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None :
            throw new InvalidOperationException($"Unexpected param {p.Name}:{p.ParameterType}"))
            .ToArray();

        var task = (Task<string>)invokeMethod.Invoke(null, args)!;
        return await task;
    }

    [Fact]
    public async Task Post_body_roundtrips_with_context_backed_serializer_options()
    {
        // Configures McpEndpointsOptions.SerializerOptions with a source-generated context.
        // The invoker will use the reflection-free path (SerializeBodyWithOptions) instead of
        // the reflective fallback. The body must still arrive at the endpoint correctly.
        var client = _factory.CreateClient();
        var serializerOptions = new JsonSerializerOptions
        {
            TypeInfoResolver = IntegrationTestJsonContext.Default,
        };
        var mcpOptions = new McpEndpointsOptions
        {
            BaseAddress = client.BaseAddress,
            SerializerOptions = serializerOptions,
        };

        var result = await InvokeAddNoteTool("addOrderNote", mcpOptions, id: 1,
            new SampleApi.Controllers.AddNoteRequest("integration-test-note"));

        // The SampleApi echoes the note back appended to the item description.
        Assert.Contains("integration-test-note", result);
    }

    private async Task<string> InvokeAddNoteTool(
        string toolName,
        McpEndpointsOptions options,
        int id,
        SampleApi.Controllers.AddNoteRequest request)
    {
        var client = _factory.CreateClient();
        var invoker = new HttpClientMcpEndpointInvoker(client, new HttpContextAccessor(), options);
        var invokeMethod = FindToolInvokeMethod(toolName);

        var args = invokeMethod.GetParameters().Select(p =>
            p.ParameterType == typeof(IMcpEndpointInvoker) ? (object)invoker :
            p.Name == "id" ? (object)id :
            p.ParameterType == typeof(SampleApi.Controllers.AddNoteRequest) ? (object)request :
            p.ParameterType == typeof(CancellationToken) ? (object)CancellationToken.None :
            throw new InvalidOperationException($"Unexpected param {p.Name}:{p.ParameterType}"))
            .ToArray();

        var task = (Task<string>)invokeMethod.Invoke(null, args)!;
        return await task;
    }

    private static Assembly SampleAssembly() => typeof(SampleApi.Controllers.OrdersController).Assembly;

    // Finds the generated static [McpServerTool] method whose tool name matches, by reading
    // the Name property off the generated McpServerTool attribute.
    private static MethodInfo FindToolInvokeMethod(string toolName)
    {
        foreach (var type in SampleAssembly().GetTypes())
        {
            if (!type.GetCustomAttributes(inherit: false)
                    .Any(a => a.GetType().Name == "McpServerToolTypeAttribute"))
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attr = method.GetCustomAttributes(inherit: false)
                    .FirstOrDefault(a => a.GetType().Name == "McpServerToolAttribute");
                if (attr is null)
                    continue;

                var name = attr.GetType().GetProperty("Name")?.GetValue(attr) as string;
                if (name == toolName)
                    return method;
            }
        }

        throw new InvalidOperationException($"No generated tool named '{toolName}' was found.");
    }
}

/// <summary>
/// Source-generated JsonSerializerContext covering the SampleApi body type used in the
/// context-backed integration test. Kept here so the test assembly is self-contained.
/// </summary>
[JsonSerializable(typeof(SampleApi.Controllers.AddNoteRequest))]
internal sealed partial class IntegrationTestJsonContext : JsonSerializerContext { }
