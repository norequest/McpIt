# McpIt attribute reference

## `[McpTool]`

On a controller action, a named minimal-API handler method, or an inline lambda. On a controller
class it only sets defaults (`NamePrefix`, `Category`) for that class's annotated actions and
exposes nothing by itself.

| Property | Purpose |
|---|---|
| `Name` | Tool name. Default: camelCase method name. Inline lambdas default to `{verb}_{route}`, e.g. `get_ping_name`. An explicit name disables the API-version suffix. |
| `Title` | Display title clients may show. Default: method name in title case (empty for lambdas). |
| `AllowDestructive` | Required acknowledgement for POST/PUT/PATCH/DELETE; otherwise `MCPGEN002`. |
| `RequiredScope` | OAuth scope the caller must have, checked before the loopback call. |
| `NamePrefix` | Class-level prefix applied to the names of its actions' tools. |
| `Category` | 1.5.0+. Grouping for search and discovery. Class-level value is the default. |
| `Keywords` | 1.5.0+. Synonyms an agent or user might use, e.g. `new[] { "purchase", "checkout" }`. |
| `Priority` | 1.5.0+. Tie-break in `search_tools` ranking. Negative values demote. |

### Safety hints from HTTP verbs

| Verb | readOnly | destructive | idempotent |
|---|---|---|---|
| GET | yes | no | yes |
| POST, PATCH | no | yes | no |
| PUT, DELETE | no | yes | yes |

All tools emit `openWorldHint: false`.

### Descriptions

- `<summary>` becomes the tool description; `<param name="x">` becomes the parameter description.
- Requires `<GenerateDocumentationFile>true</GenerateDocumentationFile>` in the project file.
- Inline lambdas have no XML docs; use `[Description("...")]` (System.ComponentModel) on the lambda.

### Validation constraints

DataAnnotations on parameters become JSON Schema constraints in the tool's input schema:

| Attribute | Schema |
|---|---|
| `[Range(min, max)]` | `minimum`, `maximum` |
| `[StringLength(max, MinimumLength = n)]` | `maxLength`, `minLength` |
| `[MinLength(n)]` / `[MaxLength(n)]` | `minLength` / `maxLength` |
| `[RegularExpression(p)]` | `pattern` |
| `[Required]` | `(required)` appended to the description |

Limitation: parameter default values are not carried over, so every parameter is listed as
required. Nullable parameters accept `null`, which the agent can pass to omit them.

## `[McpToolOutput]`

Shapes the endpoint's JSON response before it reaches the model. Order: project, cap, truncate.
Malformed JSON passes through untouched.

| Property | Purpose |
|---|---|
| `Fields` | Keep only these properties. Supports dot paths (`"customer.name"`) and array markers (`"lines[].sku"`). When the root is an array, applied to each element. |
| `MaxItems` | When the response root is an array, keep the first N elements. Does not cap nested arrays. |
| `MaxLength` | Truncate the final string to N characters. |

## Minimal APIs

```csharp
// Named handler
public static class ThingHandlers
{
    /// <summary>Gets a thing by its id.</summary>
    [McpTool(Name = "getThing")]
    public static string GetThing(int id) => $"thing-{id}";
}
var g = app.MapGroup("/api");               // MapGroup chains, including nested, are resolved
g.MapGet("/things/{id}", ThingHandlers.GetThing);

// Inline lambda
app.MapGet("/ping/{name}",
    [McpTool(Name = "ping")]
    [Description("Returns a greeting for the given name.")]
    (string name) => $"pong: {name}");
```

## API versioning

URL-segment versioning (`Asp.Versioning` or legacy `Microsoft.AspNetCore.Mvc.Versioning`) works
without changes. For routes containing `{version:apiVersion}`, the version is resolved from
`[MapToApiVersion]` on the method, then `[ApiVersion]` on the method, then on the controller
(highest wins). Derived names get a suffix such as `info_v1`, `info_v2`.

## Discovery and search (1.5.0+)

```csharp
using McpIt.Generated;

builder.Services.AddMcpServer()
    .WithHttpTransport(o => o.Stateless = true)
    .WithToolsFromAssembly()
    .WithToolSearch(McpItToolCatalog.Tools);   // offline BM25 search_tools meta-tool

app.MapMcpDiscovery(McpItToolCatalog.Tools, o =>
{
    o.ServerName = "com.example/orders";       // reverse-DNS, required while the Server Card is on
    o.ServerTitle = "Orders API";
    o.Description = "Look up and cancel customer orders.";
    o.PublicBaseUrl = new Uri("https://api.example.com/");
});
```

`McpItToolCatalog` is `internal` and generated per assembly. If the `[McpTool]` endpoints live
in a class library, expose it from that library, e.g.
`public static IReadOnlyList<McpToolDescriptor> McpTools => McpItToolCatalog.Tools;`.

## Tool-manifest hash

```csharp
app.MapMcpManifest(McpIt.Generated.McpItManifest.Json);   // GET /mcp/manifest
```

`McpItManifest.AggregateHash` fingerprints tool names, descriptions, verbs, routes and parameter
names and types. Snapshot it in CI to detect tool drift. Inline-lambda tools are not included.
