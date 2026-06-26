# McpIt Benchmarks

Two projects live under this directory.

---

## benchmarks/McpIt.Benchmarks

A BenchmarkDotNet console project (net10.0) that measures McpIt's runtime hot paths.

### What it measures

| Class | What is timed | Key metrics |
|---|---|---|
| `OutputShaperBenchmarks` | `OutputShaper.Shape` on a single JSON object and a 20-item array, across every combination of fields projection and maxLength truncation | ns/op, KB allocated per call, relative ratio to passthrough baseline |
| `QueryStringBuilderBenchmarks` | `QueryStringBuilder.Build` with all pairs present, half-null (typical generated-tool pattern), all-null, and single-pair | ns/op, allocations |
| `ToolsListSizeBenchmarks` | Constructing a tools/list JSON array for N tools (N=5/10/25/50), then shaping it to name+description and name-only projections | ns/op, byte lengths, inferred token estimates (bytes/4) printed by GlobalSetup |

The `ToolsListSizeBenchmarks` GlobalSetup also prints a one-line size report per tool count so the token-efficiency story is visible in the output without needing to post-process results.

### How to run

Build only (no JIT warm-up, confirms the project compiles):

```
dotnet build -f net10.0 benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj
```

Full benchmark run (Release mode is required for meaningful timings):

```
dotnet run -c Release -f net10.0 --project benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj
```

Dry run (executes each benchmark method once to verify correctness, no statistical timing):

```
dotnet run -c Release -f net10.0 --project benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj -- --dry-run
```

Filter to a single class:

```
dotnet run -c Release -f net10.0 --project benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj -- --filter *OutputShaper*
```

Results are written to `BenchmarkDotNet.Artifacts/` in the project directory.

---

## benchmarks/McpIt.AotCheck

A net10.0 console project that proves McpIt's AOT-safe runtime helpers are clean under the IL trim and AOT analyzers, without requiring the native AOT toolchain.

### How the check works

The project sets `IsAotCompatible`, `EnableTrimAnalyzer`, `EnableAotAnalyzer`, and `EnableSingleFileAnalyzer` to `true`. These cause the Roslyn compiler to run static IL dataflow analysis at build time and surface `IL2026` (RequiresUnreferencedCode) and `IL3050` (RequiresDynamicCode) as diagnostics. Both are promoted to errors via `<WarningsAsErrors>`, so a successful build is proof of zero such warnings for the exercised code paths.

### What is exercised

- `OutputShaper.Shape`: object projection, array projection, truncation, combined projection+truncation, invalid-JSON fallback.
- `QueryStringBuilder.Build`: mixed-null pairs, all-null, all-present.
- `McpEndpointInvocationException`: constructor + all properties.

### What is NOT exercised

Request-body serialization is intentionally excluded. That path currently uses a reflection-based `JsonSerializer` context and is not yet AOT-clean. After the `JsonSerializerContext` work lands, a corresponding assertion will be added here.

### How to run the build-time check

```
dotnet build -f net10.0 benchmarks/McpIt.AotCheck/McpIt.AotCheck.csproj
```

A clean build (exit code 0, no `IL2026`/`IL3050` errors) confirms the exercised helpers are AOT-compatible.

---

## benchmarks/aot-publish-smoke.sh

A CI-oriented bash script that performs a full `dotnet publish -p:PublishAot=true` of `McpIt.AotCheck` and fails if any McpIt-origin `IL2026`/`IL3050`/`IL30xx` warning appears in the output.

This script requires the native AOT toolchain (clang/LLVM on Linux, Xcode Command Line Tools on macOS). It is not expected to run on developer machines that lack the toolchain; its purpose is to document and enforce the CI gate.

Usage (from the repository root):

```
bash benchmarks/aot-publish-smoke.sh
```
