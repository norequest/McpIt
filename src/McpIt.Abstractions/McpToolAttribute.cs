using System;

namespace McpIt;

/// <summary>
/// Marks an ASP.NET Core controller action to be exposed as an MCP tool.
/// Opt-in: only annotated actions are turned into tools.
/// <para>
/// When placed on a controller <b>class</b>, the attribute sets defaults that are
/// inherited by the action-level <c>[McpTool]</c> attributes in that class. It does
/// not expose anything by itself: an action is still only turned into a tool if the
/// action itself carries <c>[McpTool]</c>.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class McpToolAttribute : Attribute
{
    /// <summary>
    /// Optional explicit tool name. When null, the name is derived from the method name.
    /// <para>
    /// On a controller <b>class</b> this property has no meaning and is ignored; use
    /// <see cref="NamePrefix"/> to influence the names of the class's action tools.
    /// </para>
    /// </summary>
    public string? Name { get; set; }

    /// <summary>
    /// Acknowledges that this tool performs a destructive (state-changing) operation.
    /// Set to <c>true</c> to suppress the MCPGEN002 destructive-operation warning.
    /// <para>
    /// On a controller <b>class</b> this acts as a default: an action is treated as
    /// destructive-acknowledged if either the action or the containing class sets
    /// <c>AllowDestructive = true</c>.
    /// </para>
    /// </summary>
    public bool AllowDestructive { get; set; }

    /// <summary>
    /// Optional human-friendly display title for the tool. When null, a title is derived
    /// automatically from the method name by splitting on PascalCase word boundaries
    /// (for example, "GetOrderById" becomes "Get Order By Id").
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Only applies when the attribute is placed on a controller <b>class</b>.
    /// Prepended to the derived (camelCase method-name) tool name of each
    /// <c>[McpTool]</c>-annotated action in that class. It is not applied when an
    /// action specifies an explicit <see cref="Name"/> (the explicit name wins
    /// verbatim). When placed on a method, this property is ignored.
    /// </summary>
    public string? NamePrefix { get; set; }
}
