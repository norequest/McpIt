using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

/// <summary>
/// Tests for D2: Title and OpenWorld annotations on [McpServerTool(...)].
/// </summary>
public class TitleAndOpenWorldEmitTests
{
    private static string Wrap(string method) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;
        [Route("orders")]
        public class OrdersController : ControllerBase { {{method}} }
        """;

    [Fact]
    public void Derived_title_splits_pascal_case_method_name()
    {
        var src = Wrap("""
            /// <summary>Gets an order by id.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrderById(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("Title = \"Get Order By Id\"", generated);
    }

    [Fact]
    public void Explicit_title_overrides_derived_title()
    {
        var src = Wrap("""
            /// <summary>Gets an order.</summary>
            [HttpGet("{id}")]
            [McpTool(Title = "Fetch Single Order")]
            public string GetOrder(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("Title = \"Fetch Single Order\"", generated);
        Assert.DoesNotContain("Title = \"Get Order\"", generated);
    }

    [Fact]
    public void OpenWorld_is_always_false()
    {
        var src = Wrap("""
            /// <summary>Gets an order.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrder(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("OpenWorld = false", generated);
    }

    [Fact]
    public void Simple_one_word_method_name_is_unchanged()
    {
        var src = Wrap("""
            /// <summary>Creates an order.</summary>
            [HttpPost]
            [McpTool(AllowDestructive = true)]
            public string Create() => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("Title = \"Create\"", generated);
    }

    [Fact]
    public void Title_is_present_on_all_tools_including_ones_with_explicit_name()
    {
        var src = Wrap("""
            /// <summary>Creates an order.</summary>
            [HttpPost]
            [McpTool(Name = "createOrder", AllowDestructive = true)]
            public string Create() => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        // Name and Title are independent: explicit Name does not suppress Title derivation.
        Assert.Contains("Name = \"createOrder\"", generated);
        Assert.Contains("Title = \"Create\"", generated);
        Assert.Contains("OpenWorld = false", generated);
    }

    [Fact]
    public void Title_and_openworld_generated_code_compiles_clean()
    {
        var src = Wrap("""
            /// <summary>Gets an order by id.</summary>
            [HttpGet("{id}")]
            [McpTool(Title = "Get Order")]
            public string GetOrderById(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile cleanly:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    [Fact]
    public void Title_containing_quotes_is_escaped()
    {
        var src = Wrap("""
            /// <summary>Gets an order.</summary>
            [HttpGet("{id}")]
            [McpTool(Title = "Get \"Special\" Order")]
            public string GetOrder(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("Title = \"Get \\\"Special\\\" Order\"", generated);
    }

    [Fact]
    public void GetOrders_derives_title_get_orders()
    {
        var src = Wrap("""
            /// <summary>Lists all orders.</summary>
            [HttpGet]
            [McpTool]
            public string GetOrders() => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("Title = \"Get Orders\"", generated);
    }
}
