using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SPOMover.Core.Urls;
using SPOMover.Core.Localization;

namespace SPOMover.Core.Config;

/// <summary>CFG-2: a library of a site, or a folder in it.</summary>
public sealed class Target
{
    /// <summary>https://tenant.sharepoint.com/sites/x</summary>
    public string? SiteUrl { get; set; }

    /// <summary>Library name as shown in SharePoint ("Documents"), its URL name ("Shared Documents") or full URL.</summary>
    public string? Library { get; set; }

    /// <summary>Folder inside the library, e.g. "Fin/2024". Empty — the whole library.</summary>
    public string? Folder { get; set; }

    public override string ToString() =>
        $"{SiteUrl?.TrimEnd('/')}/{Library}{(string.IsNullOrEmpty(Folder) ? string.Empty : "/" + Folder.Trim('/'))}";

    /// <param name="name">Name of the setting in messages: "source".</param>
    internal IEnumerable<ValidationIssue> Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(SiteUrl) || LinkClassifier.Classify(SiteUrl) != LinkForm.Http)
            yield return new(IssueSeverity.Error, L.T($"{name}.siteUrl: a site address https://… is required"));
        if (string.IsNullOrWhiteSpace(Library))
            yield return new(IssueSeverity.Error, L.T($"{name}.library: no library set."));
    }
}

/// <summary>THR-9…THR-11.</summary>
public sealed class PoolOptions
{
    /// <summary>Several connections in one run. Off by default: Microsoft advises against several apps for one job.</summary>
    public bool Enabled { get; set; }

    /// <summary>THR-10: share of the tenant budget (RU per 5 min) the whole run may use.</summary>
    public int TenantCeilingPercent { get; set; } = 50;

    internal IEnumerable<ValidationIssue> Validate()
    {
        if (TenantCeilingPercent is < 1 or > 100)
            yield return new(IssueSeverity.Error, L.T("pool.tenantCeilingPercent: from 1 to 100."));
    }
}

/// <summary>CFG-5, THR-1, THR-5.</summary>
public sealed class PerformanceOptions
{
    /// <summary>Requests to SharePoint at the same time, per app.</summary>
    public int Concurrency { get; set; } = 8;

    public int RequestTimeoutSeconds { get; set; } = 120;

    /// <summary>THR-1: share of the app RU limit to work at.</summary>
    public int BudgetPercent { get; set; } = 80;

    /// <summary>THR-5: time windows with their own budget. Outside all windows — <see cref="BudgetPercent"/>, or a pause with <see cref="OnlyInWindows"/>.</summary>
    public List<ScheduleWindow> Windows { get; set; } = [];

    public bool OnlyInWindows { get; set; }

    /// <summary>Time zone of the windows (Windows or IANA id). Empty — the host's local time.</summary>
    public string? TimeZone { get; set; }

    public TimeZoneInfo ResolveTimeZone() =>
        string.IsNullOrWhiteSpace(TimeZone) ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(TimeZone);

    /// <summary>Budget percent for this moment, or null when the run must wait (outside windows with OnlyInWindows).</summary>
    public int? BudgetAt(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, ResolveTimeZone());
        var window = Windows.FirstOrDefault(w => w.Contains(local.DateTime));
        if (window is not null)
            return window.BudgetPercent;
        return OnlyInWindows && Windows.Count > 0 ? null : BudgetPercent;
    }

    internal IEnumerable<ValidationIssue> Validate()
    {
        if (Concurrency is < 1 or > 64)
            yield return new(IssueSeverity.Error, L.T("performance.concurrency: from 1 to 64."));
        if (RequestTimeoutSeconds < 5)
            yield return new(IssueSeverity.Error, L.T("performance.requestTimeoutSeconds: at least 5."));
        if (BudgetPercent is < 1 or > 100)
            yield return new(IssueSeverity.Error, L.T("performance.budgetPercent: from 1 to 100."));
        if (OnlyInWindows && Windows.Count == 0)
            yield return new(IssueSeverity.Error, L.T("performance.onlyInWindows needs at least one window."));
        string? zoneError = null;
        try
        {
            ResolveTimeZone();
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            zoneError = L.T($"performance.timeZone: unknown time zone «{TimeZone}».");
        }
        if (zoneError is not null)
            yield return new(IssueSeverity.Error, zoneError);
        for (int i = 0; i < Windows.Count; i++)
        {
            foreach (string error in Windows[i].Validate())
                yield return new(IssueSeverity.Error, $"performance.windows[{i + 1}]: {error}");
        }
    }
}

/// <summary>A weekly time window, e.g. Mon–Fri 20:00–07:00. A window may cross midnight.</summary>
public sealed class ScheduleWindow
{
    /// <summary>Mon, Tue, … Empty — every day. For a window crossing midnight the day is the day it starts.</summary>
    public List<string> Days { get; set; } = [];

    public string From { get; set; } = "00:00";

    public string To { get; set; } = "24:00";

    public int BudgetPercent { get; set; } = 80;

    public bool Contains(DateTime local)
    {
        var from = ParseTime(From);
        var to = ParseTime(To);
        var t = local.TimeOfDay;
        if (from <= to)
            return DayMatches(local.DayOfWeek) && t >= from && t < to;
        // Crosses midnight: the evening part belongs to this day, the morning part to the previous one.
        return (t >= from && DayMatches(local.DayOfWeek)) || (t < to && DayMatches(local.AddDays(-1).DayOfWeek));
    }

    private bool DayMatches(DayOfWeek day) =>
        Days.Count == 0 || Days.Any(d => string.Equals(d.Trim()[..Math.Min(3, d.Trim().Length)], day.ToString()[..3], StringComparison.OrdinalIgnoreCase));

    private static TimeSpan ParseTime(string s) =>
        s == "24:00" ? TimeSpan.FromDays(1) : TimeSpan.ParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture);

    internal IEnumerable<string> Validate()
    {
        foreach (string s in new[] { From, To })
        {
            if (s != "24:00" && !TimeSpan.TryParseExact(s, @"hh\:mm", CultureInfo.InvariantCulture, out _))
                yield return L.T($"time «{s}» must be in the HH:MM format.");
        }
        string[] known = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
        foreach (string d in Days.Where(d => d.Trim().Length < 3 || !known.Contains(d.Trim()[..3].ToLowerInvariant())))
            yield return L.T($"unknown day «{d}» (Mon, Tue, …).");
        if (BudgetPercent is < 1 or > 100)
            yield return L.T("budgetPercent: from 1 to 100.");
    }
}

/// <summary>Levels 1–3 of FEATURES 9.2 and the circuit breaker (RSL-5).</summary>
public sealed class RetryOptions
{
    /// <summary>Level 1: attempts of one HTTP request.</summary>
    public int RequestAttempts { get; set; } = 6;

    /// <summary>Level 2: attempts of one file in the run (transient, conflict).</summary>
    public int FileAttempts { get; set; } = 3;

    /// <summary>Pauses before the 2nd, 3rd… attempt of a file. Seconds allow short values for tests.</summary>
    public List<double> FilePausesMinutes { get; set; } = [1, 5, 15];

    /// <summary>Level 3: how often locked files are tried again.</summary>
    public double LockedWaveMinutes { get; set; } = 10;

    /// <summary>Level 3: how long a file may stay locked before failed:locked.</summary>
    public double LockedLimitMinutes { get; set; } = 120;

    /// <summary>RSL-5: the breaker looks at this many last finished files.</summary>
    public int BreakerWindow { get; set; } = 50;

    /// <summary>RSL-5: share of failures (transient, auth, internal) in the window that stops the run.</summary>
    public double BreakerFailureRatio { get; set; } = 0.5;

    /// <summary>Auth failures in a row that stop the run (mass 401/403).</summary>
    public int AuthFailuresToStop { get; set; } = 10;

    /// <summary>THR-7: only 503 responses for this long — the app is considered blocked and the run stops.</summary>
    public double BlockedMinutes { get; set; } = 15;

    internal IEnumerable<ValidationIssue> Validate()
    {
        if (RequestAttempts is < 1 or > 20 || FileAttempts is < 1 or > 20)
            yield return new(IssueSeverity.Error, L.T("retry: the number of attempts is from 1 to 20."));
        if (FilePausesMinutes.Count == 0 || FilePausesMinutes.Any(p => p < 0))
            yield return new(IssueSeverity.Error, L.T("retry.filePausesMinutes: at least one non-negative pause is required."));
        if (LockedWaveMinutes <= 0 || LockedLimitMinutes < 0 || BlockedMinutes <= 0)
            yield return new(IssueSeverity.Error, L.T("retry: invalid intervals for locked files or blocking."));
        if (BreakerWindow < 1 || BreakerFailureRatio is <= 0 or > 1 || AuthFailuresToStop < 1)
            yield return new(IssueSeverity.Error, L.T("retry: invalid circuit breaker settings."));
    }
}

/// <summary>THR-2: SharePoint Online limits by number of licenses in the tenant (Microsoft docs, 10.2026).</summary>
public enum LicenseTier
{
    UpTo1000,
    UpTo5000,
    UpTo15000,
    UpTo50000,
    Over50000,
}

public static class SpoLimits
{
    /// <summary>RU per minute and per day for one app; RU per 5 minutes for the whole tenant.</summary>
    public static (int AppPerMinute, int AppPerDay, int TenantPer5Min) For(LicenseTier tier) => tier switch
    {
        LicenseTier.UpTo1000 => (1_250, 1_200_000, 18_750),
        LicenseTier.UpTo5000 => (2_500, 2_400_000, 37_500),
        LicenseTier.UpTo15000 => (3_750, 3_600_000, 56_250),
        LicenseTier.UpTo50000 => (5_000, 4_800_000, 75_000),
        _ => (6_250, 6_000_000, 93_750),
    };
}

/// <summary>An app registration in Entra (GUI-26). No secrets: the certificate stays in the Windows store (CFG-6).</summary>
public sealed class Connection
{
    public string Name { get; set; } = string.Empty;

    public string? TenantId { get; set; }

    public string? ClientId { get; set; }

    /// <summary>certificate (app-only, SEC-2) or deviceCode (delegated, fallback).</summary>
    public string AuthMode { get; set; } = "certificate";

    public string? CertificateThumbprint { get; set; }

    /// <summary>CurrentUser or LocalMachine.</summary>
    public string CertificateStore { get; set; } = "CurrentUser";

    public LicenseTier Licenses { get; set; } = LicenseTier.UpTo1000;

    /// <summary>Manual correction of the app limit (THR-2).</summary>
    public int? AppPerMinuteOverride { get; set; }

    public int? AppPerDayOverride { get; set; }

    /// <summary>Manual correction of the tenant limit (RU per 5 minutes) when the tier does not match reality.</summary>
    public int? TenantPer5MinOverride { get; set; }

    public static Connection Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<Connection>(json, Profile.JsonOptions) ?? throw new ProfileException(L.T("The connection is empty."));
        }
        catch (JsonException ex)
        {
            throw new ProfileException(L.T($"The connection could not be parsed: {ex.Message}"), ex);
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Profile.JsonOptions);

    public (int PerMinute, int PerDay, int TenantPer5Min) Limits()
    {
        var l = SpoLimits.For(Licenses);
        return (AppPerMinuteOverride ?? l.AppPerMinute, AppPerDayOverride ?? l.AppPerDay, TenantPer5MinOverride ?? l.TenantPer5Min);
    }

    public IEnumerable<ValidationIssue> Validate()
    {
        string who = L.T($"Connection «{Name}»");
        if (string.IsNullOrWhiteSpace(Name))
            yield return new(IssueSeverity.Error, L.T("A connection without a name."));
        if (!Guid.TryParse(ClientId, out _))
            yield return new(IssueSeverity.Error, L.T($"{who}: clientId must be a GUID."));
        if (string.IsNullOrWhiteSpace(TenantId))
            yield return new(IssueSeverity.Error, L.T($"{who}: tenantId is not set."));
        if (AuthMode == "certificate")
        {
            if (string.IsNullOrWhiteSpace(CertificateThumbprint) || !Regex.IsMatch(CertificateThumbprint.Replace(" ", ""), "^[0-9A-Fa-f]{40}$"))
                yield return new(IssueSeverity.Error, L.T($"{who}: certificateThumbprint — 40 hexadecimal characters."));
            if (CertificateStore is not ("CurrentUser" or "LocalMachine"))
                yield return new(IssueSeverity.Error, L.T($"{who}: certificateStore — CurrentUser or LocalMachine."));
        }
        else if (AuthMode != "deviceCode")
        {
            yield return new(IssueSeverity.Error, L.T($"{who}: authMode — certificate or deviceCode."));
        }
        if (AppPerMinuteOverride is < 1 || AppPerDayOverride is < 1 || TenantPer5MinOverride is < 1)
            yield return new(IssueSeverity.Error, L.T($"{who}: limits must be positive."));
    }
}

/// <summary>PLN-2, PLN-3: limits of a batch and the speed the forecast uses.</summary>
public sealed class PlanOptions
{
    /// <summary>SharePoint refuses to move a folder whose subtree has more items (list view threshold, API_SPIKE.md result 26).</summary>
    public const int ThresholdItems = 5000;

    /// <summary>
    /// PLN-2: items (files and folders of the subtree) a folder may have to move as one batch. 1 000 is about 140 s
    /// per MoveFolderByPath call (API_SPIKE.md, result 25); the limit must stay below the 5 000 threshold.
    /// </summary>
    public int MaxBatchItems { get; set; } = 1000;

    /// <summary>PLN-3: batches moved at the same time; the forecast divides by it (EXE-6 limits the real run).</summary>
    public int Parallel { get; set; } = 4;

    /// <summary>
    /// PLN-3: files per second one batch moves. Measured on fs-test with real files (270–390 KB on average): 2.2 per
    /// MoveFolderByPath call; small test files gave 6.6 (API_SPIKE.md, result 25). Corrected after the pilot.
    /// </summary>
    public double FilesPerSecond { get; set; } = 2.2;

    public IEnumerable<ValidationIssue> Validate()
    {
        if (MaxBatchItems is < 1 or >= ThresholdItems)
            yield return new(IssueSeverity.Error, L.T($"plan.maxBatchItems: from 1 to {ThresholdItems - 1} (SharePoint does not move a folder with {ThresholdItems} or more items)."));
        if (Parallel is < 1 or > 64)
            yield return new(IssueSeverity.Error, L.T("plan.parallel: from 1 to 64."));
        if (FilesPerSecond is <= 0 or > 1000 || double.IsNaN(FilesPerSecond))
            yield return new(IssueSeverity.Error, L.T("plan.filesPerSecond: more than 0 and at most 1000."));
    }
}

/// <summary>EXE-1: how batches are moved.</summary>
public sealed class MoveOptions
{
    /// <summary>
    /// LCK-2: move files opened for co-authoring in Word/Excel for the web (ShouldBypassSharedLocks). Off by default:
    /// such files are deferred and tried again in waves. Not checked on a real tenant yet (API_SPIKE.md, result 12).
    /// </summary>
    public bool BypassSharedLocks { get; set; }

    /// <summary>EXE-1: seconds per item of a folder batch the tool waits for MoveFolderByPath (measured ≈ 0.15, result 25), on top of the request timeout.</summary>
    public double SecondsPerItem { get; set; } = 0.5;

    public IEnumerable<ValidationIssue> Validate()
    {
        if (SecondsPerItem is <= 0 or > 60 || double.IsNaN(SecondsPerItem))
            yield return new(IssueSeverity.Error, L.T("move.secondsPerItem: more than 0 and at most 60."));
    }
}
