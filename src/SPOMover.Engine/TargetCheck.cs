using System.Globalization;
using SPOMover.Core;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Core.Reports;
using SPOMover.Spo;
using SPOMover.Spo.Graph;

namespace SPOMover.Engine;

/// <summary>One finding of the target check: which library, how serious, what and why it matters.</summary>
/// <param name="Severity">error (not checked), warning (files lose something or become invisible), info.</param>
/// <param name="Library">Index of the library in the profile (1… — the targets; 0 — the source).</param>
/// <param name="Kind">missing_column, type_mismatch, required_column, missing_content_type, force_checkout, moderation, minor_versions, no_versioning, unreachable.</param>
public sealed record TargetFinding(int Library, string LibraryName, string Severity, string Kind, string Message);

public sealed record TargetCheckReport(DateTimeOffset At, IReadOnlyList<TargetFinding> Findings, IReadOnlyList<string> Checked)
{
    public int Warnings => Findings.Count(f => f.Severity != "info");
    public bool Ok => Warnings == 0;
}

/// <summary>
/// Before a move: the target libraries are created by hand, and SharePoint quietly drops what a target cannot hold.
/// A column of the source that the target lacks loses its values; a content type that is missing turns into the
/// default one; required check-out, content approval and minor versions in the target change who sees the moved files.
/// Nothing here blocks a move — the findings are warnings for the person who starts it.
/// </summary>
public static class TargetCheck
{
    /// <summary>
    /// Columns SharePoint or Microsoft 365 add on their own (image tags, retention labels, service fields): they differ
    /// between libraries of the same site without anybody's doing, and moving does not need them.
    /// </summary>
    private static readonly string[] SystemPrefixes =
        ["_", "MediaService", "TaxCatchAll", "lcf76f155ced4ddcb4097134ff3c332f", "Compliance", "SharedWith", "Lookup", "Thumbnail", "Preview"];

    private static readonly string[] DefaultTypes = ["Folder", "Document", "Папка", "Документ"]; // i18n-ignore: names of the default content types on a Ukrainian site

    public static async Task<TargetCheckReport> RunAsync(GraphClient client, Profile profile, DateTimeOffset now, CancellationToken ct)
    {
        var libraries = profile.Libraries();
        var schemas = new Dictionary<int, ListSchema>();
        var findings = new List<TargetFinding>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < libraries.Count; i++)
        {
            var target = libraries[i];
            string key = $"{target.SiteUrl?.TrimEnd('/')}|{target.Library?.Trim()}";
            if (seen.TryGetValue(key, out int same))
            {
                if (schemas.TryGetValue(same, out var known))
                    schemas[i] = known;
                continue;
            }
            seen[key] = i;
            try
            {
                var (drive, _, _) = await TargetResolver.ResolveAsync(client, target, ct).ConfigureAwait(false);
                string listId = await client.GetListIdAsync(drive.Id, ct).ConfigureAwait(false);
                schemas[i] = await client.GetListSchemaAsync(target.SiteUrl!, listId, ct).ConfigureAwait(false);
            }
            catch (SpoException ex) when (ex is not SpoBlockedException)
            {
                findings.Add(new(i, Name(target), "error", "unreachable",
                    ex.ErrorClass == ErrorClass.Auth
                        ? L.T($"library settings were not read: {ex.Message} A SharePoint REST permission (Sites.Selected) on this site is needed.")
                        : L.T($"library settings were not read: {ex.Message}")));
            }
        }
        if (schemas.TryGetValue(0, out var source))
        {
            var compared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < libraries.Count; i++)
            {
                if (schemas.TryGetValue(i, out var target) && compared.Add($"{libraries[i].SiteUrl?.TrimEnd('/')}|{libraries[i].Library?.Trim()}"))
                    findings.AddRange(Compare(source, target, i, Name(libraries[i])));
            }
        }
        return new TargetCheckReport(now, findings, [.. Enumerable.Range(1, Math.Max(0, libraries.Count - 1)).Select(i => Name(libraries[i])).Distinct(StringComparer.OrdinalIgnoreCase)]);
    }

    private static string Name(Target t) => string.IsNullOrEmpty(t.Folder) ? t.Library ?? "?" : $"{t.Library} / {t.Folder.Trim('/')}";

    /// <summary>Columns users fill in: shown, writable, added to the library (not the built-in ones of every library).</summary>
    public static bool IsUserColumn(ListField f) =>
        !f.Hidden && !f.ReadOnly && !f.FromBaseType && !SystemPrefixes.Any(p => f.InternalName.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    public static IEnumerable<TargetFinding> Compare(ListSchema source, ListSchema target, int index, string name)
    {
        var targetFields = target.Fields.GroupBy(f => f.InternalName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        foreach (var f in source.Fields.Where(IsUserColumn).OrderBy(f => f.Title, StringComparer.CurrentCulture))
        {
            if (!targetFields.TryGetValue(f.InternalName, out var t))
            {
                yield return new(index, name, "warning", "missing_column",
                    L.T($"column \"{f.Title}\" ({f.InternalName}, {f.Type}) of the source is missing in the target — its values in the moved files will be lost. Add the column to the target library (the same site column) before the move."));
            }
            else if (!string.Equals(t.Type, f.Type, StringComparison.OrdinalIgnoreCase))
            {
                yield return new(index, name, "warning", "type_mismatch",
                    L.T($"column \"{f.Title}\" ({f.InternalName}) is {f.Type} in the source and {t.Type} in the target: the value may not carry over, or carry over differently."));
            }
        }
        var sourceNames = source.Fields.Select(f => f.InternalName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in target.Fields.Where(f => f.Required && IsUserColumn(f) && !sourceNames.Contains(f.InternalName)))
        {
            yield return new(index, name, "warning", "required_column",
                L.T($"the target has a required column \"{t.Title}\" ({t.InternalName}) that the source lacks: the moved files will have no value (\"required info\"), and with required check-out they may stay unpublished."));
        }
        var targetTypes = target.ContentTypes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string type in source.ContentTypes.Where(t => !DefaultTypes.Contains(t, StringComparer.OrdinalIgnoreCase) && !targetTypes.Contains(t)))
        {
            yield return new(index, name, "warning", "missing_content_type",
                L.T($"content type \"{type}\" of the source is missing in the target: files of this type will get the default type, and its columns will lose their values."));
        }
        if (target.ForceCheckout)
        {
            yield return new(index, name, "warning", "force_checkout",
                L.T("the target has \"Require check out\" on: moved files may stay checked out to the app and be invisible to others. Turn it off for the time of the move."));
        }
        if (target.EnableModeration && !source.EnableModeration)
        {
            yield return new(index, name, "warning", "moderation",
                L.T("the target has content approval on, the source does not: moved files will wait for approval and be invisible to readers."));
        }
        if (target.EnableMinorVersions && !source.EnableMinorVersions)
        {
            yield return new(index, name, "warning", "minor_versions",
                L.T("the target has minor versions (drafts) on, the source does not: moved files may become drafts invisible to readers."));
        }
        if (!target.EnableVersioning && source.EnableVersioning)
        {
            yield return new(index, name, "info", "no_versioning",
                L.T("the target has versioning off, the source has it on: new changes of the moved files will have no versions."));
        }
    }

    public static IEnumerable<IReadOnlyList<string?>> Rows(TargetCheckReport report)
    {
        yield return ["library", "severity", "kind", "message"];
        foreach (var f in report.Findings)
            yield return [f.LibraryName, f.Severity, f.Kind, f.Message];
    }

    public static string Text(TargetCheckReport report)
    {
        var sb = new System.Text.StringBuilder();
        string libraries = string.Join(", ", report.Checked);
        sb.AppendLine(L.T($"Target library check: {libraries}"));
        if (report.Findings.Count == 0)
            sb.AppendLine(L.T("No differences from the source library found: columns, content types and settings suit the move."));
        foreach (var g in report.Findings.GroupBy(f => f.LibraryName))
        {
            sb.AppendLine(L.T($"\"{g.Key}\":"));
            foreach (var f in g)
                sb.AppendLine(CultureInfo.InvariantCulture, $"  {(f.Severity == "info" ? "i" : f.Severity == "error" ? "x" : "!")} {f.Message}");
        }
        return sb.ToString();
    }
}
