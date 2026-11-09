using System.Net;
using SPOMover.Core;
using SPOMover.Core.Localization;

namespace SPOMover.Spo;

/// <summary>A Graph request that failed after all request-level retries (RTY-1: class, HTTP code, request-id).</summary>
public class SpoException(
    ErrorClass errorClass,
    string message,
    HttpStatusCode? status = null,
    string? code = null,
    string? requestId = null,
    Exception? inner = null) : Exception(message, inner)
{
    public ErrorClass ErrorClass { get; } = errorClass;

    public HttpStatusCode? Status { get; } = status;

    /// <summary>Graph error code, e.g. resourceLocked, itemNotFound.</summary>
    public string? Code { get; } = code;

    /// <summary>request-id from the response, for Microsoft support.</summary>
    public string? RequestId { get; } = requestId;

    public bool IsNotFound => Status == HttpStatusCode.NotFound || (Code?.Contains("-2147024894", StringComparison.Ordinal) ?? false)
        || (Code?.Contains("FileNotFoundException", StringComparison.Ordinal) ?? false);

    /// <summary>SharePoint refused to move a folder whose subtree is over the list view threshold (API_SPIKE.md, result 26).</summary>
    public bool IsThreshold => Code is not null && (Code.Contains("SPQueryThrottledException", StringComparison.Ordinal) || Code.Contains("-2147024860", StringComparison.Ordinal));

    /// <summary>MoveCopyUtil: the destination already exists (API_SPIKE.md, results 29 and 31).</summary>
    public bool IsDestinationExists => Code?.Contains("-2130575257", StringComparison.Ordinal) ?? false;

    /// <summary>
    /// RTY-1. SharePoint REST errors carry "number, exception type" as the code and are checked first: the list view
    /// threshold comes as 500 but is not transient, a lock comes as 400 or 423.
    /// </summary>
    public static ErrorClass Classify(HttpStatusCode status, string? code) => code switch
    {
        _ when code is null => ByStatus(status, code),
        _ when code.Contains("SPQueryThrottledException", StringComparison.Ordinal) || code.Contains("-2147024860", StringComparison.Ordinal) => ErrorClass.File,
        _ when code.Contains("-2130575257", StringComparison.Ordinal) => ErrorClass.Conflict,
        _ when code.Contains("LockException", StringComparison.Ordinal) || code.Contains("-2147018894", StringComparison.Ordinal)
            || code.Contains("CheckOutException", StringComparison.Ordinal) || code.Contains("CheckedOut", StringComparison.OrdinalIgnoreCase) => ErrorClass.Locked,
        _ when code.Contains("FileNotFoundException", StringComparison.Ordinal) || code.Contains("-2147024894", StringComparison.Ordinal) => ErrorClass.File,
        _ when code.Contains("PathTooLong", StringComparison.Ordinal) || code.Contains("-2147024690", StringComparison.Ordinal) => ErrorClass.File,
        _ => ByStatus(status, code),
    };

    private static ErrorClass ByStatus(HttpStatusCode status, string? code) => (int)status switch
    {
        401 or 403 => ErrorClass.Auth,
        409 or 412 => code is not null && code.Contains("lock", StringComparison.OrdinalIgnoreCase) ? ErrorClass.Locked : ErrorClass.Conflict,
        423 => ErrorClass.Locked,
        408 or 429 or >= 500 => ErrorClass.Transient,
        413 => ErrorClass.File,
        _ when code is not null && (code.Contains("lock", StringComparison.OrdinalIgnoreCase) || code.Contains("checkedOut", StringComparison.OrdinalIgnoreCase)) => ErrorClass.Locked,
        _ => ErrorClass.Internal,
    };
}

/// <summary>
/// A request that changes something (a move) failed in a way that leaves its outcome unknown: no response in time or
/// the connection dropped. It is not retried blindly: the caller looks at the source and the target first (class job).
/// </summary>
public sealed class SpoUncertainException(string message, Exception? inner = null) : SpoException(ErrorClass.Transient, message, inner: inner);

/// <summary>THR-7: the app gets only 503 for a long time — SharePoint has probably blocked it. The run must stop.</summary>
public sealed class SpoBlockedException(string app, TimeSpan duration)
    : SpoException(ErrorClass.Transient,
        L.T($"The app «{app}» may be blocked in SharePoint: only 503 for {duration.TotalMinutes:0} min. The run was stopped; further attempts only extend the block."))
{
    public string App { get; } = app;
}
