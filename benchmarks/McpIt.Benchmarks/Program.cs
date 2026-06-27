using BenchmarkDotNet.Running;

// Run all benchmarks in this assembly.
//
// Full benchmark run (Release mode required for accurate timings):
//   dotnet run -c Release -f net10.0 --project benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj
//
// List all benchmarks without running them:
//   dotnet run -c Release -f net10.0 --project benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj -- --list flat
//
// Dry run (verifies benchmark methods compile and execute once, no timing):
//   dotnet run -c Release -f net10.0 --project benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj -- --dry-run
//
// Run a specific class by filter:
//   dotnet run -c Release -f net10.0 --project benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj -- --filter *OutputShaper*
//
// Build-only check (no run needed to satisfy the benchmark harness requirement):
//   dotnet build -f net10.0 benchmarks/McpIt.Benchmarks/McpIt.Benchmarks.csproj

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
