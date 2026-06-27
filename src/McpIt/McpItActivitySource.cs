using System.Diagnostics;

namespace McpIt;

/// <summary>
/// Central ActivitySource for McpIt runtime tracing. Consumers opt in by attaching an
/// ActivityListener; the source is zero-cost when no listener is attached.
/// </summary>
public static class McpItActivitySource
{
    /// <summary>
    /// The ActivitySource used to emit spans for all loopback endpoint invocations.
    /// Named "McpIt" with version "1.0.0". StartActivity returns null when no listener is
    /// attached, making this a zero-overhead call path in production when tracing is off.
    /// </summary>
    public static readonly ActivitySource Source = new ActivitySource("McpIt", "1.0.0");
}
