using SPOMover.Core.Localization;

namespace SPOMover.Core.Config;

public enum IssueSeverity
{
    Warning,
    Error,
}

/// <summary>A problem found when a profile (CFG-6) or, from stage 2, a mapping (MAP-4) is validated.</summary>
/// <param name="Row">1-based number of the row as the user sees it, or null for the whole.</param>
/// <param name="Area">Part of the profile the issue belongs to, e.g. "mapping"; null — the rest.</param>
public sealed record ValidationIssue(IssueSeverity Severity, string Message, int? Row = null, string? Area = null)
{
    public override string ToString()
    {
        string level = Severity == IssueSeverity.Error ? L.T("ERROR") : L.T("Warning");
        if (Row is not int i)
            return $"{level}: {Message}";
        return Area == "mapping" ? L.T($"{level} [mapping, row {i}]: {Message}") : L.T($"{level} [row {i}]: {Message}");
    }
}
