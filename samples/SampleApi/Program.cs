using McpIt;
using System.ComponentModel;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

// --- Your normal REST API ---
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// --- The MCP server, as an ADDITIONAL endpoint on the same app ---
// Stateless mode keeps testing simple (no Mcp-Session-Id handshake needed).
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithToolsFromAssembly();

// AOT-ready body serialization: supplying a JsonSerializerContext makes the loopback
// request-body path reflection-free and Native-AOT / trim safe. Omitting SerializerOptions
// falls back to reflective serialization with no other changes required (zero-config).
builder.Services.AddMcpEndpoints(o =>
{
    o.SerializerOptions = new System.Text.Json.JsonSerializerOptions
    {
        TypeInfoResolver = SampleApi.SampleJsonContext.Default
    };
});

// OpenTelemetry: McpIt emits spans from an ActivitySource named "McpIt" around every
// loopback call (span name "mcpit.endpoint.invoke"). Wire any OTel exporter you already
// use by subscribing to the "McpIt" source:
//
// builder.Services.AddOpenTelemetry()
//     .WithTracing(t => t.AddSource("McpIt"));
//
// Tags on each span: http.request.method, url.path, http.response.status_code.
// No McpIt-specific packages are needed; the ActivitySource is always present and is a
// no-op when no listener is attached.

var app = builder.Build();

// REST API + Swagger UI
app.UseSwagger();
app.UseSwaggerUI();
app.MapControllers();

// MCP lives at /mcp (NOT the root), so it doesn't collide with the API/Swagger.
app.MapMcp("/mcp");

// TOOL-MANIFEST INTEGRITY HASH (Phase 2 / D3).
// McpManifestGenerator emits McpIt.Generated.McpItManifest at compile time with:
//   - AggregateHash : SHA-256 fingerprint over every tool's name, description, and parameters.
//   - Json          : the full manifest as a JSON string.
//   - ToolNames     : alphabetically sorted array of all tool names.
// Serve it at GET /mcp/manifest so CI and clients can snapshot and compare hashes.
app.MapMcpManifest(McpIt.Generated.McpItManifest.Json);

// MINIMAL-API: METHOD-GROUPS, MapGroup PREFIX CHAINS, AND INLINE LAMBDAS.
// Three supported shapes for minimal-API MCP tools:
//   1. Method-group handler:  app.MapGet("/route", Handlers.Method)
//   2. MapGroup prefix chain: var g = app.MapGroup("/api"); g.MapGet("/x", H)
//   3. Inline lambda:         app.MapGet("/route", [McpTool] (TypeName param) => ...)
// A named method or explicit [McpTool(Name = "...")] gives full control over the tool name
// and Title. Without an explicit Name the generator derives the tool name as verb_sanitizedRoute.
var g = app.MapGroup("/api");
g.MapGet("/things/{id}", ThingHandlers.GetThing);

// INLINE LAMBDA WITH [McpTool] (Phase 3 demo).
// A lambda has no method name, so Title is auto-derived and the tool name is derived as
// verb_sanitizedRoute: GET /ping/{name} -> tool name "get_ping_name", class suffix "GET_ping_name".
// Pass [McpTool(Name = "myTool")] to take explicit control over the name.
// Lambdas carry no XML doc comments: use [Description("...")] to supply the tool description.
app.MapGet("/ping/{name}",
    [McpTool]
    [Description("Returns a greeting for the given name.")]
    (string name) => $"pong: {name}");

// Friendly root: send a browser to the Swagger UI.
app.MapGet("/", () => Results.Redirect("/swagger"));

app.Run();

namespace SampleApi
{
    public partial class Program { }

    // SampleJsonContext makes the loopback request-body serialization reflection-free.
    // Add one [JsonSerializable(typeof(...))] line for every [FromBody] type in the project.
    // This covers the addOrderNote tool, whose body is AddNoteRequest.
    [JsonSerializable(typeof(SampleApi.Controllers.AddNoteRequest))]
    internal partial class SampleJsonContext : JsonSerializerContext { }
}

// MINIMAL-API HANDLER (method-group form).
// Put [McpTool] on a named static method and register it as a method group via MapGet/MapPost.
// The generator resolves the method symbol at compile time and emits the MCP tool using the
// combined MapGroup prefix + route segment as the loopback path (/api/things/{id} here).
// Inline lambdas also work: place [McpTool] (and [Description]) directly on the lambda.
// Named methods give a meaningful auto-derived Title and full XML doc comment support.
public static class ThingHandlers
{
    /// <summary>Gets a thing by its id.</summary>
    /// <param name="id">The numeric thing id to retrieve.</param>
    [McpTool(Name = "getThing")]
    public static string GetThing(int id) => $"thing-{id}";
}
