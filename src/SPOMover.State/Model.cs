namespace SPOMover.State;

/// <summary>Modes of FEATURES 8. The plan is not a run (Planner); verify and retry come later in stage 4.</summary>
public static class RunModes
{
    public const string Inventory = "inventory";

    /// <summary>Moves the batches of a plan (EXE-1…EXE-8).</summary>
    public const string Apply = "apply";

    /// <summary>EXE-7: deletes the source folders an apply run emptied (after verify). Its rows are folders, not files.</summary>
    public const string Cleanup = "cleanup";

    public static readonly string[] All = [Inventory, Apply, Cleanup];

    public static bool Writes(string mode) => mode is Apply or Cleanup;
}

public static class RunStatuses
{
    public const string Created = "created";
    public const string Running = "running";
    public const string Paused = "paused";
    /// <summary>Stopped on request (STA-4); can be continued.</summary>
    public const string Stopped = "stopped";
    /// <summary>Stopped by the breaker, mass auth failures or SPO blocking; can be continued after the cause is fixed.</summary>
    public const string Aborted = "aborted";
    public const string Completed = "completed";
    public const string CompletedWithErrors = "completed_with_errors";

    public static bool IsFinished(string s) => s is Completed or CompletedWithErrors;

    public static bool CanContinue(string s) => s is Created or Stopped or Aborted or Running or Paused;
}

/// <summary>STA-2. Non-final: pending, in_progress, deferred, waiting_path. Everything else is final (RTY-7).</summary>
public static class FileStatuses
{
    public const string WaitingPath = "waiting_path";
    public const string Pending = "pending";
    public const string InProgress = "in_progress";
    public const string Deferred = "deferred";

    /// <summary>Inventory: the file is listed with its path, size and metadata.</summary>
    public const string Listed = "listed";

    /// <summary>Apply: the file is in the target library at its new path.</summary>
    public const string Moved = "moved";

    /// <summary>verify: the file is in the target at its new path and no longer in the source (a later inventory says so).</summary>
    public const string Verified = "verified";

    /// <summary>Apply: the file was gone from the source before the move and is not in the target either.</summary>
    public const string SkippedNotFound = "skipped:notfound";
    public const string Ignored = "ignored";
    public const string SkippedDeleted = "skipped:deleted";

    /// <summary>Cleanup: the empty folder went to the site recycle bin.</summary>
    public const string Deleted = "deleted";

    /// <summary>Cleanup: the folder still holds something (files that stayed, new files) and is kept.</summary>
    public const string SkippedNotEmpty = "skipped:notempty";

    /// <summary>Not a file status: a filter value for all files that need attention (<see cref="StateStore.AttentionSql"/>).</summary>
    public const string Attention = "attention";

    public static string Failed(string errorClass) => "failed:" + errorClass;

    public static readonly string[] NonFinal = [WaitingPath, Pending, InProgress, Deferred];

    public static bool IsFinal(string s) => !NonFinal.Contains(s);
}

/// <summary>A file row as the engine works with it.</summary>
public sealed record FileRow
{
    public long Id { get; init; }
    public long RunId { get; init; }
    public int TargetIndex { get; init; }
    public string DriveId { get; init; } = string.Empty;
    public string ItemId { get; init; } = string.Empty;
    public string? ParentId { get; init; }
    public string Name { get; init; } = string.Empty;
    /// <summary>Path relative to the library root, with '/'.</summary>
    public string? Path { get; init; }
    public long Size { get; init; }
    public string? ETag { get; init; }
    public DateTimeOffset? Modified { get; init; }
    public string? ModifiedBy { get; init; }
    public string Status { get; init; } = FileStatuses.Pending;
    public string? StatusCode { get; init; }
    public string? Stage { get; init; }
    public string? ErrorClass { get; init; }
    public string? Error { get; init; }
    public int Attempts { get; init; }
    public DateTimeOffset? LockedSince { get; init; }
    public string? App { get; init; }
    public string? Comment { get; init; }
    /// <summary>INV-3: who holds the file checked out, if anyone.</summary>
    public string? CheckedOutBy { get; init; }
}

public sealed record RunRow(
    long Id,
    long? ParentId,
    string Mode,
    string Status,
    string? ProfileName,
    string ConfigSnapshot,
    bool Enumerate,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? StopReason);

public sealed record TargetRow(
    long RunId,
    int Index,
    string? DriveId,
    string? DriveUrl,
    string? RootItemId,
    string? RootPath,
    string? NextLink,
    string? DeltaLink,
    bool Done,
    int Checkouts = CheckoutScan.Pending,
    bool Incremental = false);

/// <summary>
/// INV-3: state of the scan for files without a published version (REST) in a run. Checked-out files come with the
/// listing itself (Graph delta, publication).
/// </summary>
public static class CheckoutScan
{
    public const int Pending = 0;
    public const int Done = 1;
    /// <summary>Not possible (no SharePoint permission or no fullcontrol, error): the reason is in the run notes.</summary>
    public const int Skipped = 2;
}

public sealed record FolderRow(string DriveId, string ItemId, string? ParentId, string Name, bool IsRoot);

public sealed record AttemptRow(DateTimeOffset At, int Attempt, string Stage, string? ErrorClass, int? HttpStatus, string? RequestId, string Message, string? App);

/// <summary>Which files a page, an export or a group action takes (GUI-4).</summary>
public sealed class FileFilter
{
    public List<string> Statuses { get; set; } = [];
    public List<string> ErrorClasses { get; set; } = [];
    public List<string> Extensions { get; set; } = [];
    public string? PathMask { get; set; }
    public List<long> FileIds { get; set; } = [];
    public List<string> Paths { get; set; } = [];
    public int? TargetIndex { get; set; }
    /// <summary>Text search in path (GUI-5).</summary>
    public string? Search { get; set; }
}

/// <summary>What the engine writes when a file reaches a final status or goes back to the queue.</summary>
public sealed record FileOutcome
{
    public required string Status { get; init; }
    public string? StatusCode { get; init; }
    public string? ErrorClass { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset? NotBefore { get; init; }
    public DateTimeOffset? LockedSince { get; init; }
    public string? ETag { get; init; }
}
