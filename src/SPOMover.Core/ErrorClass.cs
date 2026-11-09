namespace SPOMover.Core;

/// <summary>Error classes (FEATURES 9). Stage 4 splits Locked and Conflict into the classes of the move pipeline.</summary>
public enum ErrorClass
{
    /// <summary>Network, 5xx, 429 after all request retries, timeout. Retried in the run.</summary>
    Transient,
    /// <summary>412: the item changed meanwhile.</summary>
    Conflict,
    /// <summary>423, checked out, co-authoring lock. Deferred.</summary>
    Locked,
    /// <summary>401 after token refresh, 403.</summary>
    Auth,
    /// <summary>The request was refused for this item (400, 404, too large).</summary>
    File,
    /// <summary>Bug in the tool.</summary>
    Internal,
}
