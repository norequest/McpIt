namespace McpIt.Generator.Tests;

// Pins the AOT story for generated code at the source level:
//  - GET/read tools must be reflection-free (no JsonSerializer.Serialize).
//  - POST/body tools now also contain no JsonSerializer.Serialize; the body is passed
//    as (object?)body + typeof(BodyType) to the invoker overload, moving the
//    serialization concern into the runtime library where it can be made AOT-clean by
//    supplying McpEndpointsOptions.SerializerOptions with a source-generated context.
public class AotShapeTests
{
    private const string GetSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        [ApiController, Route("things")]
        public class ThingsController : ControllerBase
        {
            /// <summary>Gets a thing.</summary>
            [HttpGet("{id}")]
            [McpTool(Name = "getThing")]
            public IActionResult GetThing(int id) => Ok(id);
        }
        """;

    private const string PostSource = """
        using McpIt;
        using Microsoft.AspNetCore.Mvc;
        public record CreateThing(string Name);
        [ApiController, Route("things")]
        public class ThingsController : ControllerBase
        {
            /// <summary>Creates a thing.</summary>
            [HttpPost]
            [McpTool(Name = "createThing", AllowDestructive = true)]
            public IActionResult CreateThing([FromBody] CreateThing body) => Ok();
        }
        """;

    [Fact]
    public void Get_tool_generates_no_reflection_json()
    {
        var result = GeneratorTestHarness.Run(GetSource);
        // Non-vacuousness guard: confirm a real tool was generated before asserting absence.
        Assert.Contains("getThing", result.AllGeneratedSource);
        Assert.DoesNotContain("global::System.Text.Json.JsonSerializer.Serialize", result.AllGeneratedSource);
    }

    [Fact]
    public void Post_body_tool_generates_no_reflection_json()
    {
        var result = GeneratorTestHarness.Run(PostSource);
        // Non-vacuousness guard: confirm the tool was generated.
        Assert.Contains("createThing", result.AllGeneratedSource);
        // Body is now passed as (object?)body + typeof(BodyType) to the invoker;
        // the generated tool contains no JsonSerializer.Serialize call of its own.
        Assert.DoesNotContain("global::System.Text.Json.JsonSerializer.Serialize", result.AllGeneratedSource);
        // Confirm the object cast and typeof args are present.
        Assert.Contains("(object?)body", result.AllGeneratedSource);
        Assert.Contains("typeof(global::CreateThing)", result.AllGeneratedSource);
    }
}
