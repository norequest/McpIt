# Changelog

All notable changes to McpIt are documented here.
Format: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/). Versioning follows [Semantic Versioning](https://semver.org/).

## [1.4.0] - 2026-06-27

### Added

- **Nested and array field projection.** `[McpToolOutput(Fields = ...)]` now accepts dot-path
  segments (`"customer.name"`) and array element markers (`"lines[].sku"`). Dot paths drill into
  nested objects; array markers project every element of an array down to the named sub-property.
- **`MaxItems` on `[McpToolOutput]`.** Caps how many array elements survive field projection
  before `MaxLength` truncation. Shaping order: project fields, cap items, truncate length.
- **Per-parameter descriptions.** XML `<param name="x">...</param>` doc comments on `[McpTool]`
  actions are emitted as `[Description]` attributes on the generated tool's input parameters
  and surfaced in the MCP `inputSchema` description field.
- **`Title` on `[McpTool]`.** `[McpTool(Title = "Friendly Name")]` sets the MCP tool `title`
  field. When omitted, McpIt derives a title from the method name in title case. All generated
  tools emit `openWorld: false`.
- **OpenTelemetry spans.** McpIt emits `Activity` spans via an `ActivitySource` named `"McpIt"`
  (span name `"mcpit.endpoint.invoke"`) around every loopback call. Tags follow OTel semantic
  conventions: `http.request.method`, `url.path`, `http.response.status_code`. Any exporter
  that subscribes to `"McpIt"` picks them up with no McpIt-specific packages required.
- **AOT-ready body serialization.** `McpEndpointsOptions.SerializerOptions` accepts a
  `JsonSerializerOptions` with a `TypeInfoResolver`. When set, the loopback request-body path
  uses `GetTypeInfo(bodyType)` instead of reflective `JsonSerializer.Serialize`, making it
  Native-AOT and trim safe. Omitting it falls back to reflective serialization (zero-config).
- **Benchmark harness** (`McpIt.Benchmarks`). BenchmarkDotNet micro-benchmarks for output
  shaping, query-string building, and tool-list sizing.
- **AOT publish smoke test** (`McpIt.AotCheck` + `benchmarks/aot-publish-smoke.sh`). Validates
  that `dotnet publish -r linux-x64` with AOT warnings-as-errors and trim analysis passes clean.
- **Per-tool auth scope gate (`RequiredScope`).** `[McpTool(RequiredScope = "scope:name")]` adds
  an OAuth scope check to the generated tool. The check runs before the loopback call using
  `McpScopeGuard.HasScope`; a denied check returns a structured JSON error without invoking the
  endpoint. Matching covers space-delimited `scope` claims and individual `scp`/`scope` claims.
  `IHttpContextAccessor` is injected automatically by `AddMcpEndpoints`.
- **Tool-manifest integrity hash (`McpItManifest` + `MapMcpManifest`).** `McpManifestGenerator`
  emits `McpIt.Generated.McpItManifest` at compile time with three constant members: `AggregateHash`
  (SHA-256 over all tool fingerprints), `Json` (manifest as a JSON string), and `ToolNames` (sorted
  array). `app.MapMcpManifest(McpIt.Generated.McpItManifest.Json)` serves it at `GET /mcp/manifest`.
  Snapshot `AggregateHash` in CI to detect tool-poisoning or unintended drift between deploys.
  v1 fingerprint scope: tool name, description, and parameter surface. `NamePrefix`, API-version
  suffixes, and lambda-handler routes are not reflected in the hash.
- **Minimal-API method-group and `MapGroup` support.** The generator reliably handles method-group
  handlers and walks `MapGroup` prefix chains at compile time. A named handler method carrying
  `[McpTool]` registered via `MapGet`/`MapPost`/etc. generates a tool whose loopback path combines
  all group prefixes with the endpoint route. Inline lambdas do not generate tools (Roslyn returns
  no symbol for a lambda passed to the `Delegate`-typed `Map` overloads); put `[McpTool]` on a
  named method instead.

## [1.3.0] - 2026-06-09

### Added

- **Multi-targeting: .NET 8, 9, and 10.** McpIt is now installable on .NET 8 and .NET 9 in
  addition to .NET 10. The source generator loads on .NET 8/9/10 SDK build hosts.
- **Enforced AOT gate.** The library is marked `IsAotCompatible`; the trim and AOT analyzers
  run on every build and surface warnings on user code that would break AOT.

## [1.2.0] - 2026-06-09

### Added

- **Authentication and header forwarding.** `McpEndpointsOptions.ForwardAuthorization` copies
  the incoming `Authorization` header onto each loopback call. `ForwardedHeaders` is a general
  allowlist for additional headers. Both are off by default. Forwarding requires an explicit
  `BaseAddress` to prevent credential leakage to a spoofed `Host` header.
- **`ThrowOnUnsuccessfulResponse`.** When set, non-2xx loopback responses throw
  `McpEndpointInvocationException` (carrying `StatusCode` and `ResponseBody`) instead of
  passing the error body through to the agent as a successful result.

## [1.1.1] - 2026-06-09

### Fixed

- Generator now filters out `CancellationToken` parameters (including nullable `CancellationToken?`)
  before classifying action parameters, eliminating CS0100 "duplicate parameter name" compile
  errors when an action declares a `CancellationToken` argument alongside McpIt-generated ones.

## [1.1.0] - 2026-06-09

### Added

- **Multi-targeting: .NET 8, 9, and 10.** Initial multi-TFM release.

## [1.0.1] - 2026-06-08

### Changed

- Package icon, revamped README, and marketing kit (`docs/marketing/`).

## [1.0.0] - 2026-06-08

Initial release. Build-time Roslyn source generator turning ASP.NET Core controller actions and
minimal-API endpoints into MCP tools via `[McpTool]` and `[McpToolOutput]` attributes. Includes
the `mcp-token-report` CLI tool for offline token-cost analysis.
