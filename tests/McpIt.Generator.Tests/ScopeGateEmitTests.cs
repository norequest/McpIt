using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

/// <summary>
/// Tests for Feature D4: per-tool OAuth scope gate emission.
/// Verifies that [McpTool(RequiredScope = "...")] causes the generator to:
///   - inject IHttpContextAccessor as a DI parameter
///   - emit the McpScopeGuard.HasScope / Denied calls at the top of Invoke
///   - still pass through the invoker and output shaping after the guard
///   - leave tools WITHOUT RequiredScope completely unchanged
/// </summary>
public class ScopeGateEmitTests
{
    // Controller-based tool with RequiredScope.
    private static string ControllerSource(string scope) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;
        [Route("orders")]
        public class OrdersController : ControllerBase
        {
            /// <summary>Gets an order.</summary>
            [HttpGet("{id}")]
            [McpTool(RequiredScope = "{{scope}}")]
            public string GetOrder(int id) => "ok";
        }
        """;

    // Minimal-API method-group tool with RequiredScope.
    private static string MinApiSource(string scope) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Builder;
        namespace Demo
        {
            public static class Handlers
            {
                /// <summary>Gets an item.</summary>
                [McpTool(RequiredScope = "{{scope}}")]
                public static string GetItem(int id) => "ok";
            }
            public class AppHost : IEndpointRouteBuilder
            {
                public void Register(IEndpointRouteBuilder app)
                {
                    app.MapGet("/items/{id}", Handlers.GetItem);
                }
            }
        }
        namespace Microsoft.AspNetCore.Builder
        {
            public interface IEndpointRouteBuilder { }
            public static class MinimalApiStubExtensions
            {
                public static void MapGet<THandler>(this IEndpointRouteBuilder b, string p, THandler h) { }
            }
        }
        """;

    // Controller tool WITHOUT RequiredScope.
    private const string ControllerSourceNoScope = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        namespace Demo;
        [Route("orders")]
        public class OrdersController : ControllerBase
        {
            /// <summary>Gets an order.</summary>
            [HttpGet("{id}")]
            [McpTool]
            public string GetOrder(int id) => "ok";
        }
        """;

    // -------------------------------------------------------------------------
    // Param injection tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ScopedTool_Controller_InjectsHttpContextAccessorParam()
    {
        var result = GeneratorTestHarness.Run(ControllerSource("read"));

        Assert.Contains(
            "global::Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor",
            result.AllGeneratedSource);
    }

    [Fact]
    public void ScopedTool_MinimalApi_InjectsHttpContextAccessorParam()
    {
        var result = GeneratorTestHarness.Run(MinApiSource("write"));

        Assert.Contains(
            "global::Microsoft.AspNetCore.Http.IHttpContextAccessor httpContextAccessor",
            result.AllGeneratedSource);
    }

    [Fact]
    public void UnscopedTool_DoesNotInjectHttpContextAccessorParam()
    {
        var result = GeneratorTestHarness.Run(ControllerSourceNoScope);

        // IHttpContextAccessor must NOT appear as a parameter when there is no RequiredScope.
        Assert.DoesNotContain(
            "IHttpContextAccessor httpContextAccessor",
            result.AllGeneratedSource);
    }

    // -------------------------------------------------------------------------
    // Guard emission tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ScopedTool_EmitsHasScopeCheck()
    {
        var result = GeneratorTestHarness.Run(ControllerSource("admin"));

        Assert.Contains("McpScopeGuard.HasScope", result.AllGeneratedSource);
        Assert.Contains("\"admin\"", result.AllGeneratedSource);
    }

    [Fact]
    public void ScopedTool_EmitsDeniedCall()
    {
        var result = GeneratorTestHarness.Run(ControllerSource("admin"));

        Assert.Contains("McpScopeGuard.Denied", result.AllGeneratedSource);
    }

    [Fact]
    public void ScopedTool_GuardUsesCorrectScopeAndToolName()
    {
        var result = GeneratorTestHarness.Run(ControllerSource("orders:read"));
        var src = result.AllGeneratedSource;

        // The scope string must appear in both the HasScope and Denied calls.
        Assert.Contains("HasScope(httpContextAccessor, \"orders:read\")", src);
        Assert.Contains("Denied(", src);
        Assert.Contains("\"orders:read\"", src);
    }

    [Fact]
    public void ScopedTool_IsAsyncMethod()
    {
        var result = GeneratorTestHarness.Run(ControllerSource("read"));

        // Scope-gated tools always emit as async Task<string>.
        Assert.Contains("public static async global::System.Threading.Tasks.Task<string> Invoke", result.AllGeneratedSource);
    }

    [Fact]
    public void UnscopedTool_IsNotAsync()
    {
        var result = GeneratorTestHarness.Run(ControllerSourceNoScope);

        // Unscoped tools without output shaping use the non-async Task<string> expression body.
        Assert.Contains("public static global::System.Threading.Tasks.Task<string> Invoke", result.AllGeneratedSource);
        Assert.DoesNotContain("public static async global::System.Threading.Tasks.Task<string> Invoke", result.AllGeneratedSource);
    }

    // -------------------------------------------------------------------------
    // Invoker passthrough tests -- guard must not block the invoker call
    // -------------------------------------------------------------------------

    [Fact]
    public void ScopedTool_StillCallsInvokerAfterGuard()
    {
        var result = GeneratorTestHarness.Run(ControllerSource("read"));

        // The invoker call must still be present (after the guard).
        Assert.Contains("invoker.InvokeAsync", result.AllGeneratedSource);
    }

    [Fact]
    public void ScopedTool_NoCompilationErrors()
    {
        var result = GeneratorTestHarness.Run(ControllerSource("read"));

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));
    }

    // -------------------------------------------------------------------------
    // Output-shaping + scope gate combination
    // -------------------------------------------------------------------------

    [Fact]
    public void ScopedTool_WithOutputShaping_EmitsBothGuardAndShaper()
    {
        const string src = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            namespace Demo;
            [Route("products")]
            public class ProductsController : ControllerBase
            {
                /// <summary>Lists products.</summary>
                [HttpGet]
                [McpTool(RequiredScope = "catalog")]
                [McpToolOutput(MaxLength = 500)]
                public string[] ListProducts() => System.Array.Empty<string>();
            }
            """;

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("McpScopeGuard.HasScope", result.AllGeneratedSource);
        Assert.Contains("OutputShaper.Shape", result.AllGeneratedSource);
        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));
    }
}
