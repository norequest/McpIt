using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Basic.Reference.Assemblies;
using McpIt;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace McpIt.Generator.Tests;

/// <summary>
/// Tests for McpManifestGenerator: verifies McpItManifest emission, deterministic hashing,
/// sensitivity to surface changes, and order-independence.
/// </summary>
public class ManifestGeneratorTests
{
    // -------------------------------------------------------------------------
    // Mini test harness: runs only McpManifestGenerator.
    // -------------------------------------------------------------------------

    private static GeneratorResult RunManifest(string source)
    {
        var parseOptions = new CSharpParseOptions(documentationMode: DocumentationMode.Parse);
        var syntaxTree = CSharpSyntaxTree.ParseText(source, parseOptions, path: "Input.cs");

        var extraTypes = new[]
        {
            typeof(McpToolAttribute),
            typeof(ModelContextProtocol.Server.McpServerToolAttribute),
            typeof(Microsoft.AspNetCore.Mvc.ControllerBase),
            typeof(Microsoft.AspNetCore.Mvc.HttpGetAttribute),
            typeof(Microsoft.AspNetCore.Mvc.RouteAttribute),
            typeof(Microsoft.AspNetCore.Mvc.ApiControllerAttribute),
            typeof(Microsoft.AspNetCore.Mvc.FromBodyAttribute),
            typeof(IMcpEndpointInvoker),
        };

        var extraRefs = extraTypes
            .Select(t => t.Assembly.Location)
            .Distinct()
            .Select(loc => (MetadataReference)MetadataReference.CreateFromFile(loc));

#if NET8_0
        var frameworkRefs = Net80.References.All;
#elif NET9_0
        var frameworkRefs = Net90.References.All;
#else
        var frameworkRefs = Net100.References.All;
#endif
        var references = frameworkRefs.Concat(extraRefs).ToArray();

        var compilation = CSharpCompilation.Create(
            assemblyName: "Tests.Manifest",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            generators: [new McpManifestGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        var generated = string.Join(
            "\n\n",
            outputCompilation.SyntaxTrees
                .Where(t => t.FilePath != "Input.cs")
                .Select(t => t.ToString()));

        return new GeneratorResult(diagnostics, outputCompilation.GetDiagnostics(), generated);
    }

    // Fake Asp.Versioning attributes: the generator matches by simple type name so these
    // exercise the real version-resolution code path without a package dependency.
    private const string VersioningAttrs = """

        namespace Asp.Versioning
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Method, AllowMultiple = true)]
            public sealed class ApiVersionAttribute : System.Attribute
            {
                public ApiVersionAttribute(string version) { }
            }
            [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
            public sealed class MapToApiVersionAttribute : System.Attribute
            {
                public MapToApiVersionAttribute(string version) { }
            }
        }
        """;

    private static GeneratorResult RunManifestWithVersioning(string controller) =>
        RunManifest(controller + VersioningAttrs);

    // Extracts the 64-char hex AggregateHash from the generated source.
    private static string ExtractAggregateHash(string generatedSource)
    {
        var match = Regex.Match(
            generatedSource,
            @"AggregateHash\s*=\s*""([0-9a-f]{64})""");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // -------------------------------------------------------------------------
    // Existing tests (unchanged behaviour)
    // -------------------------------------------------------------------------

    [Fact]
    public void Emits_McpItManifest_class_with_non_empty_aggregate_hash()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order by id.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        var result = RunManifest(source);

        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("McpItManifest", result.AllGeneratedSource);
        Assert.Contains("AggregateHash", result.AllGeneratedSource);
        Assert.Contains("Json", result.AllGeneratedSource);
        Assert.Contains("ToolNames", result.AllGeneratedSource);

        var hash = ExtractAggregateHash(result.AllGeneratedSource);
        Assert.Equal(64, hash.Length);
    }

    [Fact]
    public void Reordering_tool_declarations_yields_the_same_aggregate_hash()
    {
        // Two tools declared A-then-B.
        const string sourceAB = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("demo")]
            public class DemoController : ControllerBase
            {
                /// <summary>Tool A description.</summary>
                [HttpGet("a")]
                [McpTool]
                public string ToolA() => "a";

                /// <summary>Tool B description.</summary>
                [HttpGet("b")]
                [McpTool]
                public string ToolB(int x) => "b";
            }
            """;

        // Same two tools, declaration order reversed.
        const string sourceBA = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("demo")]
            public class DemoController : ControllerBase
            {
                /// <summary>Tool B description.</summary>
                [HttpGet("b")]
                [McpTool]
                public string ToolB(int x) => "b";

                /// <summary>Tool A description.</summary>
                [HttpGet("a")]
                [McpTool]
                public string ToolA() => "a";
            }
            """;

        var hashAB = ExtractAggregateHash(RunManifest(sourceAB).AllGeneratedSource);
        var hashBA = ExtractAggregateHash(RunManifest(sourceBA).AllGeneratedSource);

        Assert.NotEmpty(hashAB);
        Assert.Equal(hashAB, hashBA);
    }

    [Fact]
    public void Changing_a_tool_description_changes_the_aggregate_hash()
    {
        const string sourceV1 = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Original description text.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        const string sourceV2 = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Changed description text.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        var hashV1 = ExtractAggregateHash(RunManifest(sourceV1).AllGeneratedSource);
        var hashV2 = ExtractAggregateHash(RunManifest(sourceV2).AllGeneratedSource);

        Assert.NotEmpty(hashV1);
        Assert.NotEmpty(hashV2);
        Assert.NotEqual(hashV1, hashV2);
    }

    [Fact]
    public void Adding_a_tool_changes_the_aggregate_hash()
    {
        const string sourceOne = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order by id.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        const string sourceTwo = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order by id.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";

                /// <summary>Lists all orders.</summary>
                [HttpGet]
                [McpTool]
                public string ListOrders() => "[]";
            }
            """;

        var hashOne = ExtractAggregateHash(RunManifest(sourceOne).AllGeneratedSource);
        var hashTwo = ExtractAggregateHash(RunManifest(sourceTwo).AllGeneratedSource);

        Assert.NotEmpty(hashOne);
        Assert.NotEmpty(hashTwo);
        Assert.NotEqual(hashOne, hashTwo);
    }

    [Fact]
    public void Tool_less_compilation_emits_no_manifest()
    {
        // No [McpTool] annotations: the generator must emit NOTHING. Emitting an empty
        // McpItManifest into every tool-less assembly (including McpIt.dll) would collide
        // with the consumer's own generated McpItManifest (CS0436).
        const string source = "namespace Demo; public class Empty { }";

        var result = RunManifest(source);

        Assert.DoesNotContain("McpItManifest", result.AllGeneratedSource);
    }

    [Fact]
    public void Json_constant_embeds_tool_name_and_aggregate_hash_fields()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order by id.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        var result = RunManifest(source);
        var generatedSource = result.AllGeneratedSource;

        // The camelCase tool name must appear in the emitted source (either in JSON or ToolNames).
        Assert.Contains("getOrder", generatedSource);
        Assert.Contains("aggregateHash", generatedSource);
        Assert.Contains("parameterCount", generatedSource);
    }

    [Fact]
    public void Explicit_Name_arg_on_McpTool_is_used_verbatim_in_the_manifest()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Fetches an order.</summary>
                [HttpGet("{id}")]
                [McpTool(Name = "fetch_order_by_id")]
                public string GetOrder(int id) => "{}";
            }
            """;

        var result = RunManifest(source);

        Assert.Contains("fetch_order_by_id", result.AllGeneratedSource);
        Assert.DoesNotContain("getOrder", result.AllGeneratedSource);
    }

    // -------------------------------------------------------------------------
    // P3: NamePrefix enrichment tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Class_level_NamePrefix_is_applied_to_manifest_tool_name()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            [McpTool(NamePrefix = "orders_")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        var result = RunManifest(source);

        // The manifest tool name must carry the prefix.
        Assert.Contains("orders_getOrder", result.AllGeneratedSource);
        // The un-prefixed name must not appear on its own (it would indicate the prefix was not applied).
        Assert.DoesNotContain("\"getOrder\"", result.AllGeneratedSource);
    }

    [Fact]
    public void Explicit_Name_overrides_class_NamePrefix_in_manifest()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            [McpTool(NamePrefix = "orders_")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order.</summary>
                [HttpGet("{id}")]
                [McpTool(Name = "custom_name")]
                public string GetOrder(int id) => "{}";
            }
            """;

        var result = RunManifest(source);

        // Explicit name wins verbatim; prefix must NOT be prepended.
        Assert.Contains("custom_name", result.AllGeneratedSource);
        Assert.DoesNotContain("orders_custom_name", result.AllGeneratedSource);
        Assert.DoesNotContain("orders_getOrder", result.AllGeneratedSource);
    }

    // -------------------------------------------------------------------------
    // P3: API-version suffix tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Version_suffix_is_applied_to_manifest_tool_name_for_fully_derived_name()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            using Asp.Versioning;

            [ApiVersion("1.0")]
            [Route("v{version:apiVersion}/account")]
            public class AccountController : ControllerBase
            {
                /// <summary>Account info.</summary>
                [HttpGet("info")]
                [McpTool]
                public string Info() => "ok";
            }
            """;

        var result = RunManifestWithVersioning(source);

        // Fully derived name (no explicit Name, no NamePrefix): suffix must be applied.
        Assert.Contains("info_v1", result.AllGeneratedSource);
    }

    [Fact]
    public void Version_suffix_is_NOT_applied_when_explicit_Name_is_set()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            using Asp.Versioning;

            [ApiVersion("1.0")]
            [Route("v{version:apiVersion}/account")]
            public class AccountController : ControllerBase
            {
                /// <summary>Account info.</summary>
                [HttpGet("info")]
                [McpTool(Name = "accountInfo")]
                public string Info() => "ok";
            }
            """;

        var result = RunManifestWithVersioning(source);

        // Explicit name: suffix must NOT be appended.
        Assert.Contains("accountInfo", result.AllGeneratedSource);
        Assert.DoesNotContain("accountInfo_v1", result.AllGeneratedSource);
    }

    [Fact]
    public void Version_suffix_is_NOT_applied_when_NamePrefix_is_set()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            using Asp.Versioning;

            [ApiVersion("1.0")]
            [Route("v{version:apiVersion}/account")]
            [McpTool(NamePrefix = "acct_")]
            public class AccountController : ControllerBase
            {
                /// <summary>Account info.</summary>
                [HttpGet("info")]
                [McpTool]
                public string Info() => "ok";
            }
            """;

        var result = RunManifestWithVersioning(source);

        // NamePrefix-derived name: suffix must NOT be appended (mirrors ModelBuilder line 55).
        Assert.Contains("acct_info", result.AllGeneratedSource);
        Assert.DoesNotContain("acct_info_v1", result.AllGeneratedSource);
    }

    [Fact]
    public void Non_zero_minor_version_suffix_uses_underscore_separator()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            using Asp.Versioning;

            [ApiVersion("2.1")]
            [Route("v{version:apiVersion}/things")]
            public class ThingsController : ControllerBase
            {
                /// <summary>List things.</summary>
                [HttpGet]
                [McpTool]
                public string List() => "[]";
            }
            """;

        var result = RunManifestWithVersioning(source);

        // Non-zero minor: "2.1" -> _v2_1 (dot replaced by underscore).
        Assert.Contains("list_v2_1", result.AllGeneratedSource);
    }

    // -------------------------------------------------------------------------
    // P3: HTTP verb + route enrichment tests
    // -------------------------------------------------------------------------

    [Fact]
    public void Json_includes_verb_and_route_fields()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order by id.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        var result = RunManifest(source);
        var generatedSource = result.AllGeneratedSource;

        // Both "verb" and "route" keys must appear in the emitted JSON constant.
        Assert.Contains("\"verb\"", generatedSource);
        Assert.Contains("\"route\"", generatedSource);
        // The actual values for this action.
        Assert.Contains("GET", generatedSource);
        Assert.Contains("orders/{id}", generatedSource);
    }

    [Fact]
    public void Changing_route_changes_aggregate_hash()
    {
        const string sourceV1 = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        // Same everything except the method-level route segment changed.
        const string sourceV2 = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("v2/orders")]
            public class OrdersController : ControllerBase
            {
                /// <summary>Gets an order.</summary>
                [HttpGet("{id}")]
                [McpTool]
                public string GetOrder(int id) => "{}";
            }
            """;

        var hashV1 = ExtractAggregateHash(RunManifest(sourceV1).AllGeneratedSource);
        var hashV2 = ExtractAggregateHash(RunManifest(sourceV2).AllGeneratedSource);

        Assert.NotEmpty(hashV1);
        Assert.NotEmpty(hashV2);
        Assert.NotEqual(hashV1, hashV2);
    }

    [Fact]
    public void Changing_http_verb_changes_aggregate_hash()
    {
        const string sourceGet = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("things")]
            public class ThingsController : ControllerBase
            {
                /// <summary>Does a thing.</summary>
                [HttpGet("do")]
                [McpTool]
                public string DoThing() => "{}";
            }
            """;

        // Same route and name but verb changed to POST.
        const string sourcePost = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("things")]
            public class ThingsController : ControllerBase
            {
                /// <summary>Does a thing.</summary>
                [HttpPost("do")]
                [McpTool(AllowDestructive = true)]
                public string DoThing() => "{}";
            }
            """;

        var hashGet = ExtractAggregateHash(RunManifest(sourceGet).AllGeneratedSource);
        var hashPost = ExtractAggregateHash(RunManifest(sourcePost).AllGeneratedSource);

        Assert.NotEmpty(hashGet);
        Assert.NotEmpty(hashPost);
        Assert.NotEqual(hashGet, hashPost);
    }

    [Fact]
    public void Order_independence_holds_with_verb_and_route_in_fingerprint()
    {
        // Two tools with different verbs and routes: A=GET/alpha/a, B=POST/alpha/b.
        const string sourceAB = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("alpha")]
            public class AlphaController : ControllerBase
            {
                /// <summary>Read A.</summary>
                [HttpGet("a")]
                [McpTool]
                public string ReadA() => "a";

                /// <summary>Write B.</summary>
                [HttpPost("b")]
                [McpTool(AllowDestructive = true)]
                public string WriteB() => "b";
            }
            """;

        const string sourceBA = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("alpha")]
            public class AlphaController : ControllerBase
            {
                /// <summary>Write B.</summary>
                [HttpPost("b")]
                [McpTool(AllowDestructive = true)]
                public string WriteB() => "b";

                /// <summary>Read A.</summary>
                [HttpGet("a")]
                [McpTool]
                public string ReadA() => "a";
            }
            """;

        var hashAB = ExtractAggregateHash(RunManifest(sourceAB).AllGeneratedSource);
        var hashBA = ExtractAggregateHash(RunManifest(sourceBA).AllGeneratedSource);

        Assert.NotEmpty(hashAB);
        Assert.Equal(hashAB, hashBA);
    }

    [Fact]
    public void Class_route_and_method_route_are_combined_correctly_in_Json()
    {
        const string source = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;

            namespace Demo;

            [Route("api/products")]
            public class ProductsController : ControllerBase
            {
                /// <summary>Get product.</summary>
                [HttpGet("{productId}")]
                [McpTool]
                public string GetProduct(int productId) => "{}";
            }
            """;

        var result = RunManifest(source);
        var generatedSource = result.AllGeneratedSource;

        // Combined route must appear in the JSON constant.
        Assert.Contains("api/products/{productId}", generatedSource);
    }
}
