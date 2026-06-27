using BenchmarkDotNet.Attributes;
using McpIt;

namespace McpIt.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="QueryStringBuilder.Build"/> covering typical usage
/// patterns in generated MCP tool invocations: all parameters supplied, a mix of
/// present and omitted (null) parameters, all omitted, and a single-parameter call.
/// </summary>
[MemoryDiagnoser]
public class QueryStringBuilderBenchmarks
{
    // Seven key=value pairs, all non-null: richest search/filter query.
    private static readonly string?[] SevenPairs_AllPresent =
    [
        "category=electronics",
        "minPrice=10.00",
        "maxPrice=500.00",
        "inStock=true",
        "sort=price_asc",
        "page=1",
        "pageSize=20"
    ];

    // Seven pairs with every other one null: optional filters left at default,
    // which is the most common generated-tool call pattern.
    private static readonly string?[] SevenPairs_HalfNull =
    [
        "category=electronics",
        null,
        "inStock=true",
        null,
        "page=1",
        null,
        "pageSize=20"
    ];

    // All null: no parameters supplied, method returns null immediately.
    private static readonly string?[] FourPairs_AllNull = [null, null, null, null];

    // Single pair: minimal one-parameter endpoint.
    private static readonly string?[] OnePair = ["id=prod-0042"];

    [Benchmark(Baseline = true, Description = "7 pairs, all present")]
    public string? Build_7AllPresent()
        => QueryStringBuilder.Build(SevenPairs_AllPresent);

    [Benchmark(Description = "7 pairs, half null (alternating)")]
    public string? Build_7HalfNull()
        => QueryStringBuilder.Build(SevenPairs_HalfNull);

    [Benchmark(Description = "4 pairs, all null (returns null)")]
    public string? Build_4AllNull()
        => QueryStringBuilder.Build(FourPairs_AllNull);

    [Benchmark(Description = "1 pair (minimal)")]
    public string? Build_1Pair()
        => QueryStringBuilder.Build(OnePair);
}
