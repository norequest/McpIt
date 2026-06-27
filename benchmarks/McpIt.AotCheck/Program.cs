using McpIt;
using System.Text.Json;
using System.Text.Json.Serialization;

// AOT compatibility probe for McpIt's runtime helpers.
//
// The trim/AOT/single-file analyzers (IsAotCompatible + EnableTrimAnalyzer +
// EnableAotAnalyzer + EnableSingleFileAnalyzer in the .csproj) run at BUILD TIME
// as Roslyn diagnostics. IL2026/IL3050 are promoted to errors, so a clean build
// proves zero such warnings for the code paths exercised below.
//
// Helpers exercised:
//   1. OutputShaper.Shape    -- uses JsonDocument DOM + Utf8JsonWriter (reflection-free)
//   2. QueryStringBuilder.Build -- uses LINQ Where/ToArray + string.Join (AOT-safe)
//   3. McpEndpointInvocationException -- plain exception subclass (AOT-safe)
//   4. Body-path (AOT-clean variant) -- JsonSerializerOptions.GetTypeInfo +
//      JsonSerializer.Serialize(object?, JsonTypeInfo): both unannotated, so this
//      code path is IL2026/IL3050-clean when the consumer supplies a source-generated
//      JsonSerializerContext via McpEndpointsOptions.SerializerOptions.
//      NOTE: zero-config AOT is impossible; this test exercises the CONTEXT-BACKED path.

// 1. OutputShaper.Shape: project an object to a subset of top-level fields.
const string productJson =
    """{"id":"prod-0001","name":"Widget Pro","sku":"WGT-001","price":49.99,"inStock":true,"description":"A great widget."}""";

var projected = OutputShaper.Shape(productJson, maxLength: null, fields: ["id", "name"]);
Console.WriteLine($"projected:  {projected}");

// OutputShaper.Shape: truncate only (no projection).
var truncated = OutputShaper.Shape(productJson, maxLength: 30, fields: null);
Console.WriteLine($"truncated:  {truncated}");

// OutputShaper.Shape: project then truncate (combined code path).
var both = OutputShaper.Shape(productJson, maxLength: 40, fields: ["id", "name", "price"]);
Console.WriteLine($"both:       {both}");

// OutputShaper.Shape: array input (projects each element individually).
const string arrayJson =
    """[{"id":"a","name":"Alpha","extra":"drop-me"},{"id":"b","name":"Beta","extra":"drop-me"}]""";
var projectedArray = OutputShaper.Shape(arrayJson, maxLength: null, fields: ["id", "name"]);
Console.WriteLine($"array:      {projectedArray}");

// OutputShaper.Shape: invalid JSON (fallback path, must not throw).
var fallback = OutputShaper.Shape("not-valid-json", maxLength: null, fields: ["id"]);
Console.WriteLine($"fallback:   {fallback}");

// 2. QueryStringBuilder.Build: mixed present/null pairs.
var qs = QueryStringBuilder.Build(["category=electronics", null, "page=1", null, "pageSize=20"]);
Console.WriteLine($"qs:         {qs}");

// QueryStringBuilder.Build: all null returns null.
var qsNull = QueryStringBuilder.Build([null, null]);
Console.WriteLine($"qs null:    {qsNull ?? "(null)"}");

// QueryStringBuilder.Build: all present.
var qsFull = QueryStringBuilder.Build(["a=1", "b=2", "c=3"]);
Console.WriteLine($"qs full:    {qsFull}");

// 3. McpEndpointInvocationException: construct, throw, and inspect properties.
try
{
    throw new McpEndpointInvocationException(
        httpMethod: "GET",
        requestUrl: "http://localhost:5000/api/products/prod-9999",
        statusCode: 404,
        responseBody: """{"error":"not_found","detail":"Product prod-9999 does not exist."}""");
}
catch (McpEndpointInvocationException ex)
{
    Console.WriteLine(
        $"exception:  method={ex.HttpMethod} status={ex.StatusCode} " +
        $"hasUrl={!string.IsNullOrEmpty(ex.RequestUrl)} bodyLen={ex.ResponseBody.Length}");
}

// 4. Body serialization via source-generated JsonSerializerContext (AOT-clean path).
//
// This mirrors what HttpClientMcpEndpointInvoker.SerializeBodyWithOptions does:
//   - JsonSerializerOptions.GetTypeInfo(Type) has no [RequiresUnreferencedCode] attribute.
//   - JsonSerializer.Serialize(object?, JsonTypeInfo) has no [RequiresUnreferencedCode] attribute.
// When McpEndpointsOptions.SerializerOptions is set with a source-generated TypeInfoResolver,
// the invoker takes this path and reflection is never used. The IL2026/IL3050 build-time
// gate on this project proves the path is clean (would fail to compile if annotated APIs crept in).
var bodyOpts = new JsonSerializerOptions { TypeInfoResolver = AotCheckContext.Default };
var sampleBody = new AotBodySample("Widget Pro", 5);
var bodyTypeInfo = bodyOpts.GetTypeInfo(typeof(AotBodySample));
var bodyJson = JsonSerializer.Serialize(sampleBody, bodyTypeInfo);
Console.WriteLine($"body-path:  {bodyJson}");

// Source-generated context covering the AotBodySample type used in section 4 above.
// The [JsonSerializable] attribute triggers the STJ source generator at compile time,
// producing strongly-typed metadata that the GetTypeInfo + Serialize path uses instead
// of runtime reflection.
[JsonSerializable(typeof(AotBodySample))]
internal sealed partial class AotCheckContext : JsonSerializerContext { }

internal sealed record AotBodySample(string Name, int Qty);
