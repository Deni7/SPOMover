using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SPOMover.Core;
using SPOMover.Spo.Auth;
using SPOMover.Spo.Throttling;
using SPOMover.Core.Localization;

namespace SPOMover.Spo.Graph;

public sealed class GraphClientOptions
{
    public string BaseUrl { get; init; } = "https://graph.microsoft.com/v1.0";

    /// <summary>RSL-6: identifies the traffic as a known internal app.</summary>
    public string UserAgent { get; init; } = "NONISV|SPOMover|SPOMover/" + (typeof(GraphClient).Assembly.GetName().Version?.ToString(2) ?? "1.0");

    /// <summary>Level 1 of FEATURES 9.2.</summary>
    public int RequestAttempts { get; init; } = 6;

    /// <summary>Time to get response headers; transfers of file content are bounded by the caller's token.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(120);

    public int ChunkSize { get; init; } = 10 * 1024 * 1024; // multiple of 320 KiB

    /// <summary>Upper bound of the exponential pause between retries.</summary>
    public TimeSpan MaxBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>First pause of the exponential backoff (tests use milliseconds).</summary>
    public TimeSpan BaseBackoff { get; init; } = TimeSpan.FromSeconds(1);
}

/// <summary>
/// Graph client of one app. Every request goes through the app budget, the tenant budget and the app throttle
/// state; failures are retried here (level 1) and surface as <see cref="SpoException"/> with a class (RTY-1).
/// </summary>
public sealed class GraphClient
{
    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly RuBudget _appBudget;
    private readonly RuBudget? _tenantBudget;
    private readonly ThrottleState _throttle;
    private readonly GraphClientOptions _options;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly Func<string, ITokenProvider>? _sharePointTokens;

    /// <param name="sharePointTokens">Tokens for SharePoint REST by tenant host; null — REST is not available.</param>
    public GraphClient(string app, HttpClient http, ITokenProvider tokens, RuBudget appBudget, RuBudget? tenantBudget,
        ThrottleState throttle, GraphClientOptions options, ILogger logger, TimeProvider? time = null, Func<string, ITokenProvider>? sharePointTokens = null)
    {
        _sharePointTokens = sharePointTokens;
        App = app;
        _http = http;
        _tokens = tokens;
        _appBudget = appBudget;
        _tenantBudget = tenantBudget;
        _throttle = throttle;
        _options = options;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public string App { get; }

    // ---- operations ----

    /// <summary>Only gets a token: proves the app registration and the certificate work.</summary>
    public Task CheckTokenAsync(CancellationToken ct) => _tokens.GetTokenAsync(forceRefresh: false, ct);

    // ---- SharePoint REST (INV-3) ----

    /// <summary>GUID of the SharePoint list behind a drive: GET /drives/{id}/list.</summary>
    public Task<string> GetListIdAsync(string driveId, CancellationToken ct) =>
        SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/drives/{driveId}/list?$select=id"),
            RequestCost.Get, e => e.GetProperty("id").GetString()!, "list", ct);

    /// <summary>Items (files and folders) of a list as SharePoint counts them: the total for the listing progress.</summary>
    public Task<long> GetListItemCountAsync(string siteUrl, string listId, CancellationToken ct) =>
        SendJsonAsync(() => Rest(HttpMethod.Get, $"{siteUrl.TrimEnd('/')}/_api/web/lists(guid'{listId}')?$select=ItemCount"),
            RequestCost.Get, e => e.GetProperty("ItemCount").GetInt64(), "itemcount", ct, tokens: SharePointTokens(siteUrl));

    /// <summary>Columns, content types and the settings of a library that matter for files moved into it (three REST requests).</summary>
    public async Task<ListSchema> GetListSchemaAsync(string siteUrl, string listId, CancellationToken ct)
    {
        string list = $"{siteUrl.TrimEnd('/')}/_api/web/lists(guid'{listId}')";
        var tokens = SharePointTokens(siteUrl);
        static bool B(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
        static string S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : string.Empty;
        var settings = await SendJsonAsync(() => Rest(HttpMethod.Get, $"{list}?$select=Title,EnableVersioning,EnableMinorVersions,ForceCheckout,EnableModeration"),
            RequestCost.Get, e => (Title: S(e, "Title"), Versioning: B(e, "EnableVersioning"), Minor: B(e, "EnableMinorVersions"), Checkout: B(e, "ForceCheckout"), Moderation: B(e, "EnableModeration")),
            "list-settings", ct, tokens: tokens).ConfigureAwait(false);
        var fields = await SendJsonAsync(() => Rest(HttpMethod.Get, $"{list}/fields?$select=InternalName,Title,TypeAsString,Required,Hidden,ReadOnlyField,FromBaseType&$top=5000"),
            RequestCost.List, e => (IReadOnlyList<ListField>)e.GetProperty("value").EnumerateArray()
                .Select(f => new ListField(S(f, "InternalName"), S(f, "Title"), S(f, "TypeAsString"), B(f, "Required"), B(f, "Hidden"), B(f, "ReadOnlyField"), B(f, "FromBaseType")))
                .ToList(), "list-fields", ct, tokens: tokens).ConfigureAwait(false);
        var types = await SendJsonAsync(() => Rest(HttpMethod.Get, $"{list}/contenttypes?$select=Name,Hidden"),
            RequestCost.List, e => (IReadOnlyList<string>)e.GetProperty("value").EnumerateArray().Where(t => !B(t, "Hidden")).Select(t => S(t, "Name")).ToList(),
            "list-contenttypes", ct, tokens: tokens).ConfigureAwait(false);
        return new ListSchema(settings.Title, settings.Versioning, settings.Minor, settings.Checkout, settings.Moderation, fields, types);
    }

    /// <summary>EXE-7: deletes a folder by library-relative path (into the site recycle bin); false if it was not there.</summary>
    public async Task<bool> DeleteItemByPathAsync(string driveId, string path, CancellationToken ct)
    {
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, $"{_options.BaseUrl}/drives/{driveId}/root:/{EscapePath(path.Trim('/'))}"),
                RequestCost.Write, authenticated: true, _ => Task.FromResult(true), "delete", ct, changes: true).ConfigureAwait(false);
            return true;
        }
        catch (SpoException ex) when (ex.IsNotFound)
        {
            return false;
        }
    }

    /// <summary>
    /// INV-3: files without any published version (checked out since upload); the listing does not return them.
    /// With the write role SharePoint refuses (403); it needs fullcontrol (API_SPIKE.md, result 18).
    /// </summary>
    public Task<IReadOnlyList<CheckedOutItem>> GetUnpublishedFilesAsync(string siteUrl, string listId, CancellationToken ct) =>
        SendJsonAsync(() => Rest(HttpMethod.Post, $"{siteUrl.TrimEnd('/')}/_api/web/lists(guid'{listId}')/GetCheckedOutFiles()?$expand=CheckedOutBy"),
            RequestCost.List, e => (IReadOnlyList<CheckedOutItem>)e.GetProperty("value").EnumerateArray().Select(f =>
            {
                string path = f.TryGetProperty("ServerRelativePath", out var p) && p.ValueKind == JsonValueKind.Object && p.TryGetProperty("DecodedUrl", out var d)
                    ? d.GetString()!
                    : f.GetProperty("ServerRelativeUrl").GetString()!;
                return new CheckedOutItem(path, f.TryGetProperty("CheckedOutBy", out var by) && by.ValueKind == JsonValueKind.Object ? UserName(by) : null);
            }).ToList(), "unpublished", ct, tokens: SharePointTokens(siteUrl));

    // ---- moving (EXE-1, EXE-3) ----

    /// <summary>
    /// EXE-1: SP.MoveCopyUtil.MoveFolderByPath — moves a folder with its subtree to a path that must not exist yet,
    /// inside one site collection, synchronously. Created/Modified and Author/Editor are kept
    /// (RetainEditorAndModifiedOnMove, API_SPIKE.md results 28, 35). Not retried after a timeout or a dropped connection:
    /// that throws <see cref="SpoUncertainException"/>.
    /// </summary>
    /// <param name="timeout">How long to wait for the answer: about 0.15 s per item of the subtree (result 25), plus a margin.</param>
    public Task MoveFolderAsync(string siteUrl, string sourceUrl, string targetUrl, bool bypassSharedLocks, TimeSpan timeout, CancellationToken ct) =>
        MoveAsync("MoveFolderByPath", siteUrl, sourceUrl, targetUrl, bypassSharedLocks, timeout, ct);

    /// <summary>EXE-1: SP.MoveCopyUtil.MoveFileByPath — a single file, to a path that must not exist (KeepBoth = false).</summary>
    public Task MoveFileAsync(string siteUrl, string sourceUrl, string targetUrl, bool bypassSharedLocks, CancellationToken ct) =>
        MoveAsync("MoveFileByPath", siteUrl, sourceUrl, targetUrl, bypassSharedLocks, _options.RequestTimeout, ct);

    private Task MoveAsync(string method, string siteUrl, string sourceUrl, string targetUrl, bool bypassSharedLocks, TimeSpan timeout, CancellationToken ct)
    {
        // The verbose form is what SharePoint accepted on the test tenant (API_SPIKE.md, second pass).
        string body = JsonSerializer.Serialize(new
        {
            srcPath = new Dictionary<string, object> { ["__metadata"] = new { type = "SP.ResourcePath" }, ["DecodedUrl"] = sourceUrl },
            destPath = new Dictionary<string, object> { ["__metadata"] = new { type = "SP.ResourcePath" }, ["DecodedUrl"] = targetUrl },
            options = new Dictionary<string, object>
            {
                ["__metadata"] = new { type = "SP.MoveCopyOptions" },
                ["KeepBoth"] = false,
                ["ShouldBypassSharedLocks"] = bypassSharedLocks,
                ["RetainEditorAndModifiedOnMove"] = true,
            },
        });
        return SendAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{siteUrl.TrimEnd('/')}/_api/SP.MoveCopyUtil.{method}()")
            {
                Content = new StringContent(body, Encoding.UTF8),
            };
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json;odata=verbose");
            request.Headers.Accept.ParseAdd("application/json;odata=nometadata");
            return request;
        }, RequestCost.Write, authenticated: true, _ => Task.FromResult(true), method, ct, timeout, SharePointTokens(siteUrl), changes: true);
    }

    /// <summary>EXE-3: creates a folder under <paramref name="parentPath"/> ("" — the root); true if it was already there.</summary>
    public async Task<bool> CreateFolderAsync(string driveId, string parentPath, string name, CancellationToken ct)
    {
        string url = parentPath.Trim('/').Length == 0
            ? $"{_options.BaseUrl}/drives/{driveId}/root/children"
            : $"{_options.BaseUrl}/drives/{driveId}/root:/{EscapePath(parentPath.Trim('/'))}:/children";
        string body = JsonSerializer.Serialize(new Dictionary<string, object> { ["name"] = name, ["folder"] = new { }, ["@microsoft.graph.conflictBehavior"] = "fail" });
        try
        {
            await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") },
                RequestCost.Write, authenticated: true, _ => Task.FromResult(true), "create-folder", ct).ConfigureAwait(false);
            return false;
        }
        catch (SpoException ex) when (ex.Status == HttpStatusCode.Conflict)
        {
            return true; // nameAlreadyExists
        }
    }

    /// <summary>An item by library-relative path, or null if there is none.</summary>
    public async Task<SpoItem?> FindItemByPathAsync(string driveId, string path, CancellationToken ct)
    {
        try
        {
            return await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get,
                    $"{_options.BaseUrl}/drives/{driveId}/root:/{EscapePath(path.Trim('/'))}?$select=id,name,size,folder,file,parentReference,publication"),
                RequestCost.Get, SpoItem.Parse, "find", ct).ConfigureAwait(false);
        }
        catch (SpoException ex) when (ex.IsNotFound)
        {
            return null;
        }
    }

    /// <summary>EXE-5: files and folders directly in a folder (library-relative path, "" — the root).</summary>
    public async Task<IReadOnlyList<SpoChild>> GetChildrenAsync(string driveId, string path, CancellationToken ct)
    {
        var result = new List<SpoChild>();
        string? url = path.Trim('/').Length == 0
            ? $"{_options.BaseUrl}/drives/{driveId}/root/children?$select=id,name,size,folder,file,publication&$top=999"
            : $"{_options.BaseUrl}/drives/{driveId}/root:/{EscapePath(path.Trim('/'))}:/children?$select=id,name,size,folder,file,publication&$top=999";
        while (url is not null)
        {
            string current = url;
            var (items, next) = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, current), RequestCost.List,
                e => (e.GetProperty("value").EnumerateArray().Select(SpoItem.Parse).ToList(),
                      e.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null),
                "children", ct).ConfigureAwait(false);
            result.AddRange(items.Select(i => new SpoChild(i.Id, i.Name, i.IsFolder, i.Size, i.CheckedOutBy)));
            url = next;
        }
        return result;
    }

    private ITokenProvider SharePointTokens(string siteUrl) =>
        _sharePointTokens?.Invoke(new Uri(siteUrl).Host) ?? throw new SpoException(ErrorClass.Auth, L.T("SharePoint REST is not set up for this app."));

    private static HttpRequestMessage Rest(HttpMethod method, string url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.ParseAdd("application/json;odata=nometadata");
        if (method == HttpMethod.Post)
            request.Content = new StringContent(string.Empty);
        return request;
    }

    private static string? UserName(JsonElement user) =>
        user.TryGetProperty("Title", out var t) && t.GetString() is { Length: > 0 } title ? title
        : user.TryGetProperty("EMail", out var m) ? m.GetString() : null;

    /// <summary>Site by URL: GET /sites/{host}:/{path}. Works with Sites.Selected, unlike search.</summary>
    public Task<SpoSite> GetSiteAsync(string siteUrl, CancellationToken ct)
    {
        var uri = new Uri(siteUrl.TrimEnd('/'));
        string path = uri.AbsolutePath.Trim('/');
        string url = path.Length == 0 ? $"{_options.BaseUrl}/sites/{uri.Host}" : $"{_options.BaseUrl}/sites/{uri.Host}:/{EscapePath(Uri.UnescapeDataString(path))}";
        return SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url + "?$select=id,webUrl"), RequestCost.Get,
            e => new SpoSite(e.GetProperty("id").GetString()!, e.GetProperty("webUrl").GetString()!), "site", ct);
    }

    public Task<IReadOnlyList<SpoDrive>> GetDrivesAsync(string siteId, CancellationToken ct) =>
        SendJsonAsync<IReadOnlyList<SpoDrive>>(() => new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/sites/{siteId}/drives?$select=id,name,webUrl"),
            RequestCost.List,
            e => e.GetProperty("value").EnumerateArray()
                .Select(d => new SpoDrive(d.GetProperty("id").GetString()!, d.GetProperty("name").GetString()!, d.GetProperty("webUrl").GetString()!))
                .ToList(),
            "drives", ct);

    /// <summary>GUI-17: site search. With Sites.Selected it may return nothing — entering the URL is the main way.</summary>
    public Task<IReadOnlyList<SpoSite>> SearchSitesAsync(string query, CancellationToken ct) =>
        SendJsonAsync<IReadOnlyList<SpoSite>>(() => new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/sites?search={Uri.EscapeDataString(query)}&$select=id,webUrl,displayName"),
            RequestCost.List,
            e => e.GetProperty("value").EnumerateArray().Select(s => new SpoSite(s.GetProperty("id").GetString()!, s.GetProperty("webUrl").GetString()!)).ToList(),
            "site-search", ct);

    /// <summary>GUI-17: subfolders of a folder (null — the library root) for the folder tree.</summary>
    public async Task<IReadOnlyList<SpoItem>> GetChildFoldersAsync(string driveId, string? itemId, CancellationToken ct)
    {
        var result = new List<SpoItem>();
        string? url = $"{_options.BaseUrl}/drives/{driveId}/{(itemId is null ? "root" : "items/" + itemId)}/children?$select=id,name,folder,parentReference&$top=999";
        while (url is not null)
        {
            string current = url;
            var (items, next) = await SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, current), RequestCost.List,
                e => (e.GetProperty("value").EnumerateArray().Select(SpoItem.Parse).ToList(),
                      e.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null),
                "children", ct).ConfigureAwait(false);
            result.AddRange(items.Where(i => i.IsFolder));
            url = next;
        }
        return result.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public Task<SpoItem> GetItemByPathAsync(string driveId, string path, CancellationToken ct) =>
        SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/drives/{driveId}/root:/{EscapePath(path.Trim('/'))}"),
            RequestCost.Get, SpoItem.Parse, "folder", ct);

    /// <summary>
    /// INV-1 for a folder of a library: a page of the children of one folder (files and subfolders) with the same fields
    /// as delta. Delta works only from the root of a SharePoint library, so a folder is walked folder by folder instead of
    /// listing the whole library and dropping what lies outside. <paramref name="link"/> is the saved nextLink; the
    /// result has no delta link.
    /// </summary>
    public Task<DeltaPage> GetChildrenPageAsync(string driveId, string itemId, string? link, CancellationToken ct)
    {
        string url = link ?? $"{_options.BaseUrl}/drives/{driveId}/items/{itemId}/children?$select=id,name,size,file,folder,parentReference,eTag,cTag,lastModifiedDateTime,lastModifiedBy,publication&$top=999";
        return SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), RequestCost.List,
            e => new DeltaPage(
                e.GetProperty("value").EnumerateArray().Select(SpoItem.Parse).ToList(),
                e.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null,
                null),
            "children", ct);
    }

    /// <summary>ENU-1: a page of root delta; <paramref name="link"/> is the saved nextLink to continue from.</summary>
    public Task<DeltaPage> GetDeltaPageAsync(string driveId, string? link, CancellationToken ct)
    {
        string url = link ?? $"{_options.BaseUrl}/drives/{driveId}/root/delta?$select=id,name,size,file,folder,root,deleted,parentReference,eTag,cTag,lastModifiedDateTime,lastModifiedBy,publication";
        return SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get, url), link is null ? RequestCost.List : RequestCost.DeltaWithToken,
            e => new DeltaPage(
                e.GetProperty("value").EnumerateArray().Select(SpoItem.Parse).ToList(),
                e.TryGetProperty("@odata.nextLink", out var n) ? n.GetString() : null,
                e.TryGetProperty("@odata.deltaLink", out var d) ? d.GetString() : null),
            "delta", ct);
    }

    /// <summary>Fresh metadata with the current version (UPL-4) and check-out state (UPL-6).</summary>
    public Task<SpoItem> GetItemAsync(string driveId, string itemId, CancellationToken ct) =>
        SendJsonAsync(() => new HttpRequestMessage(HttpMethod.Get,
                $"{_options.BaseUrl}/drives/{driveId}/items/{itemId}?$select=id,name,size,eTag,cTag,lastModifiedDateTime,lastModifiedBy,parentReference,file,folder,publication&$expand=listItem($select=id;$expand=fields($select=_UIVersionString,CheckoutUser))"),
            RequestCost.Get, SpoItem.Parse, "metadata", ct);

    /// <summary>Downloads file content to <paramref name="destination"/> (follows the redirect to the download URL).</summary>
    public Task DownloadAsync(string driveId, string itemId, string destination, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/drives/{driveId}/items/{itemId}/content"),
            RequestCost.Download, authenticated: true, async response =>
            {
                await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                return true;
            }, "download", ct);

    /// <summary>GUI-6: content of an earlier version (e.g. "3.0" — the one before the tool wrote the file).</summary>
    public Task DownloadVersionAsync(string driveId, string itemId, string versionId, string destination, CancellationToken ct) =>
        SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"{_options.BaseUrl}/drives/{driveId}/items/{itemId}/versions/{Uri.EscapeDataString(versionId)}/content"),
            RequestCost.Download, authenticated: true, async response =>
            {
                await using var file = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                return true;
            }, "download-version", ct);

    /// <summary>
    /// UPL-2, UPL-3: replaces the content only if the item still has <paramref name="eTag"/>;
    /// otherwise 412 → <see cref="ErrorClass.Conflict"/>. Large files go through an upload session.
    /// </summary>
    public async Task<SpoItem> UploadAsync(string driveId, string itemId, string source, string eTag, long simpleMaxBytes, CancellationToken ct)
    {
        long size = new FileInfo(source).Length;
        if (size <= simpleMaxBytes)
        {
            return await SendJsonAsync(() =>
            {
                var request = new HttpRequestMessage(HttpMethod.Put, $"{_options.BaseUrl}/drives/{driveId}/items/{itemId}/content")
                {
                    Content = new StreamContent(File.OpenRead(source)),
                };
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                request.Headers.TryAddWithoutValidation("If-Match", eTag);
                return request;
            }, RequestCost.Upload, SpoItem.Parse, "upload", ct, TransferTimeout(size)).ConfigureAwait(false);
        }
        return await UploadInChunksAsync(driveId, itemId, source, size, eTag, ct).ConfigureAwait(false);
    }

    private async Task<SpoItem> UploadInChunksAsync(string driveId, string itemId, string source, long size, string eTag, CancellationToken ct)
    {
        string uploadUrl = await SendJsonAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/drives/{driveId}/items/{itemId}/createUploadSession")
            {
                Content = new StringContent("""{"item":{"@microsoft.graph.conflictBehavior":"replace"}}""", Encoding.UTF8, "application/json"),
            };
            request.Headers.TryAddWithoutValidation("If-Match", eTag);
            return request;
        }, RequestCost.Upload, e => e.GetProperty("uploadUrl").GetString()!, "upload-session", ct).ConfigureAwait(false);

        long offset = 0;
        int resumes = 0;
        while (true)
        {
            long start = offset;
            int length = (int)Math.Min(_options.ChunkSize, size - start);
            byte[] chunk = new byte[length];
            await using (var fs = File.OpenRead(source))
            {
                fs.Seek(start, SeekOrigin.Begin);
                await fs.ReadExactlyAsync(chunk, ct).ConfigureAwait(false);
            }

            // If-Match is only checked when the session is created: before the chunk that commits the file,
            // make sure nobody saved it meanwhile, otherwise their edit would be overwritten.
            if (start + length >= size)
            {
                var current = await GetItemAsync(driveId, itemId, ct).ConfigureAwait(false);
                if (current.ETag != eTag)
                {
                    try
                    {
                        await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, uploadUrl), RequestCost.Get, authenticated: false,
                            _ => Task.FromResult(true), "upload-cancel", ct).ConfigureAwait(false);
                    }
                    catch (SpoException)
                    {
                    }
                    throw new SpoException(ErrorClass.Conflict, L.T("The file was changed during a chunked upload; the session was cancelled."), HttpStatusCode.PreconditionFailed);
                }
            }

            (SpoItem? Item, long Next) step;
            try
            {
                step = await SendAsync(() =>
                {
                    var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = new ByteArrayContent(chunk) };
                    request.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, start + length - 1, size);
                    return request;
                }, RequestCost.UploadChunk, authenticated: false, async response =>
                {
                    using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
                    if (response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created)
                        return ((SpoItem?)SpoItem.Parse(doc.RootElement), size);
                    return ((SpoItem?)null, NextExpected(doc.RootElement, start + length));
                }, "upload-chunk", ct, TransferTimeout(length)).ConfigureAwait(false);
            }
            catch (SpoException ex) when (ex.ErrorClass == ErrorClass.Transient && resumes++ < 3)
            {
                // UPL-3: ask the session where to continue instead of starting over.
                _logger.LogWarning("Resuming upload session {Item} after an error: {Message}", itemId, ex.Message);
                offset = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, uploadUrl), RequestCost.Get, authenticated: false,
                    async response =>
                    {
                        using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
                        return NextExpected(doc.RootElement, start);
                    }, "upload-status", ct).ConfigureAwait(false);
                continue;
            }

            if (step.Item is not null)
                return step.Item;
            offset = step.Next;
        }
    }

    private static long NextExpected(JsonElement e, long fallback)
    {
        if (e.TryGetProperty("nextExpectedRanges", out var ranges) && ranges.GetArrayLength() > 0)
        {
            string first = ranges[0].GetString()!;
            return long.Parse(first.Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
        }
        return fallback;
    }

    // ---- transport ----

    private Task<T> SendJsonAsync<T>(Func<HttpRequestMessage> factory, int cost, Func<JsonElement, T> parse, string operation, CancellationToken ct,
        TimeSpan? timeout = null, ITokenProvider? tokens = null) =>
        SendAsync(factory, cost, authenticated: true, async response =>
        {
            using var doc = await ReadJsonAsync(response, ct).ConfigureAwait(false);
            return parse(doc.RootElement);
        }, operation, ct, timeout, tokens);

    /// <summary>The request body is sent within the timeout too: allow for a slow link (128 KB/s) on top of the base time.</summary>
    private TimeSpan TransferTimeout(long bytes) => _options.RequestTimeout + TimeSpan.FromSeconds(bytes / (128.0 * 1024));

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// RSL-1…RSL-3, THR-1, THR-3: one request with budget, throttle state, token refresh on 401,
    /// Retry-After on 429/503 for the whole app, exponential backoff with jitter for network errors and 5xx.
    /// </summary>
    /// <param name="changes">
    /// The request changes something and may have done so even without an answer: after a timeout, a dropped connection
    /// or a 5xx it is not repeated, <see cref="SpoUncertainException"/> is thrown instead. 429 and 503 are still retried:
    /// SharePoint did not take the request.
    /// </param>
    private async Task<T> SendAsync<T>(Func<HttpRequestMessage> factory, int cost, bool authenticated,
        Func<HttpResponseMessage, Task<T>> read, string operation, CancellationToken ct, TimeSpan? timeout = null, ITokenProvider? tokens = null, bool changes = false)
    {
        tokens ??= _tokens;
        var limit = timeout ?? _options.RequestTimeout;
        bool refreshed = false;
        SpoException? last = null;
        for (int attempt = 1; attempt <= _options.RequestAttempts; attempt++)
        {
            if (_throttle.IsBlocked)
                throw new SpoBlockedException(App, _throttle.Only503For);

            await _appBudget.WaitAsync(cost, ct).ConfigureAwait(false);
            if (_tenantBudget is not null)
                await _tenantBudget.WaitAsync(cost, ct).ConfigureAwait(false);
            await _throttle.EnterAsync(ct).ConfigureAwait(false);

            HttpResponseMessage? response = null;
            TimeSpan? delay = null;
            try
            {
                using var request = factory();
                request.Headers.UserAgent.Clear();
                request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
                if (authenticated)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await tokens.GetTokenAsync(forceRefresh: false, ct).ConfigureAwait(false));

                using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    deadline.CancelAfter(limit);
                    try
                    {
                        response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        throw new TimeoutException(L.T($"No response within {limit.TotalSeconds:0} s."));
                    }
                }

                int code = (int)response.StatusCode;
                if (code is >= 200 and < 300)
                {
                    _throttle.OnSuccess();
                    return await read(response).ConfigureAwait(false);
                }

                var (errorCode, message) = await ReadErrorAsync(response, ct).ConfigureAwait(false);
                string? requestId = RequestId(response);

                if (code == 401 && authenticated && !refreshed)
                {
                    _throttle.OnOtherResponse();
                    refreshed = true;
                    await tokens.GetTokenAsync(forceRefresh: true, ct).ConfigureAwait(false);
                    _logger.LogInformation("{App}: 401 on {Operation}, token refreshed forcibly", App, operation);
                    attempt--; // the refresh itself is not a failed attempt
                    continue;
                }

                if (code is 429 or 503)
                {
                    var retryAfter = RetryAfter(response) ?? Backoff(attempt, TimeSpan.FromSeconds(10));
                    _throttle.OnThrottled(retryAfter, code == 503);
                    _logger.LogWarning("{App}: {Code} on {Operation}, app paused for {Seconds:0} s", App, code, operation, retryAfter.TotalSeconds);
                    last = new SpoException(ErrorClass.Transient, $"{code} {errorCode}: {message}", response.StatusCode, errorCode, requestId);
                    continue; // the pause is applied by the throttle state for all requests
                }

                _throttle.OnOtherResponse();
                var errorClass = SpoException.Classify(response.StatusCode, errorCode);
                var ex = new SpoException(errorClass, $"{code} {errorCode}: {message}", response.StatusCode, errorCode, requestId);
                if (errorClass != ErrorClass.Transient)
                    throw ex;
                if (changes)
                    throw new SpoUncertainException($"{operation}: {code} {errorCode}: {message}", ex);
                last = ex;
                delay = Backoff(attempt, _options.BaseBackoff);
            }
            catch (Exception ex) when (changes && ex is HttpRequestException or TimeoutException or IOException)
            {
                throw new SpoUncertainException($"{operation}: {ex.Message}", ex);
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or IOException or JsonException)
            {
                // JsonException: a body cut off in transit.
                last = new SpoException(ErrorClass.Transient, $"{operation}: {ex.Message}", inner: ex);
                delay = Backoff(attempt, _options.BaseBackoff);
            }
            finally
            {
                response?.Dispose();
                _throttle.Exit();
            }

            if (delay is { } d && attempt < _options.RequestAttempts)
                await Task.Delay(d, _time, ct).ConfigureAwait(false);
        }
        throw last ?? new SpoException(ErrorClass.Transient, L.T($"{operation}: attempts exhausted."));
    }

    private TimeSpan Backoff(int attempt, TimeSpan first)
    {
        double ms = first.TotalMilliseconds * Math.Pow(2, attempt - 1);
        ms = Math.Min(ms, _options.MaxBackoff.TotalMilliseconds);
        return TimeSpan.FromMilliseconds(ms * (0.75 + (Random.Shared.NextDouble() * 0.5)));
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta)
            return delta;
        if (header?.Date is { } date)
        {
            var wait = date - _time.GetUtcNow();
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }
        return null;
    }

    private static string? RequestId(HttpResponseMessage response) =>
        response.Headers.TryGetValues("request-id", out var values) ? values.FirstOrDefault()
        : response.Headers.TryGetValues("SPRequestGuid", out var sp) ? sp.FirstOrDefault() : null;

    private static async Task<(string? Code, string Message)> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            return (null, response.ReasonPhrase ?? string.Empty);
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("odata.error", out var spError))
            {
                string? spCode = spError.TryGetProperty("code", out var sc) ? sc.GetString() : null;
                string spMessage = spError.TryGetProperty("message", out var sm) && sm.ValueKind == JsonValueKind.Object && sm.TryGetProperty("value", out var sv)
                    ? sv.GetString() ?? string.Empty
                    : string.Empty;
                return (spCode, spMessage);
            }
            if (doc.RootElement.TryGetProperty("error", out var error))
            {
                string? code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                if (error.TryGetProperty("innerError", out var inner) && inner.TryGetProperty("code", out var ic) && ic.GetString() is { Length: > 0 } innerCode)
                    code = innerCode;
                return (code, error.TryGetProperty("message", out var m) ? m.GetString() ?? string.Empty : string.Empty);
            }
        }
        catch (JsonException)
        {
        }
        return (null, body.Length > 300 ? body[..300] : body);
    }

    /// <summary>Escapes each segment of a path for a Graph URL.</summary>
    internal static string EscapePath(string path) =>
        string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}
