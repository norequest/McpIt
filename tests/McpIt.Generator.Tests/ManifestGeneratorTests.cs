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

    // Extracts the 64-char hex AggregateHash from the generated source.
    private static string ExtractAggregateHash(string generatedSource)
    {
        var match = Regex.Match(
            generatedSource,
            @"AggregateHash\s*=\s*""([0-9a-f]{64})""");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    // -------------------------------------------------------------------------
    // Tests
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
    public void Empty_tool_set_emits_manifest_with_known_stable_hash()
    {
        // No [McpTool] annotations: generator still emits McpItManifest.
        // AggregateHash must equal SHA-256("") for an empty compilation.
        const string source = "namespace Demo; public class Empty { }";

        var resultFirst = RunManifest(source);
        var resultSecond = RunManifest(source);

        Assert.Contains("McpItManifest", resultFirst.AllGeneratedSource);

        var hashFirst = ExtractAggregateHash(resultFirst.AllGeneratedSource);
        var hashSecond = ExtractAggregateHash(resultSecond.AllGeneratedSource);

        Assert.Equal(64, hashFirst.Length);
        Assert.Equal(hashFirst, hashSecond);

        // SHA-256("") is a well-known constant.
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", hashFirst);
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
}
