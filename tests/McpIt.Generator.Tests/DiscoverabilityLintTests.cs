using System.Linq;
using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

// MCPGEN004 (description too short) and MCPGEN005 (undescribed parameters): Info-level hints.
public class DiscoverabilityLintTests
{
    private static string Wrap(string method) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;
        [Route("orders")]
        public class OrdersController : ControllerBase { {{method}} }
        """;

    private static Diagnostic[] Diags(GeneratorResult r, string id) =>
        r.Diagnostics.Where(d => d.Id == id).ToArray();

    [Fact]
    public void MCPGEN004_fires_for_fewer_than_four_words_at_info_severity()
    {
        var result = GeneratorTestHarness.Run(Wrap("""
            /// <summary>Gets an order.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string Get(int id) => "ok";
            """));

        var d = Assert.Single(Diags(result, "MCPGEN004"));
        Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        Assert.Contains("'get'", d.GetMessage());
        Assert.Contains("what the tool does and when to use it", d.GetMessage());
        Assert.Equal("Input.cs", d.Location.GetLineSpan().Path);
    }

    [Fact]
    public void MCPGEN004_fires_when_description_just_repeats_the_title()
    {
        var result = GeneratorTestHarness.Run(Wrap("""
            /// <summary>Get order by id.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrderById(int id) => "ok";
            """));

        Assert.Single(Diags(result, "MCPGEN004"));
    }

    [Fact]
    public void MCPGEN004_does_not_fire_for_a_descriptive_summary()
    {
        var result = GeneratorTestHarness.Run(Wrap("""
            /// <summary>Gets one order with its line items; use it after searching orders.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string Get(int id) => "ok";
            """));

        Assert.Empty(Diags(result, "MCPGEN004"));
    }

    [Fact]
    public void MCPGEN004_is_not_stacked_on_MCPGEN001()
    {
        var result = GeneratorTestHarness.Run(Wrap("""
            [HttpGet("{id}")]
            [McpTool]
            public string Get(int id) => "ok";
            """));

        Assert.Single(Diags(result, "MCPGEN001"));
        Assert.Empty(Diags(result, "MCPGEN004"));
    }

    [Fact]
    public void MCPGEN005_lists_every_undescribed_parameter_once_per_tool()
    {
        var result = GeneratorTestHarness.Run(Wrap("""
            /// <summary>Searches orders by customer and status, newest first.</summary>
            /// <param name="customer">Customer id to filter on.</param>
            [HttpGet]
            [McpTool]
            public string Search(string customer, string? status, int page, System.Threading.CancellationToken ct) => "ok";
            """));

        var d = Assert.Single(Diags(result, "MCPGEN005"));
        Assert.Equal(DiagnosticSeverity.Info, d.Severity);
        Assert.Contains("'search'", d.GetMessage());
        Assert.Contains(": status, page;", d.GetMessage());
        Assert.DoesNotContain("customer", d.GetMessage().Split(':')[1]);
        Assert.DoesNotContain("ct", d.GetMessage().Split(':')[1].Split(';')[0]);
    }

    [Fact]
    public void MCPGEN005_accepts_Description_attribute_on_parameters()
    {
        var result = GeneratorTestHarness.Run(Wrap("""
            /// <summary>Searches orders by customer and status, newest first.</summary>
            /// <param name="customer">Customer id to filter on.</param>
            [HttpGet]
            [McpTool]
            public string Search(string customer, [System.ComponentModel.Description("Order status filter.")] string? status) => "ok";
            """));

        Assert.Empty(Diags(result, "MCPGEN005"));
    }

    [Fact]
    public void MCPGEN005_does_not_fire_for_parameterless_tools()
    {
        var result = GeneratorTestHarness.Run(Wrap("""
            /// <summary>Lists all orders placed today across every store.</summary>
            [HttpGet]
            [McpTool]
            public string Today(System.Threading.CancellationToken ct) => "ok";
            """));

        Assert.Empty(Diags(result, "MCPGEN005"));
        Assert.Empty(Diags(result, "MCPGEN004"));
    }

    [Fact]
    public void Lints_also_run_for_inline_lambda_tools()
    {
        var src = """
            using McpIt;
            using Microsoft.AspNetCore.Builder;
            namespace Demo
            {
                public class AppHost
                {
                    public void Register(object app)
                    {
                        app.MapGet("/stock/{sku}",
                            [McpTool(Name = "getStock")]
                            [System.ComponentModel.Description("Stock.")]
                            (string sku, [System.ComponentModel.Description("Product SKU.")] string? warehouse) => sku);
                    }
                }
            }

            namespace Microsoft.AspNetCore.Builder
            {
                public static class TestWebApp
                {
                    public static object MapGet(this object app, string route, System.Delegate handler) => app;
                }
            }
            """;

        var result = GeneratorTestHarness.Run(src);
        Assert.Single(Diags(result, "MCPGEN004"));
        var d = Assert.Single(Diags(result, "MCPGEN005"));
        Assert.Contains(": sku;", d.GetMessage());
    }
}
