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
    .WithToolsFromAssembly()
    // search_tools meta-tool (1.5.0): agents describe the task, McpIt ranks the generated tools
    // offline (BM25 over McpItToolCatalog, no model or network calls) and returns the best matches.
    .WithToolSearch(McpIt.Generated.McpItToolCatalog.Tools);

// Source-generated body serialization: supplying a JsonSerializerContext serializes loopback
// request bodies without reflection at runtime. Omitting SerializerOptions falls back to
// reflective serialization (zero-config). This alone does not make the app Native-AOT
// publishable: generated tools still call an invoker overload marked RequiresUnreferencedCode.
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
//   - AggregateHash : SHA-256 over each tool's name, description, verb, route and parameter
//                     names/types (inline-lambda tools are not included).
//   - Json          : the full manifest as a JSON string.
//   - ToolNames     : alphabetically sorted array of all tool names.
// Serve it at GET /mcp/manifest so CI and clients can snapshot and compare hashes.
app.MapMcpManifest(McpIt.Generated.McpItManifest.Json);

// AGENT DISCOVERY DOCUMENTS (1.5.0), built once from the generated catalog:
//   GET /llms.txt                     markdown index of every tool, grouped by Category
//   GET /mcp/server-card              MCP Server Card (draft SEP-2127 shape)
//   GET /.well-known/ai-catalog.json  domain-level pointer to the server card
// Set PublicBaseUrl in production; URLs are never derived from the client-controlled Host header.
app.MapMcpDiscovery(McpIt.Generated.McpItToolCatalog.Tools, o =>
{
    o.ServerName = "io.github.norequest/mcpit-sample";
    o.ServerTitle = "McpIt Sample Orders API";
    o.Description = "Look up, search, annotate and cancel sample orders.";
});

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
// A lambda has no method name, so its Title is empty unless set, and the tool name is derived as
// verb_sanitizedRoute: GET /ping/{name} -> tool name "get_ping_name", class suffix "GET_ping_name".
// Pass [McpTool(Name = "myTool", Title = "My Tool")] to take explicit control over both.
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
