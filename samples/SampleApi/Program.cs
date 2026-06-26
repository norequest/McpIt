using McpIt;
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

// MINIMAL-API METHOD-GROUP + MapGroup (Phase 2 / D2).
// The generator resolves MapGroup prefix chains at compile time and combines them with the
// route passed to MapGet. ThingHandlers.GetThing carries [McpTool], so the generated tool's
// loopback path becomes /api/things/{id}.
// NOTE: inline lambdas (e.g. app.MapGet("/x", id => ...)) do NOT generate tools because
// Roslyn cannot resolve a symbol for a lambda passed to the Delegate-typed Map overloads.
// Always put [McpTool] on a named method and reference it as a method group.
var g = app.MapGroup("/api");
g.MapGet("/things/{id}", ThingHandlers.GetThing);

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

// MINIMAL-API HANDLER (Phase 2 / D2).
// Put [McpTool] on a named static method and register it as a method group via MapGet/MapPost.
// The generator resolves the method symbol at compile time and emits the MCP tool using the
// combined MapGroup prefix + route segment as the loopback path (/api/things/{id} here).
// Do NOT put [McpTool] on inline lambdas: Roslyn cannot resolve a symbol for them.
public static class ThingHandlers
{
    /// <summary>Gets a thing by its id.</summary>
    /// <param name="id">The numeric thing id to retrieve.</param>
    [McpTool(Name = "getThing")]
    public static string GetThing(int id) => $"thing-{id}";
}
