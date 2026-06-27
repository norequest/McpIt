using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace McpIt;

/// <summary>
/// AOT-clean per-tool OAuth scope gate used by generated tools that have a
/// <see cref="McpToolAttribute.RequiredScope"/> set.
/// </summary>
public static class McpScopeGuard
{
    /// <summary>
    /// Returns <c>true</c> when the current HTTP context's <c>ClaimsPrincipal</c> carries the
    /// <paramref name="requiredScope"/>. Returns <c>true</c> unconditionally when
    /// <paramref name="requiredScope"/> is null or empty (no gate). Returns <c>false</c> when
    /// the accessor, its <c>HttpContext</c>, or the <c>User</c> is null.
    /// <para>
    /// Scope matching checks two claim shapes (both are common in OAuth/OIDC tokens):
    /// <list type="bullet">
    ///   <item>A <c>scope</c> claim whose value is space-delimited (e.g. <c>"read write"</c>).</item>
    ///   <item>Individual <c>scope</c> or <c>scp</c> claims where the value equals the required scope.</item>
    /// </list>
    /// </para>
    /// </summary>
    public static bool HasScope(IHttpContextAccessor? accessor, string requiredScope)
    {
        if (string.IsNullOrEmpty(requiredScope))
            return true;

        var user = accessor?.HttpContext?.User;
        if (user is null)
            return false;

        foreach (var claim in user.Claims)
        {
            if (claim.Type != "scope" && claim.Type != "scp")
                continue;

            // Space-delimited OAuth convention: a single "scope" claim may contain multiple values.
            // Split on space and check each token; includes the single-value (no-space) case.
            var value = claim.Value;
            if (value.Length == 0)
                continue;

            foreach (var token in value.Split(' '))
            {
                if (token == requiredScope)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns a stable JSON error string for a denied scope check.
    /// Built with <see cref="Utf8JsonWriter"/> (no reflection, AOT-clean).
    /// Example: <c>{"error":"forbidden","tool":"myTool","requiredScope":"admin"}</c>.
    /// </summary>
    public static string Denied(string toolName, string requiredScope)
    {
        using var ms = new MemoryStream();
        using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("error", "forbidden");
            writer.WriteString("tool", toolName);
            writer.WriteString("requiredScope", requiredScope);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }
}
