using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

/// <summary>
/// Tests for Phase 3 validation-constraint schema: DataAnnotation attributes
/// on controller-action parameters are copied to the generated MCP tool method,
/// giving the SDK (via AIFunctionFactory) enough information to surface
/// minimum/maximum/maxLength/pattern etc. in the tool's JSON input schema.
/// A concise hint is also appended to [Description] as belt-and-suspenders.
///
/// Spike finding (confirmed before these tests were written): ModelContextProtocol
/// 1.4.0 via Microsoft.Extensions.AI.Abstractions 10.5.2 DOES honor DataAnnotation
/// attributes on method parameters -- [Range(1,100)] produces "minimum":1,"maximum":100
/// in the JSON schema, [StringLength(50)] produces "maxLength":50, etc.
/// See ValidationSchemaEmitTests class comments for before/after examples.
///
/// Approach chosen: BOTH (attribute copy + description enrichment).
/// </summary>
public class ValidationSchemaEmitTests
{
    // ---------------------------------------------------------------------------
    // Test harness note: DataAnnotations arrive via the framework FrameworkReference
    // (Microsoft.AspNetCore.App) already included in the test project. Do NOT add a
    // standalone System.ComponentModel.DataAnnotations reference -- the types would
    // conflict with the framework reference set (CS0433).
    // ---------------------------------------------------------------------------

    // ---------------------------------------------------------------------------
    // [Range] tests
    // ---------------------------------------------------------------------------

    private const string RangeIntSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        using System.ComponentModel.DataAnnotations;

        namespace Demo;

        [Route("orders")]
        public class OrdersController : ControllerBase
        {
            /// <summary>Gets an order.</summary>
            /// <param name="qty">Quantity to order.</param>
            [HttpGet]
            [McpTool]
            public string GetOrder([Range(1, 100)] int qty) => "ok";
        }
        """;

    [Fact]
    public void Range_int_emits_DataAnnotations_attribute_on_generated_param()
    {
        var result = GeneratorTestHarness.Run(RangeIntSource);
        var src = result.AllGeneratedSource;

        // Attribute copy: the generated parameter carries [Range(1, 100)].
        Assert.Contains("DataAnnotations.Range(1, 100)", src);
    }

    [Fact]
    public void Range_int_emits_constraint_hint_in_Description()
    {
        var result = GeneratorTestHarness.Run(RangeIntSource);
        var src = result.AllGeneratedSource;

        // Description enrichment: the hint "(range: 1 to 100)" is appended.
        Assert.Contains("(range: 1 to 100)", src);
    }

    [Fact]
    public void Range_int_preserves_xml_doc_description_before_hint()
    {
        var result = GeneratorTestHarness.Run(RangeIntSource);
        var src = result.AllGeneratedSource;

        // The original xml-doc description should appear alongside the hint.
        Assert.Contains("Quantity to order.", src);
        Assert.Contains("(range: 1 to 100)", src);
    }

    [Fact]
    public void Range_int_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(RangeIntSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // [Range] with double arguments
    // ---------------------------------------------------------------------------

    private const string RangeDoubleSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        using System.ComponentModel.DataAnnotations;

        namespace Demo;

        [Route("products")]
        public class ProductsController : ControllerBase
        {
            /// <summary>Sets a discount.</summary>
            [HttpPost]
            [McpTool]
            public string SetDiscount([Range(0.0, 1.0)] double rate) => "ok";
        }
        """;

    [Fact]
    public void Range_double_emits_double_literals_in_attribute()
    {
        var result = GeneratorTestHarness.Run(RangeDoubleSource);
        var src = result.AllGeneratedSource;

        // Double literals must include a decimal point to select the correct ctor.
        Assert.Contains("Range(0.0, 1.0)", src);
    }

    [Fact]
    public void Range_double_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(RangeDoubleSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // [StringLength] tests
    // ---------------------------------------------------------------------------

    private const string StringLengthSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        using System.ComponentModel.DataAnnotations;

        namespace Demo;

        [Route("items")]
        public class ItemsController : ControllerBase
        {
            /// <summary>Creates an item.</summary>
            [HttpPost]
            [McpTool]
            public string Create([StringLength(50, MinimumLength = 3)] string name) => "ok";
        }
        """;

    [Fact]
    public void StringLength_emits_DataAnnotations_attribute_with_min_and_max()
    {
        var result = GeneratorTestHarness.Run(StringLengthSource);
        var src = result.AllGeneratedSource;

        Assert.Contains("StringLength(50, MinimumLength = 3)", src);
    }

    [Fact]
    public void StringLength_emits_hint_with_length_range()
    {
        var result = GeneratorTestHarness.Run(StringLengthSource);
        var src = result.AllGeneratedSource;

        Assert.Contains("(length: 3 to 50)", src);
    }

    [Fact]
    public void StringLength_max_only_emits_max_length_hint()
    {
        const string src = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            using System.ComponentModel.DataAnnotations;
            namespace Demo;
            [Route("items")]
            public class ItemsController : ControllerBase
            {
                [HttpPost]
                [McpTool]
                public string Create([StringLength(100)] string code) => "ok";
            }
            """;

        var result = GeneratorTestHarness.Run(src);
        Assert.Contains("StringLength(100)", result.AllGeneratedSource);
        Assert.Contains("(max length: 100)", result.AllGeneratedSource);
    }

    [Fact]
    public void StringLength_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(StringLengthSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // [MinLength] / [MaxLength] tests
    // ---------------------------------------------------------------------------

    private const string MinMaxLengthSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        using System.ComponentModel.DataAnnotations;

        namespace Demo;

        [Route("tags")]
        public class TagsController : ControllerBase
        {
            /// <summary>Adds a tag.</summary>
            [HttpPost]
            [McpTool]
            public string AddTag([MinLength(2)] [MaxLength(10)] string tag) => "ok";
        }
        """;

    [Fact]
    public void MinLength_emits_DataAnnotations_attribute()
    {
        var result = GeneratorTestHarness.Run(MinMaxLengthSource);
        Assert.Contains("MinLength(2)", result.AllGeneratedSource);
    }

    [Fact]
    public void MaxLength_emits_DataAnnotations_attribute()
    {
        var result = GeneratorTestHarness.Run(MinMaxLengthSource);
        Assert.Contains("MaxLength(10)", result.AllGeneratedSource);
    }

    [Fact]
    public void MinMaxLength_emits_combined_hint()
    {
        var result = GeneratorTestHarness.Run(MinMaxLengthSource);
        var src = result.AllGeneratedSource;

        Assert.Contains("min length: 2", src);
        Assert.Contains("max length: 10", src);
    }

    [Fact]
    public void MinMaxLength_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(MinMaxLengthSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // [RegularExpression] tests
    // ---------------------------------------------------------------------------

    private const string RegexSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        using System.ComponentModel.DataAnnotations;

        namespace Demo;

        [Route("pins")]
        public class PinsController : ControllerBase
        {
            /// <summary>Validates a PIN.</summary>
            [HttpGet]
            [McpTool]
            public string Validate([RegularExpression(@"^\d{4}$")] string pin) => "ok";
        }
        """;

    [Fact]
    public void Regex_emits_DataAnnotations_attribute_with_pattern()
    {
        var result = GeneratorTestHarness.Run(RegexSource);
        var src = result.AllGeneratedSource;

        Assert.Contains("RegularExpression(", src);
    }

    [Fact]
    public void Regex_emits_pattern_hint_in_Description()
    {
        var result = GeneratorTestHarness.Run(RegexSource);
        var src = result.AllGeneratedSource;

        Assert.Contains("pattern:", src);
    }

    [Fact]
    public void Regex_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(RegexSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // [Required] tests
    // ---------------------------------------------------------------------------

    private const string RequiredSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        using System.ComponentModel.DataAnnotations;

        namespace Demo;

        [Route("users")]
        public class UsersController : ControllerBase
        {
            /// <summary>Creates a user.</summary>
            [HttpPost]
            [McpTool]
            public string Create([Required] string email) => "ok";
        }
        """;

    [Fact]
    public void Required_emits_DataAnnotations_Required_attribute()
    {
        var result = GeneratorTestHarness.Run(RequiredSource);
        Assert.Contains("DataAnnotations.Required", result.AllGeneratedSource);
    }

    [Fact]
    public void Required_emits_required_hint()
    {
        var result = GeneratorTestHarness.Run(RequiredSource);
        Assert.Contains("required", result.AllGeneratedSource);
    }

    [Fact]
    public void Required_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(RequiredSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // Multiple constraints on one parameter
    // ---------------------------------------------------------------------------

    private const string MultiConstraintSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        using System.ComponentModel.DataAnnotations;

        namespace Demo;

        [Route("products")]
        public class ProductsController : ControllerBase
        {
            /// <summary>Gets products.</summary>
            /// <param name="count">Number of results.</param>
            [HttpGet]
            [McpTool]
            public string List([Required] [Range(1, 50)] int count) => "ok";
        }
        """;

    [Fact]
    public void Multiple_constraints_all_emitted_as_attributes()
    {
        var result = GeneratorTestHarness.Run(MultiConstraintSource);
        var src = result.AllGeneratedSource;

        Assert.Contains("DataAnnotations.Required", src);
        Assert.Contains("DataAnnotations.Range(1, 50)", src);
    }

    [Fact]
    public void Multiple_constraints_compose_stable_hint_order()
    {
        var result = GeneratorTestHarness.Run(MultiConstraintSource);
        var src = result.AllGeneratedSource;

        // Both hints present; stable order is required first then range.
        var reqPos = src.IndexOf("required", System.StringComparison.Ordinal);
        var rangePos = src.IndexOf("range:", System.StringComparison.Ordinal);
        Assert.True(reqPos >= 0, "required hint not found");
        Assert.True(rangePos >= 0, "range hint not found");
        Assert.True(reqPos < rangePos, "required should appear before range in stable order");
    }

    [Fact]
    public void Multiple_constraints_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(MultiConstraintSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // Backward compatibility: parameters with NO DataAnnotations are unchanged.
    // ---------------------------------------------------------------------------

    private const string NoAnnotationSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;

        namespace Demo;

        [Route("orders")]
        public class OrdersController : ControllerBase
        {
            /// <summary>Gets an order by id.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrder(int id) => "ok";
        }
        """;

    [Fact]
    public void Parameter_without_DataAnnotations_emits_no_DataAnnotation_attributes()
    {
        var result = GeneratorTestHarness.Run(NoAnnotationSource);
        var src = result.AllGeneratedSource;

        // No DataAnnotations namespace should appear in generated code.
        Assert.DoesNotContain("DataAnnotations", src);
    }

    [Fact]
    public void Parameter_without_DataAnnotations_emits_no_constraint_hint()
    {
        var result = GeneratorTestHarness.Run(NoAnnotationSource);
        var src = result.AllGeneratedSource;

        // No parenthesised hint should appear.
        Assert.DoesNotContain("(range:", src);
        Assert.DoesNotContain("(required)", src);
        Assert.DoesNotContain("(max length:", src);
        Assert.DoesNotContain("(pattern:", src);
    }

    [Fact]
    public void No_annotation_generated_source_compiles_clean()
    {
        var result = GeneratorTestHarness.Run(NoAnnotationSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Generated code did not compile:\n" + string.Join("\n", errors.Select(e => e.ToString())));
    }

    // ---------------------------------------------------------------------------
    // Description-only parameter (no DataAnnotations) is also unchanged.
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parameter_with_description_only_emits_description_unchanged()
    {
        const string src = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            [Route("items")]
            public class ItemsController : ControllerBase
            {
                /// <summary>Gets an item.</summary>
                /// <param name="id">The item id.</param>
                [HttpGet("{id}")]
                [McpTool]
                public string GetItem(int id) => "ok";
            }
            """;

        var result = GeneratorTestHarness.Run(src);
        var source = result.AllGeneratedSource;

        // Description is present, no constraint stuff added.
        Assert.Contains("The item id.", source);
        Assert.DoesNotContain("DataAnnotations", source);
    }

    // ---------------------------------------------------------------------------
    // Emitter-level unit tests (no Roslyn harness)
    // ---------------------------------------------------------------------------

    // Before Phase 3: Emitter produced no DataAnnotation attributes and no hint.
    // After Phase 3: Emitter produces both when Constraints is set.
    //
    // BEFORE (qty param, no constraints):
    //   [global::System.ComponentModel.Description("Quantity.")] global::System.Int32 qty
    //
    // AFTER (qty param with range:1:100):
    //   [global::System.ComponentModel.DataAnnotations.Range(1, 100)]
    //   [global::System.ComponentModel.Description("Quantity. (range: 1 to 100)")]
    //   global::System.Int32 qty

    [Fact]
    public void Emitter_no_constraints_produces_unchanged_output()
    {
        // Build a minimal model with no constraints and confirm output is identical
        // to the pre-Phase-3 behaviour.
        var model = BuildSingleParamModel("qty", "global::System.Int32",
            description: "Quantity.", constraints: null);

        var src = Emitter.Emit(model);

        Assert.Contains("Description(\"Quantity.\")", src);
        Assert.DoesNotContain("DataAnnotations", src);
    }

    [Fact]
    public void Emitter_range_constraint_appends_hint_to_existing_description()
    {
        var model = BuildSingleParamModel("qty", "global::System.Int32",
            description: "Quantity.", constraints: "range:1:100");

        var src = Emitter.Emit(model);

        Assert.Contains("Range(1, 100)", src);
        Assert.Contains("Quantity. (range: 1 to 100)", src);
    }

    [Fact]
    public void Emitter_constraints_without_description_emits_hint_only()
    {
        var model = BuildSingleParamModel("qty", "global::System.Int32",
            description: null, constraints: "range:1:100");

        var src = Emitter.Emit(model);

        Assert.Contains("Range(1, 100)", src);
        // When there is no base description the [Description] value is just the hint.
        Assert.Contains("Description(\"(range: 1 to 100)\")", src);
    }

    [Fact]
    public void Emitter_strlen_with_min_produces_correct_attribute_and_hint()
    {
        var model = BuildSingleParamModel("name", "global::System.String",
            description: null, constraints: "strlen:3:50");

        var src = Emitter.Emit(model);

        Assert.Contains("StringLength(50, MinimumLength = 3)", src);
        Assert.Contains("(length: 3 to 50)", src);
    }

    [Fact]
    public void Emitter_strlen_max_only_produces_correct_attribute_and_hint()
    {
        var model = BuildSingleParamModel("code", "global::System.String",
            description: null, constraints: "strlen::100");

        var src = Emitter.Emit(model);

        Assert.Contains("StringLength(100)", src);
        Assert.Contains("(max length: 100)", src);
    }

    [Fact]
    public void Emitter_regex_in_attribute_uses_verbatim_literal()
    {
        var model = BuildSingleParamModel("pin", "global::System.String",
            description: null, constraints: @"regex:^\d{4}$");

        var src = Emitter.Emit(model);

        // The pattern should appear inside @"..." in the emitted attribute.
        Assert.Contains("RegularExpression(@\"", src);
    }

    [Fact]
    public void Emitter_required_plus_range_stable_order_in_hint()
    {
        var model = BuildSingleParamModel("count", "global::System.Int32",
            description: null, constraints: "req|range:1:50");

        var src = Emitter.Emit(model);

        Assert.Contains("Required", src);
        Assert.Contains("Range(1, 50)", src);

        // Hint order: required before range.
        var reqIdx = src.IndexOf("required", System.StringComparison.Ordinal);
        var rangeIdx = src.IndexOf("range:", System.StringComparison.Ordinal);
        Assert.True(reqIdx < rangeIdx, "required should appear before range in hint");
    }

    // ---------------------------------------------------------------------------
    // Helper
    // ---------------------------------------------------------------------------

    private static EndpointModel BuildSingleParamModel(
        string paramName,
        string typeFqn,
        string? description,
        string? constraints)
    {
        var param = new ParameterModel(
            Name: paramName,
            TypeFullyQualified: typeFqn,
            Source: ParameterSource.Query,
            Description: description,
            Constraints: constraints);

        return new EndpointModel(
            Namespace: "Demo",
            GeneratedClassName: "TestTool",
            ToolName: "test",
            Description: "Test tool.",
            HttpMethod: "GET",
            RouteTemplate: "items",
            Parameters: new McpIt.Generator.Internal.EquatableArray<ParameterModel>([param]),
            ReadOnly: true,
            Destructive: false,
            Idempotent: true,
            AllowDestructive: false,
            OutputMaxLength: null,
            OutputFields: new McpIt.Generator.Internal.EquatableArray<string>([]),
            OutputMaxItems: null,
            Title: "Test",
            Location: null);
    }
}
