using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

/// <summary>
/// Tests for A3: per-parameter descriptions from XML &lt;param&gt; doc comments
/// are emitted as [Description("...")] parameter attributes.
/// </summary>
public class ParamDescriptionEmitTests
{
    private static string Wrap(string method) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;
        [Route("orders")]
        public class OrdersController : ControllerBase { {{method}} }
        """;

    [Fact]
    public void Param_with_xml_doc_emits_description_attribute()
    {
        var src = Wrap("""
            /// <summary>Gets an order.</summary>
            /// <param name="id">The unique order identifier.</param>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrder(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("[global::System.ComponentModel.Description(\"The unique order identifier.\")]", generated);
        Assert.Contains("global::System.Int32 id", generated);
    }

    [Fact]
    public void Param_without_doc_does_not_get_description_attribute()
    {
        var src = Wrap("""
            /// <summary>Gets an order.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrder(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        // No [Description(...)] on the id parameter (the method-level [Description] is still there)
        Assert.DoesNotContain("[global::System.ComponentModel.Description(\"", generated
            .Replace("[global::System.ComponentModel.Description(\"Gets an order.\")]", ""));
    }

    [Fact]
    public void Multiple_params_each_get_their_own_description_attribute()
    {
        var src = Wrap("""
            /// <summary>Searches orders.</summary>
            /// <param name="status">Filter by order status.</param>
            /// <param name="limit">Max number of results to return.</param>
            [HttpGet]
            [McpTool]
            public string Search(string? status, int? limit) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        Assert.Contains("Filter by order status.", generated);
        Assert.Contains("Max number of results to return.", generated);
    }

    [Fact]
    public void Only_documented_params_get_description_attribute_undocumented_params_are_plain()
    {
        var src = Wrap("""
            /// <summary>Gets page of orders.</summary>
            /// <param name="page">The page number (one-based).</param>
            [HttpGet]
            [McpTool]
            public string List(int page, int? size) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        // page has a description
        Assert.Contains("[global::System.ComponentModel.Description(\"The page number (one-based).\")]", generated);
        // size has no description
        Assert.DoesNotContain("\"The page number (one-based).\")] global::System.Nullable<global::System.Int32> size", generated);
    }

    [Fact]
    public void Param_description_generated_code_compiles_clean()
    {
        var src = Wrap("""
            /// <summary>Gets an order.</summary>
            /// <param name="id">The unique order identifier.</param>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrder(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile cleanly:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    [Fact]
    public void Description_text_containing_quotes_is_escaped()
    {
        var src = Wrap("""
            /// <summary>Gets an order.</summary>
            /// <param name="id">The order "id" field.</param>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrder(int id) => "ok";
            """);

        var result = GeneratorTestHarness.Run(src);
        var generated = result.AllGeneratedSource;

        // The quotes in the description must be escaped in the generated C# string literal.
        Assert.Contains("The order \\\"id\\\" field.", generated);
    }
}
