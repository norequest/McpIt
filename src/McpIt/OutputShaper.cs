using System;
using System.Collections.Generic;
using System.Text.Json;

namespace McpIt;

/// <summary>
/// Best-effort shaping of tool output: projects to selected fields (including nested
/// paths and array-element paths), caps array item count, and/or truncates to a
/// maximum character length. Never throws; on any parse failure it falls back to the
/// original string. Order of operations: project fields, then cap items, then truncate.
/// </summary>
public static class OutputShaper
{
    /// <summary>
    /// Projects json to the requested fields and truncates to maxLength.
    /// Delegates to the four-argument overload with maxItems = null.
    /// </summary>
    public static string Shape(string json, int? maxLength, string[]? fields)
        => Shape(json, maxLength, fields, null);

    /// <summary>
    /// Shapes the JSON string: field projection, then array item capping, then length
    /// truncation. All parameters are optional and null-safe. Never throws.
    /// </summary>
    /// <param name="json">The JSON string to shape.</param>
    /// <param name="maxLength">Optional maximum character length after all shaping.</param>
    /// <param name="fields">
    /// Optional field paths to keep. Supports dot-separated nested paths
    /// ("customer.name"), array-element projections ("items[].sku"), and plain
    /// top-level names ("id"). Paths sharing a prefix are merged into one output node.
    /// A JSON array root projects each element by the field paths.
    /// </param>
    /// <param name="maxItems">
    /// When set and >= 0 and the (already projected) root is a JSON array, keeps only
    /// the first maxItems elements. No-op on non-array roots.
    /// </param>
    public static string Shape(string json, int? maxLength, string[]? fields, int? maxItems)
    {
        var result = json;

        if (fields is { Length: > 0 })
            result = Project(result, fields);

        if (maxItems is { } items && items >= 0)
            result = CapItems(result, items);

        if (maxLength is { } max && max >= 0 && result.Length > max)
            result = result[..max];

        return result;
    }

    // -------------------------------------------------------------------------
    // Projection tree
    // -------------------------------------------------------------------------

    private sealed class ProjectionNode
    {
        public Dictionary<string, ProjectionNode>? Children { get; private set; }

        // True when this node is the final segment of a path: write the entire value.
        public bool IsTerminal { get; set; }

        // True when this property holds a JSON array and the sub-paths apply per element.
        public bool IsArray { get; set; }

        public ProjectionNode GetOrCreateChild(string key)
        {
            Children ??= new Dictionary<string, ProjectionNode>(StringComparer.Ordinal);
            if (!Children.TryGetValue(key, out var child))
            {
                child = new ProjectionNode();
                Children[key] = child;
            }
            return child;
        }
    }

    /// <summary>
    /// Builds a projection tree from a set of field paths.
    /// Dot separates object segments; a segment ending with [] marks an array property
    /// whose remaining path applies to each element.
    /// </summary>
    private static ProjectionNode BuildProjectionTree(string[] fields)
    {
        var root = new ProjectionNode();

        foreach (var field in fields)
        {
            if (string.IsNullOrEmpty(field)) continue;

            var segments = field.Split('.');
            var current = root;

            for (int i = 0; i < segments.Length; i++)
            {
                var seg = segments[i];
                var isArraySeg = seg.EndsWith("[]", StringComparison.Ordinal);
                var key = isArraySeg ? seg[..^2] : seg;

                // Skip empty segments from malformed paths (e.g. leading/trailing dots).
                if (string.IsNullOrEmpty(key)) continue;

                var child = current.GetOrCreateChild(key);

                if (i == segments.Length - 1)
                {
                    // Final segment: emit the complete value at this depth.
                    child.IsTerminal = true;
                }
                else if (isArraySeg)
                {
                    // Intermediate array segment: iterate each element with sub-paths.
                    child.IsArray = true;
                }
                // else: intermediate object segment; no flag needed, just recurse.

                current = child;
            }
        }

        return root;
    }

    // -------------------------------------------------------------------------
    // Projection
    // -------------------------------------------------------------------------

    private static string Project(string json, string[] fields)
    {
        try
        {
            var tree = BuildProjectionTree(fields);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                if (root.ValueKind == JsonValueKind.Object)
                {
                    WriteProjectedObject(writer, root, tree);
                }
                else if (root.ValueKind == JsonValueKind.Array)
                {
                    writer.WriteStartArray();
                    foreach (var element in root.EnumerateArray())
                    {
                        if (element.ValueKind == JsonValueKind.Object)
                            WriteProjectedObject(writer, element, tree);
                        else
                            element.WriteTo(writer);
                    }
                    writer.WriteEndArray();
                }
                else
                {
                    // Scalar root: nothing to project.
                    return json;
                }
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch
        {
            // Best-effort: on any parse/serialization error, return the original.
            return json;
        }
    }

    private static void WriteProjectedObject(
        Utf8JsonWriter writer, JsonElement obj, ProjectionNode node)
    {
        writer.WriteStartObject();

        if (node.Children is not null)
        {
            foreach (var (key, childNode) in node.Children)
            {
                if (!obj.TryGetProperty(key, out var value)) continue;
                writer.WritePropertyName(key);
                WriteProjectedValue(writer, value, childNode);
            }
        }

        writer.WriteEndObject();
    }

    private static void WriteProjectedValue(
        Utf8JsonWriter writer, JsonElement value, ProjectionNode node)
    {
        // Terminal: emit the full value without further drilling.
        if (node.IsTerminal)
        {
            value.WriteTo(writer);
            return;
        }

        // Array node: map the remaining sub-paths over each element of the array.
        if (node.IsArray)
        {
            if (value.ValueKind == JsonValueKind.Array)
            {
                writer.WriteStartArray();
                foreach (var element in value.EnumerateArray())
                {
                    if (element.ValueKind == JsonValueKind.Object && node.Children is not null)
                        WriteProjectedObject(writer, element, node);
                    else
                        element.WriteTo(writer);
                }
                writer.WriteEndArray();
            }
            else
            {
                // Not actually an array in the JSON; pass through as-is (best effort).
                value.WriteTo(writer);
            }
            return;
        }

        // Object node: recurse into the nested object.
        if (value.ValueKind == JsonValueKind.Object)
            WriteProjectedObject(writer, value, node);
        else
            value.WriteTo(writer); // Non-object value at an object path; pass through.
    }

    // -------------------------------------------------------------------------
    // Array item capping
    // -------------------------------------------------------------------------

    private static string CapItems(string json, int maxItems)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Array) return json;

            var buffer = new System.IO.MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                int count = 0;
                foreach (var element in root.EnumerateArray())
                {
                    if (count >= maxItems) break;
                    element.WriteTo(writer);
                    count++;
                }
                writer.WriteEndArray();
            }

            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch
        {
            return json;
        }
    }
}
