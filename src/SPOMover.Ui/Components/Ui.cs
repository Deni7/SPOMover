using System.Globalization;
using MudBlazor;
using SPOMover.Core.Localization;
using SPOMover.State;

namespace SPOMover.Ui.Components;

/// <summary>Labels and colors shared by the pages.</summary>
public static class Ui
{
    /// <summary>Numbers and dates in the format of the UI language.</summary>
    public static CultureInfo Culture => L.Culture;

    public static string N(long n) => n.ToString("N0", Culture);

    public static string Time(DateTimeOffset? t) => t is { } v
        ? v.ToLocalTime().ToString(L.Language == L.Ukrainian ? "dd.MM.yyyy HH:mm:ss" : "yyyy-MM-dd HH:mm:ss", Culture)
        : "—";

    public static string Duration(TimeSpan? t) => t is not { } d ? "—"
        : d.TotalDays >= 1 ? L.T($"{(int)d.TotalDays} d {d:hh\\:mm}")
        : d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", Culture)
        : d.ToString(@"m\:ss", Culture);

    public static string Size(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => L.T($"{bytes / 1024.0 / 1024 / 1024:0.0} GB"),
        >= 1024 * 1024 => L.T($"{bytes / 1024.0 / 1024:0.0} MB"),
        >= 1024 => L.T($"{bytes / 1024.0:0} KB"),
        _ => L.T($"{bytes} B"),
    };

    public static string Mode(string mode) => mode switch
    {
        RunModes.Inventory => L.T("inventory — listing the source"),
        RunModes.Apply => L.T("apply — moving"),
        RunModes.Cleanup => L.T("cleanup — empty source folders"),
        _ => mode,
    };

    public static string RunStatus(string s) => s switch
    {
        RunStatuses.Created => L.T("created"),
        RunStatuses.Running => L.T("running"),
        RunStatuses.Paused => L.T("paused"),
        RunStatuses.Stopped => L.T("stopped"),
        RunStatuses.Aborted => L.T("aborted"),
        RunStatuses.Completed => L.T("completed"),
        RunStatuses.CompletedWithErrors => L.T("completed with errors"),
        _ => s,
    };

    public static Color RunColor(string s) => s switch
    {
        RunStatuses.Running => Color.Info,
        RunStatuses.Paused => Color.Warning,
        RunStatuses.Completed => Color.Success,
        RunStatuses.CompletedWithErrors or RunStatuses.Aborted => Color.Error,
        _ => Color.Default,
    };

    public static Color FileColor(string? code) => code switch
    {
        null => Color.Default,
        _ when code.StartsWith("failed:", StringComparison.Ordinal) => Color.Error,
        FileStatuses.Listed or FileStatuses.Moved or FileStatuses.Verified or FileStatuses.Deleted => Color.Success,
        FileStatuses.Deferred or FileStatuses.Pending or FileStatuses.InProgress => Color.Secondary,
        _ => Color.Default,
    };

    public static string ErrorClass(string? c) => c switch
    {
        "transient" => L.T("transient"),
        "conflict" => L.T("conflict"),
        "locked" => L.T("locked"),
        "auth" => L.T("access"),
        "file" => L.T("file"),
        "internal" => L.T("internal"),
        "job" => L.T("unknown result"),
        "verify" => L.T("verification"),
        "notfound" => L.T("not in source"),
        null or "" => "—",
        _ => c,
    };

    /// <summary>Status filter values; "attention" is not a status but all files the "Need attention" card counts.</summary>
    public static readonly string[] FileStatusFilters =
    [
        FileStatuses.Attention, "listed", "moved", "verified", "deleted", "waiting_path", "pending", "in_progress", "deferred", "ignored", "skipped", "skipped:notempty", "failed",
        "failed:transient", "failed:conflict", "failed:locked", "failed:auth", "failed:file", "failed:internal", "failed:job", "failed:verify",
    ];

    public static string FileStatusFilter(string value) =>
        value == FileStatuses.Attention ? L.T("attention — need attention") : value;

    public static readonly string[] ErrorClasses = ["transient", "conflict", "locked", "auth", "file", "internal", "job", "verify"];

    /// <summary>A number as the user types it back (invariant, no thousands separator).</summary>
    public static string Number(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);

    public static string BatchStatus(string s) => s switch
    {
        StateStore.BatchStatuses.Pending => L.T("queued"),
        StateStore.BatchStatuses.InProgress => L.T("running"),
        StateStore.BatchStatuses.Deferred => L.T("deferred"),
        StateStore.BatchStatuses.Done => L.T("done"),
        StateStore.BatchStatuses.Partial => L.T("partial"),
        _ when s.StartsWith("failed:", StringComparison.Ordinal) => L.T($"error: {ErrorClass(s[7..])}"),
        _ => s,
    };

    public static Color BatchColor(string s) => s switch
    {
        StateStore.BatchStatuses.Done => Color.Success,
        StateStore.BatchStatuses.InProgress => Color.Info,
        StateStore.BatchStatuses.Deferred or StateStore.BatchStatuses.Partial => Color.Warning,
        _ when s.StartsWith("failed:", StringComparison.Ordinal) => Color.Error,
        _ => Color.Default,
    };

    /// <summary>A library-relative path for display: the root is "/".</summary>
    public static string Path(string? p) => string.IsNullOrEmpty(p) ? "/" : p;
}
