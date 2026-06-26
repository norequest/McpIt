using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

/// <summary>
/// Tests for A2: MaxItems output capping emitted from [McpToolOutput(MaxItems = N)].
/// </summary>
public class MaxItemsEmitTests
{
    private static string Wrap(string method) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;
        [Route("items")]
        public class ItemsController : ControllerBase { {{method}} }
        """;

    [Fact]
    public void MaxItems_only_generates_async_shaper_call_with_fourth_arg()
    {
        var src = Wrap("""
            /// <summary>Lists items.</summary>
            [HttpGet]
            [McpTool]
            [McpToolOutput(MaxItems = 10)]
            public string List() => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("async global::System.Threading.Tasks.Task<string>", generated);
        Assert.Contains("global::McpIt.OutputShaper.Shape", generated);
        // MaxItems = 10 as fourth arg, no MaxLength so null, no Fields so null
        Assert.Contains("Shape(__r, null, null, 10)", generated);
    }

    [Fact]
    public void MaxItems_with_MaxLength_and_Fields_passes_all_four_args()
    {
        var src = Wrap("""
            /// <summary>Lists items with full shaping.</summary>
            [HttpGet]
            [McpTool]
            [McpToolOutput(MaxLength = 200, Fields = new[]{"id","name"}, MaxItems = 5)]
            public string List() => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("OutputShaper.Shape", generated);
        Assert.Contains("200", generated);
        Assert.Contains("\"id\"", generated);
        Assert.Contains("\"name\"", generated);
        Assert.Contains(", 5)", generated);
    }

    [Fact]
    public void MaxItems_with_zero_value_does_not_trigger_shaping()
    {
        // MaxItems = 0 is treated as "not set" (matches > 0 guard in ModelBuilder).
        var src = Wrap("""
            /// <summary>Lists items.</summary>
            [HttpGet]
            [McpTool]
            [McpToolOutput(MaxItems = 0)]
            public string List() => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.DoesNotContain("OutputShaper", generated);
        Assert.DoesNotContain("async global::System.Threading.Tasks.Task<string>", generated);
    }

    [Fact]
    public void MaxItems_generated_code_compiles_clean()
    {
        var src = Wrap("""
            /// <summary>Lists items.</summary>
            [HttpGet]
            [McpTool]
            [McpToolOutput(MaxItems = 10)]
            public string List() => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile cleanly:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    [Fact]
    public void Existing_shaping_call_now_uses_four_arg_overload()
    {
        // Previously the emitter called the 3-arg Shape; after A2 it always calls the 4-arg
        // overload. A MaxLength-only attribute should still pass null as the fourth arg.
        var src = Wrap("""
            /// <summary>Gets an item.</summary>
            [HttpGet("{id}")]
            [McpTool]
            [McpToolOutput(MaxLength = 100)]
            public string Get(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("Shape(__r, 100, null, null)", generated);
    }
}
