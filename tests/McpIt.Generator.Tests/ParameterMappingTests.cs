using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

public class ParameterMappingTests
{
    private static string Wrap(string method) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;
        public record CreateOrderRequest(string Sku, int Qty);
        [Route("orders")]
        public class OrdersController : ControllerBase
        {
            {{method}}
        }
        """;

    [Fact]
    public void Route_param_detected_and_query_param_classified()
    {
        var src = Wrap("""
            /// <summary>x</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string Get(int id, string? q) => "ok";
            """);
        var result = GeneratorTestHarness.Run(src);
        Assert.Contains("param id -> ParameterSource.Route", result.AllGeneratedSource);
        Assert.Contains("param q -> ParameterSource.Query", result.AllGeneratedSource);
    }

    [Fact]
    public void Complex_type_becomes_body()
    {
        var src = Wrap("""
            /// <summary>x</summary>
            [HttpPost]
            [McpTool]
            public string Create(CreateOrderRequest request) => "ok";
            """);
        var result = GeneratorTestHarness.Run(src);
        Assert.Contains("param request -> ParameterSource.Body", result.AllGeneratedSource);
    }

    [Fact]
    public void FromBody_primitive_becomes_body()
    {
        var src = Wrap("""
            /// <summary>x</summary>
            [HttpPost]
            [McpTool]
            public string Create([FromBody] string raw) => "ok";
            """);
        var result = GeneratorTestHarness.Run(src);
        Assert.Contains("param raw -> ParameterSource.Body", result.AllGeneratedSource);
    }

    [Fact]
    public void CancellationToken_param_is_not_duplicated_or_serialized()
    {
        var src = Wrap("""
            /// <summary>x</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string Get(int id, System.Threading.CancellationToken cancellationToken) => "ok";
            """);
        var result = GeneratorTestHarness.Run(src);

        // The generated wrapper must compile: no CS0100 duplicate-parameter (or any) error.
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, "Unexpected generated-code errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        // The user's CancellationToken must not be treated as a request body.
        Assert.DoesNotContain("param cancellationToken -> ParameterSource.Body", result.AllGeneratedSource);
    }

    [Fact]
    public void CancellationToken_dropped_while_real_body_param_still_serialized()
    {
        var src = Wrap("""
            /// <summary>x</summary>
            [HttpPost]
            [McpTool(AllowDestructive = true)]
            public string Create(CreateOrderRequest request, System.Threading.CancellationToken cancellationToken) => "ok";
            """);
        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0, "Unexpected generated-code errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        // The genuine body param is still serialized; the CancellationToken is dropped from the model.
        Assert.Contains("param request -> ParameterSource.Body", result.AllGeneratedSource);
        Assert.DoesNotContain("param cancellationToken ->", result.AllGeneratedSource);
    }

    [Fact]
    public void Description_attribute_on_parameter_flows_into_generated_tool()
    {
        var src = Wrap("""
            /// <summary>x</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string Get([System.ComponentModel.Description("The order id")] int id) => "ok";
            """);
        var result = GeneratorTestHarness.Run(src);
        Assert.Contains("[global::System.ComponentModel.Description(\"The order id\")]", result.AllGeneratedSource);
    }

    [Fact]
    public void Xml_param_doc_wins_over_Description_attribute()
    {
        var src = Wrap("""
            /// <summary>x</summary>
            /// <param name="id">From XML</param>
            [HttpGet("{id}")]
            [McpTool]
            public string Get([System.ComponentModel.Description("From attribute")] int id) => "ok";
            """);
        var result = GeneratorTestHarness.Run(src);
        Assert.Contains("Description(\"From XML\")", result.AllGeneratedSource);
        Assert.DoesNotContain("From attribute\")", result.AllGeneratedSource);
    }
}
