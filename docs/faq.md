# McpIt FAQ

Answers to the questions developers ask when they want AI agents to call an existing ASP.NET Core API over the Model Context Protocol (MCP). Every answer reflects McpIt 1.4.0 (current NuGet release) unless marked otherwise.

- Package: [`McpIt` on NuGet](https://www.nuget.org/packages/McpIt)
- Source: [github.com/norequest/McpIt](https://github.com/norequest/McpIt)
- Comparison with alternatives: [comparison.md](comparison.md)

---

## Getting started

### How do I expose my existing ASP.NET Core Web API as MCP tools?

1. Install the package:

   ```bash
   dotnet add package McpIt
   ```

2. Register the MCP server and the McpIt invoker in `Program.cs`, and map the MCP endpoint:

   ```csharp
   using McpIt;

   var builder = WebApplication.CreateBuilder(args);
   builder.Services.AddControllers();

   builder.Services.AddMcpServer()
       .WithHttpTransport(o => o.Stateless = true)
       .WithToolsFromAssembly();

   builder.Services.AddMcpEndpoints();

   var app = builder.Build();
   app.MapControllers();
   app.MapMcp("/mcp");
   app.Run();
   ```

3. Mark each endpoint you want agents to use:

   ```csharp
   /// <summary>Gets an order by its id.</summary>
   /// <param name="id">The numeric order id.</param>
   [HttpGet("{id}")]
   [McpTool(Name = "getOrder")]
   public ActionResult<Order> GetOrder(int id) { ... }
   ```

Build and run. The tools are listed by `tools/list` at `/mcp`.

### How do I turn a controller action into an MCP tool?

Put `[McpTool]` on the action. The tool name defaults to the camelCase method name; set `Name` to choose it. The XML `<summary>` becomes the tool description and each `<param>` becomes a parameter description. Placing `[McpTool]` on the controller class only sets defaults (for example `NamePrefix`) and does not expose anything on its own.

### How do I turn a minimal-API endpoint into an MCP tool?

Three shapes are supported:

```csharp
// 1. Named method-group handler
public static class ThingHandlers
{
    /// <summary>Gets a thing by its id.</summary>
    [McpTool(Name = "getThing")]
    public static string GetThing(int id) => $"thing-{id}";
}

// 2. MapGroup prefix chain (nested groups work too): route becomes /api/things/{id}
var g = app.MapGroup("/api");
g.MapGet("/things/{id}", ThingHandlers.GetThing);

// 3. Inline lambda: name auto-derived as "get_ping_name" unless you set Name
app.MapGet("/ping/{name}",
    [McpTool]
    [Description("Returns a greeting for the given name.")]
    (string name) => $"pong: {name}");
```

Lambdas have no XML doc comments, so use `[Description]` for their description.

### How do I connect Claude Code, VS Code or Cursor to the tools?

Run your app and point the client at the `/mcp` URL (replace the port with your app's).

```bash
claude mcp add --transport http my-api http://localhost:5000/mcp
```

```jsonc
// VS Code: .vscode/mcp.json
{ "servers": { "my-api": { "type": "http", "url": "http://localhost:5000/mcp" } } }
```

Cursor and other clients accept a Streamable HTTP server URL in their MCP configuration in the same way.

---

## What McpIt is and is not

### Is McpIt an MCP server?

No. It is a library. Your ASP.NET Core app becomes the MCP server through Microsoft's official [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) SDK. McpIt generates the tool classes that server exposes.

### Do I still need the official MCP C# SDK?

It is already there: `McpIt` depends on `ModelContextProtocol.AspNetCore` (1.4.0 at the time of McpIt 1.4.0) and brings it in transitively. Anything the SDK offers (hand-written `[McpServerTool]` classes, prompts, resources) can live in the same app next to the generated tools.

### What exactly does the source generator produce?

For each `[McpTool]` endpoint, a static class marked `[McpServerToolType]` with a method marked `[McpServerTool(Name, Title, ReadOnly, Destructive, Idempotent, OpenWorld = false)]`. Its parameters mirror the endpoint's route, query and body parameters, with `[Description]` and DataAnnotations copied over. Parameter default values are not carried over, so every parameter is listed as required in the input schema (nullable types accept `null`). It also emits `McpIt.Generated.McpItManifest` (tool names, a JSON manifest and an aggregate SHA-256 hash) and, from 1.5.0, `McpIt.Generated.McpItToolCatalog` (an `internal` build-time list of tool descriptors).

### Does McpIt need an OpenAPI or Swagger document?

No. It reads your C# source at compile time. It does not read, generate or depend on OpenAPI.

### Does McpIt use runtime reflection?

Tool discovery is done at compile time by the generator, not by scanning controllers at runtime. At run time, request bodies are serialized reflectively unless you supply a `JsonSerializerContext`, and the SDK's `WithToolsFromAssembly()` registration uses reflection. See [Does McpIt support Native AOT?](#does-mcpit-support-native-aot).

### How does a tool call reach my endpoint?

The generated tool calls `IMcpEndpointInvoker`, which sends a loopback HTTP request to your own app through a typed `HttpClient`. Your endpoint handles it like any other request, so routing, model binding, filters, validation, middleware and authorization all run. By default the base address is detected from the incoming MCP request; set `BaseAddress` to pin it (required when forwarding credentials).

---

## Security

### Are all my endpoints exposed to agents?

No. Only endpoints marked `[McpTool]` become tools.

### How do I avoid exposing a destructive endpoint by accident?

POST, PUT, PATCH and DELETE endpoints are marked destructive in the MCP tool annotations, and the build emits warning `MCPGEN002` until you acknowledge the endpoint with `[McpTool(AllowDestructive = true)]`. GET is marked read-only and idempotent.

### My API requires a Bearer token or an API key. How do agents authenticate?

Forward the credentials the MCP client sent to `/mcp`:

```csharp
builder.Services.AddMcpEndpoints(o =>
{
    o.BaseAddress = new Uri("https://localhost:5001/");   // required when forwarding
    o.ForwardAuthorization = true;                         // copy Authorization
    o.ForwardedHeaders.Add("X-Api-Key");                   // and any other named headers
    o.ThrowOnUnsuccessfulResponse = true;                  // surface 401/403/500 as errors
});
```

Forwarding without a pinned `BaseAddress` throws at startup, because an auto-detected host comes from the client-controlled `Host` header.

### Can I require an OAuth scope per tool?

Yes: `[McpTool(RequiredScope = "orders:write")]`. Before calling the endpoint, the generated tool reads every claim whose type is literally `scope` or `scp`, splits each value on spaces, and looks for an exact match. When the scope is missing it returns `{"error":"forbidden","tool":"...","requiredScope":"..."}`.

With JwtBearer, the default inbound claim mapping renames `scp` to a long URI claim type the gate does not read, so set `options.MapInboundClaims = false`.

### How do I detect that the tool surface changed between deploys?

Serve the generated manifest with `app.MapMcpManifest(McpIt.Generated.McpItManifest.Json)` (default `GET /mcp/manifest`) and compare `aggregateHash` against a value stored in CI. The hash covers each tool's name, description, verb, route and parameter names and types. It does not cover parameter descriptions, `Title`, annotations, `RequiredScope`, validation constraints or output shaping, so treat it as a drift check rather than full tool-poisoning protection. Inline-lambda tools are left out of the manifest, named minimal-API handlers contribute an empty verb and route, and API-versioned routes are stored as the raw template.

---

## Token cost and output size

### How do I stop large API responses from flooding the model's context?

```csharp
[McpToolOutput(Fields = new[] { "id", "customer.name", "lines[].sku" }, MaxItems = 20, MaxLength = 2000)]
```

`Fields` projects the JSON response (top-level names, dot paths, `[]` array markers), `MaxItems` keeps the first N elements when the response root is a JSON array (nested arrays are not capped), `MaxLength` truncates the result. Malformed JSON passes through untouched.

### How do I measure how many tokens my tool definitions cost?

```bash
dotnet tool install -g McpIt.TokenReport.Tool
mcp-token-report http://localhost:5000/mcp --budget 2000
```

It reads any MCP server (or a saved `tools/list` JSON), prints per-tool and total estimated tokens, and exits non-zero over budget. Counts come from an offline heuristic, so they are estimates for comparison, not billing figures.

---

## Platform and compatibility

### Which .NET versions are supported?

.NET 8, 9 and 10. The generator loads on the .NET 8, 9 and 10 SDK compilers.

### Does McpIt support Native AOT?

Not fully yet. The `McpIt` runtime library is marked `IsAotCompatible` and builds with trim and AOT analyzer warnings treated as errors. But every generated tool (GET included) calls `IMcpEndpointInvoker.InvokeAsync(..., object?, Type?, ...)`, which is annotated `[RequiresUnreferencedCode]`/`[RequiresDynamicCode]`, so your app gets IL2026/IL3050 warnings under trim or AOT analysis. `AddControllers()` (MVC) is not AOT-compatible either.

What helps today: supply a source-generated `JsonSerializerContext` through `AddMcpEndpoints(o => o.SerializerOptions = ...)` so request bodies are serialized without reflection at runtime. The repository also has a manual AOT publish script (`benchmarks/aot-publish-smoke.sh`); it is not run by CI.

### Does it work with API versioning?

Yes, URL-segment versioning with `Asp.Versioning` or the legacy `Microsoft.AspNetCore.Mvc.Versioning`. A `{version:apiVersion}` route token is resolved at build time from `[MapToApiVersion]` / `[ApiVersion]`, and versioned tools get a suffix such as `info_v1` and `info_v2`. Warning `MCPGEN003` flags a token that cannot be resolved.

### Which MCP transport does it use?

Whatever you configure on the official SDK. The documented and tested setup is Streamable HTTP via `WithHttpTransport(...)` and `app.MapMcp("/mcp")` inside your web app.

### Can I trace tool calls?

Yes. McpIt emits OpenTelemetry spans named `mcpit.endpoint.invoke` from the `ActivitySource` `"McpIt"`. Subscribe with `.AddSource("McpIt")`.

---

## Discovery features (1.5.0)


### How do I help an agent pick the right tool when I expose many endpoints?

McpIt 1.5.0 adds `Category`, `Keywords` and `Priority` to `[McpTool]`, a generated catalog `McpIt.Generated.McpItToolCatalog.Tools`, an offline BM25 `search_tools` meta-tool, and discovery documents (`/llms.txt`, an MCP Server Card at `/mcp/server-card` and `/.well-known/ai-catalog.json`):

```csharp
builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly()
    .WithToolSearch(McpItToolCatalog.Tools);

app.MapMcpDiscovery(McpItToolCatalog.Tools, o => o.ServerName = "com.example/orders");   // ServerName is required
```

`McpItToolCatalog` is `internal`, one per assembly with tools, so this works when the `[McpTool]` endpoints are in the same project as `Program.cs`; for tools in a class library, expose `McpItToolCatalog.Tools` from that library through a public static property and pass that in. Info diagnostics `MCPGEN004` (description has fewer than four words or only repeats the tool's name or title) and `MCPGEN005` (parameter without description) flag weak descriptions. See the README section "Help agents find the right tool (1.5.0)".


---

## Licensing

### Is McpIt free for commercial use?

Yes. It is MIT licensed.
