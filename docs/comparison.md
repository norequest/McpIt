# Exposing a .NET API over MCP: McpIt compared with the alternatives

There are several ways to let AI agents call an ASP.NET Core API through the Model Context Protocol (MCP). This page describes them honestly so you can pick the right one, including when McpIt is the wrong choice.

Facts about other projects come from their public pages, linked next to each claim, and were checked on **2026-10-03**. Projects change; check the linked page before relying on a detail. Corrections are welcome as issues or pull requests.

---

## Short answer

- **You have an ASP.NET Core Web API and want agents to call selected endpoints, with the tool definitions checked at build time:** McpIt.
- **You are writing a new MCP server whose tools are not HTTP endpoints, or you need prompts and resources:** the official MCP C# SDK alone ([modelcontextprotocol/csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)). McpIt is built on it and can sit next to it.
- **You cannot change the API's code, or it is not .NET, and you have an OpenAPI document:** an OpenAPI-to-MCP proxy or a gateway such as Azure API Management.
- **You prefer runtime discovery over code generation:** one of the runtime-reflection libraries listed below.

---

## The approaches

### 1. Official MCP C# SDK, hand-written tools

The SDK ([`ModelContextProtocol`](https://www.nuget.org/packages/ModelContextProtocol) and [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore), Apache-2.0) is the Tier 1 C# SDK listed on [modelcontextprotocol.io/docs/sdk](https://modelcontextprotocol.io/docs/sdk). You declare tools as classes marked `[McpServerToolType]` with methods marked `[McpServerTool]`, register them with `AddMcpServer().WithHttpTransport().WithToolsFromAssembly()`, and serve them with `app.MapMcp()` ([getting started](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html)). Its getting-started documentation does not describe turning existing controllers into tools automatically: each tool is a method you write.

**Relationship to McpIt:** McpIt depends on `ModelContextProtocol.AspNetCore` and generates exactly these `[McpServerToolType]` classes from your endpoints. The SDK still serves the protocol.

### 2. Build-time generation from your endpoints (McpIt)

[McpIt](https://github.com/norequest/McpIt) (MIT) is a Roslyn source generator. `[McpTool]` on a controller action or minimal-API handler makes the generator emit an SDK tool class at compile time. A tool call is executed as a loopback HTTP request to your own app, so the full ASP.NET Core pipeline runs.

- Tool definitions are compiled code: visible in the IDE, checked by the compiler, and covered by build diagnostics (missing description, unacknowledged destructive endpoint, unresolved API-version token).
- No OpenAPI document and no runtime scanning of controllers.
- Opt-in per endpoint.
- Extras: response shaping (`[McpToolOutput]`), Authorization and header forwarding, per-tool OAuth scope gate, verb-derived MCP safety annotations, validation constraints in the input schema, OpenTelemetry spans, a compile-time tool-manifest hash, and an offline token-cost CLI.
- Limits: tools only (use the SDK directly for prompts and resources); parameter defaults are not carried over, so every parameter is required in the schema; the manifest hash leaves out inline-lambda tools and gives named minimal-API handlers an empty verb and route; full Native-AOT publishing of generated tools is not supported yet.

### 3. Runtime libraries that expose existing endpoints

These libraries share McpIt's goal (existing ASP.NET Core endpoints become MCP tools) but discover endpoints at runtime instead of generating code at build time. Descriptions are taken from each project's own page.

| Project | How it works (per its page) | License |
|---|---|---|
| [ZeroMCP](https://github.com/ZeroMCP/ZeroMCP.net) ([NuGet](https://www.nuget.org/packages/ZeroMCP)) | `[Mcp]` on controller actions or `.AsMcp()` on minimal APIs; runtime discovery; calls run through the ASP.NET Core pipeline; also exposes resources and prompts. Successor of the deprecated [SwaggerMcp](https://www.nuget.org/packages/SwaggerMcp). | MIT |
| [Nabu.Mcp.AspNetCore](https://github.com/hovik-aghajanyan/nabu.net) ([NuGet](https://www.nuget.org/packages/Nabu.Mcp.AspNetCore)) | `[McpTool]` on controllers and minimal APIs, with add-on packages for SignalR and OData; replays each call as a synthetic HTTP request through the pipeline; filters tools per caller by authorization. | MIT |
| [Zero.Mcp.Extensions](https://github.com/LadislavSopko/net-api-with-mcp) ([NuGet](https://www.nuget.org/packages/Zero.Mcp.Extensions/)) | Puts the official SDK's attributes on controllers and scans them at runtime; honours `[Authorize]`. | Apache-2.0 |
| [AutoMcp](https://github.com/wertzui/AutoMCP) ([NuGet](https://www.nuget.org/packages/AutoMcp)) | `WithAutoMcp()` discovers endpoints at runtime; OData add-on. | Unlicense |
| [McpEndpointsTools](https://github.com/DED-Zlodey/McpEndpointsTools) ([NuGet](https://www.nuget.org/packages/McpEndpointsTools)) | Scans controller methods at runtime, `[McpIgnore]` to exclude; alpha. | MIT |

**Trade-off vs. McpIt:** runtime discovery needs no build step and some of these libraries cover more MCP features (resources, prompts) or more endpoint types (SignalR, OData). Build-time generation catches mistakes at compile time, keeps the tool surface reviewable as code, avoids runtime scanning, and lets McpIt fingerprint the tool surface in a constant hash.

### 4. OpenAPI-to-MCP bridges

These read an OpenAPI (Swagger) document and turn its operations into tools. They work for APIs you cannot modify, and for non-.NET APIs, but tool quality depends on the OpenAPI document, and you usually expose everything in it unless you filter.

| Project | How it works (per its page) | License |
|---|---|---|
| [MCPify](https://github.com/abdebek/MCPify) ([NuGet](https://www.nuget.org/packages/MCPify/)) | Loads an OpenAPI/Swagger document at runtime and proxies each operation as a tool; OAuth support. | MIT |
| [MCPInvoke](https://github.com/grparry/MCPInvoke) ([NuGet](https://www.nuget.org/packages/MCPInvoke)) | Builds tools from the app's Swagger/OpenAPI document, with optional attributes. | MIT |
| [openapi-to-mcp](https://github.com/ouvreboite/openapi-to-mcp) ([NuGet](https://www.nuget.org/packages/openapi-to-mcp)) | `dotnet tool` that runs a stdio MCP proxy from any OpenAPI spec. | see repo |
| [QuickMCP](https://github.com/gunpal5/QuickMCP) ([NuGet](https://www.nuget.org/packages/QuickMCP)) | CLI and library that build a server from OpenAPI or Google Discovery specs. | MIT |

### 5. Managed and platform options

- **Azure API Management** can expose REST API operations as an MCP server without code changes; per its documentation it exposes tools only ([overview](https://learn.microsoft.com/azure/api-management/mcp-server-overview)). Good when the API already sits behind APIM.
- **Azure Functions MCP extension** ([`Microsoft.Azure.Functions.Worker.Extensions.Mcp`](https://www.nuget.org/packages/Microsoft.Azure.Functions.Worker.Extensions.Mcp)) declares tools with `[McpToolTrigger]` on isolated-worker functions. It is for Functions apps, not ASP.NET Core controllers.

---

## Feature comparison: McpIt vs. the official SDK alone

| | Official SDK alone | McpIt (on the official SDK) |
|---|---|---|
| Tool definition | Hand-written `[McpServerTool]` method per tool | Generated from `[McpTool]` on an existing endpoint |
| Keeps tool in sync with the endpoint | Manual | Regenerated on every build |
| Input schema | From the tool method's parameters | From the endpoint's route, query and body parameters, plus DataAnnotations |
| Descriptions | `[Description]` on the tool method | XML `<summary>` / `<param>` or `[Description]` on the endpoint |
| Safety annotations | Set by hand | Derived from the HTTP verb; destructive endpoints need `AllowDestructive = true` |
| Execution | Your tool method's code | Loopback HTTP call into your endpoint (full pipeline) |
| Response shaping | Your code | `[McpToolOutput(Fields, MaxItems, MaxLength)]` |
| Auth to protected endpoints | Your code | `ForwardAuthorization`, `ForwardedHeaders`, `RequiredScope` |
| Tool-surface hash | Not provided | `McpItManifest.AggregateHash`, `MapMcpManifest` |
| Prompts, resources, sampling | Yes | Not generated; use the SDK directly in the same app |
| Non-HTTP tools (stdio, local processes) | Yes | Not the target use case |

---

## Sources

- Official SDK: https://github.com/modelcontextprotocol/csharp-sdk, https://csharp.sdk.modelcontextprotocol.io/v1/concepts/getting-started.html, https://modelcontextprotocol.io/docs/sdk, https://www.nuget.org/packages/ModelContextProtocol.AspNetCore
- Runtime libraries: https://github.com/ZeroMCP/ZeroMCP.net, https://www.nuget.org/packages/SwaggerMcp, https://github.com/hovik-aghajanyan/nabu.net, https://github.com/LadislavSopko/net-api-with-mcp, https://github.com/wertzui/AutoMCP, https://github.com/DED-Zlodey/McpEndpointsTools
- OpenAPI bridges: https://github.com/abdebek/MCPify, https://github.com/grparry/MCPInvoke, https://github.com/ouvreboite/openapi-to-mcp, https://github.com/gunpal5/QuickMCP
- Platforms: https://learn.microsoft.com/azure/api-management/mcp-server-overview, https://www.nuget.org/packages/Microsoft.Azure.Functions.Worker.Extensions.Mcp
- McpIt: https://github.com/norequest/McpIt, https://www.nuget.org/packages/McpIt
