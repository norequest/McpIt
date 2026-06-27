namespace McpIt.Runtime.Tests;

public class OutputShaperTests
{
    // ---- existing tests (unchanged) ----

    [Fact]
    public void Projects_object_to_selected_fields()
    {
        var json = """{"id":1,"status":"open","secret":"x","note":"y"}""";
        var shaped = OutputShaper.Shape(json, null, ["id", "status"]);

        Assert.Contains("\"id\":1", shaped);
        Assert.Contains("\"status\":\"open\"", shaped);
        Assert.DoesNotContain("secret", shaped);
        Assert.DoesNotContain("note", shaped);
    }

    [Fact]
    public void Projects_array_elements_to_selected_fields()
    {
        var json = """[{"id":1,"status":"open","x":9},{"id":2,"status":"done","x":8}]""";
        var shaped = OutputShaper.Shape(json, null, ["id", "status"]);

        Assert.Contains("\"id\":1", shaped);
        Assert.Contains("\"id\":2", shaped);
        Assert.Contains("\"status\":\"done\"", shaped);
        Assert.DoesNotContain("\"x\"", shaped);
    }

    [Fact]
    public void Truncates_to_max_length()
    {
        var json = "abcdefghij";
        var shaped = OutputShaper.Shape(json, 4, null);
        Assert.Equal("abcd", shaped);
    }

    [Fact]
    public void Projects_then_truncates_in_order()
    {
        var json = """{"id":1,"status":"open","secret":"longvalueignored"}""";
        var shaped = OutputShaper.Shape(json, 8, ["id"]);
        // projection gives {"id":1} then truncate to 8 chars
        Assert.Equal("{\"id\":1}".Substring(0, 8), shaped);
    }

    [Fact]
    public void Malformed_json_passes_through_unchanged_when_projecting()
    {
        var json = "not json at all";
        var shaped = OutputShaper.Shape(json, null, ["id"]);
        Assert.Equal("not json at all", shaped);
    }

    [Fact]
    public void Malformed_json_still_truncates()
    {
        var json = "not json at all";
        var shaped = OutputShaper.Shape(json, 3, ["id"]);
        Assert.Equal("not", shaped);
    }

    [Fact]
    public void No_options_returns_original()
    {
        var json = """{"id":1}""";
        var shaped = OutputShaper.Shape(json, null, null);
        Assert.Equal(json, shaped);
    }

    [Fact]
    public void Empty_fields_array_does_not_project()
    {
        var json = """{"id":1,"status":"open"}""";
        var shaped = OutputShaper.Shape(json, null, []);
        Assert.Equal(json, shaped);
    }

    // ---- new tests: nested / path-based projection ----

    [Fact]
    public void Projects_nested_object_path()
    {
        var json = """{"customer":{"name":"Alice","email":"a@b.c","secret":"x"},"other":"y"}""";
        var shaped = OutputShaper.Shape(json, null, ["customer.name"]);

        Assert.Contains("\"customer\"", shaped);
        Assert.Contains("\"name\":\"Alice\"", shaped);
        Assert.DoesNotContain("\"email\"", shaped);
        Assert.DoesNotContain("secret", shaped);
        Assert.DoesNotContain("other", shaped);
    }

    [Fact]
    public void Projects_array_items_field()
    {
        var json = """{"items":[{"sku":"A","price":10},{"sku":"B","price":20}],"total":30}""";
        var shaped = OutputShaper.Shape(json, null, ["items[].sku"]);

        Assert.Contains("\"items\"", shaped);
        Assert.Contains("\"sku\":\"A\"", shaped);
        Assert.Contains("\"sku\":\"B\"", shaped);
        Assert.DoesNotContain("price", shaped);
        Assert.DoesNotContain("total", shaped);
    }

    [Fact]
    public void Merges_paths_with_shared_prefix()
    {
        var json = """{"customer":{"name":"Alice","email":"a@b.c","secret":"x"},"other":"y"}""";
        var shaped = OutputShaper.Shape(json, null, ["customer.name", "customer.email"]);

        Assert.Contains("\"customer\"", shaped);
        Assert.Contains("\"name\":\"Alice\"", shaped);
        Assert.Contains("\"email\":\"a@b.c\"", shaped);
        Assert.DoesNotContain("secret", shaped);
        Assert.DoesNotContain("other", shaped);
    }

    [Fact]
    public void Bare_top_level_field_backward_compatible()
    {
        var json = """{"id":42,"hidden":"x"}""";
        var shaped = OutputShaper.Shape(json, null, ["id"]);

        Assert.Contains("\"id\":42", shaped);
        Assert.DoesNotContain("hidden", shaped);
    }

    [Fact]
    public void Top_level_array_projection_backward_compatible()
    {
        var json = """[{"id":1,"x":9},{"id":2,"x":8}]""";
        var shaped = OutputShaper.Shape(json, null, ["id"]);

        Assert.Contains("\"id\":1", shaped);
        Assert.Contains("\"id\":2", shaped);
        Assert.DoesNotContain("\"x\"", shaped);
    }

    // ---- new tests: array item capping (4-arg overload) ----

    [Fact]
    public void MaxItems_caps_array_elements()
    {
        var json = """[1,2,3,4,5]""";
        var shaped = OutputShaper.Shape(json, null, null, 3);

        // First 3 elements present, last 2 absent.
        Assert.StartsWith("[", shaped);
        Assert.EndsWith("]", shaped);
        Assert.Contains("1", shaped);
        Assert.Contains("2", shaped);
        Assert.Contains("3", shaped);
        Assert.DoesNotContain("4", shaped);
        Assert.DoesNotContain("5", shaped);
    }

    [Fact]
    public void MaxItems_noop_on_object_root()
    {
        var json = """{"a":1,"b":2}""";
        var shaped = OutputShaper.Shape(json, null, null, 1);

        Assert.Equal(json, shaped);
    }

    [Fact]
    public void MaxItems_zero_yields_empty_array()
    {
        var json = """[1,2,3]""";
        var shaped = OutputShaper.Shape(json, null, null, 0);

        Assert.Equal("[]", shaped);
    }

    [Fact]
    public void Order_of_operations_project_then_cap_then_truncate()
    {
        // After project(["id"]): [{"id":1},{"id":2},{"id":3}]
        // After cap(2):          [{"id":1},{"id":2}]
        // After truncate(8):     [{"id":1          (8 chars)
        var json = """[{"id":1,"x":9},{"id":2,"x":8},{"id":3,"x":7}]""";
        var shaped = OutputShaper.Shape(json, 8, ["id"], 2);

        Assert.Equal(8, shaped.Length);
        Assert.StartsWith("[{\"id\":1", shaped);
        Assert.DoesNotContain("\"x\"", shaped);
        Assert.DoesNotContain("3", shaped);
    }

    [Fact]
    public void Malformed_json_returns_original_for_nested_path()
    {
        var json = "not valid json {{{{";
        var shaped = OutputShaper.Shape(json, null, ["a.b.c"]);
        Assert.Equal(json, shaped);
    }

    [Fact]
    public void Null_fields_leaves_json_unchanged()
    {
        var json = """{"id":1,"name":"test"}""";
        var shaped = OutputShaper.Shape(json, null, (string[]?)null, null);
        Assert.Equal(json, shaped);
    }

    [Fact]
    public void Empty_fields_leaves_json_unchanged()
    {
        var json = """{"id":1,"name":"test"}""";
        var shaped = OutputShaper.Shape(json, null, [], null);
        Assert.Equal(json, shaped);
    }
}
