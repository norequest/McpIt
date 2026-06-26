# McpIt vNext: Token Efficiency, AOT, Safety/Observability, Minimal APIs

Date: 2026-06-27
Status: Proposed
Target: McpIt 1.4.0 (Phase 1), 1.5.0 (Phase 2), spike for 1.6.0 (Phase 3)

## Context

McpIt is a build-time C# source generator. `[McpTool]` on an ASP.NET Core controller
action emits a `[McpServerToolType]` static class whose `Invoke` method loopback-calls the
endpoint through `IMcpEndpointInvoker`. The official `ModelContextProtocol` SDK registers
those generated classes and, at runtime via reflection, derives each tool's input schema
from the generated method signature plus `[System.ComponentModel.Description]`.

Two facts from the current code drive this design:

1. **McpIt does not author input schemas.** `Emitter.cs` emits typed parameters; the SDK's
   `AIFunctionFactory` builds the JSON schema from those types and from `[Description]`
   attributes at runtime. So "richer schemas" is achieved by *propagating metadata onto the
   generated parameters*, not by hand-writing JSON Schema (which would mean bypassing the
   SDK registration path).
2. **The body path is the only reflective hole.** `BuildBodyExpression` emits
   `JsonSerializer.Serialize(body)` (reflection-based). GET/read tools are already
   reflection-free. Closing this one call makes the request path fully AOT-clean.

This plan covers four tracks chosen by the maintainer. The four tracks are **not** equally
parallelizable: the generator core (`Emitter.cs`, `ModelBuilder.cs`, `EndpointModel.cs`) is
a shared hot path touched by three of them, so generator-side work is serialized into one
coherent change while the genuinely disjoint pieces (runtime shaping, benchmarks) run in
parallel.

## Goals

- Track A. Token efficiency: nested field projection, array item capping, per-parameter
  descriptions surfaced into the input schema.
- Track B. Minimal API endpoints become tools (not just controllers).
- Track C. AOT-clean request path (no reflective body serialization) plus a benchmark and
  AOT-publish smoke harness that makes the "only fully AOT-clean MCP toolkit for .NET" claim
  provable.
- Track D. Safety and observability: per-tool OpenTelemetry spans, refined tool annotations,
  tool-manifest integrity hash, optional per-tool auth scope gate.

## Non-goals (this iteration)

- MCP resources, prompts, completions, sampling, elicitation. Out of scope; tools only.
- Hand-authored JSON Schema that bypasses the SDK. We stay on the SDK registration path.
- `[FromForm]` / `IFormFile` / multipart uploads and streaming/binary responses.
- The 2026-07-28 stateless RC. We target spec 2025-11-25 semantics.

---

## Track A: Token-efficiency engine

### A1. Nested field projection (dot and array paths)

Today `OutputShaper.Project` keeps only top-level properties. Extend `Fields` to accept
path expressions:

- `customer.name` walks into a nested object.
- `items[].sku` projects a field from every element of an array property.
- A bare `id` keeps the existing top-level behavior (fully backward compatible).

Design: parse each field path into segments once, then rebuild the JSON with `Utf8JsonWriter`
preserving the requested shape. Unknown paths are skipped silently (matching today's
best-effort contract: never throw, fall back to original on parse failure). Projection still
runs before truncation.

Owner: `src/McpIt/OutputShaper.cs` (runtime, isolated). No generator change beyond passing
the same `Fields` array, which already flows through.

### A2. Array item capping (`MaxItems`)

Add `MaxItems` to `[McpToolOutput]`. When the response is a JSON array, keep only the first
`MaxItems` elements. List endpoints are the worst token offenders; capping at the source is
the highest-leverage token win. Order of operations: project fields, then cap items, then
truncate length.

Owner: `src/McpIt.Abstractions/McpToolOutputAttribute.cs` (new property),
`ModelBuilder.GetOutputShaping` (read it), `EndpointModel` (carry it), `Emitter` (pass it to
`OutputShaper.Shape`), `OutputShaper.Shape` (new parameter). This crosses the generator core,
so it is part of the serialized generator change.

### A3. Per-parameter descriptions from XML `<param>` docs

`ModelBuilder` already reads the method `<summary>`. Also read each `<param name="x">` doc
and emit `[System.ComponentModel.Description("...")]` on the corresponding generated
parameter. The SDK surfaces these in the tool's input schema, which measurably improves
tool-calling accuracy. This is the achievable, reflection-free slice of "richer schemas":
metadata propagation, no schema authoring.

Owner: `ModelBuilder` (parse `<param>`), `ParameterModel` (carry `Description`), `Emitter`
(emit the attribute on each parameter). Part of the serialized generator change.

### A-stretch (Phase 3 spike). Validation-constraint schema

`[Range]`, `[StringLength]`, `[RegularExpression]`, enums into JSON Schema keywords
(`minimum`, `maxLength`, `pattern`, `enum`). This depends on whether the SDK's schema builder
honors DataAnnotations; if not, it requires either propagating the attributes onto generated
params (and a schema-transform hook) or emitting explicit schema. Deferred to a spike because
the SDK interaction is unverified.

---

## Track B: Minimal API support (Phase 2, spike first)

Controllers expose verb and route via `[HttpGet("...")]` attributes that `ModelBuilder`
reads. Minimal APIs put verb and route in the `app.MapGet("/route", handler)` invocation, so
there is no attribute on a method declaration for the existing `ForAttributeWithMetadataName`
pipeline to find.

Approach: add a second incremental pipeline keyed on `Map{Verb}` invocation syntax. For each
`MapGet/MapPost/MapPut/MapPatch/MapDelete` call:

1. Recover the verb from the `Map*` method name and the route from the first string argument.
2. Resolve the handler to an `IMethodSymbol` (method-group handler such as `MapGet("/x",
   Handlers.GetThing)`, or an attributed lambda where C# allows attributes on the lambda).
3. Require `[McpTool]` on that handler symbol to stay opt-in and consistent with controllers.
4. Reuse `ModelBuilder` parameter classification and description extraction unchanged.

Spike question to resolve before committing the full design: can Roslyn reliably resolve the
route literal, verb, and handler symbol across the common minimal-API shapes (inline lambda,
method group, `RouteGroupBuilder` prefixes via `MapGroup`)? The spike builds the smallest
pipeline that turns one `MapGet` with a method-group handler into a working tool, then we
extend. Risk is real; this is why it is Phase 2 and gated on a spike rather than bundled with
Phase 1.

---

## Track C: AOT-clean request path + benchmarks

### C1. Reflection-free body serialization

Collect every distinct body-parameter type across all discovered endpoints. Emit one
`partial` `JsonSerializerContext` (`McpItJsonContext`) annotated with `[JsonSerializable(
typeof(T))]` for each body type, in the generated namespace. Change `BuildBodyExpression`
from `JsonSerializer.Serialize(body)` to
`JsonSerializer.Serialize(body, McpItJsonContext.Default.{Type})`.

Why this works: the emitted context is itself processed by System.Text.Json's own source
generator in the same compilation, so the (de)serialization metadata is generated at build
time with no runtime reflection. This requires McpIt to emit one extra source file that
aggregates all body types (a new collection step in the generator pipeline rather than the
current per-endpoint `RegisterSourceOutput`).

Edge cases to handle: no body types (emit nothing), duplicate types (dedupe), generic and
nested types (use fully-qualified names already available from `ParameterClassifier`).

### C2. Benchmark and AOT-publish smoke harness

New `benchmarks/McpIt.Benchmarks` project (BenchmarkDotNet) measuring generated-tool dispatch
and `tools/list` payload size, plus a scripted `dotnet publish -p:PublishAot=true` of a small
sample that asserts zero trim/AOT analyzer warnings. Results recorded in
`docs/benchmarks-results.md`. This makes the AOT and token claims measurable and regression
-guarded rather than rhetorical.

Owner: `benchmarks/**` and a publish script (fully disjoint, parallel-safe). C1 is part of the
serialized generator change.

---

## Track D: Safety and observability

### D1. OpenTelemetry spans

Add a static `ActivitySource` named `McpIt` in the runtime (`ActivitySource` ships in the
ASP.NET Core shared framework, so no new package). The generated `Invoke` starts an activity
named after the tool and tags it with tool name, HTTP method, resolved status, and duration;
`HttpClientMcpEndpointInvoker` enriches it. Standard OTel exporters pick it up. Closes the
"MCP servers ship with no observability" gap and is purely additive.

Owner: new `src/McpIt/McpItActivitySource.cs`, small `Emitter` wrap, `HttpClientMcpEndpointInvoker`
tags. The Emitter wrap is part of the serialized generator change.

### D2. Refined annotations (`Title`, `OpenWorld`)

The SDK's `[McpServerTool]` supports `Title` and `OpenWorldHint`. Emit a human-friendly
`Title` (from an explicit `[McpTool(Title=...)]` or a prettified method name) and set
`OpenWorld=false` by default (loopback to our own endpoints is a closed world). Small, drives
better client auto-approve UX.

Owner: `McpToolAttribute` (new `Title`), `ModelBuilder`, `Emitter`. Serialized generator change.

### D3. Tool-manifest integrity hash (Phase 2)

Emit a generated `McpItManifest` exposing a stable hash over each tool's (name, description,
verb, route, parameter shape). Clients/CI can compare the deployed manifest against an
expected hash to detect post-deploy tool-poisoning or drift (tool poisoning is CVE-tracked).
Optional `MapMcpManifest` endpoint to serve it. Phase 2 because it needs a new aggregate
generator output and an endpoint surface.

### D4. Per-tool auth scope gate (Phase 2)

`[McpTool(RequiredScope="orders:write")]` makes the generated `Invoke` check the current
`ClaimsPrincipal` (via `IHttpContextAccessor`, already registered) for the scope before the
loopback call, returning a structured error if absent. Defense in depth at the tool boundary.
Phase 2.

---

## Phased build plan

**Phase 1 (1.4.0), build now.** Two parallel waves around the shared generator core.

- Wave 1 (parallel, disjoint files, TDD each):
  - Runtime shaping: A1 nested projection + A2 cap logic in `OutputShaper.cs` and new tests.
  - Benchmarks + AOT smoke: C2 new `benchmarks/**` project and publish script.
- Wave 2 (single coherent generator change after Wave 1, TDD): A2 plumbing, A3 param
  descriptions, C1 `JsonSerializerContext`, D1 OTel wrap, D2 annotations. One agent owns
  `Emitter.cs`, `ModelBuilder.cs`, `EndpointModel.cs`, `ParameterClassifier.cs`, the two
  abstraction attributes, and generator tests, because they all converge on the emit pipeline.
- Wave 3: integration test updates, sample app demonstrates new attributes, README, CHANGELOG.

**Phase 2 (1.5.0).** B minimal APIs (spike then build), D3 manifest hash, D4 auth scope.

**Phase 3 (1.6.0).** A-stretch validation-constraint schema, after a spike confirms the SDK
schema-builder interaction.

## Testing strategy

- Generator tests assert emitted source text for each new behavior (the existing
  `GeneratorTestHarness` pattern), including: nested `Fields`, `MaxItems`, `<param>` ->
  `[Description]`, body serialization via `McpItJsonContext`, activity wrap, `Title`/`OpenWorld`.
- Runtime tests cover `OutputShaper` paths: nested object, `items[].field`, `MaxItems` on
  arrays and on non-arrays (no-op), order of operations, malformed-JSON fallback, `MaxItems=0`.
- Integration tests extend `EndToEndToolInvocationTests` for a capped/projected list endpoint
  and a body endpoint serialized via the generated context.
- AOT smoke: `dotnet publish -p:PublishAot=true` of the sample yields zero IL2026/IL3050
  warnings on the request path.
- All work runs `dotnet test -f net10.0` locally (net9 runtime absent in dev; CI covers all
  TFMs). Baseline before changes: 101 passing on net10.0.

## Risks

- Minimal API resolution (Track B) is the main unknown; isolated to Phase 2 and spike-gated.
- `JsonSerializerContext` emission interacts with STJ's own generator; verify ordering and
  that user body types in other assemblies are referenceable. Fallback: keep the reflective
  path behind a flag if a body type cannot be added to the context.
- Backward compatibility: all new attribute members are additive and default to today's
  behavior. Existing generated output for existing inputs must not change except for the
  (now context-backed) body call and the additive activity wrap.

---

## Implementation notes and findings (2026-06-27)

### C1 correction: source generators cannot feed STJ's generator

The original C1 plan (emit a `[JsonSerializable]` partial `JsonSerializerContext` for McpIt to
have System.Text.Json's own source generator fill in) does not work. Roslyn source generators
all run against the same input compilation and cannot see each other's emitted sources within a
pass, so STJ's generator never observes a context McpIt emits. There is also no fully
trim/AOT-clean `JsonSerializer` overload that generated code can call without naming a concrete
`JsonTypeInfo<T>`, which would require the consumer's context type to be known at emit time.

Conclusion: truly zero-config AOT body serialization is not achievable from the generator alone.
The delivered design is the honest escape hatch, which is also what the official MCP SDK itself
requires for AOT: generated code carries no `JsonSerializer` call and passes `(object body,
Type bodyType)` to the invoker; `McpEndpointsOptions.SerializerOptions` lets the consumer supply
a source-generated context so the body path is reflection-free, with a reflective fallback when
not supplied. The library stays analyzer-clean by isolating and annotating the reflective branch.

### Delivered in 1.4.0 (this branch)

- A1 nested/array field projection, A2 `MaxItems` capping (runtime + generator).
- A3 per-parameter descriptions from XML `<param>` docs into the input schema.
- C1 AOT-ready body path (escape-hatch design above) and C2 benchmark + AOT-analyzer harness.
- D1 OpenTelemetry spans (invoker side), D2 refined `Title` / `OpenWorld` annotations.

### Delivered in Phase 2 (same branch)

- Track B minimal APIs, expanded: method-group handlers and `MapGroup` prefix chaining now
  generate tools. Caveat: inline lambda handlers passed to the real
  `WebApplication.MapGet(string, Delegate)` overload still do not generate, because Roslyn
  returns no symbol for a lambda bound to a `Delegate` parameter. The supported rule is "put
  `[McpTool]` on a named handler method, not an inline lambda." Lambda support resolves only
  against a generic test stub today.
- D4 per-tool auth scope: `[McpTool(RequiredScope = "...")]` injects `IHttpContextAccessor`
  into the generated tool and gates the call via `McpScopeGuard` (matches space-delimited
  `scope` and `scp` claims), returning a structured JSON error when the scope is absent.
- D3 tool-manifest integrity hash: a second `[Generator]` emits `McpIt.Generated.McpItManifest`
  (order-independent SHA-256 `AggregateHash` plus a constant `Json`), served via
  `app.MapMcpManifest(...)`. It skips tool-less assemblies so it does not collide with a
  consumer's manifest. V1 fingerprint covers tool name, description, and parameter surface;
  `NamePrefix`, API-version suffixes, and lambda routes are not yet reflected.

### Deferred to Phase 3

- A-stretch validation-constraint schema (`[Range]`/`[StringLength]`/`[RegularExpression]`/
  enum to JSON Schema keywords), gated on verifying the SDK schema-builder interaction.
- Minimal-API lambda handlers against the real `Delegate`-typed overloads; manifest v1
  derivation gaps (prefix/version/lambda); manifest coverage of minimal-API route identity.
