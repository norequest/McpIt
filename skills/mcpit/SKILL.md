---
name: mcpit
description: Expose an existing ASP.NET Core Web API (controllers or minimal APIs) to AI agents as MCP tools with the McpIt NuGet package. Use when the user wants Claude, Copilot, Cursor or another agent to call their .NET API, asks to "add MCP" or "turn my endpoints into MCP tools" in an ASP.NET Core project, or is already using McpIt's [McpTool] / [McpToolOutput] attributes and needs help with setup, auth forwarding, output trimming, token cost or MCPGEN build warnings.
license: MIT
---

# McpIt: ASP.NET Core endpoints as MCP tools

McpIt is a .NET library (Roslyn source generator plus a small runtime). The developer marks
controller actions or minimal-API handlers with `[McpTool]`; at build time McpIt generates the
`[McpServerTool]` classes on top of Microsoft's official `ModelContextProtocol.AspNetCore` SDK,
and the app serves an MCP server at `/mcp`. A tool call is a loopback HTTP request to the same
app, so routing, model binding, filters, validation, auth and middleware all run normally.

It is a library, not a hosted server: the user's own app becomes the MCP server.

## When McpIt is the right choice

- **Yes:** an existing ASP.NET Core API (.NET 8, 9 or 10) whose endpoints agents should call.
- **No, use the official SDK alone:** tools that are not HTTP endpoints (files, processes, stdio
  servers), or MCP prompts/resources/sampling. Both can live in the same app.
- **No:** non-.NET APIs, or code the user cannot change (use an OpenAPI-to-MCP gateway).

## Setup workflow

1. **Check the project.** Find the ASP.NET Core project (`Microsoft.NET.Sdk.Web`) and its
   `Program.cs`. Note whether it uses controllers, minimal APIs, or both, and its target framework.

2. **Install.**

   ```bash
   dotnet add package McpIt
   ```

   This brings in `McpIt.Abstractions` and `ModelContextProtocol.AspNetCore` transitively. Do not
   add the MCP SDK package separately.

3. **Wire up `Program.cs`.** Add these alongside the existing registrations; do not remove any:

   ```csharp
   using McpIt;

   builder.Services.AddMcpServer()
       .WithHttpTransport(o => o.Stateless = true)
       .WithToolsFromAssembly();      // discovers the tools McpIt generated

   builder.Services.AddMcpEndpoints(); // the invoker the generated tools use

   // after builder.Build():
   app.MapMcp("/mcp");
   ```

4. **Choose endpoints with the user.** Exposure is opt-in. Propose a short list rather than
   annotating everything: start with read-only GETs that answer questions an agent would ask.
   Ask before exposing anything that writes, deletes, sends money or messages, or touches admin
   or user-management functions. Fewer, well-described tools work better than many, and every
   tool costs context tokens on every request.

5. **Annotate.** Put `[McpTool]` on each chosen action or handler, and write XML docs, because
   they become what the agent reads:

   ```csharp
   /// <summary>Gets the full detail of a single order by its id.</summary>
   /// <param name="id">The numeric order id to look up.</param>
   [HttpGet("{id}")]
   [McpTool(Name = "getOrder")]
   public ActionResult<Order> GetOrder(int id) { ... }
   ```

   - The `<summary>` says what the tool does and when to use it, in words a user would use,
     not just the method name restated.
   - Every parameter gets a `<param>` with its meaning, format and units.
   - XML docs need `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in the
     `.csproj`. Without it, descriptions come out empty and `MCPGEN001` fires.
   - For POST/PUT/PATCH/DELETE, add `AllowDestructive = true` only after the user confirms the
     agent should be able to do that. Consider `RequiredScope` for these.

   Full attribute reference: [references/attributes.md](references/attributes.md).

6. **Trim large responses.** If an endpoint returns big objects or long lists, add
   `[McpToolOutput]` so only what the agent needs reaches the model:

   ```csharp
   [McpToolOutput(Fields = new[] { "id", "status", "customer.name", "lines[].sku" },
                  MaxItems = 20, MaxLength = 4000)]
   ```

7. **Handle auth.** If the endpoints require authentication, the loopback call arrives
   unauthenticated by default and gets 401. See
   [references/auth-and-security.md](references/auth-and-security.md) before changing anything:
   forwarding credentials requires pinning `BaseAddress`.

8. **Build and fix diagnostics.** Run `dotnet build` and resolve every `MCPGEN` warning (see
   [references/troubleshooting.md](references/troubleshooting.md)). Do not suppress `MCPGEN002`
   with a pragma; acknowledge it with `AllowDestructive = true` or remove the tool.

9. **Verify.** Run the app and connect a client:

   ```bash
   claude mcp add --transport http my-api http://localhost:5000/mcp
   ```

   Use the app's real port from `Properties/launchSettings.json`. Confirm the tools appear and
   call one read-only tool end to end.

10. **Optional: measure token cost.**

    ```bash
    dotnet tool install -g McpIt.TokenReport.Tool
    mcp-token-report http://localhost:5000/mcp
    mcp-token-report http://localhost:5000/mcp --budget 2000   # exit 1 when over budget, for CI
    ```

## Many tools (McpIt 1.5.0+)

When an API exposes more than a dozen or so tools, help the agent find the right one:

- Tag tools with `Category`, `Keywords` (synonyms users might say) and `Priority`.
- Register the offline `search_tools` meta-tool:
  `.WithToolsFromAssembly().WithToolSearch(McpIt.Generated.McpItToolCatalog.Tools)`.
- Serve discovery documents (`/llms.txt`, MCP Server Card, `/.well-known/ai-catalog.json`) with
  `app.MapMcpDiscovery(McpItToolCatalog.Tools, o => { o.ServerName = "com.example/orders"; ... })`.

Details in [references/attributes.md](references/attributes.md).

## Rules

- Never annotate every endpoint by default. Agree the tool list with the user.
- Never enable `ForwardAuthorization` or `ForwardedHeaders` without setting `BaseAddress`
  (startup throws, by design, to stop credentials leaking to a spoofed `Host`).
- Keep the user's existing REST behavior unchanged. McpIt adds tools; it should not need edits
  to business logic.
- Prefer precise descriptions over more tools. If two tools would confuse an agent, merge or
  rename them.

## Further reading

- README: https://github.com/norequest/McpIt/blob/main/README.md
- FAQ: https://github.com/norequest/McpIt/blob/main/docs/faq.md
- Sample app: https://github.com/norequest/McpIt/tree/main/samples/SampleApi
