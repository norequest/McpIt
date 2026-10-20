# Auth and security with McpIt

## Why protected endpoints return 401

Generated tools call the endpoint through a loopback HTTP request. By default that request
carries no headers, so `[Authorize]` endpoints reject it. Forward what the MCP client sent:

```csharp
builder.Services.AddMcpEndpoints(options =>
{
    options.BaseAddress = new Uri("https://localhost:5001/"); // required when forwarding
    options.ForwardAuthorization = true;                      // copy the Authorization header
    options.ForwardedHeaders.Add("X-Api-Key");                // and others by name
});
```

- Forwarding only works when the MCP client authenticated to `/mcp` itself, so there is a header
  to copy. Protect the MCP endpoint, e.g. `app.MapMcp("/mcp").RequireAuthorization();`.
- **`BaseAddress` is mandatory when forwarding.** Without it, the loopback host comes from the
  client-controlled `Host` header, which could send credentials to a spoofed host, so startup
  throws. Set it to the app's own trusted address, read from configuration in production.
- For service-to-service credentials that do not depend on the caller, register a
  `DelegatingHandler` on the typed `IMcpEndpointInvoker` HttpClient instead of forwarding.

## Make failures visible to the agent

```csharp
options.ThrowOnUnsuccessfulResponse = true; // non-2xx throws McpEndpointInvocationException
```

Without this, a 401/500 body is returned to the agent as if it were a normal result.

## Per-tool scope gate

```csharp
/// <summary>Cancels an order. Requires the "orders:write" scope.</summary>
[HttpDelete("{id}")]
[McpTool(Name = "cancelOrder", AllowDestructive = true, RequiredScope = "orders:write")]
public ActionResult<Order> CancelOrder(int id) { ... }
```

- Reads `scope` and `scp` claims, splits on spaces, exact match.
- With JwtBearer, set `options.MapInboundClaims = false`, otherwise `scp` is renamed to a URI
  claim type the gate does not read.
- On failure the tool returns
  `{"error":"forbidden","tool":"cancelOrder","requiredScope":"orders:write"}` without calling the
  endpoint.

## Checklist before exposing a write endpoint

1. The user explicitly agreed the agent may perform this action.
2. The endpoint already enforces authorization for normal HTTP callers. McpIt does not bypass it.
3. Consider `RequiredScope` so only agents granted that scope can use the tool.
4. The description says plainly what the action changes and that it cannot be undone, if so.
5. `[McpToolOutput]` keeps sensitive response fields (emails, tokens, internal ids) away from
   the model where they are not needed.
