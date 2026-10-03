using System;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace McpIt.Generator;

/// <summary>
/// MCPGEN004/MCPGEN005: Info-level hints that a tool is hard for a model to discover or call
/// correctly. Shared by the controller and minimal-API pipelines.
/// </summary>
internal static class DiscoverabilityLint
{
    private const int MinDescriptionWords = 4;

    internal static void Report(SourceProductionContext spc, EndpointModel model)
    {
        if (model.Location is not { } loc) return;

        // MCPGEN001 already covers a missing description; don't pile MCPGEN004 on top of it.
        if (model.HasDescription && IsTooShort(model))
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.ShortDescription, loc.ToLocation(), model.ToolName));
        }

        if (model.UndescribedParameters.Count > 0)
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.UndescribedParameter, loc.ToLocation(),
                model.ToolName, string.Join(", ", model.UndescribedParameters)));
        }
    }

    // Too short = fewer than MinDescriptionWords words, or nothing more than the tool's title or
    // name (compared case-insensitively, ignoring spacing and punctuation, so "Get order." matches
    // both the title "Get Order" and the name "getOrder").
    internal static bool IsTooShort(EndpointModel model)
    {
        var description = model.Description!;
        var words = description
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Count(w => w.Any(char.IsLetterOrDigit));
        if (words < MinDescriptionWords) return true;

        var normalized = Normalize(description);
        return normalized == Normalize(model.ToolName)
            || (model.Title is { } title && normalized == Normalize(title));
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
