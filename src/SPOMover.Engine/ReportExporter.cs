using System.Globalization;
using System.Text;
using SPOMover.Core.Localization;
using SPOMover.Core.Reports;
using SPOMover.State;

namespace SPOMover.Engine;

/// <summary>
/// Reports of a run from the state store: files (REP-2), folders with totals (INV-2, INV-6), check-outs (INV-3),
/// files that need attention and the run summary (REP-4).
/// </summary>
public static class ReportExporter
{
    public static readonly string[] Names = ["files", "folders", "checkouts", "attention", "summary"];

    /// <summary>Reports that are text, not tables.</summary>
    public static bool IsText(string name) => name is "summary";

    public static string FileName(string name) => name + (IsText(name) ? ".txt" : ".csv");

    public static void ExportAll(StateStore store, long runId, string directory)
    {
        Directory.CreateDirectory(directory);
        foreach (string name in Names)
        {
            using var stream = File.Create(Path.Combine(directory, FileName(name)));
            Write(store, runId, name, stream);
        }
    }

    public static void Write(StateStore store, long runId, string name, Stream stream)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
        if (IsText(name))
        {
            w.Write(Summary(store, runId));
            return;
        }
        foreach (var row in Rows(store, runId, name))
            Row(w, row);
    }

    /// <summary>The same report as an Excel workbook.</summary>
    public static void WriteXlsx(StateStore store, long runId, string name, Stream stream) =>
        SimpleXlsxWriter.Write(stream, name, IsText(name)
            ? Summary(store, runId).Split('\n').Select(l => (IReadOnlyList<string?>)[l.TrimEnd('\r')])
            : Rows(store, runId, name));

    /// <summary>Header and rows of a table report.</summary>
    public static IEnumerable<IReadOnlyList<string?>> Rows(StateStore store, long runId, string name) => name switch
    {
        "files" => FileRows(store.AllFiles(runId)),
        "folders" => Prepend(["library", "path", "depth", "files", "bytes", "direct_files", "max_path"],
            store.GetFolderStats(runId).Select(s => (IReadOnlyList<string?>)[N(s.TargetIndex + 1), s.Path.Length == 0 ? "/" : s.Path, N(s.Depth), N(s.Files),
                N(s.Bytes), N(s.DirectFiles), N(s.MaxPath)])),
        "checkouts" => Prepend(["library", "path", "checked_out_by", "published", "size"],
            store.GetCheckouts(runId).Select(c => (IReadOnlyList<string?>)[N(c.TargetIndex + 1), c.Path, c.CheckedOutBy, c.Published ? L.T("yes") : L.T("no"), N(c.Size)])),
        "attention" => Prepend(["id", "path", "status", "error_class", "message", "attempts", "action"],
            store.AllFiles(runId).Where(NeedsAttention).Select(f => (IReadOnlyList<string?>)[N(f.Id), f.Path ?? f.Name, f.StatusCode ?? f.Status,
                f.ErrorClass, f.Error, N(f.Attempts), Recommend(f)])),
        _ => throw new ArgumentException(L.T($"Unknown report \"{name}\"."), nameof(name)),
    };

    /// <summary>Any filtered view of the files table.</summary>
    public static IEnumerable<IReadOnlyList<string?>> FileRows(IEnumerable<FileRow> files) =>
        Prepend(["id", "library", "path", "format", "size", "modified", "modified_by", "status", "error_class", "error", "attempts", "comment"],
            files.Select(f => (IReadOnlyList<string?>)[N(f.Id), N(f.TargetIndex + 1), f.Path ?? f.Name, Path.GetExtension(f.Name).TrimStart('.'), N(f.Size),
                f.Modified?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), f.ModifiedBy, f.StatusCode ?? f.Status,
                f.ErrorClass, f.Error, N(f.Attempts), f.Comment]));

    public static void WriteCsv(Stream stream, IEnumerable<IReadOnlyList<string?>> rows)
    {
        using var w = new StreamWriter(stream, new UTF8Encoding(true), leaveOpen: true);
        foreach (var row in rows)
            Row(w, row);
    }

    private static IEnumerable<IReadOnlyList<string?>> Prepend(string?[] header, IEnumerable<IReadOnlyList<string?>> rows) =>
        rows.Prepend(header);

    public static bool NeedsAttention(FileRow f) => (f.StatusCode ?? f.Status).StartsWith("failed:", StringComparison.Ordinal);

    /// <summary>What to do with the file next (P-8).</summary>
    public static string Recommend(FileRow f) => f.ErrorClass switch
    {
        "transient" => L.T("retry: a temporary SharePoint or network error"),
        "locked" => L.T("retry later: the file is open or checked out; ask the owner to close it"),
        "auth" => L.T("check the app's permissions (Sites.Selected) on the site, then retry"),
        "internal" => L.T("tool error: give the log to the developer, then retry"),
        _ => string.Empty,
    };

    /// <summary>REP-4.</summary>
    public static string Summary(StateStore store, long runId)
    {
        var run = store.GetRun(runId) ?? throw new ArgumentException(L.T($"Run {runId} not found."));
        var counts = store.CountByStatus(runId);
        var (files, bytes) = store.Totals(runId);
        var duration = (run.FinishedAt ?? DateTimeOffset.UtcNow) - (run.StartedAt ?? run.CreatedAt);
        var sb = new StringBuilder();
        string stop = run.StopReason is null ? string.Empty : " — " + run.StopReason;
        sb.AppendLine(L.T($"Run {run.Id} ({run.Mode}), profile {run.ProfileName}"));
        sb.AppendLine(L.T($"Status: {run.Status}{stop}"));
        sb.AppendLine(L.T($"Duration: {duration:d\\.hh\\:mm\\:ss}"));
        sb.AppendLine(L.T($"Files: {files}, size {bytes / 1024.0 / 1024.0 / 1024.0:0.00} GB"));
        if (duration.TotalMinutes > 0)
            sb.AppendLine(L.T($"Speed: {files / Math.Max(duration.TotalMinutes, 1.0 / 60):0.0} files/min"));
        sb.AppendLine(L.T("By library:"));
        var names = LibraryNames(run);
        // Every library of the run, empty target libraries too.
        var byIndex = store.BreakdownBy(runId, "target_idx").ToDictionary(b => int.Parse(b.Key, CultureInfo.InvariantCulture));
        foreach (int i in Enumerable.Range(0, names.Count).Union(byIndex.Keys).Order())
        {
            string name = i < names.Count ? names[i] : (i + 1).ToString(CultureInfo.InvariantCulture);
            var (total, size) = byIndex.TryGetValue(i, out var b) ? (b.Total, b.Bytes) : (0, 0);
            string source = i == 0 ? L.T("  (source)") : string.Empty;
            sb.AppendLine(L.T($"  {name,-30} {total,12} files {size / 1024.0 / 1024.0 / 1024.0,10:0.00} GB{source}"));
        }
        var folders = store.GetFolderStats(runId, 0, maxDepth: 1);
        if (folders.Count > 1)
        {
            sb.AppendLine(L.T("Source, top-level folders:"));
            foreach (var s in folders.Where(s => s.Depth == 1).OrderByDescending(s => s.Bytes).Take(30))
                sb.AppendLine(L.T($"  {s.Path,-40} {s.Files,12} files {s.Bytes / 1024.0 / 1024.0 / 1024.0,10:0.00} GB, longest path {s.MaxPath}"));
        }
        sb.AppendLine(L.T($"Checked out or without a published version: {store.CountCheckouts(runId)}"));
        sb.AppendLine(L.T("By status:"));
        foreach (var (status, n) in counts.OrderBy(x => x.Key, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {status,-28} {n}");
        long attention = store.CountAttention(runId);
        sb.AppendLine(L.T($"Need attention: {attention}"));
        foreach (var note in store.GetNotes(runId))
            sb.AppendLine(L.T($"Note: {note.Message}"));
        return sb.ToString();
    }

    /// <summary>Library names of a run in the order of its target indexes (source first).</summary>
    public static IReadOnlyList<string> LibraryNames(RunRow run)
    {
        try
        {
            return RunConfig.Parse(run.ConfigSnapshot).Profile.Libraries().Select(t => t.Library ?? string.Empty).ToList();
        }
        catch (Core.Config.ProfileException)
        {
            return [];
        }
    }

    private static string N(long n) => n.ToString(CultureInfo.InvariantCulture);

    private static void Row(StreamWriter w, IReadOnlyList<string?> values)
    {
        w.Write(string.Join(',', values.Select(v => CsvReport.Escape(v, ','))));
        w.Write("\r\n");
    }
}
