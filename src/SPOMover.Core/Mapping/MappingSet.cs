using System.Text;
using SPOMover.Core.Config;
using SPOMover.Core.Urls;
using SPOMover.Core.Localization;

namespace SPOMover.Core.Mapping;

/// <summary>
/// MAP-1: one row of the mapping — a path in the source library (folder or file) and where it goes.
/// The same JSON is stored in the profile, imported from CSV/XLSX and shown in the editor.
/// </summary>
public sealed class MappingEntry
{
    /// <summary>Library value that keeps the subtree in the source (an exception inside a moved folder).</summary>
    public const string Stay = "-";

    /// <summary>Path in the source library, relative to its root: "Фін/2023". "/" — the whole library.</summary>
    public string? Source { get; set; }

    /// <summary>Target library (one of the profile's target libraries) or <see cref="Stay"/>.</summary>
    public string? Library { get; set; }

    /// <summary>Path in the target library the source turns into. Empty — the same path as in the source; "/" — the library root.</summary>
    public string? Path { get; set; }

    /// <summary>Free text for people: who agreed, why.</summary>
    public string? Comment { get; set; }
}

/// <summary>A row of the mapping in the form lookups use.</summary>
/// <param name="Row">1-based row number as the user sees it.</param>
/// <param name="Source">Cleaned source path ("" — the library root).</param>
/// <param name="Library">Target library as named in the profile; null — the subtree stays in the source.</param>
/// <param name="TargetPath">Cleaned target path ("" — the library root).</param>
public sealed record MappingRule(int Row, string Source, string? Library, string TargetPath)
{
    public bool Stays => Library is null;

    public override string ToString() =>
        $"{(Source.Length == 0 ? "/" : Source)} → {(Library is null ? L.T("keep in source") : $"{Library}:/{TargetPath}")}";
}

/// <summary>Where one path of the source goes.</summary>
/// <param name="NewPath">Path in the target library, null when the item stays.</param>
public sealed record MappingMatch(MappingRule Rule, string? Library, string? NewPath)
{
    public bool Stays => Library is null;
}

public sealed record MappingBuild(MappingSet? Set, IReadOnlyList<ValidationIssue> Issues)
{
    public bool IsValid => Set is not null && Issues.All(i => i.Severity != IssueSeverity.Error);
}

/// <summary>
/// MAP-2…MAP-4: a validated mapping. For any path of the source, the rule with the longest matching source wins
/// (MAP-2); matching ignores case and percent-encoding and happens only at folder boundaries (MAP-3), so the order
/// of rows does not matter.
/// </summary>
public sealed class MappingSet
{
    public const string Area = "mapping";

    /// <summary>Characters SharePoint does not allow in file and folder names.</summary>
    private static readonly char[] Forbidden = ['"', '*', ':', '<', '>', '?', '|'];

    private readonly Dictionary<string, MappingRule> _bySource;

    private MappingSet(IReadOnlyList<MappingRule> rules)
    {
        Rules = rules;
        _bySource = rules.ToDictionary(r => Key(r.Source), StringComparer.Ordinal);
    }

    public IReadOnlyList<MappingRule> Rules { get; }

    public static MappingSet Empty { get; } = new([]);

    /// <summary>
    /// Validates the rows against the profile's libraries. Invalid rows are left out of the set; the set is still
    /// returned so the editor and the coverage report can show what the valid rows do.
    /// </summary>
    public static MappingBuild Build(IReadOnlyList<MappingEntry> entries, string? sourceLibrary, IReadOnlyCollection<string> targetLibraries)
    {
        var issues = new List<ValidationIssue>();
        var rules = new List<MappingRule>();
        var targets = new HashSet<string>(targetLibraries.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()), StringComparer.OrdinalIgnoreCase);
        void Error(int row, string message) => issues.Add(new(IssueSeverity.Error, message, row, Area));
        void Warn(int row, string message) => issues.Add(new(IssueSeverity.Warning, message, row, Area));

        for (int i = 0; i < entries.Count; i++)
        {
            int row = i + 1;
            var e = entries[i];
            if (string.IsNullOrWhiteSpace(e.Source) && string.IsNullOrWhiteSpace(e.Library) && string.IsNullOrWhiteSpace(e.Path))
                continue; // an empty row of the editor
            if (string.IsNullOrWhiteSpace(e.Source))
            {
                Error(row, L.T("no source set. For the whole library enter «/»."));
                continue;
            }
            if (e.Source.Contains("://", StringComparison.Ordinal))
            {
                Error(row, L.T("the source is a path from the root of the source library (e.g. «Fin/2023»), not an address."));
                continue;
            }
            string? sourceError = PathError(e.Source);
            if (sourceError is not null)
            {
                Error(row, L.T($"source: {sourceError}"));
                continue;
            }
            string source = Clean(e.Source);

            string library = (e.Library ?? string.Empty).Trim();
            if (library.Length == 0)
            {
                Error(row, L.T("no library set. To keep the folder in the source, enter «{0}».", MappingEntry.Stay));
                continue;
            }
            if (library == MappingEntry.Stay)
            {
                if (!string.IsNullOrWhiteSpace(e.Path))
                    Warn(row, L.T("a path is not needed for «keep in source» and is ignored."));
                rules.Add(new MappingRule(row, source, null, source));
                continue;
            }
            if (sourceLibrary is not null && string.Equals(library, sourceLibrary.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                Error(row, L.T($"library «{library}» is the source. To keep the folder in place, enter «{MappingEntry.Stay}»."));
                continue;
            }
            if (!targets.TryGetValue(library, out string? canonical))
            {
                string known = string.Join(", ", targets.Order(StringComparer.OrdinalIgnoreCase));
                Error(row, targets.Count == 0
                    ? L.T($"library «{library}» is not among the profile's target libraries (the list is empty).")
                    : L.T($"library «{library}» is not among the profile's target libraries ({known})."));
                continue;
            }

            string target;
            if (string.IsNullOrWhiteSpace(e.Path))
            {
                target = source;
            }
            else
            {
                if (e.Path.Contains("://", StringComparison.Ordinal))
                {
                    Error(row, L.T("the path is from the root of the target library (e.g. «2023» or «/»), not an address."));
                    continue;
                }
                string? pathError = PathError(e.Path) ?? NameError(e.Path);
                if (pathError is not null)
                {
                    Error(row, L.T($"path: {pathError}"));
                    continue;
                }
                target = Clean(e.Path);
            }
            if (source.Length == 0 && target.Length > 0)
                Warn(row, L.T("the whole library moves into a folder — check that this is intended."));
            rules.Add(new MappingRule(row, source, canonical, target));
        }

        // Duplicates and conflicts: the same source twice.
        var kept = new List<MappingRule>();
        foreach (var group in rules.GroupBy(r => Key(r.Source)))
        {
            var first = group.First();
            kept.Add(first);
            foreach (var other in group.Skip(1))
            {
                bool same = string.Equals(other.Library, first.Library, StringComparison.OrdinalIgnoreCase) && Key(other.TargetPath) == Key(first.TargetPath);
                if (same)
                    Warn(other.Row, L.T($"duplicates row {first.Row}; ignored."));
                else
                    Error(other.Row, L.T($"the same source as in row {first.Row}, but a different target. Keep one of the rules."));
                if (!same)
                    kept.Remove(first);
            }
        }
        var set = new MappingSet(kept.OrderBy(r => r.Row).ToList());

        // Rows that only repeat what the enclosing rule already does.
        foreach (var rule in set.Rules.Where(r => r.Source.Length > 0))
        {
            var parent = set.ParentOf(rule);
            if (parent is null)
                continue;
            string rest = rule.Source[(parent.Source.Length == 0 ? 0 : parent.Source.Length + 1)..];
            bool redundant = string.Equals(parent.Library, rule.Library, StringComparison.OrdinalIgnoreCase)
                && (rule.Stays || Key(Join(parent.TargetPath, rest)) == Key(rule.TargetPath));
            if (redundant)
                Warn(rule.Row, L.T($"changes nothing: row {parent.Row} already moves this folder the same way."));
        }

        // Several sources into the same folder: allowed (folders merge), but names may collide.
        foreach (var group in set.Rules.Where(r => !r.Stays).GroupBy(r => (Library: r.Library!.ToLowerInvariant(), Path: Key(r.TargetPath))).Where(g => g.Count() > 1))
        {
            var rows = group.Select(r => r.Row).ToList();
            string list = string.Join(", ", rows);
            Warn(rows[^1], L.T($"rows {list} lead into one folder «{group.First().Library}:/{group.First().TargetPath}» — the contents will merge; the coverage check will show name conflicts."));
        }
        return new MappingBuild(set, issues);
    }

    /// <summary>MAP-2: the rule for a path of the source, or null if no rule covers it (the item stays, MAP-5).</summary>
    public MappingMatch? Resolve(string path)
    {
        var segments = Segments(path);
        var keys = segments.Select(s => s.ToLowerInvariant()).ToArray();
        for (int n = segments.Length; n >= 0; n--)
        {
            if (!_bySource.TryGetValue(string.Join('/', keys, 0, n), out var rule))
                continue;
            if (rule.Stays)
                return new MappingMatch(rule, null, null);
            string rest = string.Join('/', segments, n, segments.Length - n);
            return new MappingMatch(rule, rule.Library, Join(rule.TargetPath, rest));
        }
        return null;
    }

    /// <summary>The closest rule above <paramref name="rule"/>, if any.</summary>
    public MappingRule? ParentOf(MappingRule rule)
    {
        var keys = Segments(rule.Source).Select(s => s.ToLowerInvariant()).ToArray();
        for (int n = keys.Length - 1; n >= 0; n--)
        {
            if (_bySource.TryGetValue(string.Join('/', keys, 0, n), out var parent))
                return parent;
        }
        return null;
    }

    /// <summary>MAP-3: a path as the user wrote it, cleaned: percent-decoded, '/' separators, no empty segments or spaces around them.</summary>
    public static string Clean(string path) => string.Join('/', Segments(path));

    /// <summary>Comparison key of a path (MAP-3): cleaned and lower case.</summary>
    public static string Key(string path) => Clean(path).ToLowerInvariant();

    public static string Join(string folder, string rest) =>
        folder.Length == 0 ? rest : rest.Length == 0 ? folder : folder + "/" + rest;

    private static string[] Segments(string path)
    {
        string text = UrlText.ContainsPercentEscape(path) ? UrlText.Decode(path) : path;
        return text.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? PathError(string path)
    {
        foreach (string segment in Segments(path))
        {
            if (segment is "." or "..")
                return L.T("«.» and «..» are not allowed in a path.");
        }
        return null;
    }

    /// <summary>Names SharePoint refuses for new folders (MAP-4).</summary>
    private static string? NameError(string path)
    {
        var segments = Segments(path);
        for (int i = 0; i < segments.Length; i++)
        {
            string s = segments[i];
            if (s.IndexOfAny(Forbidden) >= 0)
            {
                string chars = string.Join(' ', Forbidden);
                return L.T($"«{s}» contains a character that is not allowed ({chars}).");
            }
            if (s.EndsWith('.'))
                return L.T($"«{s}» ends with a dot.");
            if (s.Contains("_vti_", StringComparison.OrdinalIgnoreCase))
                return L.T($"«{s}» contains the reserved «_vti_».");
            if (i == 0 && string.Equals(s, "Forms", StringComparison.OrdinalIgnoreCase))
                return L.T("«Forms» in the library root is a SharePoint system folder.");
        }
        return null;
    }

    /// <summary>MAP-6: rows as CSV (source,library,path,comment), UTF-8 with BOM.</summary>
    /// <summary>
    /// A sample file for the import (MAP-6): every kind of row with a comment, in the profile's target libraries when
    /// it has them. Semicolon-separated with a BOM, so Excel with a Ukrainian locale opens it in columns.
    /// </summary>
    public static byte[] SampleCsv(IReadOnlyList<string> libraries)
    {
        string a = libraries.Count > 0 ? libraries[0] : L.T("Archive-Finance");
        string b = libraries.Count > 1 ? libraries[1] : libraries.Count > 0 ? libraries[0] : L.T("Archive-HR");
        string[][] rows =
        [
            ["source", "library", "path", "comment"],
            [L.T("Finance"), a, "", L.T("The whole folder with subfolders — to the same path in the target library (Finance/...)")],
            [L.T("Finance/Drafts"), MappingEntry.Stay, "", L.T("Exception: this subfolder stays in the source")],
            [L.T("HR/2023"), b, "2023", L.T("Into folder 2023 of the target library")],
            [L.T("Legal"), b, "/", L.T("The folder's contents — into the root of the target library")],
            [L.T("General/Regulations.docx"), a, L.T("References/Regulations.docx"), L.T("A single file — under another path")],
        ];
        var sb = new StringBuilder();
        foreach (var r in rows)
            sb.Append(string.Join(';', r.Select(v => Reports.CsvReport.Escape(v, ';')))).Append("\r\n");
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }

    public static byte[] ToCsv(IEnumerable<MappingEntry> entries)
    {
        var sb = new StringBuilder("source,library,path,comment\r\n");
        foreach (var e in entries)
        {
            sb.Append(Reports.CsvReport.Escape(e.Source, ',')).Append(',').Append(Reports.CsvReport.Escape(e.Library, ','))
                .Append(',').Append(Reports.CsvReport.Escape(e.Path, ',')).Append(',').Append(Reports.CsvReport.Escape(e.Comment, ',')).Append("\r\n");
        }
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(sb.ToString())];
    }
}
