# Troubleshooting McpIt

## Build diagnostics

| Id | Meaning | Fix |
|---|---|---|
| `MCPGEN001` | Tool has no description. | Add a `<summary>` and enable `<GenerateDocumentationFile>true</GenerateDocumentationFile>`. For lambdas, use `[Description]`. |
| `MCPGEN002` | Destructive verb (POST/PUT/PATCH/DELETE) exposed without acknowledgement. | Confirm with the user, then `[McpTool(AllowDestructive = true)]`; or remove `[McpTool]`. |
| `MCPGEN003` | Route has `{version:apiVersion}` but no version could be resolved. | Add `[ApiVersion]` on the controller or `[MapToApiVersion]` / `[ApiVersion]` on the action. |
| `MCPGEN004` (info, 1.5.0+) | Description is under four words or only repeats the name/title. | Describe what the tool does and when to use it. |
| `MCPGEN005` (info, 1.5.0+) | A parameter has no description. | Add `<param name="...">`. |

Enabling `GenerateDocumentationFile` can surface CS1591 warnings for public members without XML
docs. If the project treats warnings as errors, add `<NoWarn>$(NoWarn);CS1591</NoWarn>` rather
than skipping the docs.

## Tools do not appear in `tools/list`

- `.WithToolsFromAssembly()` is missing, or the `[McpTool]` endpoints are in another assembly.
  Use `.WithToolsFromAssembly(typeof(SomeTypeInThatAssembly).Assembly)`.
- `AddMcpEndpoints()` is missing (tools fail at call time instead).
- `app.MapMcp("/mcp")` is missing, or the client points at the wrong port or path.
- The project did not rebuild. Inspect generated code with
  `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>` and look under
  `obj/**/generated/McpIt.Generator/`.
- `[McpTool]` on the controller class alone exposes nothing; it must be on each action.
- A minimal-API route that is not a string literal (a constant, variable or interpolated
  string) in `MapGet`/`MapPost`/etc. is skipped without a warning. Use a literal route.

## Tool call returns 401 / 403, or a "forbidden" result

A 401 or 403 from the endpoint means the loopback call is unauthenticated. See
`auth-and-security.md`. A tool result of `{"error":"forbidden",...}` comes from the
`RequiredScope` gate, before the endpoint is called: the caller's token lacks the scope, or
JwtBearer claim mapping renamed `scp` (`MapInboundClaims = false`).

## Tool call returns 404

- Versioned route without a resolvable version (also `MCPGEN003`).
- A `MapGroup` prefix the generator could not resolve (not a string literal), so the loopback
  path is missing it. Use literals in `MapGroup` too.
- `BaseAddress` points at the wrong host or path base. With `UsePathBase`, include the base.

## Startup throws about BaseAddress

`ForwardAuthorization` or `ForwardedHeaders` is set without `BaseAddress`. This is intentional;
set `BaseAddress` to the app's trusted URL.

## Responses are huge or the agent gets lost

- Add `[McpToolOutput(Fields = ..., MaxItems = ..., MaxLength = ...)]`.
- Run `mcp-token-report http://localhost:<port>/mcp` to find the costliest tools.
- Reduce the tool count, sharpen descriptions, and on 1.5.0+ add `Keywords` and `WithToolSearch`.

## Native AOT

Not supported for apps with generated tools yet: generated tools call an invoker marked
`[RequiresUnreferencedCode]`/`[RequiresDynamicCode]` (IL2026/IL3050), and MVC is not AOT-
compatible. A `JsonSerializerContext` via `AddMcpEndpoints(o => o.SerializerOptions = ...)` keeps
request-body serialization off the reflection path.
