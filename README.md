<div align="center">

<img src="assets/icon.png" width="120" alt="McpIt" />

# McpIt

**You already have a Web API. Expose it to AI agents as MCP tools at build time: one `[McpTool]` attribute, reflection-free for read tools, zero proxy, zero hand-written server.**

[![NuGet](https://img.shields.io/nuget/v/McpIt.svg)](https://www.nuget.org/packages/McpIt)
[![Downloads](https://img.shields.io/nuget/dt/McpIt.svg)](https://www.nuget.org/packages/McpIt)
[![CI](https://github.com/norequest/McpIt/actions/workflows/ci.yml/badge.svg)](https://github.com/norequest/McpIt/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

</div>

---

McpIt is a build-time Roslyn source generator that turns your existing ASP.NET Core endpoints into [Model Context Protocol](https://modelcontextprotocol.io) tools. The official MCP C# SDK makes you hand-write `[McpServerTool]` classes for every operation you want an agent to use. McpIt generates those tool classes for you from the controller actions and minimal-API endpoints you already have: you mark an action with `[McpTool]`, and at compile time McpIt emits the MCP tool on top of the official [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) SDK. No runtime reflection for tool discovery, no internal HTTP self-call, no separate server to write and keep in sync.

---

## Install

```bash
dotnet add package McpIt
```

`McpIt` brings in the official MCP SDK transitively, so you do not need to add `ModelContextProtocol.AspNetCore` yourself. The `[McpTool]` and `[McpToolOutput]` attributes ship in the small `McpIt.Abstractions` package, which also comes in transitively.

Minimal setup in `Program.cs`:

```csharp
using McpIt;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();   // discovers the tools McpIt generated

builder.Services.AddMcpEndpoints();   // in-process invoker for the generated tools

var app = builder.Build();
app.MapControllers();
app.MapMcp("/mcp");   // MCP server at /mcp; your API stays where it is
app.Run();
```

Your REST API runs unchanged, and an MCP server is now served at `/mcp`.

---

## Before / after

A normal ASP.NET Core controller action:

```csharp
[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    [HttpGet("{id}")]
    public string GetOrder(int id) => $"order-{id}";
}
```

The same action, exposed to AI agents. Add one attribute (and a `<summary>` for the description):

```csharp
using McpIt;
using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("orders")]
public class OrdersController : ControllerBase
{
    /// <summary>Gets an order by its id.</summary>
    [HttpGet("{id}")]
    [McpTool(Name = "getOrder")]
    public string GetOrder(int id) => $"order-{id}";
}
```

At compile time McpIt generates an MCP tool class for `getOrder`. It reads the action's HTTP verb, route template, parameters, and description, and builds the tool's input schema and safety hints from them. The `id` route parameter becomes a typed tool argument, and the `<summary>` becomes the tool description. Nothing else changes in your project.

To an MCP client, a `tools/list` call now returns the tool:

```json
{
  "tools": [
    {
      "name": "getOrder",
      "description": "Gets an order by its id.",
      "inputSchema": {
        "type": "object",
        "properties": { "id": { "type": "integer" } },
        "required": ["id"]
      },
      "annotations": { "readOnlyHint": true, "idempotentHint": true }
    }
  ]
}
```

When the agent calls `getOrder`, McpIt invokes your real `GetOrder` action in-process, so your routing, model binding, validation, and business logic all run exactly as they do for an HTTP caller. There is no second HTTP request and no reflection at runtime for the call path (see the AOT note for request-body serialization).

---

## Why McpIt

Without McpIt you write and maintain a parallel `[McpServerTool]` class for every endpoint you want an agent to reach, keeping its parameters, schema, and description in sync with the controller by hand. McpIt removes that layer: the endpoints you already have become the tools.

The only comparable library, `Api.ToMcp`, performs an internal HTTP self-call at runtime and supports controllers only. McpIt differs on four points:

1. **Direct in-process invocation.** Tool calls run your action directly, with no internal HTTP self-call.
2. **Controllers and minimal APIs.** Both endpoint styles can be exposed with `[McpTool]`.
3. **AOT-friendly, zero runtime reflection for tool discovery.** It is a source generator, so the tool code exists at build time. The runtime and generated read (GET/HEAD) tools use only reflection-free JSON, and `McpIt` is marked `IsAotCompatible` (the trim and AOT analyzers gate it on every build). Note: tools that take a request body currently serialize it with reflection-based `System.Text.Json`, and the MCP SDK's `WithToolsFromAssembly()` registration is reflection-based, so use explicit `.WithTools<...>()` registration for a fully AOT-published app.
4. **Polished and tested.** A thorough test suite covers generation, invocation, output shaping, and the token report.

---

## Features

- **Controllers and minimal APIs.** Mark a controller action or a minimal-API handler method with `[McpTool]` to opt it in. For minimal APIs, put `[McpTool]` on a named handler method (not an inline lambda) and register it with `MapGet`/`MapPost`/etc.; `MapGroup` prefix chaining is supported. Exposure is opt-in: only annotated endpoints become tools. See [Minimal-API support](#minimal-api-support).
- **Tool names.** `[McpTool]` derives a camelCase name from the method, or set `Name` explicitly. Placed on a controller class, `[McpTool]` sets defaults (such as `NamePrefix`) for that class's annotated actions without exposing anything on its own.
- **Output shaping with `[McpToolOutput]`.** Keep responses lean. `Fields` projects the response to the JSON properties you list: top-level names, dot paths (`"customer.name"` drills into a nested object), and array markers (`"lines[].sku"` projects each array element down to that sub-property). `MaxItems` caps array elements. `MaxLength` truncates the final result. Shaping order: project, cap, truncate. Malformed JSON passes through untouched.

  ```csharp
  [HttpGet("{id}/lines")]
  [McpTool(Name = "getOrderLines", Title = "Order Lines")]
  [McpToolOutput(Fields = new[] { "id", "customer.name", "lines[].sku" }, MaxItems = 20, MaxLength = 2000)]
  public ActionResult<OrderDetail> GetOrderLines(int id, [FromQuery] int maxLines = 10) { ... }
  ```

- **Per-tool auth scope gate.** `[McpTool(RequiredScope = "orders:write")]` makes the generated tool verify the caller's OAuth scope before the loopback call. A denied check returns a structured JSON error without invoking the endpoint. See [Per-tool auth scope gate](#per-tool-auth-scope-gate).
- **Tool-manifest integrity hash.** At compile time, `McpManifestGenerator` emits `McpIt.Generated.McpItManifest` with `AggregateHash`, `Json`, and `ToolNames`. `app.MapMcpManifest(McpIt.Generated.McpItManifest.Json)` serves it at `GET /mcp/manifest`. Snapshot the hash in CI to detect tool-poisoning or drift. See [Tool-manifest integrity hash](#tool-manifest-integrity-hash).
- **Safety hints from HTTP verbs.** MCP tool annotations are derived from the verb: GET and HEAD are read-only and idempotent; POST, PUT, PATCH, and DELETE are flagged destructive (PUT and DELETE also idempotent). Exposing a destructive operation raises a build warning until you acknowledge it with `[McpTool(AllowDestructive = true)]`.
- **MCPGEN diagnostics.** Build-time warnings keep your tool surface honest: `MCPGEN001` when a tool has no description, `MCPGEN002` when a destructive operation is exposed without acknowledgement, `MCPGEN003` when a versioned route token is present but no API version can be resolved.
- **API versioning.** URL-segment versioning (`Asp.Versioning` and the legacy `Microsoft.AspNetCore.Mvc.Versioning`) works out of the box. No changes to your controllers are required; see the [API versioning](#api-versioning) section below.
- **Per-parameter descriptions.** XML `<param name="x">...</param>` doc comments on a `[McpTool]` action are emitted as `[Description]` on the generated tool's input parameters and surfaced in the MCP `inputSchema`, so agents see them alongside the type and required/optional flag.
- **Tool `Title`.** `[McpTool(Title = "Friendly Name")]` sets the MCP tool `title` field that clients may show in their UI instead of the raw tool name. Without it, McpIt derives a title from the method name.
- **OpenTelemetry spans.** McpIt emits spans from an `ActivitySource` named `"McpIt"` around every loopback call. Wire any OTel exporter with `.AddSource("McpIt")`; no extra packages needed.
- **AOT-ready body serialization.** Provide a `JsonSerializerContext` via `AddMcpEndpoints(o => o.SerializerOptions = ...)` to make the loopback request-body path reflection-free. Omitting it falls back to reflective serialization.
- **Token-cost report.** The `mcp-token-report` tool measures what your tool list costs the model and can fail a CI build over a budget (see below).

---

## Authentication

The generated tools reach your endpoints through an in-process loopback HTTP call. By default that call carries no headers, so if an endpoint is protected (Basic, Bearer, an API key) the loopback arrives unauthenticated and the endpoint returns 401. Forward the credentials the MCP client already sent:

```csharp
builder.Services.AddMcpEndpoints(options =>
{
    options.BaseAddress = new Uri("https://localhost:5001/");  // required when forwarding (see below)
    options.ForwardAuthorization = true;        // copy the incoming Authorization header
    options.ForwardedHeaders.Add("X-Api-Key");  // and any other headers, by name (case-insensitive)
});
```

`ForwardAuthorization` copies the incoming request's `Authorization` header onto each loopback call; `ForwardedHeaders` is a general allowlist for anything else (API keys, cookies, tenant headers). Both are off by default, so existing apps are unaffected. Forwarding requires that the MCP client authenticated to reach `/mcp` in the first place (so there is an incoming header to copy); for service-to-service credentials independent of the caller, register a `DelegatingHandler` on the typed `IMcpEndpointInvoker` client instead.

**Forwarding requires an explicit `BaseAddress`.** Without forwarding, McpIt auto-detects the loopback host from the incoming request. That host comes from the client-controlled `Host` header, so forwarding credentials to an auto-detected host could leak them to a spoofed host. To prevent that, enabling `ForwardAuthorization` or `ForwardedHeaders` without setting `BaseAddress` throws at startup. Pin `BaseAddress` to the host you trust.

By default a non-2xx response from your endpoint is returned to the agent as-is, which can surface a 401 page as if it were a successful result. Turn that into a real error:

```csharp
options.ThrowOnUnsuccessfulResponse = true;   // non-2xx throws McpEndpointInvocationException
```

`McpEndpointInvocationException` carries the `StatusCode` and `ResponseBody`. This is opt-in to preserve the prior pass-through behavior.

---

## Per-tool auth scope gate

`[McpTool(RequiredScope = "...")]` makes the generated tool verify the caller's `ClaimsPrincipal` carries that OAuth scope before the loopback call. If the scope is absent the tool returns a structured JSON error immediately, without touching the endpoint.

```csharp
/// <summary>Cancels an order. Requires the "orders:write" scope.</summary>
[HttpDelete("{id}")]
[McpTool(Name = "cancelOrder", AllowDestructive = true, RequiredScope = "orders:write")]
public ActionResult<Order> CancelOrder(int id) { ... }
```

Scope matching handles two common OAuth/OIDC claim shapes:

- A space-delimited `scope` claim (e.g. `"read orders:write"`): each token is checked individually.
- Individual `scope` or `scp` claims where the value equals the required scope exactly.

When the check fails, the tool returns a JSON error:

```json
{"error":"forbidden","tool":"cancelOrder","requiredScope":"orders:write"}
```

The runtime helper that the generated code calls is `McpIt.McpScopeGuard`:

- `McpScopeGuard.HasScope(IHttpContextAccessor?, string)`: returns `true` when the current user carries the scope. Returns `true` unconditionally when `requiredScope` is null or empty (no gate). Returns `false` when the accessor, its `HttpContext`, or the `User` is null.
- `McpScopeGuard.Denied(string toolName, string requiredScope)`: returns the stable JSON error string. Built with `Utf8JsonWriter`, so no reflection and AOT-clean.

`IHttpContextAccessor` is registered automatically by `AddMcpEndpoints`, so no extra DI setup is needed.

---

## API versioning

McpIt supports URL-segment API versioning out of the box, for both the modern `Asp.Versioning` package and the legacy `Microsoft.AspNetCore.Mvc.Versioning`. No changes to your controllers are required.

When a route contains a `{version:apiVersion}` token, the generator resolves each endpoint's version from attributes already on your code and bakes a concrete segment into the loopback path it emits. Without this the loopback call would 404 on the literal token.

```csharp
namespace Api.V1;

[ApiController]
[ApiVersion("1.0")]
[Route("v{version:apiVersion}/account")]
public class AccountController : ControllerBase
{
    /// <summary>Returns account info.</summary>
    [HttpGet("info")]
    [McpTool]
    public string Info() => "v1 account info";
}

namespace Api.V2;

[ApiController]
[ApiVersion("2.0")]
[Route("v{version:apiVersion}/account")]
public class AccountController : ControllerBase
{
    /// <summary>Returns account info.</summary>
    [HttpGet("info")]
    [McpTool]
    public string Info() => "v2 account info";
}
```

McpIt generates two distinct tools: `info_v1` (loopback path `/v1/account/info`) and `info_v2` (loopback path `/v2/account/info`). The version suffix keeps names unique so both `Info` actions appear in `tools/list` without collision, even though they share a class name and a method name across the two version namespaces. A single controller can also map several versions with `[MapToApiVersion]` on differently named actions; the resolved version is folded into each derived name the same way.

**Version resolution order per action:** `[MapToApiVersion]` on the method, then `[ApiVersion]` on the method, then `[ApiVersion]` on the controller. When several versions apply, the highest wins.

**Segment format:** a minor version of zero is dropped (`[ApiVersion("1.0")]` produces `/v1/`), matching the common convention. A non-zero minor is preserved (`2.1` produces `/v2.1/`).

**Name suffix opt-out:** set an explicit name with `[McpTool(Name = "myTool")]` or a class-level `NamePrefix`, and McpIt uses that name as-is with no auto-suffix.

**Build warning `MCPGEN003`:** if a route contains `{version:apiVersion}` but no `[ApiVersion]` or `[MapToApiVersion]` can be found on the action or its controller, the build warns rather than silently emitting a tool that would 404.

---

## Minimal-API support

McpIt generates tools for minimal-API handlers alongside controller actions. The critical rule: **put `[McpTool]` on a named handler method, not an inline lambda.**

Supported: method-group handlers and `MapGroup` prefix chaining. The generator resolves the `MapGroup` chain at compile time and combines every prefix with the route segment passed to `MapGet`/`MapPost`/etc.

Not supported for tool generation: inline lambdas passed directly to `app.MapGet(...)`. Roslyn returns no symbol for a lambda passed to the `Delegate`-typed `Map` overloads, so no tool is generated. Extract the handler to a named static method and reference it as a method group.

```csharp
// Handler class: put [McpTool] on a named method.
public static class ThingHandlers
{
    /// <summary>Gets a thing by its id.</summary>
    /// <param name="id">The numeric thing id to retrieve.</param>
    [McpTool(Name = "getThing")]
    public static string GetThing(int id) => $"thing-{id}";
}

// Registration: MapGroup prefix + method group.
// The generator combines the prefix and route into the loopback path: /api/things/{id}.
var g = app.MapGroup("/api");
g.MapGet("/things/{id}", ThingHandlers.GetThing);

// This does NOT generate a tool (inline lambda, no resolvable symbol):
// app.MapGet("/things/{id}", (int id) => $"thing-{id}");
```

Nested `MapGroup` chains work: if you wrap groups inside groups, the generator walks the full chain and concatenates all segments.

---

## Token report tool

`mcp-token-report` is an offline analyzer that shows how many tokens your `tools/list` surface spends in the model's context. AI agents load every tool's name, description, and input schema before the user asks anything, so a large tool surface is a real, recurring context cost. The tool reads a running MCP server or a saved `tools/list` JSON file, reports per-tool and total token counts, and can gate a build with `--budget`. It is fully offline and deterministic, so it is safe in CI.

```bash
dotnet tool install -g McpIt.TokenReport.Tool

mcp-token-report http://localhost:5199/mcp            # or a saved tools-list.json
mcp-token-report http://localhost:5199/mcp --markdown # Markdown table for CI artifacts
mcp-token-report http://localhost:5199/mcp --budget 2000   # exit 1 if over budget
```

Token counts use an offline heuristic tokenizer (estimates, not exact billing): ideal for comparing tools and catching bloat.

---

## Nested and array field projection

`[McpToolOutput(Fields = ...)]` accepts dot paths and array markers in addition to top-level property names.

- **Dot path** (`"customer.name"`): drills into a nested object and keeps only that leaf.
- **Array marker** (`"lines[].sku"`): projects every element of an array down to the named sub-property.
- **`MaxItems`**: caps how many array elements survive projection before `MaxLength` truncation.

Shaping order: project fields, cap items, truncate length.

```csharp
[HttpGet("{id}/lines")]
[McpTool(Name = "getOrderLines", Title = "Order Lines")]
[McpToolOutput(
    Fields = new[] { "id", "customer.name", "lines[].sku" },
    MaxItems = 20,
    MaxLength = 2000)]
public ActionResult<OrderDetail> GetOrderLines(int id, [FromQuery] int maxLines = 10) { ... }
```

Given a response like `{ "id": 1, "customer": { "name": "Ada", "email": "..." }, "lines": [{ "sku": "A1", "qty": 2 }] }`, the projected output is `{ "id": 1, "customer": { "name": "Ada" }, "lines": [{ "sku": "A1" }] }`. The `email` and `qty` fields never reach the model.

---

## Per-parameter descriptions

XML `<param name="...">` doc comments on a `[McpTool]` action are emitted as `[Description]` attributes on the generated tool's input parameters and surfaced in the MCP `inputSchema` description field, so agents see them alongside the type and required/optional flag.

```csharp
/// <summary>Gets the full detail of a single order by its id.</summary>
/// <param name="id">The numeric order id to look up.</param>
[HttpGet("{id}")]
[McpTool(Name = "getOrder", Title = "Get Order by ID")]
public ActionResult<Order> GetOrderDetail(int id) { ... }
```

The `<summary>` becomes the tool-level description. Each `<param>` tag becomes the matching parameter description. Both are optional but recommended for agent-facing tools.

---

## Tool Title

`[McpTool(Title = "Friendly Name")]` sets the MCP tool `title` field that clients may display in their UI instead of the raw tool name. Without a `Title`, McpIt derives one from the method name in title case. All generated tools emit `openWorld: false`.

```csharp
[McpTool(Name = "getOrder", Title = "Get Order by ID")]
```

---

## OpenTelemetry

McpIt emits spans from an `ActivitySource` named `"McpIt"` around every loopback call. The span name is `"mcpit.endpoint.invoke"`. Three semantic-convention tags are attached: `http.request.method`, `url.path` (path only, no query string), and `http.response.status_code`. When `ThrowOnUnsuccessfulResponse` is enabled, a non-2xx response also sets the span status to `Error`.

Wire any OpenTelemetry exporter that subscribes to the `"McpIt"` source:

```csharp
// Add the OpenTelemetry.Extensions.Hosting package and any exporter of your choice,
// then subscribe to the "McpIt" ActivitySource:
// builder.Services.AddOpenTelemetry()
//     .WithTracing(t => t.AddSource("McpIt"));
```

No McpIt-specific packages are required. The `ActivitySource` is always present and is a no-op when no listener is attached, so there is no overhead in apps that do not use OpenTelemetry.

---

## AOT-ready body serialization

By default, when a generated tool needs to POST a `[FromBody]` payload to an endpoint, McpIt serializes it with reflective `System.Text.Json`. For Native-AOT or fully trimmed apps, supply a `JsonSerializerContext` via `SerializerOptions`:

```csharp
// In Program.cs:
builder.Services.AddMcpEndpoints(o =>
{
    o.SerializerOptions = new System.Text.Json.JsonSerializerOptions
    {
        TypeInfoResolver = SampleJsonContext.Default
    };
});

// Declare the context once with one [JsonSerializable] line per [FromBody] type:
[JsonSerializable(typeof(AddNoteRequest))]
internal partial class SampleJsonContext : JsonSerializerContext { }
```

When `SerializerOptions` is set, the loopback request-body path calls `GetTypeInfo(bodyType)` instead of `JsonSerializer.Serialize` with reflection, making it Native-AOT and trim safe. Omitting `SerializerOptions` falls back to reflective serialization with no other changes required (zero-config default).

---

## Tool-manifest integrity hash

At compile time, `McpManifestGenerator` emits a class `McpIt.Generated.McpItManifest` with three constant members:

- `AggregateHash`: SHA-256 fingerprint computed over all tool names, descriptions, and parameter surfaces, sorted for stability.
- `Json`: the full manifest as a compile-time JSON string: `{"aggregateHash":"...","tools":[{"name":"...","hash":"...","parameterCount":N}]}`.
- `ToolNames`: alphabetically sorted `string[]` of every tool name in the assembly.

Serve the manifest at runtime with one call in `Program.cs`:

```csharp
// Default path: GET /mcp/manifest
app.MapMcpManifest(McpIt.Generated.McpItManifest.Json);

// Custom path:
app.MapMcpManifest(McpIt.Generated.McpItManifest.Json, "/api/tool-manifest");
```

`MapMcpManifest` maps a GET endpoint that writes the constant string directly with no runtime serialization (AOT-clean). The method is an extension on `IEndpointRouteBuilder` from the `McpIt` namespace.

**Use case: CI drift detection.** Store `McpIt.Generated.McpItManifest.AggregateHash` as a reference value in your CI pipeline. On each deploy, fetch `GET /mcp/manifest` and compare `aggregateHash`. A mismatch means a tool was added, removed, renamed, or its parameter surface changed since the reference was captured. MCP clients can perform the same check to detect tool-poisoning between sessions.

**v1 fingerprint scope.** The hash covers tool name, description, and parameter surface (names and types). It does not reflect: `NamePrefix` on a controller class, API-version suffixes appended to derived tool names, or the route of a lambda handler. Keep that scope in mind when interpreting hash changes across versions.

---

## Compatibility

- **Targets .NET 8, 9, and 10.** Builds with the .NET 8 SDK and newer (the source generator loads on the .NET 8/9/10 SDK build hosts).
- **Built on the official MCP SDK.** McpIt layers on `ModelContextProtocol.AspNetCore` 1.4.0. It generates the tool classes; the official SDK serves them over the MCP transport you configure (`AddMcpServer().WithHttpTransport(...)`).
- **AOT-friendly.** Generation happens at compile time. The library is `IsAotCompatible` and the read path is reflection-free; see the note above for the request-body and tool-registration caveats.

---

## Links

- NuGet: [`McpIt`](https://www.nuget.org/packages/McpIt) · [`McpIt.Abstractions`](https://www.nuget.org/packages/McpIt.Abstractions) · [`McpIt.TokenReport.Tool`](https://www.nuget.org/packages/McpIt.TokenReport.Tool)
- Repository: [github.com/norequest/McpIt](https://github.com/norequest/McpIt)
- Official MCP C# SDK: [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol)

## License

[MIT](LICENSE). Free for personal and commercial use, no warranty. Keep the copyright and license notice.
