using System;

namespace McpIt;

/// <summary>
/// Trims and/or projects the output of an MCP tool before it is returned to the model,
/// keeping tool responses small and focused.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class McpToolOutputAttribute : Attribute
{
    /// <summary>
    /// When greater than zero, truncates the (possibly projected) response to this many characters.
    /// </summary>
    public int MaxLength { get; set; }

    /// <summary>
    /// When set, keeps only these top-level JSON properties (for an object response, or for each
    /// element of an array response). All other properties are dropped.
    /// </summary>
    public string[]? Fields { get; set; }

    /// <summary>
    /// When greater than zero, keeps only the first N elements of an array response.
    /// Applied after field projection and before length truncation.
    /// </summary>
    public int MaxItems { get; set; }
}
