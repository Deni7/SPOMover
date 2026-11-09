using System.Text.Json;

namespace SPOMover.Spo.Graph;

public sealed record SpoSite(string Id, string WebUrl);

public sealed record SpoDrive(string Id, string Name, string WebUrl);

/// <summary>Drive item with what the run needs (ENU-3).</summary>
public sealed record SpoItem(
    string Id,
    string Name,
    string? ParentId,
    bool IsFolder,
    bool IsFile,
    bool IsRoot,
    bool Deleted,
    long Size,
    string? ETag,
    string? CTag,
    DateTimeOffset? Modified,
    string? ModifiedBy,
    string? Version = null,
    string? CheckedOutBy = null)
{
    public static SpoItem Parse(JsonElement e)
    {
        string? Str(JsonElement el, string name) => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        string? parent = e.TryGetProperty("parentReference", out var p) ? Str(p, "id") : null;
        string? modifiedBy = e.TryGetProperty("lastModifiedBy", out var mb) && mb.TryGetProperty("user", out var u)
            ? Str(u, "email") ?? Str(u, "displayName")
            : null;
        DateTimeOffset? modified = Str(e, "lastModifiedDateTime") is { } m && DateTimeOffset.TryParse(m, out var dt) ? dt : null;

        string? version = null;
        string? checkedOut = null;
        if (e.TryGetProperty("publication", out var pub) && pub.ValueKind == JsonValueKind.Object)
        {
            version = Str(pub, "versionId");
            // Graph delta returns checkedOutBy with the level (API_SPIKE.md, result 16).
            if (Str(pub, "level") == "checkout")
            {
                checkedOut = pub.TryGetProperty("checkedOutBy", out var by) && by.TryGetProperty("user", out var who)
                    ? Str(who, "displayName") ?? Str(who, "email") ?? "unknown"
                    : "unknown";
            }
        }
        if (e.TryGetProperty("listItem", out var li) && li.TryGetProperty("fields", out var f))
        {
            version ??= Str(f, "_UIVersionString");
            if (f.TryGetProperty("CheckoutUser", out var cu) && cu.ValueKind != JsonValueKind.Null)
                checkedOut = cu.ValueKind == JsonValueKind.Object ? Str(cu, "LookupValue") ?? "unknown" : cu.ToString();
        }

        return new SpoItem(
            Str(e, "id") ?? string.Empty,
            Str(e, "name") ?? string.Empty,
            parent,
            e.TryGetProperty("folder", out _),
            e.TryGetProperty("file", out _),
            e.TryGetProperty("root", out _),
            e.TryGetProperty("deleted", out _),
            e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0,
            Str(e, "eTag"),
            Str(e, "cTag"),
            modified,
            modifiedBy,
            version,
            checkedOut);
    }
}

/// <summary>INV-3: a checked-out file: server-relative URL (decoded) and who holds it.</summary>
public sealed record CheckedOutItem(string ServerRelativeUrl, string? By);

/// <summary>A child of a folder: name, folder or file, size.</summary>
public sealed record SpoChild(string Id, string Name, bool IsFolder, long Size, string? CheckedOutBy);

/// <summary>A column of a SharePoint list, as REST /fields gives it.</summary>
public sealed record ListField(string InternalName, string Title, string Type, bool Required, bool Hidden, bool ReadOnly, bool FromBaseType);

/// <summary>
/// The settings of a library that decide what happens to files moved into it: columns, content types, versioning,
/// required check-out, content approval.
/// </summary>
public sealed record ListSchema(string Title, bool EnableVersioning, bool EnableMinorVersions, bool ForceCheckout, bool EnableModeration,
    IReadOnlyList<ListField> Fields, IReadOnlyList<string> ContentTypes);

public sealed record DeltaPage(IReadOnlyList<SpoItem> Items, string? NextLink, string? DeltaLink);

/// <summary>THR-1: cost of requests in resource units, from the Microsoft table.</summary>
public static class RequestCost
{
    public const int Get = 1;
    public const int DeltaWithToken = 1;
    public const int List = 2;
    public const int Download = 1;
    public const int Upload = 2;
    public const int UploadChunk = 1;
    /// <summary>A server-side move or folder creation: a write, priced like an upload.</summary>
    public const int Write = 2;
}
