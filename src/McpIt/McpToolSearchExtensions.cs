using System;
using System.Collections.Generic;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;

namespace McpIt;

/// <summary>
/// Registers the <c>search_tools</c> meta-tool on an MCP server.
/// </summary>
public static class McpToolSearchExtensions
{
    /// <summary>
    /// Adds a <c>search_tools</c> meta-tool that ranks <paramref name="tools"/> against a
    /// plain-language task description, so an agent connected to an API with many tools can
    /// find the right one instead of guessing. The index is built once, here.
    /// </summary>
    /// <param name="builder">The MCP server builder from <c>AddMcpServer()</c>.</param>
    /// <param name="tools">The catalog to search, typically
    /// <c>McpIt.Generated.McpItToolCatalog.Tools</c>.</param>
    /// <param name="configure">Optional: rename the tool, change the result cap or override its
    /// description.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <example>
    /// <code>
    /// builder.Services.AddMcpServer()
    ///     .WithHttpTransport()
    ///     .WithToolsFromAssembly()
    ///     .WithToolSearch(McpIt.Generated.McpItToolCatalog.Tools);
    /// </code>
    /// </example>
    /// <remarks>Trim and Native AOT safe: no reflection-based tool creation is involved.</remarks>
    public static IMcpServerBuilder WithToolSearch(
        this IMcpServerBuilder builder,
        IReadOnlyList<McpToolDescriptor> tools,
        Action<McpToolSearchOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(tools);

        var options = new McpToolSearchOptions();
        configure?.Invoke(options);

        var tool = new McpToolSearchTool(new McpToolIndex(tools), options);

        // Register the prebuilt instance directly: the MCP server collects every McpServerTool
        // singleton from DI. This avoids WithTools<T>() / type scanning (reflection) entirely.
        builder.Services.AddSingleton<McpServerTool>(tool);
        return builder;
    }
}
