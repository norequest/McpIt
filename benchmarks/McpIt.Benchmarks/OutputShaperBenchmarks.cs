using BenchmarkDotNet.Attributes;
using McpIt;

namespace McpIt.Benchmarks;

/// <summary>
/// Benchmarks for <see cref="OutputShaper"/> covering the token/size reduction
/// hot paths: field projection on object and array payloads, maxLength truncation,
/// and the combined projection-then-truncation path.
///
/// Baseline is a passthrough call (no shaping) so all other cases show relative cost.
/// The [MemoryDiagnoser] shows heap allocations per operation, which matters because
/// projection allocates a MemoryStream and an Utf8JsonWriter for each call.
/// </summary>
[MemoryDiagnoser]
public class OutputShaperBenchmarks
{
    // A representative product-detail JSON object (~380 bytes / ~95 tokens).
    // Typical of a single-resource GET response shaped by [McpToolOutput(Fields=...)].
    private const string ProductJson =
        "{\"id\":\"prod-0042\",\"name\":\"Widget Pro 3000\",\"sku\":\"WGT-PRO-3K\",\"price\":149.99," +
        "\"currency\":\"USD\",\"category\":\"Electronics\",\"description\":\"High-performance widget " +
        "for professional use. Supports 4K output and USB-C connectivity with backward-" +
        "compatible USB-A adapter included.\",\"inStock\":true,\"quantity\":42," +
        "\"tags\":[\"electronics\",\"professional\",\"gadget\"]," +
        "\"createdAt\":\"2024-01-15T10:30:00Z\",\"updatedAt\":\"2024-06-01T08:00:00Z\"}";

    // A JSON array of 20 product summaries (~3 KB / ~750 tokens).
    // Typical of a list-endpoint response where only a few fields are needed.
    private static readonly string ProductArrayJson = BuildProductArray(20);

    private static readonly string[] TwoFields = ["id", "name"];
    private static readonly string[] FiveFields = ["id", "name", "sku", "price", "inStock"];

    private static string BuildProductArray(int count)
    {
        var items = Enumerable.Range(1, count).Select(i =>
            $"{{\"id\":\"prod-{i:D4}\",\"name\":\"Product {i}\",\"sku\":\"SKU-{i:D4}\"," +
            $"\"price\":{10.0 + i * 1.5:F2},\"currency\":\"USD\",\"inStock\":true," +
            $"\"quantity\":{i * 3},\"description\":\"Description for product {i} including " +
            $"features, compatibility notes, and warranty information.\"," +
            $"\"createdAt\":\"2024-01-{(i % 28) + 1:D2}T10:00:00Z\"}}");
        return "[" + string.Join(",", items) + "]";
    }

    // --- Baseline: no shaping (passthrough) ---

    [Benchmark(Baseline = true, Description = "Passthrough (no fields, no maxLength)")]
    public string NoShaping()
        => OutputShaper.Shape(ProductJson, maxLength: null, fields: null);

    // --- Field projection only ---

    [Benchmark(Description = "Project object to 2 fields (id, name)")]
    public string ProjectObject_TwoFields()
        => OutputShaper.Shape(ProductJson, maxLength: null, fields: TwoFields);

    [Benchmark(Description = "Project object to 5 fields (id, name, sku, price, inStock)")]
    public string ProjectObject_FiveFields()
        => OutputShaper.Shape(ProductJson, maxLength: null, fields: FiveFields);

    [Benchmark(Description = "Project array (20 items) to 2 fields each")]
    public string ProjectArray_TwoFields()
        => OutputShaper.Shape(ProductArrayJson, maxLength: null, fields: TwoFields);

    [Benchmark(Description = "Project array (20 items) to 5 fields each")]
    public string ProjectArray_FiveFields()
        => OutputShaper.Shape(ProductArrayJson, maxLength: null, fields: FiveFields);

    // --- MaxLength truncation only ---

    [Benchmark(Description = "Truncate to 200 chars (well below input length)")]
    public string MaxLengthOnly_200()
        => OutputShaper.Shape(ProductJson, maxLength: 200, fields: null);

    [Benchmark(Description = "MaxLength above input length (no-op truncation path)")]
    public string MaxLengthOnly_NoTruncation()
        => OutputShaper.Shape(ProductJson, maxLength: 10_000, fields: null);

    // --- Combined: projection then truncation ---

    [Benchmark(Description = "Project to 2 fields then truncate to 80 chars")]
    public string ProjectThenTruncate_Object()
        => OutputShaper.Shape(ProductJson, maxLength: 80, fields: TwoFields);

    [Benchmark(Description = "Project array to 2 fields then truncate to 512 chars")]
    public string ProjectThenTruncate_Array()
        => OutputShaper.Shape(ProductArrayJson, maxLength: 512, fields: TwoFields);
}
