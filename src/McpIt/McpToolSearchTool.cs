using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpIt;

/// <summary>
/// The <c>search_tools</c> meta-tool: lets an agent describe a task in plain words and get back
/// the best-matching tools from the catalog, ranked by <see cref="McpToolIndex"/>.
/// </summary>
/// <remarks>
/// Implemented as a direct <see cref="McpServerTool"/> subclass with a hand-built input schema
/// and a <see cref="Utf8JsonWriter"/> response, instead of <c>McpServerTool.Create(Delegate)</c>,
/// whose AIFunctionFactory path relies on reflection and is not trim/AOT safe. Usually registered
/// through <see cref="McpToolSearchExtensions.WithToolSearch"/>.
/// </remarks>
public sealed class McpToolSearchTool : McpServerTool
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        // The response goes to a model, not into HTML: keep non-ASCII text (and quotes in
        // descriptions) readable instead of spending tokens on \uXXXX escapes.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly McpToolIndex _index;
    private readonly int _maxResults;

    /// <summary>Creates the meta-tool over a prebuilt index.</summary>
    /// <param name="index">The catalog to search.</param>
    /// <param name="options">Tool name, result cap and optional description override.</param>
    /// <exception cref="ArgumentException"><see cref="McpToolSearchOptions.ToolName"/> is empty.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <see cref="McpToolSearchOptions.MaxResults"/> is outside 1..<see cref="McpToolSearchOptions.MaxResultsCap"/>.
    /// </exception>
    public McpToolSearchTool(McpToolIndex index, McpToolSearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(index);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ToolName, nameof(options));
        if (options.MaxResults < 1 || options.MaxResults > McpToolSearchOptions.MaxResultsCap)
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.MaxResults,
                $"McpToolSearchOptions.MaxResults must be between 1 and {McpToolSearchOptions.MaxResultsCap}.");

        _index = index;
        _maxResults = options.MaxResults;

        ProtocolTool = new Tool
        {
            Name = options.ToolName,
            Title = "Search tools",
            Description = options.Description ?? DefaultDescription(index.Count),
            InputSchema = BuildInputSchema(_maxResults),
            Annotations = new ToolAnnotations
            {
                Title = "Search tools",
                ReadOnlyHint = true,
                DestructiveHint = false,
                IdempotentHint = true,
                OpenWorldHint = false,
            },
        };
    }

    /// <inheritdoc />
    public override Tool ProtocolTool { get; }

    /// <inheritdoc />
    public override IReadOnlyList<object> Metadata { get; } = Array.Empty<object>();

    /// <inheritdoc />
    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var args = request.Params?.Arguments;

        string? query = null;
        if (args is not null && args.TryGetValue("query", out var q) && q.ValueKind == JsonValueKind.String)
            query = q.GetString();

        if (string.IsNullOrWhiteSpace(query))
            return new ValueTask<CallToolResult>(Text(
                "Missing required argument 'query': describe the task in plain words, e.g. \"cancel an order\".",
                isError: true));

        int? limit = null;
        if (args!.TryGetValue("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out var parsed))
            limit = parsed;

        return new ValueTask<CallToolResult>(Text(Search(query, limit), isError: false));
    }

    /// <summary>
    /// Runs a search and returns the compact JSON the tool sends to the model:
    /// <c>{"query":"...","results":[{"name","title","description","score","readOnly","destructive","parameters":[...]}]}</c>,
    /// best match first, with <c>score</c> rounded to three decimals.
    /// </summary>
    /// <param name="query">A plain-language description of the task.</param>
    /// <param name="limit">Number of results; clamped to 1..<see cref="McpToolSearchOptions.MaxResults"/>.
    /// Null uses <see cref="McpToolSearchOptions.MaxResults"/>.</param>
    public string Search(string query, int? limit = null)
    {
        var top = Math.Clamp(limit ?? _maxResults, 1, _maxResults);
        var matches = _index.Search(query, top);

        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("query", query);
            writer.WriteStartArray("results");
            foreach (var match in matches)
            {
                var tool = match.Tool;
                writer.WriteStartObject();
                writer.WriteString("name", tool.Name);
                writer.WriteString("title", tool.Title ?? string.Empty);
                if (tool.Description is null)
                    writer.WriteNull("description");
                else
                    writer.WriteString("description", tool.Description);
                writer.WriteNumber("score", Math.Round(match.Score, 3, MidpointRounding.AwayFromZero));
                writer.WriteBoolean("readOnly", tool.ReadOnly);
                writer.WriteBoolean("destructive", tool.Destructive);
                writer.WriteStartArray("parameters");
                foreach (var p in tool.Parameters ?? [])
                    writer.WriteStringValue(p);
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static CallToolResult Text(string text, bool isError) => new()
    {
        Content = [new TextContentBlock { Text = text }],
        IsError = isError ? true : null,
    };

    private static string DefaultDescription(int toolCount) =>
        $"Find the right tool for a task among this server's {toolCount} tools. Call this FIRST " +
        "whenever you are unsure which tool fits the user's request, instead of guessing from names. " +
        "Pass the task in plain words (e.g. \"cancel an order\", \"list unpaid invoices\", \"who bought this\"); " +
        "you get the best-matching tools ranked by relevance, with their parameters and whether they " +
        "are read-only or destructive. Then call the chosen tool directly. Matching is by keyword " +
        "(no typo correction): if nothing fits, retry with synonyms or fewer words.";

    // Hand-built JSON Schema: no reflection-based schema generation (AOT/trim safe).
    private static JsonElement BuildInputSchema(int maxResults)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("type", "object");
            w.WriteStartObject("properties");

            w.WriteStartObject("query");
            w.WriteString("type", "string");
            w.WriteString("description",
                "What you want to do, in plain words, e.g. \"refund an invoice\" or \"list customers\".");
            w.WriteEndObject();

            w.WriteStartObject("limit");
            w.WriteString("type", "integer");
            w.WriteNumber("minimum", 1);
            w.WriteNumber("maximum", maxResults);
            w.WriteString("description", $"Maximum number of tools to return (default {maxResults}).");
            w.WriteEndObject();

            w.WriteEndObject();
            w.WriteStartArray("required");
            w.WriteStringValue("query");
            w.WriteEndArray();
            w.WriteEndObject();
        }

        using var doc = JsonDocument.Parse(ms.ToArray());
        return doc.RootElement.Clone();
    }
}
