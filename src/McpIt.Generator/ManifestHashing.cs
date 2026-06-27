using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace McpIt.Generator;

/// <summary>
/// Deterministic SHA-256 hashing utilities for the MCP tool-manifest surface.
/// All methods are pure and side-effect-free, safe for Roslyn incremental-generator pipelines.
/// </summary>
internal static class ManifestHashing
{
    // Separator characters chosen to be unambiguous in any realistic tool name / type name.
    private const char SepField = '\n';   // between fingerprint fields
    private const char SepParam = ';';    // between parameters
    private const char SepNameType = ':'; // between param name and type within one param

    /// <summary>
    /// Builds the canonical fingerprint string for one tool. Captures the model-facing surface:
    /// the derived tool name, the description, the HTTP verb, the combined route template,
    /// and the ordered sequence of parameter name:type pairs.
    /// </summary>
    internal static string Fingerprint(ManifestEntry entry)
    {
        var sb = new StringBuilder();
        sb.Append("name=").Append(entry.ToolName).Append(SepField);
        sb.Append("description=").Append(entry.Description).Append(SepField);
        sb.Append("verb=").Append(entry.HttpVerb).Append(SepField);
        sb.Append("route=").Append(entry.Route).Append(SepField);
        sb.Append("params=");
        var first = true;
        foreach (var p in entry.Parameters)
        {
            if (!first) sb.Append(SepParam);
            sb.Append(p.Name).Append(SepNameType).Append(p.TypeFullyQualified);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// SHA-256 hex digest of the tool's own canonical fingerprint.
    /// </summary>
    internal static string PerToolHash(ManifestEntry entry) => Sha256Hex(Fingerprint(entry));

    /// <summary>
    /// Order-independent aggregate hash: sorts entries by tool name (Ordinal), then SHA-256s
    /// the newline-delimited sequence of per-tool fingerprints.
    /// An empty set returns the SHA-256 of the empty string:
    ///   e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855.
    /// </summary>
    internal static string AggregateHash(IEnumerable<ManifestEntry> entries)
    {
        // Sort by tool name for a stable, declaration-order-independent canonical form.
        var sorted = entries.OrderBy(e => e.ToolName, StringComparer.Ordinal);
        var canonical = string.Join("\n", sorted.Select(Fingerprint));
        return Sha256Hex(canonical);
    }

    private static string Sha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        using var sha = SHA256.Create();
        return BytesToHex(sha.ComputeHash(bytes));
    }

    private static string BytesToHex(byte[] hash)
    {
        const string HexChars = "0123456789abcdef";
        var chars = new char[hash.Length * 2];
        for (var i = 0; i < hash.Length; i++)
        {
            chars[i * 2] = HexChars[hash[i] >> 4];
            chars[i * 2 + 1] = HexChars[hash[i] & 0xF];
        }
        return new string(chars);
    }
}
