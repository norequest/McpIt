// Phase 3: inline lambda handlers with Delegate-typed Map* overloads.
//
// FINDINGS (Phase 3):
//   The real WebApplication.MapGet(string, Delegate) overload uses System.Delegate as the
//   handler parameter type. Roslyn's GetSymbolInfo returns null for a lambda expression
//   bound to System.Delegate because the type is too broad to infer a specific delegate
//   type, which means the lambda cannot be resolved to an IMethodSymbol.
//
//   Fix: when GetSymbolInfo returns null AND the handler argument is a lambda syntax node
//   (ParenthesizedLambdaExpressionSyntax or SimpleLambdaExpressionSyntax), the generator
//   now builds the EndpointModel directly from the lambda's AttributeLists and parameter
//   syntax via the SemanticModel:
//
//   - [McpTool] is detected in lambda.AttributeLists, verified via GetSymbolInfo(attr).
//   - Named args (Name, Title, AllowDestructive, RequiredScope) are read from the attribute
//     argument syntax (NameEquals nodes with literal values).
//   - Each lambda parameter requires an explicit type declaration; GetDeclaredSymbol on the
//     ParameterSyntax returns an IParameterSymbol that is passed to ParameterClassifier.Classify.
//   - The generated class name is derived from VERB + sanitized(route) + containing type name
//     (same formula as the existing generic-stub lambda path).
//   - Tool name defaults to lowercased "verb_sanitizedRoute" unless McpTool(Name=...) is set.
//   - Lambdas have no XML doc; description comes from [Description] attribute only.
//
// STUB NOTE:
//   Each test below defines a Delegate-typed MapGet stub (the real failure shape) rather than
//   the unconstrained generic THandler stub used by MinimalApiGeneratorTests.cs. This is the
//   critical difference that reproduced the original bug and proves the fix.

using System.Linq;
using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

public class LambdaHandlerTests
{
    // Delegate-typed stub: reproduces the real ASP.NET Core Map* overload shape
    // where GetSymbolInfo returns null for lambda args and the generic-THandler path
    // does not apply.
    private const string DelegateStub = """

        namespace Microsoft.AspNetCore.Builder
        {
            public static class TestWebApp
            {
                public static object MapGet(this object app, string route, System.Delegate handler) => app;
                public static object MapPost(this object app, string route, System.Delegate handler) => app;
                public static object MapPut(this object app, string route, System.Delegate handler) => app;
                public static object MapPatch(this object app, string route, System.Delegate handler) => app;
                public static object MapDelete(this object app, string route, System.Delegate handler) => app;
                public static object MapGroup(this object app, string prefix) => app;
            }
        }
        """;

    // Wraps handler registrations inside an AppHost class (the lambda's containing type
    // becomes "AppHost", which is encoded into the generated class name).
    private static string Wrap(string registrations) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Builder;

        namespace Demo
        {
            public class AppHost
            {
                public void Register(object app)
                {
                    {{registrations}}
                }
            }
        }
        {{DelegateStub}}
        """;

    // -------------------------------------------------------------------------
    // Core case: Delegate-typed MapGet with [McpTool] lambda
    // -------------------------------------------------------------------------

    [Fact]
    public void DelegateLambda_BasicGet_EmitsTool()
    {
        // This is the exact shape that FAILED before Phase 3:
        // GetSymbolInfo on the lambda returned null because MapGet takes System.Delegate.
        var src = Wrap("""app.MapGet("/items/{id}", [McpTool] (int id) => "ok");""");

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected compilation errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        Assert.Contains("McpServerToolType", result.AllGeneratedSource);
        // Class name: MinApi_<VERB>_<sanitized-route>_<ContainingType>_Tool
        Assert.Contains("MinApi_GET_items_id_AppHost_Tool", result.AllGeneratedSource);
        Assert.Contains("\"GET\"", result.AllGeneratedSource);
        Assert.Contains("items/{id}", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_RouteAndQueryParams_ClassifiedCorrectly()
    {
        var src = Wrap("""
            app.MapGet("/items/{id}", [McpTool] (int id, string? expand) => "ok");
            """);

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected compilation errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        // Route token {id} -> id classified as Route; expand is a query string param.
        Assert.Contains("param id -> ParameterSource.Route", result.AllGeneratedSource);
        Assert.Contains("param expand -> ParameterSource.Query", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_DerivedToolName_IsVerbUnderscoreRoute()
    {
        // When no McpTool(Name=...) is set the tool name is derived from the verb and route.
        var src = Wrap("""app.MapGet("/items", [McpTool] () => "[]");""");

        var result = GeneratorTestHarness.Run(src);

        // Tool name hint = "get_items"
        Assert.Contains("get_items", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_ExplicitToolName_UsedVerbatim()
    {
        var src = Wrap("""app.MapGet("/items", [McpTool(Name = "listItems")] () => "[]");""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("listItems", result.AllGeneratedSource);
        // The route-derived fallback "get_items" must not appear as the tool name string.
        Assert.DoesNotContain("\"get_items\"", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_NoMcpToolAttribute_NotEmitted()
    {
        // Lambda without [McpTool] must not produce a tool.
        var src = Wrap("""app.MapGet("/items/{id}", (int id) => "ok");""");

        var result = GeneratorTestHarness.Run(src);

        Assert.DoesNotContain("McpServerToolType", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_Post_MarkedDestructive()
    {
        var src = Wrap("""
            app.MapPost("/items", [McpTool(AllowDestructive = true)] (int qty) => "created");
            """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("\"POST\"", result.AllGeneratedSource);
        Assert.Contains("Destructive = true", result.AllGeneratedSource);
        Assert.Contains("McpServerToolType", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_Delete_AllowDestructiveReadFromSyntax()
    {
        var src = Wrap("""
            app.MapDelete("/items/{id}", [McpTool(AllowDestructive = true)] (int id) => "deleted");
            """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("\"DELETE\"", result.AllGeneratedSource);
        Assert.Contains("Destructive = true", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_CancellationToken_IsFiltered()
    {
        // CancellationToken parameters must be omitted from the generated tool signature.
        var src = Wrap("""
            app.MapGet("/items/{id}", [McpTool] (int id, System.Threading.CancellationToken ct) => "ok");
            """);

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected compilation errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        Assert.Contains("param id -> ParameterSource.Route", result.AllGeneratedSource);
        Assert.DoesNotContain("param ct", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_MapGroup_PrefixPrepended()
    {
        // MapGroup prefix must be prepended to the lambda's route, same as for method-group handlers.
        var src = Wrap("""
            app.MapGroup("/api").MapGet("/items/{id}", [McpTool] (int id) => "ok");
            """);

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected compilation errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        // Full route must include the group prefix.
        Assert.Contains("api/items/{id}", result.AllGeneratedSource);
        Assert.Contains("McpServerToolType", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_RequiredScope_ReadFromSyntax()
    {
        var src = Wrap("""
            app.MapGet("/admin/data", [McpTool(RequiredScope = "admin")] () => "data");
            """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("admin", result.AllGeneratedSource);
        Assert.Contains("McpServerToolType", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_ExplicitTitle_ReadFromSyntax()
    {
        var src = Wrap("""
            app.MapGet("/items", [McpTool(Title = "List All Items")] () => "[]");
            """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("List All Items", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_Put_IsIdempotentAndDestructive()
    {
        var src = Wrap("""
            app.MapPut("/items/{id}", [McpTool(AllowDestructive = true)] (int id) => "replaced");
            """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("\"PUT\"", result.AllGeneratedSource);
        Assert.Contains("Idempotent = true", result.AllGeneratedSource);
        Assert.Contains("ReadOnly = false", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_Get_IsReadOnly()
    {
        var src = Wrap("""app.MapGet("/items", [McpTool] () => "[]");""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("ReadOnly = true", result.AllGeneratedSource);
        Assert.Contains("Destructive = false", result.AllGeneratedSource);
    }

    [Fact]
    public void DelegateLambda_MultipleInSameHost_BothToolsEmitted()
    {
        // Two independently-named lambdas in the same host must each produce a distinct tool.
        var src = Wrap("""
            app.MapGet("/items", [McpTool] () => "[]");
            app.MapGet("/items/{id}", [McpTool] (int id) => "ok");
            """);

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected compilation errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        // Both class names must appear in the generated output.
        Assert.Contains("MinApi_GET_items_AppHost_Tool", result.AllGeneratedSource);
        Assert.Contains("MinApi_GET_items_id_AppHost_Tool", result.AllGeneratedSource);
    }
}
