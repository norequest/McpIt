// FINDINGS: Minimal-API support - spike results
//
// SUPPORTED (Phase 1, this file):
//   Shape 1 - Method-group handler:
//       app.MapGet("/items/{id}", Handlers.GetItem)
//     where GetItem carries [McpTool]. Resolved cleanly via
//     SemanticModel.GetSymbolInfo on the method-group expression.
//     Verb derived from the Map* method name; route taken from the first
//     string-literal argument. ParameterClassifier reuses the existing
//     route-token matching logic unchanged ({id} binds to param named "id").
//
// DEFERRED (Phase 2 recommended):
//   Shape 2 - Lambda with attribute:
//       app.MapGet("/items", [McpTool] (int id) => ...)
//     SemanticModel.GetSymbolInfo on a lambda expression returns null (the
//     lambda has no named symbol at the call site). GetDeclaredSymbol requires
//     the concrete CSharpSemanticModel overload for AnonymousFunctionExpressionSyntax
//     which is not reachable via the generic SyntaxNode path in netstandard2.0/
//     Roslyn 4.8.0. A Phase 2 implementation should: (a) detect the lambda arg
//     syntactically, (b) cast ctx.SemanticModel to CSharpSemanticModel and call
//     GetDeclaredSymbol(AnonymousFunctionExpressionSyntax), then (c) check the
//     resulting IMethodSymbol.GetAttributes() for McpToolAttribute. This is
//     achievable but requires a CSharpSemanticModel cast that is safe inside an
//     IIncrementalGenerator running against the C# compiler.
//
//   MapGroup prefix handling:
//     app.MapGroup("/api").MapGet("/items", ...)  -- group prefix not followed;
//     route captured from the innermost MapGet call only. Resolving the full
//     prefix requires walking the receiver chain through the semantic model,
//     which adds complexity. Recommended for Phase 2.
//
// STUB NOTE:
//   The inline test sources below define stub MapGet/MapPost/etc. extension
//   methods in the Microsoft.AspNetCore.Builder namespace. This is necessary
//   because GeneratorTestHarness builds its compilation from
//   Basic.Reference.Assemblies (BCL only) plus MVC/McpIt assemblies; it does
//   not include the real Microsoft.AspNetCore.Routing assembly. The stub
//   reproduces the (this X, string, Delegate) signature that is sufficient for
//   the generator to see the invocation syntactically and resolve the handler
//   symbol semantically. Users on real ASP.NET Core apps have the genuine
//   WebApplication.MapGet and do not need the stub; the generator matches by
//   Map* method name, not by the exact declaring type.

using Microsoft.CodeAnalysis;

namespace McpIt.Generator.Tests;

public class MinimalApiGeneratorTests
{
    // Stub appended to every test source: mirrors Microsoft.AspNetCore.Builder
    // Map* extension method signatures without requiring the routing assembly.
    //
    // Using unconstrained generic THandler rather than System.Delegate avoids
    // implicit method-group-to-Delegate conversion issues that can produce
    // spurious CS0037 diagnostics in certain Roslyn versions. In production the
    // real WebApplication.MapGet uses System.Delegate; the generator matches by
    // method name and handler symbol only, so both forms work identically.
    private const string Stub = """

        namespace Microsoft.AspNetCore.Builder
        {
            public interface IEndpointRouteBuilder { }
            public static class MinimalApiStubExtensions
            {
                public static void MapGet<THandler>(this IEndpointRouteBuilder b, string p, THandler h) { }
                public static void MapPost<THandler>(this IEndpointRouteBuilder b, string p, THandler h) { }
                public static void MapPut<THandler>(this IEndpointRouteBuilder b, string p, THandler h) { }
                public static void MapPatch<THandler>(this IEndpointRouteBuilder b, string p, THandler h) { }
                public static void MapDelete<THandler>(this IEndpointRouteBuilder b, string p, THandler h) { }
            }
        }
        """;

    // Minimal app host that registers routes. The generator picks up the
    // MapGet/... InvocationExpressionSyntax nodes inside the Register method.
    private static string Wrap(string handlers, string registrations) => $$"""
        using McpIt;
        using Microsoft.AspNetCore.Builder;

        namespace Demo
        {
            {{handlers}}

            public class AppHost : IEndpointRouteBuilder
            {
                public void Register(IEndpointRouteBuilder app)
                {
                    {{registrations}}
                }
            }
        }
        {{Stub}}
        """;

    // -------------------------------------------------------------------------
    // Shape 1: method-group handlers
    // -------------------------------------------------------------------------

    [Fact]
    public void MethodGroup_Get_WithRouteParam_EmitsTool()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Gets an item by id.</summary>
                    [McpTool]
                    public static string GetItem(int id) => "ok";
                }
                """,
            registrations: """app.MapGet("/items/{id}", Handlers.GetItem);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.DoesNotContain(result.Diagnostics,
            d => d.Severity == DiagnosticSeverity.Error);
        Assert.Contains("McpServerToolType", result.AllGeneratedSource);
        Assert.Contains("GetItem", result.AllGeneratedSource);
        Assert.Contains("\"GET\"", result.AllGeneratedSource);
        Assert.Contains("items/{id}", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_Get_RouteParam_ClassifiedAsRoute()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Gets an item.</summary>
                    [McpTool]
                    public static string GetItem(int id, string? expand) => "ok";
                }
                """,
            registrations: """app.MapGet("/items/{id}", Handlers.GetItem);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("param id -> ParameterSource.Route", result.AllGeneratedSource);
        Assert.Contains("param expand -> ParameterSource.Query", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_Post_ComplexType_ClassifiedAsBody()
    {
        var src = Wrap(
            handlers: """
                public record CreateItemRequest(string Name, int Qty);
                public static class Handlers
                {
                    /// <summary>Creates an item.</summary>
                    [McpTool(AllowDestructive = true)]
                    public static string CreateItem(CreateItemRequest body) => "ok";
                }
                """,
            registrations: """app.MapPost("/items", Handlers.CreateItem);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("param body -> ParameterSource.Body", result.AllGeneratedSource);
        Assert.Contains("\"POST\"", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_DerivedToolName_IsCamelCase()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Lists all items.</summary>
                    [McpTool]
                    public static string ListAllItems() => "ok";
                }
                """,
            registrations: """app.MapGet("/items", Handlers.ListAllItems);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("listAllItems", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_ExplicitToolName_UsedVerbatim()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Gets items.</summary>
                    [McpTool(Name = "fetchItems")]
                    public static string GetItems() => "ok";
                }
                """,
            registrations: """app.MapGet("/items", Handlers.GetItems);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("fetchItems", result.AllGeneratedSource);
        // The derived camelCase name must not appear
        Assert.DoesNotContain("\"getItems\"", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_XmlSummary_IsIncluded()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Retrieves an item by its unique identifier.</summary>
                    [McpTool]
                    public static string GetItem(int id) => "ok";
                }
                """,
            registrations: """app.MapGet("/items/{id}", Handlers.GetItem);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("Retrieves an item by its unique identifier.", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_Delete_MarkedDestructive()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Deletes an item.</summary>
                    [McpTool(AllowDestructive = true)]
                    public static string DeleteItem(int id) => "ok";
                }
                """,
            registrations: """app.MapDelete("/items/{id}", Handlers.DeleteItem);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("\"DELETE\"", result.AllGeneratedSource);
        Assert.Contains("Destructive = true", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_NoMcpToolAttribute_ProducesNoOutput()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    // No [McpTool] attribute - should not be emitted.
                    public static string GetItem(int id) => "ok";
                }
                """,
            registrations: """app.MapGet("/items/{id}", Handlers.GetItem);""");

        var result = GeneratorTestHarness.Run(src);

        // The generated source will be empty (or contain only other tool output).
        Assert.DoesNotContain("MinApi_Handlers_GetItem_Tool", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_CancellationToken_IsFiltered()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Gets an item.</summary>
                    [McpTool]
                    public static string GetItem(int id, System.Threading.CancellationToken ct) => "ok";
                }
                """,
            registrations: """app.MapGet("/items/{id}", Handlers.GetItem);""");

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));
        Assert.DoesNotContain("param ct -> ParameterSource.Body", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_HintName_HasMinApiPrefix_AvoidingControllerCollision()
    {
        // Same namespace, same method name but one is a controller action and one
        // is a minimal-API handler. Both must produce distinct hint names.
        const string src = """
            using McpIt;
            using Microsoft.AspNetCore.Mvc;
            using Microsoft.AspNetCore.Builder;

            namespace Demo
            {
                [Route("items")]
                public class ItemsController : ControllerBase
                {
                    /// <summary>Gets an item (controller).</summary>
                    [HttpGet("{id}")]
                    [McpTool]
                    public string GetItem(int id) => "ctrl";
                }

                public static class Handlers
                {
                    /// <summary>Gets an item (minimal API).</summary>
                    [McpTool]
                    public static string GetItem(int id) => "minimal";
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

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));

        // Controller tool keeps its name; minimal-API tool gets the MinApi_ prefix.
        Assert.Contains("ItemsController_GetItem_Tool", result.AllGeneratedSource);
        Assert.Contains("MinApi_Handlers_GetItem_Tool", result.AllGeneratedSource);
    }

    [Fact]
    public void MethodGroup_Put_IsIdempotentNotReadOnly()
    {
        var src = Wrap(
            handlers: """
                public record UpdateItemRequest(string Name);
                public static class Handlers
                {
                    /// <summary>Replaces an item.</summary>
                    [McpTool(AllowDestructive = true)]
                    public static string ReplaceItem(int id, UpdateItemRequest body) => "ok";
                }
                """,
            registrations: """app.MapPut("/items/{id}", Handlers.ReplaceItem);""");

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("\"PUT\"", result.AllGeneratedSource);
        Assert.Contains("ReadOnly = false", result.AllGeneratedSource);
        Assert.Contains("Idempotent = true", result.AllGeneratedSource);
    }

    // -------------------------------------------------------------------------
    // Shape 2: lambda handlers -- DEFERRED (see findings at top of file)
    //
    // Investigation result: Roslyn's GetSymbolInfo DOES return an anonymous
    // method IMethodSymbol for a lambda expression. However, the compiler-
    // generated method name (e.g. "<Register>b__0") contains angle brackets
    // which make invalid C# class identifiers. The generator therefore skips
    // MethodKind.AnonymousFunction symbols explicitly (Phase 2 should sanitize
    // the name or use a different naming strategy for lambda handlers).
    //
    // This test guards the "not emitted" behavior. Phase 2 will replace it.
    // -------------------------------------------------------------------------

    [Fact]
    public void LambdaHandler_WithMcpToolAttribute_NotEmitted_Deferred()
    {
        // The [McpTool] attribute here is on the lambda, not on a named method.
        // The generator explicitly skips MethodKind.AnonymousFunction symbols to
        // avoid emitting classes with angle-bracket names like
        // "MinApi_AppHost_<Register>b__0_Tool" which do not compile.
        const string src = """
            using McpIt;
            using Microsoft.AspNetCore.Builder;

            namespace Demo
            {
                public class AppHost : IEndpointRouteBuilder
                {
                    public void Register(IEndpointRouteBuilder app)
                    {
                        app.MapGet("/items/{id}", [McpTool] (int id) => id.ToString());
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

        var result = GeneratorTestHarness.Run(src);

        // Lambda tools are NOT emitted in Phase 1. Phase 2 will update this test.
        Assert.DoesNotContain("McpServerToolType", result.AllGeneratedSource);
    }
}
