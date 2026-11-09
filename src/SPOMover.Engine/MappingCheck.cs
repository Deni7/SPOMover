using System.Globalization;
using System.Text;
using SPOMover.Core.Config;
using SPOMover.Core.Localization;
using SPOMover.Core.Mapping;
using SPOMover.Core.Reports;
using SPOMover.State;

namespace SPOMover.Engine;

public sealed record RuleCoverage(MappingRule Rule, long Files, long Bytes);

public sealed record FolderAmount(string Path, long Files, long Bytes);

public sealed record LibraryAmount(string Library, long Files, long Bytes, bool Inventoried);

/// <summary>MAP-5 and the checks of MAP-4 that need the inventory.</summary>
/// <param name="RunId">Inventory run the mapping was checked against.</param>
/// <param name="UnmappedFolders">Largest folders (two levels deep) no rule covers: their files stay in the source.</param>
/// <param name="LongPaths">Moved files whose server-relative path would be longer than <see cref="MappingCheck.MaxPath"/>.</param>
public sealed record MappingReport(
    long RunId,
    DateTimeOffset InventoryAt,
    long Files,
    long Bytes,
    IReadOnlyList<RuleCoverage> Rules,
    long MovedFiles,
    long MovedBytes,
    long StayFiles,
    long StayBytes,
    long UnmappedFiles,
    long UnmappedBytes,
    IReadOnlyList<FolderAmount> UnmappedFolders,
    IReadOnlyList<LibraryAmount> ByLibrary,
    long Conflicts,
    long LongPaths,
    long CheckedOutMoved,
    IReadOnlyList<ValidationIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.Severity == IssueSeverity.Error);
}

/// <summary>
/// MAP-5: applies the profile's mapping to the last inventory of its source library — where every file would go,
/// how much each row covers, what stays, and the problems only the inventory shows (MAP-4): names that collide in a
/// target library, paths that get too long, rows that match nothing, checked-out files inside moved folders.
/// The result per file is kept in the run (mapping preview) for the reports.
/// </summary>
public static class MappingCheck
{
    /// <summary>SharePoint limit of a decoded server-relative path.</summary>
    public const int MaxPath = 400;

    /// <summary>Preview target index of a library the inventory did not list: 1000 + its position in the profile.</summary>
    private const int NotListed = 1000;

    public static readonly string[] Reports = ["coverage", "unmapped", "problems", "preview"];

    /// <summary>The last finished inventory of the profile's source, if any.</summary>
    public static RunRow? LastInventory(StateStore store, Profile profile)
    {
        if (profile.Name is null || profile.Source is null)
            return null;
        return store.ListRuns(500)
            .Where(r => string.Equals(r.ProfileName, profile.Name, StringComparison.OrdinalIgnoreCase) && r.Mode == RunModes.Inventory && RunStatuses.IsFinished(r.Status))
            .FirstOrDefault(r => SameSource(RunConfig.Parse(r.ConfigSnapshot).Profile.Source, profile.Source));
    }

    private static bool SameSource(Target? a, Target? b) =>
        a is not null && b is not null
        && string.Equals(a.SiteUrl?.TrimEnd('/'), b.SiteUrl?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Library?.Trim(), b.Library?.Trim(), StringComparison.OrdinalIgnoreCase)
        && string.Equals((a.Folder ?? string.Empty).Trim('/'), (b.Folder ?? string.Empty).Trim('/'), StringComparison.OrdinalIgnoreCase);

    public static MappingReport Run(StateStore store, Profile profile, RunRow? inventory = null)
    {
        inventory ??= LastInventory(store, profile)
            ?? throw new InvalidOperationException(L.T("This profile has no finished inventory of the source yet. Run inventory first."));
        var build = profile.BuildMapping();
        var set = build.Set ?? MappingSet.Empty;
        var issues = build.Issues.ToList();

        // Libraries of the inventory by name: the target index in the run and the server-relative path of the library.
        var runLibraries = RunConfig.Parse(inventory.ConfigSnapshot).Profile.Libraries();
        var runTargets = store.GetTargets(inventory.Id).ToDictionary(t => t.Index);
        var targetIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var libraryPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < profile.TargetLibraries.Count; i++)
        {
            string name = profile.TargetLibraries[i].Trim();
            int run = Enumerable.Range(1, Math.Max(0, runLibraries.Count - 1)).FirstOrDefault(k => string.Equals(runLibraries[k].Library?.Trim(), name, StringComparison.OrdinalIgnoreCase));
            targetIndex[name] = run > 0 ? run : NotListed + i;
            libraryPath[name] = run > 0 && runTargets.TryGetValue(run, out var t) && t.DriveUrl is { } url
                ? Uri.UnescapeDataString(new Uri(url).AbsolutePath).TrimEnd('/')
                // Not listed: the URL name of the library is unknown; its display name is a close guess for the length check.
                : Uri.UnescapeDataString(new Uri(profile.Source!.SiteUrl!).AbsolutePath).TrimEnd('/') + "/" + name;
            if (run == 0)
                issues.Add(new(IssueSeverity.Warning, L.T($"Library \"{name}\" was not listed in run {inventory.Id}: conflicts with its contents were not checked. Repeat the inventory."), null, MappingSet.Area));
        }

        var perRule = set.Rules.ToDictionary(r => r.Row, _ => (Files: 0L, Bytes: 0L));
        var perLibrary = profile.TargetLibraries.ToDictionary(l => l.Trim(), _ => (Files: 0L, Bytes: 0L), StringComparer.OrdinalIgnoreCase);
        var unmappedFolders = new Dictionary<string, (long Files, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        long files = 0, bytes = 0, moved = 0, movedBytes = 0, stay = 0, stayBytes = 0, unmapped = 0, unmappedBytes = 0, longPaths = 0;

        IEnumerable<StateStore.PreviewRow> Preview()
        {
            foreach (var (id, path, size) in store.SourceFiles(inventory.Id))
            {
                files++;
                bytes += size;
                var match = set.Resolve(path);
                if (match is null)
                {
                    unmapped++;
                    unmappedBytes += size;
                    string folder = TopFolder(path);
                    unmappedFolders.TryGetValue(folder, out var u);
                    unmappedFolders[folder] = (u.Files + 1, u.Bytes + size);
                    yield return new StateStore.PreviewRow(id, null, null, null);
                    continue;
                }
                var c = perRule[match.Rule.Row];
                perRule[match.Rule.Row] = (c.Files + 1, c.Bytes + size);
                if (match.Stays)
                {
                    stay++;
                    stayBytes += size;
                    yield return new StateStore.PreviewRow(id, match.Rule.Row, -1, null);
                    continue;
                }
                moved++;
                movedBytes += size;
                var l = perLibrary[match.Library!];
                perLibrary[match.Library!] = (l.Files + 1, l.Bytes + size);
                if (libraryPath[match.Library!].Length + 1 + match.NewPath!.Length > MaxPath)
                    longPaths++;
                yield return new StateStore.PreviewRow(id, match.Rule.Row, targetIndex[match.Library!], match.NewPath);
            }
        }
        store.SavePreview(inventory.Id, Preview());

        long conflicts = store.PreviewConflicts(inventory.Id).Count;
        long checkedOut = store.GetCheckouts(inventory.Id)
            .Count(c => c.TargetIndex == 0 && set.Resolve(c.Path) is { Stays: false });

        foreach (var rule in set.Rules.Where(r => perRule[r.Row].Files == 0))
            issues.Add(new(IssueSeverity.Warning, L.T("matches no file of the inventory (no such folder, or it is empty)."), rule.Row, MappingSet.Area));
        if (conflicts > 0)
            issues.Add(new(IssueSeverity.Error, L.T($"Name conflicts in the target libraries: {Ui(conflicts)} files (\"Problems\" report)."), null, MappingSet.Area));
        if (longPaths > 0)
            issues.Add(new(IssueSeverity.Error, L.T($"Path after the move longer than {MaxPath} characters: {Ui(longPaths)} files (\"Problems\" report)."), null, MappingSet.Area));
        if (checkedOut > 0)
            issues.Add(new(IssueSeverity.Warning, L.T($"Checked-out files in the folders being moved: {Ui(checkedOut)}. They must be released before the wave (LCK-1)."), null, MappingSet.Area));
        if (unmapped > 0)
            issues.Add(new(IssueSeverity.Warning, L.T($"Without a rule, staying in the source: {Ui(unmapped)} files (\"Without a rule\" report)."), null, MappingSet.Area));
        if (runTargets.TryGetValue(0, out var src) && src.Checkouts == CheckoutScan.Skipped)
            issues.Add(new(IssueSeverity.Warning, L.T("Files without a published version were not checked in this inventory (see the run notes)."), null, MappingSet.Area));

        return new MappingReport(
            inventory.Id, inventory.FinishedAt ?? inventory.CreatedAt, files, bytes,
            set.Rules.Select(r => new RuleCoverage(r, perRule[r.Row].Files, perRule[r.Row].Bytes)).ToList(),
            moved, movedBytes, stay, stayBytes, unmapped, unmappedBytes,
            unmappedFolders.OrderByDescending(x => x.Value.Bytes).Take(100).Select(x => new FolderAmount(x.Key, x.Value.Files, x.Value.Bytes)).ToList(),
            profile.TargetLibraries.Select(l => l.Trim()).Select(l => new LibraryAmount(l, perLibrary[l].Files, perLibrary[l].Bytes, targetIndex[l] < NotListed)).ToList(),
            conflicts, longPaths, checkedOut, issues);
    }

    /// <summary>The first two folders of a file's path: what the "without a rule" list groups by.</summary>
    private static string TopFolder(string path)
    {
        var segments = path.Split('/');
        return segments.Length <= 1 ? L.T("(root)") : string.Join('/', segments.Take(Math.Min(2, segments.Length - 1)));
    }

    private static string Ui(long n) => n.ToString("N0", L.Culture);

    // ------------------------------------------------------------------ reports

    public static string FileName(string name) => $"mapping-{name}.csv";

    /// <summary>Rows of a mapping report (needs <see cref="Run"/> first: the preview lives in the inventory run).</summary>
    public static IEnumerable<IReadOnlyList<string?>> Rows(StateStore store, Profile profile, MappingReport report, string name)
    {
        var libraries = profile.TargetLibraries.Select(l => l.Trim()).ToList();
        var runLibraries = RunConfig.Parse(store.GetRun(report.RunId)!.ConfigSnapshot).Profile.Libraries();
        string Library(int? index) => index switch
        {
            null => string.Empty,
            -1 => MappingEntry.Stay,
            >= NotListed when index - NotListed < libraries.Count => libraries[index.Value - NotListed],
            _ when index < runLibraries.Count => runLibraries[index.Value].Library ?? string.Empty,
            _ => index.Value.ToString(CultureInfo.InvariantCulture),
        };
        switch (name)
        {
            case "coverage":
                return Prepend(["row", "source", "library", "path", "files", "bytes"],
                    report.Rules.Select(r => (IReadOnlyList<string?>)[N(r.Rule.Row), Slash(r.Rule.Source), r.Rule.Library ?? MappingEntry.Stay,
                        r.Rule.Stays ? string.Empty : Slash(r.Rule.TargetPath), N(r.Files), N(r.Bytes)]));
            case "unmapped":
                return Prepend(["folder", "files", "bytes"],
                    report.UnmappedFolders.Select(f => (IReadOnlyList<string?>)[f.Path, N(f.Files), N(f.Bytes)]));
            case "problems":
                return Prepend(["kind", "source_path", "library", "new_path", "detail"], Problems());
            case "preview":
                return Prepend(["source_path", "size", "rule", "library", "new_path"],
                    store.PreviewRows(report.RunId).Select(p => (IReadOnlyList<string?>)[p.SourcePath, N(p.Size), p.RuleRow is { } r ? N(r) : null,
                        Library(p.TargetIndex), p.NewPath]));
            default:
                throw new ArgumentException(L.T($"Unknown report \"{name}\"."), nameof(name));
        }

        IEnumerable<IReadOnlyList<string?>> Problems()
        {
            foreach (var c in store.PreviewConflicts(report.RunId))
            {
                yield return [c.With == "moved" ? "conflict" : "exists", c.SourcePath, Library(c.TargetIndex), c.NewPath,
                    c.With == "moved" ? L.T("another source file lands on the same path") : L.T("the target library already has a file with this path")];
            }
            // Long paths: recomputed from the preview with the same prefix lengths as the check.
            var prefix = new Dictionary<int, int>();
            foreach (var p in store.PreviewRows(report.RunId).Where(p => p.TargetIndex is >= 1 && p.NewPath is not null))
            {
                int index = p.TargetIndex!.Value;
                if (!prefix.TryGetValue(index, out int length))
                {
                    var target = store.GetTargets(report.RunId).FirstOrDefault(t => t.Index == index);
                    length = target?.DriveUrl is { } url
                        ? Uri.UnescapeDataString(new Uri(url).AbsolutePath).TrimEnd('/').Length
                        : Uri.UnescapeDataString(new Uri(profile.Source!.SiteUrl!).AbsolutePath).TrimEnd('/').Length + 1 + Library(index).Length;
                    prefix[index] = length;
                }
                int total = length + 1 + p.NewPath!.Length;
                if (total > MaxPath)
                    yield return ["long_path", p.SourcePath, Library(index), p.NewPath, L.T($"{total} characters (limit {MaxPath})")];
            }
            foreach (var c in store.GetCheckouts(report.RunId).Where(c => c.TargetIndex == 0))
            {
                if (profile.BuildMapping().Set?.Resolve(c.Path) is { Stays: false } m)
                    yield return [c.Published ? "checked_out" : "unpublished", c.Path, m.Library, m.NewPath, c.CheckedOutBy];
            }
        }
    }

    public static void WriteCsv(StateStore store, Profile profile, MappingReport report, string name, Stream output) =>
        ReportExporter.WriteCsv(output, Rows(store, profile, report, name));

    public static void WriteXlsx(StateStore store, Profile profile, MappingReport report, string name, Stream output) =>
        SimpleXlsxWriter.Write(output, name, Rows(store, profile, report, name));

    /// <summary>A short text summary for the CLI and the run notes.</summary>
    public static string Summary(MappingReport r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(L.T($"Inventory: run {r.RunId}, {r.InventoryAt.ToLocalTime():dd.MM.yyyy HH:mm}; files {Ui(r.Files)}, {Gb(r.Bytes)}"));
        sb.AppendLine(L.T($"Moving: {Ui(r.MovedFiles)} files, {Gb(r.MovedBytes)}"));
        sb.AppendLine(L.T($"Staying by the \"-\" rule: {Ui(r.StayFiles)} files, {Gb(r.StayBytes)}"));
        sb.AppendLine(L.T($"Without a rule (staying): {Ui(r.UnmappedFiles)} files, {Gb(r.UnmappedBytes)}"));
        sb.AppendLine(L.T("By library:"));
        foreach (var l in r.ByLibrary)
        {
            string notListed = l.Inventoried ? string.Empty : L.T("  (not listed)");
            sb.AppendLine(L.T($"  {l.Library,-30} {Ui(l.Files),12} files {Gb(l.Bytes),12}{notListed}"));
        }
        sb.AppendLine(L.T($"Name conflicts: {Ui(r.Conflicts)}; paths too long: {Ui(r.LongPaths)}; checked out among the moved: {Ui(r.CheckedOutMoved)}"));
        foreach (var i in r.Issues)
            sb.AppendLine(i.ToString());
        return sb.ToString();
    }

    private static string Gb(long bytes)
    {
        string value = (bytes / 1024.0 / 1024.0 / 1024.0).ToString("0.00", CultureInfo.InvariantCulture);
        return L.T($"{value} GB");
    }

    private static string Slash(string path) => path.Length == 0 ? "/" : path;

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static IEnumerable<IReadOnlyList<string?>> Prepend(string?[] header, IEnumerable<IReadOnlyList<string?>> rows) => rows.Prepend(header);
}
