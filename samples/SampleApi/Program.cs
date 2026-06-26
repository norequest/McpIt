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
