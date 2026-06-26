// FINDINGS: Minimal-API support - Phase 2 results
//
// SUPPORTED (Phase 2, this file):
//   Shape 1 - Method-group handler:
//       app.MapGet("/items/{id}", Handlers.GetItem)
//     where GetItem carries [McpTool]. Resolved cleanly via
//     SemanticModel.GetSymbolInfo on the method-group expression.
//     Verb derived from the Map* method name; route taken from the first
//     string-literal argument. ParameterClassifier reuses the existing
//     route-token matching logic unchanged ({id} binds to param named "id").
//
//   Shape 2 - Lambda handler with attribute (ADDED Phase 2):
//       app.MapGet("/items/{id}", [McpTool] (int id) => ...)
//     Works when the Map* method uses an unconstrained generic THandler parameter
//     (as in the test stub below). In that case GetSymbolInfo returns the lambda
//     as an IMethodSymbol with MethodKind.AnonymousFunction. The compiler-generated
//     name (e.g. "<Register>b__0") contains angle brackets that are invalid in a
//     C# class identifier; Phase 2 fixes this by deriving the class name from
//     VERB + sanitized(route) + ContainingTypeName (e.g. "MinApi_GET_items_id_AppHost_Tool").
//     NOTE: when the real ASP.NET Core Map* APIs are used (which take System.Delegate),
//     GetSymbolInfo returns null for lambda args and the lambda is silently skipped.
//     In practice, consumers should use named method groups for production tools or
//     ensure their Map* overload uses a generic type parameter.
//
//   MapGroup prefix handling (ADDED Phase 2):
//     app.MapGroup("/api").MapGet("/items", ...)  -> full route "/api/items"
//     var g = app.MapGroup("/api"); g.MapGet("/items", H)  -> "/api/items"
//     Nested groups: app.MapGroup("/a").MapGroup("/b").MapGet("/x", H) -> "/a/b/x"
//     Non-literal prefix (computed value): walk stops silently; endpoint continues
//     without a prefix rather than crashing.
//
//   Overloaded method-group handlers:
//     First candidate returned by GetSymbolInfo.CandidateSymbols is picked
//     deterministically. Fully ambiguous overloads without any candidate are
//     simply filtered out (no tool emitted).
//
// STUB NOTE:
//   The inline test sources below define stub MapGet/MapPost/etc. and MapGroup
//   extension methods in the Microsoft.AspNetCore.Builder namespace. This is
//   necessary because GeneratorTestHarness builds its compilation from
//   Basic.Reference.Assemblies (BCL only) plus MVC/McpIt assemblies; it does
//   not include the real Microsoft.AspNetCore.Routing assembly. The stubs use
//   unconstrained generic THandler so Roslyn can infer the lambda type for the
//   lambda handler tests. Users on real ASP.NET Core apps have the genuine
//   WebApplication.MapGet and do not need stubs; the generator matches by
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
                // MapGroup returns IEndpointRouteBuilder so chain calls like .MapGet(...) compile.
                public static IEndpointRouteBuilder MapGroup(this IEndpointRouteBuilder b, string prefix) => b;
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
    // Shape 2: lambda handlers (Phase 2 -- replaces the former "deferred" test)
    // -------------------------------------------------------------------------

    [Fact]
    public void LambdaHandler_WithMcpToolAttribute_EmitsTool()
    {
        // The [McpTool] attribute here is on the lambda, not on a named method.
        // Phase 2 fix: class name is derived from VERB + sanitized route + containing type,
        // avoiding the compiler-generated angle-bracket name ("MinApi_GET_items_id_AppHost_Tool").
        var src = Wrap(
            handlers: string.Empty,
            registrations: """
                /// <summary>Gets an item.</summary>
                app.MapGet("/items/{id}", [McpTool] (int id) => id.ToString());
                """);

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));
        Assert.Contains("McpServerToolType", result.AllGeneratedSource);
        // Class name must be a valid C# identifier (no angle brackets).
        Assert.Contains("MinApi_GET_items_id_AppHost_Tool", result.AllGeneratedSource);
        Assert.Contains("\"GET\"", result.AllGeneratedSource);
        Assert.Contains("items/{id}", result.AllGeneratedSource);
    }

    [Fact]
    public void LambdaHandler_DerivedToolName_FromRouteAndVerb()
    {
        var src = Wrap(
            handlers: string.Empty,
            registrations: """
                /// <summary>Lists all items.</summary>
                app.MapGet("/items", [McpTool] () => "ok");
                """);

        var result = GeneratorTestHarness.Run(src);

        // Tool name hint is "get_items" when no explicit Name is provided.
        Assert.Contains("get_items", result.AllGeneratedSource);
    }

    [Fact]
    public void LambdaHandler_ExplicitToolName_UsedVerbatim()
    {
        var src = Wrap(
            handlers: string.Empty,
            registrations: """
                /// <summary>Gets items.</summary>
                app.MapGet("/items", [McpTool(Name = "fetchItems")] () => "ok");
                """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("fetchItems", result.AllGeneratedSource);
    }

    [Fact]
    public void LambdaHandler_RouteParam_ClassifiedCorrectly()
    {
        var src = Wrap(
            handlers: string.Empty,
            registrations: """
                /// <summary>Gets an item.</summary>
                app.MapGet("/items/{id}", [McpTool] (int id, string? expand) => id.ToString());
                """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("param id -> ParameterSource.Route", result.AllGeneratedSource);
        Assert.Contains("param expand -> ParameterSource.Query", result.AllGeneratedSource);
    }

    [Fact]
    public void LambdaHandler_WithoutMcpToolAttribute_NotEmitted()
    {
        var src = Wrap(
            handlers: string.Empty,
            registrations: """app.MapGet("/items/{id}", (int id) => id.ToString());""");

        var result = GeneratorTestHarness.Run(src);

        Assert.DoesNotContain("McpServerToolType", result.AllGeneratedSource);
    }

    // -------------------------------------------------------------------------
    // MapGroup prefix chaining (Phase 2)
    // -------------------------------------------------------------------------

    [Fact]
    public void MapGroup_DirectChain_PrependsPrefixToRoute()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Gets an item.</summary>
                    [McpTool]
                    public static string GetItem(int id) => "ok";
                }
                """,
            registrations: """app.MapGroup("/api").MapGet("/items/{id}", Handlers.GetItem);""");

        var result = GeneratorTestHarness.Run(src);

        var errors = result.CompilationDiagnostics
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.True(errors.Count == 0,
            "Unexpected errors: " + string.Join("; ", errors.Select(e => e.Id + " " + e.GetMessage())));
        // Full route must include the group prefix.
        Assert.Contains("api/items/{id}", result.AllGeneratedSource);
        Assert.Contains("McpServerToolType", result.AllGeneratedSource);
    }

    [Fact]
    public void MapGroup_VariableAssignment_PrependsPrefixToRoute()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Lists items.</summary>
                    [McpTool]
                    public static string ListItems() => "ok";
                }
                """,
            registrations: """
                var g = app.MapGroup("/api");
                g.MapGet("/items", Handlers.ListItems);
                """);

        var result = GeneratorTestHarness.Run(src);

        Assert.Contains("api/items", result.AllGeneratedSource);
    }

    [Fact]
    public void MapGroup_NoGroup_RouteIsUnchanged()
    {
        var src = Wrap(
            handlers: """
                public static class Handlers
                {
                    /// <summary>Gets an item.</summary>
                    [McpTool]
                    public static string GetItem(int id) => "ok";
                }
                """,
            registrations: """app.MapGet("/items/{id}", Handlers.GetItem);""");

        var result = GeneratorTestHarness.Run(src);

        // Without MapGroup the route should be the bare path, not prefixed.
        Assert.Contains("items/{id}", result.AllGeneratedSource);
        Assert.DoesNotContain("api/items", result.AllGeneratedSource);
    }
}
