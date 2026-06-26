using System.Collections.Immutable;
using Basic.Reference.Assemblies;
using McpIt;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace McpIt.Generator.Tests;

public sealed record GeneratorResult(
    ImmutableArray<Diagnostic> Diagnostics,
    ImmutableArray<Diagnostic> CompilationDiagnostics,
    string AllGeneratedSource);

public static class GeneratorTestHarness
{
    public static GeneratorResult Run(string source)
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
            typeof(McpIt.IMcpEndpointInvoker),
            // Required so generated scope-gated tools that reference IHttpContextAccessor compile.
            typeof(Microsoft.AspNetCore.Http.IHttpContextAccessor),
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
            assemblyName: "Tests.Generated",
            syntaxTrees: [syntaxTree],
            references: references,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = CSharpGeneratorDriver.Create(
            generators: [new McpToolGenerator().AsSourceGenerator()],
            parseOptions: parseOptions);

        var ranDriver = driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        var generated = string.Join(
            "\n\n",
            outputCompilation.SyntaxTrees
                .Where(t => t.FilePath != "Input.cs")
                .Select(t => t.ToString()));

        var compilationDiagnostics = outputCompilation.GetDiagnostics();

        return new GeneratorResult(diagnostics, compilationDiagnostics, generated);
    }
}
