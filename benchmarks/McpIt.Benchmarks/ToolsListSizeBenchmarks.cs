using BenchmarkDotNet.Attributes;
using McpIt;
using System.Text;
using System.Text.Json;

namespace McpIt.Benchmarks;

/// <summary>
/// Estimates the byte and approximate token cost of MCP tools/list payloads at varying
/// tool counts, then measures the shaping time and size reduction achieved by
/// <see cref="OutputShaper"/>.
///
/// Token estimate: bytes / 4 (rough approximation for ASCII JSON, similar to the GPT/Claude
/// tokenizer average for this kind of structured data).
///
/// The benchmark methods return byte counts so BenchmarkDotNet does not elide the work.
/// The <c>[GlobalSetup]</c> also prints exact size figures to the console once per
/// parameter value, making the token-efficiency story visible in the benchmark output.
///
/// This benchmark is SELF-CONTAINED: the tool JSON is defined inline. It does NOT
/// depend on the McpIt source generator running.
/// </summary>
[MemoryDiagnoser]
public class ToolsListSizeBenchmarks
{
    /// <summary>Number of MCP tool definitions in the payload.</summary>
    [Params(5, 10, 25, 50)]
    public int ToolCount { get; set; }

    private string _toolsArray = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _toolsArray = BuildToolsArray(ToolCount);

        var rawBytes = Encoding.UTF8.GetByteCount(_toolsArray);
        var nameDescShaped = OutputShaper.Shape(_toolsArray, maxLength: null, fields: ["name", "description"]);
        var nameDescBytes = Encoding.UTF8.GetByteCount(nameDescShaped);
        var nameOnlyShaped = OutputShaper.Shape(_toolsArray, maxLength: null, fields: ["name"]);
        var nameOnlyBytes = Encoding.UTF8.GetByteCount(nameOnlyShaped);

        // Printed once per ToolCount value before the timed iterations.
        Console.WriteLine(
            $"[ToolsListSizeBenchmarks] N={ToolCount,2}: " +
            $"raw={rawBytes,6}B (~{rawBytes / 4,4} tokens) | " +
            $"name+desc={nameDescBytes,5}B (~{nameDescBytes / 4,3} tokens, " +
            $"{100 - nameDescBytes * 100 / rawBytes,2}% smaller) | " +
            $"name-only={nameOnlyBytes,4}B (~{nameOnlyBytes / 4,3} tokens, " +
            $"{100 - nameOnlyBytes * 100 / rawBytes,2}% smaller)");
    }

    /// <summary>
    /// Baseline: measure the byte length of the full unshappped tools array.
    /// In practice this represents the cost of returning all tool metadata verbatim.
    /// </summary>
    [Benchmark(Baseline = true, Description = "Raw tools array byte length")]
    public int ByteLength_Raw()
        => Encoding.UTF8.GetByteCount(_toolsArray);

    /// <summary>
    /// Project each tool entry to {name, description}: strips inputSchema but keeps
    /// enough context for an LLM to select the right tool.
    /// </summary>
    [Benchmark(Description = "Project name + description (bytes)")]
    public int ByteLength_NameAndDescription()
    {
        var shaped = OutputShaper.Shape(_toolsArray, maxLength: null, fields: ["name", "description"]);
        return Encoding.UTF8.GetByteCount(shaped);
    }

    /// <summary>
    /// Project each tool entry to {name} only: maximum token reduction when the caller
    /// only needs a tool list for discovery (not parameter details).
    /// </summary>
    [Benchmark(Description = "Project name only (bytes)")]
    public int ByteLength_NameOnly()
    {
        var shaped = OutputShaper.Shape(_toolsArray, maxLength: null, fields: ["name"]);
        return Encoding.UTF8.GetByteCount(shaped);
    }

    /// <summary>
    /// Build a JSON array representing N MCP tool definitions.
    /// Each tool has a realistic name, verbose description, and a 3-parameter
    /// inputSchema. Defined inline so this benchmark has no generator dependency.
    /// </summary>
    private static string BuildToolsArray(int toolCount)
    {
        var tools = Enumerable.Range(1, toolCount).Select(i => new
        {
            name = $"get_product_{i:D3}",
            description =
                $"Retrieves the full product record for the given identifier from the catalog. " +
                $"Returns name, SKU, pricing tiers, current stock level, category metadata, " +
                $"and audit timestamps. Tool {i} of {toolCount} in the products endpoint group.",
            inputSchema = new
            {
                type = "object",
                properties = new Dictionary<string, object>
                {
                    ["id"] = new { type = "string", description = "Unique product identifier (UUID v4)." },
                    ["includePricing"] = new { type = "boolean", description = "Include tiered pricing information in the response." },
                    ["locale"] = new { type = "string", description = "BCP-47 locale for localized name and description fields (e.g. en-US, ka-GE)." }
                },
                required = new[] { "id" }
            }
        });

        return JsonSerializer.Serialize(tools);
    }
}
