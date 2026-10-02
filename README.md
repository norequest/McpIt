<div align="center">

<img src="assets/icon.png" width="120" alt="McpIt" />

# McpIt

**Expose your existing ASP.NET Core Web API to AI agents as MCP tools. Add one `[McpTool]` attribute to a controller action or minimal-API handler; a source generator writes the MCP tool for you at build time.**

[![NuGet](https://img.shields.io/nuget/v/McpIt.svg)](https://www.nuget.org/packages/McpIt)
[![Downloads](https://img.shields.io/nuget/dt/McpIt.svg)](https://www.nuget.org/packages/McpIt)
[![CI](https://github.com/norequest/McpIt/actions/workflows/ci.yml/badge.svg)](https://github.com/norequest/McpIt/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)

</div>

---

McpIt is a .NET library (a Roslyn source generator plus a small runtime) that turns the ASP.NET Core endpoints you already have into [Model Context Protocol](https://modelcontextprotocol.io) (MCP) tools that Claude, ChatGPT, GitHub Copilot, Cursor and other MCP clients can call.

- **Input:** controller actions and minimal-API handlers marked `[McpTool]`.
- **Output:** at compile time, one `[McpServerTool]` class per endpoint, built on Microsoft's official [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore) C# SDK, served at `/mcp` from the same app.
- **No hand-written tool classes, no OpenAPI document, no separate server process.** Tool names, input schemas, descriptions and safety hints come from the code you already wrote: HTTP verb, route, parameters, DataAnnotations and XML doc comments.
- **Targets .NET 8, 9 and 10.** MIT licensed.

---

## Install

```bash
dotnet add package McpIt
```

`McpIt` brings in the official MCP SDK transitively, so you do not need to add `ModelContextProtocol.AspNetCore` yourself. The `[McpTool]` and `[McpToolOutput]` attributes ship in the small `McpIt.Abstractions` package, which also comes in transitively.

## 30-second example

`Program.cs`:

```csharp
using McpIt;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddControllers();

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly();   // discovers the tools McpIt generated

builder.Services.AddMcpEndpoints();   // invoker the generated tools use to call your endpoints

var app = builder.Build();
app.MapControllers();
app.MapMcp("/mcp");   // MCP server at /mcp; your API stays where it is
app.Run();
```

A controller action, opted in with one attribute:

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

Your REST API runs unchanged, and an MCP server is now served at `/mcp`. Point any MCP client at it, for example:

```bash
# Claude Code
claude mcp add --transport http my-api http://localhost:5000/mcp
```

```jsonc
// VS Code (.vscode/mcp.json)
{ "servers": { "my-api": { "type": "http", "url": "http://localhost:5000/mcp" } } }
```

---

## How it works

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

When the agent calls `getOrder`, the generated tool sends a loopback HTTP request to your own app (through a typed `HttpClient` registered by `AddMcpEndpoints`). Your real `GetOrder` action handles it, so routing, model binding, filters, validation, middleware and business logic all run exactly as they do for any other HTTP caller. The response body is returned to the agent, optionally shaped first with `[McpToolOutput]`.

Tool discovery is done by the source generator, not by scanning your controllers with reflection at runtime. The generated read (GET/HEAD) path uses reflection-free JSON; see [AOT-ready body serialization](#aot-ready-body-serialization) for the request-body and tool-registration caveats.

---

## When to use McpIt vs. the official MCP C# SDK alone

McpIt does not replace the official SDK. It sits on top of it: the SDK serves the MCP protocol and transport, McpIt writes the tool classes.

| Your situation | Use |
|---|---|
| You already have an ASP.NET Core Web API (controllers or minimal APIs) and want agents to call some of its endpoints | **McpIt**: mark the endpoints with `[McpTool]` |
| You want the tool surface to stay in sync with the API as it changes, with build warnings for missing descriptions and unacknowledged destructive endpoints | **McpIt** |
| You are building a new MCP server whose tools are not HTTP endpoints (file system, local processes, a desktop app, a stdio server) | **Official SDK alone**: write `[McpServerToolType]` / `[McpServerTool]` classes |
| You need prompts, resources, sampling or other MCP features beyond tools | **Official SDK** (you can use it alongside McpIt in the same app) |
| Your API is not .NET, or you cannot change its code, and you only have an OpenAPI document | An OpenAPI-to-MCP gateway or proxy, not McpIt |

Without McpIt you write and maintain a parallel `[McpServerTool]` class for every endpoint you want an agent to reach, keeping its parameters, schema, and description in sync with the controller by hand. McpIt removes that layer: the endpoints you already have become the tools.

How McpIt compares with other ways to expose a .NET API over MCP: [docs/comparison.md](https://github.com/norequest/McpIt/blob/main/docs/comparison.md). Common questions: [FAQ](#faq) and [docs/faq.md](https://github.com/norequest/McpIt/blob/main/docs/faq.md).

---

## Why McpIt

1. **Build-time generation.** It is a Roslyn source generator, so the tool code exists at compile time, shows up in your IDE, and is checked by the compiler. No runtime scanning of controllers and no OpenAPI document to keep current.
2. **Controllers and minimal APIs.** Both endpoint styles can be exposed with `[McpTool]`, including `MapGroup` prefix chains and inline lambdas.
3. **Your real pipeline runs.** Tool calls go through your actual endpoint, so filters, validation, auth and middleware apply. Credentials can be forwarded, and per-tool OAuth scope gates are built in.
4. **Token-efficient and safe by default.** `[McpToolOutput]` trims responses before they reach the model, HTTP verbs map to MCP safety hints, destructive endpoints need explicit acknowledgement, and a tool-manifest hash lets CI detect tool drift.
5. **AOT-friendly.** `McpIt` is marked `IsAotCompatible` (the trim and AOT analyzers gate it on every build) and the generated read path is reflection-free. Tools that take a request body serialize it reflectively unless you supply a `JsonSerializerContext`, and the SDK's `WithToolsFromAssembly()` registration is reflection-based, so use explicit `.WithTools<...>()` registration for a fully AOT-published app.
6. **Tested.** The test suite covers generation, invocation, output shaping, and the token report.

---

## Features

- **Controllers and minimal APIs.** Mark a controller action or a minimal-API handler method with `[McpTool]` to opt it in. For minimal APIs, put `[McpTool]` on a named handler method or directly on an inline lambda and register it with `MapGet`/`MapPost`/etc.; `MapGroup` prefix chaining is supported. Exposure is opt-in: only annotated endpoints become tools. See [Minimal-API support](#minimal-api-support).
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
- **Validation-constraint schema.** DataAnnotations on action or handler parameters (`[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[RegularExpression]`, `[Required]`) are copied onto the generated tool's input parameters. The MCP SDK surfaces them as JSON Schema constraints (`minimum`, `maximum`, `minLength`, `maxLength`, `pattern`). No extra configuration is needed. See [Validation-constraint schema](#validation-constraint-schema).
- **Tool `Title`.** `[McpTool(Title = "Friendly Name")]` sets the MCP tool `title` field that clients may show in their UI instead of the raw tool name. Without it, McpIt derives a title from the method name.
- **OpenTelemetry spans.** McpIt emits spans from an `ActivitySource` named `"McpIt"` around every loopback call. Wire any OTel exporter with `.AddSource("McpIt")`; no extra packages needed.
- **AOT-ready body serialization.** Provide a `JsonSerializerContext` via `AddMcpEndpoints(o => o.SerializerOptions = ...)` to make the loopback request-body path reflection-free. Omitting it falls back to reflective serialization.
- **Token-cost report.** The `mcp-token-report` tool measures what your tool list costs the model and can fail a CI build over a budget (see below).

---

## Authentication

The generated tools reach your endpoints through a loopback HTTP call to your own app. By default that call carries no headers, so if an endpoint is protected (Basic, Bearer, an API key) the loopback arrives unauthenticated and the endpoint returns 401. Forward the credentials the MCP client already sent:

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

McpIt generates tools for minimal-API handlers alongside controller actions. Three handler shapes are all supported:

- **Named method-group handlers.** `[McpTool]` on a named static or instance method, referenced as a method group in the `MapGet`/`MapPost`/etc. call.
- **`MapGroup` prefix chains.** The generator resolves the chain at compile time and combines every prefix with the route segment. Both direct-chain (`app.MapGroup("/api").MapGet(...)`) and variable form (`var g = app.MapGroup("/api"); g.MapGet(...)`) work, including nested groups.
- **Inline lambdas.** `[McpTool]` placed in the attribute list directly on the lambda expression.

**Inline lambda tool names.** A lambda has no method name, so the tool name is auto-derived as `{verb}_{sanitizedRoute}` (for example, a GET handler on `/ping/{name}` produces tool name `get_ping_name`). Use `[McpTool(Name = "...")]` to set the tool name explicitly and `[McpTool(Title = "...")]` to set the display title.

```csharp
// Named method-group handler: [McpTool] on the method itself.
public static class ThingHandlers
{
    /// <summary>Gets a thing by its id.</summary>
    /// <param name="id">The numeric thing id to retrieve.</param>
    [McpTool(Name = "getThing")]
    public static string GetThing(int id) => $"thing-{id}";
}

// MapGroup prefix chain: generator combines prefix and route -> /api/things/{id}.
var g = app.MapGroup("/api");
g.MapGet("/things/{id}", ThingHandlers.GetThing);

// Inline lambda: [McpTool] in the lambda attribute list.
// Auto-derived tool name: "get_ping_name". Override with Name = "..." if needed.
app.MapGet("/ping/{name}", [McpTool] (string name) => $"pong {name}");
```

Nested `MapGroup` chains work: the generator walks the full chain and concatenates all segments.

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

## Validation-constraint schema

When a `[McpTool]` action or handler declares parameters with DataAnnotations, McpIt copies those constraints onto the generated tool's input parameters. The official MCP SDK surfaces them as JSON Schema keywords (`minimum`, `maximum`, `minLength`, `maxLength`, `pattern`), so the model receives a constrained input schema. No extra configuration is needed: McpIt reads the DataAnnotations already on your action.

Supported attributes: `[Range]`, `[StringLength]`, `[MinLength]`, `[MaxLength]`, `[RegularExpression]`, `[Required]`.

As a belt-and-suspenders measure McpIt also appends a concise human-readable hint to the parameter's `[Description]` (for example, `(range: 1 to 100)`) so agents that read descriptions directly also see the constraint.

```csharp
[HttpGet("products")]
[McpTool(Name = "listProducts", Title = "List Products")]
public ActionResult<Product[]> ListProducts(
    [Range(1, 100)] int pageSize = 20,
    [StringLength(50)] string? q = null)
{ ... }
```

The model receives:

```json
{
  "inputSchema": {
    "type": "object",
    "properties": {
      "pageSize": { "type": "integer", "minimum": 1, "maximum": 100 },
      "q":        { "type": "string",  "maxLength": 50 }
    }
  }
}
```

Attribute-to-schema mapping:

- `[Range(min, max)]`: `minimum` and `maximum`.
- `[StringLength(max)]`: `maxLength`. With `MinimumLength = min` also adds `minLength`.
- `[MinLength(n)]`: `minLength`.
- `[MaxLength(n)]`: `maxLength`.
- `[RegularExpression(pattern)]`: `pattern`.
- `[Required]`: marks the parameter as required in the schema.

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
- `Json`: the full manifest as a compile-time JSON string: `{"aggregateHash":"...","tools":[{"name":"...","verb":"GET","route":"orders/{id}","hash":"...","parameterCount":N}]}`.
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

**Fingerprint scope.** The hash covers tool name (with class-level `NamePrefix` and API-version suffix applied, matching the names the MCP client sees), description, HTTP verb, combined route (class `[Route]` plus method verb-route argument), and parameter surface (names and types). Changing a controller route or HTTP verb now changes `AggregateHash`. Remaining limitation: minimal-API handler methods receive empty verb and route in the manifest because those values come from the `MapGet`/`MapPost`/etc. call syntax rather than from attributes, and are not visible to the attribute-driven pipeline at compile time. Keep that scope in mind when interpreting hash changes.

---

## Help agents find the right tool (1.5.0)

> **Unreleased.** These APIs ship in McpIt 1.5.0. The current NuGet release is 1.4.0.

Agents pick tools by reading names and descriptions. When an API exposes many tools, or a user's wording differs from your endpoint names, the agent can miss the right one. 1.5.0 adds discovery metadata and an offline search tool.

**Tag tools with a category, keywords and a priority:**

```csharp
/// <summary>Creates a new order for the current customer.</summary>
[HttpPost]
[McpTool(Name = "createOrder", AllowDestructive = true,
         Category = "orders",
         Keywords = new[] { "purchase", "checkout", "buy" },
         Priority = 10)]
public ActionResult<Order> CreateOrder(CreateOrderRequest request) { ... }
```

`Category` on a controller class acts as a default for its actions. `Keywords` are synonyms the agent might use. `Priority` breaks ties when several tools match equally well (negative values demote a tool).

**Generated catalog.** The generator emits `McpIt.Generated.McpItToolCatalog.Tools`, a build-time list describing every `[McpTool]` endpoint (name, title, description, verb, route, category, keywords, priority, parameters, read-only and destructive flags). No reflection is involved.

**`search_tools` meta-tool.** Register it on the MCP server builder and agents can ask for the tools relevant to a task instead of reading the whole list. Ranking is offline (BM25 over the catalog), so there are no model or network calls:

```csharp
using McpIt.Generated;

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly()
    .WithToolSearch(McpItToolCatalog.Tools);
```

**Discovery documents.** Let agents and crawlers find your MCP server before they connect. `MapMcpDiscovery` builds three documents once at startup from the same catalog:

```csharp
app.MapMcpDiscovery(McpItToolCatalog.Tools, o =>
{
    o.ServerName = "com.example/orders";            // reverse-DNS name, required
    o.ServerTitle = "Orders API";
    o.Description = "Look up and cancel customer orders.";
    o.PublicBaseUrl = new Uri("https://api.example.com/");
});
```

| Path | Content |
|---|---|
| `/llms.txt` | Markdown index of every tool, grouped by `Category`, flagged read-only or destructive |
| `/mcp/server-card` | MCP Server Card (`application/mcp-server-card+json`), following the draft SEP-2127 shape |
| `/.well-known/ai-catalog.json` | Domain-level pointer to the server card |

The Server Card spec is still a draft, so its shape may change. Absolute URLs come only from `PublicBaseUrl`, never from the client-controlled `Host` header. The call returns a route group, so `.RequireAuthorization()` protects all three documents.

**Description-quality diagnostics.** Two new Info-level build diagnostics nudge you toward descriptions agents can act on: `MCPGEN004` when a tool description is too short, and `MCPGEN005` when a tool parameter has no description.


---

## FAQ

**How do I expose my existing ASP.NET Core Web API as MCP tools?**
Install `McpIt`, add `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()`, `AddMcpEndpoints()` and `app.MapMcp("/mcp")` to `Program.cs`, then put `[McpTool]` on each controller action or minimal-API handler you want agents to call. See the [30-second example](#30-second-example).

**Is McpIt an MCP server?**
No. McpIt is a library. Your ASP.NET Core app becomes the MCP server, using the official `ModelContextProtocol.AspNetCore` SDK for the protocol and transport. McpIt generates the tool classes that server exposes.

**Do I still need the official ModelContextProtocol C# SDK?**
Yes, and you already have it: `McpIt` depends on `ModelContextProtocol.AspNetCore` and brings it in transitively. You can still write hand-made `[McpServerTool]` classes, prompts and resources with the SDK in the same app.

**Does McpIt need an OpenAPI / Swagger document?**
No. It reads your C# code at compile time (attributes, routes, parameters, DataAnnotations, XML doc comments). Swagger can stay or go; it is unrelated.

**Does it work with minimal APIs?**
Yes. Named method-group handlers, `MapGroup` prefix chains (including nested groups) and inline lambdas are supported. See [Minimal-API support](#minimal-api-support).

**Are all my endpoints exposed?**
No. Exposure is opt-in: only endpoints marked `[McpTool]` become tools. POST, PUT, PATCH and DELETE endpoints raise build warning `MCPGEN002` until you acknowledge them with `AllowDestructive = true`.

**How does a tool call reach my controller?**
The generated tool makes a loopback HTTP request to your own app through a typed `HttpClient`, so your routing, model binding, filters, validation and middleware all run. See [How it works](#how-it-works).

**My endpoints require authentication. Does that work?**
Yes. Set `ForwardAuthorization = true` (and optionally `ForwardedHeaders`) together with a pinned `BaseAddress` in `AddMcpEndpoints`, and the MCP caller's credentials are forwarded to the endpoint. `[McpTool(RequiredScope = "...")]` adds a per-tool OAuth scope check. See [Authentication](#authentication).

**How do I keep tool responses small so they don't waste the model's context?**
Use `[McpToolOutput(Fields = ..., MaxItems = ..., MaxLength = ...)]` to project, cap and truncate responses, and the `mcp-token-report` tool to measure and budget the token cost of your `tools/list`.

**Does it support Native AOT?**
The `McpIt` runtime is marked `IsAotCompatible` and the read path is reflection-free. For a fully AOT-published app, supply a `JsonSerializerContext` for request bodies and register tools explicitly with `.WithTools<...>()` instead of `WithToolsFromAssembly()`. See [AOT-ready body serialization](#aot-ready-body-serialization).

**Which .NET versions are supported?**
.NET 8, 9 and 10.

**Which MCP clients can use the tools?**
Any client that speaks MCP over Streamable HTTP, for example Claude Code, VS Code with GitHub Copilot, Cursor, and agents built with MCP client SDKs.

**How is McpIt different from other options?**
See [docs/comparison.md](https://github.com/norequest/McpIt/blob/main/docs/comparison.md) for a sourced comparison with the official SDK alone, OpenAPI-to-MCP proxies and gateways, and other .NET libraries.

More questions: [docs/faq.md](https://github.com/norequest/McpIt/blob/main/docs/faq.md).

---

## Compatibility

- **Targets .NET 8, 9, and 10.** Builds with the .NET 8 SDK and newer (the source generator loads on the .NET 8/9/10 SDK build hosts).
- **Built on the official MCP SDK.** McpIt layers on `ModelContextProtocol.AspNetCore` 1.4.0. It generates the tool classes; the official SDK serves them over the MCP transport you configure (`AddMcpServer().WithHttpTransport(...)`).
- **AOT-friendly.** Generation happens at compile time. The library is `IsAotCompatible` and the read path is reflection-free; see the note above for the request-body and tool-registration caveats.

---

## Links

- NuGet: [`McpIt`](https://www.nuget.org/packages/McpIt) · [`McpIt.Abstractions`](https://www.nuget.org/packages/McpIt.Abstractions) · [`McpIt.TokenReport.Tool`](https://www.nuget.org/packages/McpIt.TokenReport.Tool)
- Repository: [github.com/norequest/McpIt](https://github.com/norequest/McpIt)
- Docs: [FAQ](https://github.com/norequest/McpIt/blob/main/docs/faq.md) · [Comparison with alternatives](https://github.com/norequest/McpIt/blob/main/docs/comparison.md) · [`llms.txt`](https://github.com/norequest/McpIt/blob/main/llms.txt) · [`llms-full.txt`](https://github.com/norequest/McpIt/blob/main/llms-full.txt) · [Changelog](https://github.com/norequest/McpIt/blob/main/CHANGELOG.md)
- Official MCP C# SDK: [`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol) · [modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)

## License

[MIT](LICENSE). Free for personal and commercial use, no warranty. Keep the copyright and license notice.
