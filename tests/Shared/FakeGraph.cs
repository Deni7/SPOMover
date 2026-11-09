using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SPOMover.Core.Config;
using SPOMover.Spo;
using SPOMover.Spo.Auth;
using SPOMover.Spo.Graph;
using SPOMover.Spo.Throttling;

namespace SPOMover.Tests.Shared;

/// <summary>
/// In-memory emulation of the Graph endpoints the tool uses, with eTags, versions, check-out, locks,
/// delta paging, upload sessions and fault injection. Requests are attributed to apps by their token.
/// </summary>
public sealed class FakeGraph : HttpMessageHandler
{
    public const string Base = "https://graph.test/v1.0";
    public const string Host = "contoso.sharepoint.com";
    private const string UploadBase = "https://upload.test/session/";

    private readonly object _sync = new();
    private readonly Dictionary<string, (string Id, string WebUrl)> _sites = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<FakeDrive>> _drivesBySite = new();
    private readonly Dictionary<string, FakeDrive> _drives = new();
    private readonly Dictionary<string, Session> _sessions = new();
    private readonly ConcurrentDictionary<string, int> _tokenGeneration = new();
    private int _nextId = 1;
    /// <summary>Change counter: every add, upload, rename, move and delete gets the next number (delta tokens).</summary>
    private int _seq;

    public int DeltaPageSize { get; set; } = 3;

    /// <summary>Delta returns files before their folders (allowed by Graph; tests path resolution).</summary>
    public bool FilesBeforeFolders { get; set; }

    /// <summary>Items per page of /children (Graph pages by $top; the fake by this number, "skip=N" in nextLink).</summary>
    public int ChildrenPageSize { get; set; } = int.MaxValue;

    /// <summary>Called for every request before normal handling; a non-null response is returned as is.</summary>
    public List<Func<FakeRequest, HttpResponseMessage?>> Faults { get; } = [];

    /// <summary>Called after a successful upload of an item's content (before the response is sent).</summary>
    public Action<FakeRequest, FakeItem>? OnUploaded { get; set; }

    /// <summary>Called after content was served.</summary>
    public Action<FakeRequest, FakeItem>? OnDownloaded { get; set; }

    public ConcurrentQueue<FakeRequest> Log { get; } = new();

    /// <summary>SharePoint REST answers (INV-3); false — 403, as without the SharePoint Sites.Selected permission.</summary>
    public bool SharePointRest { get; set; } = true;

    /// <summary>The app has fullcontrol on the site; with write only, GetCheckedOutFiles answers 403 (API_SPIKE.md, result 18).</summary>
    public bool FullControl { get; set; } = true;

    /// <summary>MoveFolderByPath refuses a folder with more items in its subtree (list view threshold, API_SPIKE.md result 26).</summary>
    public int ThresholdItems { get; set; } = 5000;

    /// <summary>
    /// How MoveFolderByPath treats checked-out files inside the folder: false — refuses the whole folder with a lock
    /// error (what the tenant does, API_SPIKE.md result 42); true — moves everything else, leaves the locked files in
    /// the source folder and answers 200 (a hypothetical answer, kept so the engine copes with it too).
    /// </summary>
    public bool FolderMoveLeavesLocked { get; set; }

    /// <summary>Paths deleted with DELETE /root:/path (EXE-7), in order.</summary>
    public List<string> Deletes { get; } = [];

    /// <summary>REST ItemCount of a list fails (the listing must go on without a percentage).</summary>
    public bool ItemCountFails { get; set; }

    /// <summary>The next N moves are done, but the answer is lost (504): the caller does not know the outcome.</summary>
    public int DropAnswerAfterMove { get; set; }

    /// <summary>MoveCopyUtil requests (method, source, target) in the order they came.</summary>
    public ConcurrentQueue<(string Method, string Source, string Target)> Moves { get; } = new();

    public sealed class FakeDrive
    {
        /// <summary>Files without a published version: not in the listing, only in GetCheckedOutFiles (path, checked out by).</summary>
        public List<(string Path, string By)> Unpublished { get; } = [];

        public required string Id { get; init; }
        public required string Name { get; init; }
        public required string WebUrl { get; init; }
        public Dictionary<string, FakeItem> Items { get; } = [];
        public required FakeItem Root { get; init; }

        /// <summary>Deleted items with the change number of the delete, for delta with a token.</summary>
        public List<(string Id, string? ParentId, bool IsFolder, int Seq)> Deleted { get; } = [];

        /// <summary>Columns users added (REST /fields shows them with the built-in Title): internal name, title, type, required.</summary>
        public List<(string Name, string Title, string Type, bool Required)> Columns { get; } = [];

        public List<string> ContentTypes { get; } = ["Документ", "Папка"];

        public bool ForceCheckout { get; set; }

        public bool Moderation { get; set; }

        public bool MinorVersions { get; set; }

        public bool Versioning { get; set; } = true;
    }

    public sealed class FakeItem
    {
        public required string Id { get; init; }
        public required string Name { get; set; }
        public string? ParentId { get; set; }
        public bool IsFolder { get; init; }
        public bool IsRoot { get; init; }
        public byte[] Content { get; set; } = [];
        public int Version { get; set; } = 1;
        public string ETag => $"\"{{{Id}}},{Version}\"";
        public string? CheckedOutBy { get; set; }
        /// <summary>Uploads answered with 423 before the lock goes away.</summary>
        public int LockedUploads { get; set; }
        public int Uploads { get; set; }
        /// <summary>Content of earlier versions by major version number.</summary>
        [System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
        public Dictionary<int, byte[]> History { get; } = [];
        public DateTimeOffset Modified { get; set; } = new(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        /// <summary>Change number of the last change (see delta tokens).</summary>
        public int Changed { get; set; }
    }

    public sealed record FakeRequest(string Method, string Path, string? App, string? ItemName, string? DriveId, string? ItemId);

    private sealed class Session
    {
        public required string DriveId { get; init; }
        public required string ItemId { get; init; }
        public MemoryStream Data { get; } = new();
        public long Total { get; set; }
    }

    // ---------------------------------------------------------------- setup

    public FakeDrive AddLibrary(string sitePath, string libraryName, string urlName)
    {
        lock (_sync)
        {
            if (!_sites.TryGetValue(sitePath, out var site))
            {
                site = ($"site-{_nextId++}", $"https://{Host}{sitePath}");
                _sites[sitePath] = site;
                _drivesBySite[site.Id] = [];
            }
            string driveId = $"drive-{_nextId++}";
            var root = new FakeItem { Id = $"root-{driveId}", Name = "root", IsFolder = true, IsRoot = true };
            var drive = new FakeDrive { Id = driveId, Name = libraryName, WebUrl = $"{site.WebUrl}/{Uri.EscapeDataString(urlName)}", Root = root };
            drive.Items[root.Id] = root;
            _drives[driveId] = drive;
            _drivesBySite[site.Id].Add(drive);
            return drive;
        }
    }

    // ---------------------------------------------------------------- persistence (for the demo)

    private sealed record SavedSite(string Path, string Id, string WebUrl, List<SavedDrive> Drives);
    private sealed record SavedDrive(string Id, string Name, string WebUrl, List<FakeItem> Items);
    private sealed record Saved(int NextId, int DeltaPageSize, List<SavedSite> Sites);

    /// <summary>Writes the sites, libraries and items (with content and versions) to a JSON file.</summary>
    public void Save(string path)
    {
        Saved saved;
        lock (_sync)
            saved = new Saved(_nextId, DeltaPageSize, [.. _sites.Select(s => new SavedSite(s.Key, s.Value.Id, s.Value.WebUrl,
                [.. _drivesBySite[s.Value.Id].Select(d => new SavedDrive(d.Id, d.Name, d.WebUrl, [.. d.Items.Values]))]))]);
        string temp = path + ".tmp";
        using (var stream = File.Create(temp))
            JsonSerializer.Serialize(stream, saved);
        File.Move(temp, path, overwrite: true);
    }

    public static FakeGraph Load(string path)
    {
        Saved saved;
        using (var stream = File.OpenRead(path))
            saved = JsonSerializer.Deserialize<Saved>(stream) ?? throw new InvalidDataException(path);
        var graph = new FakeGraph { _nextId = saved.NextId, DeltaPageSize = saved.DeltaPageSize };
        foreach (var site in saved.Sites)
        {
            graph._sites[site.Path] = (site.Id, site.WebUrl);
            graph._drivesBySite[site.Id] = [];
            foreach (var d in site.Drives)
            {
                var drive = new FakeDrive { Id = d.Id, Name = d.Name, WebUrl = d.WebUrl, Root = d.Items.Single(i => i.IsRoot) };
                foreach (var item in d.Items)
                    drive.Items[item.Id] = item;
                graph._drives[d.Id] = drive;
                graph._seq = Math.Max(graph._seq, d.Items.Max(i => i.Changed));
                graph._drivesBySite[site.Id].Add(drive);
            }
        }
        return graph;
    }

    /// <summary>Adds a file under a folder path like "Fin/2024", creating folders.</summary>
    public FakeItem AddFile(FakeDrive drive, string path, byte[] content)
    {
        lock (_sync)
        {
            var parts = path.Split('/');
            var parent = drive.Root;
            foreach (string folderName in parts[..^1])
            {
                var folder = drive.Items.Values.FirstOrDefault(i => i.IsFolder && i.ParentId == parent.Id && i.Name == folderName);
                if (folder is null)
                {
                    folder = new FakeItem { Id = $"f{_nextId++}", Name = folderName, ParentId = parent.Id, IsFolder = true, Changed = ++_seq };
                    drive.Items[folder.Id] = folder;
                }
                parent = folder;
            }
            var item = new FakeItem { Id = $"i{_nextId++}", Name = parts[^1], ParentId = parent.Id, Content = content, Changed = ++_seq };
            drive.Items[item.Id] = item;
            return item;
        }
    }

    /// <summary>
    /// Deletes an item; a folder takes its subtree with it. Delta with a token reports only the item itself, as
    /// SharePoint may: the children have to be dropped by whoever applies the changes.
    /// </summary>
    public void Remove(FakeDrive drive, FakeItem item)
    {
        lock (_sync)
        {
            var gone = new List<FakeItem> { item };
            for (int i = 0; i < gone.Count; i++)
                gone.AddRange(drive.Items.Values.Where(x => x.ParentId == gone[i].Id));
            foreach (var x in gone)
                drive.Items.Remove(x.Id);
            drive.Deleted.Add((item.Id, item.ParentId, item.IsFolder, ++_seq));
        }
    }

    /// <summary>Renames an item. For a folder, delta reports only the folder, not its children (as Graph does).</summary>
    public void Rename(FakeItem item, string name)
    {
        lock (_sync)
        {
            item.Name = name;
            item.Changed = ++_seq;
        }
    }

    /// <summary>Moves an item under another folder ("" — the root), creating that folder path if needed.</summary>
    public void Move(FakeDrive drive, FakeItem item, string folderPath)
    {
        lock (_sync)
        {
            var parent = drive.Root;
            foreach (string name in folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var folder = drive.Items.Values.FirstOrDefault(i => i.IsFolder && i.ParentId == parent.Id && i.Name == name);
                if (folder is null)
                {
                    folder = new FakeItem { Id = $"f{_nextId++}", Name = name, ParentId = parent.Id, IsFolder = true, Changed = ++_seq };
                    drive.Items[folder.Id] = folder;
                }
                parent = folder;
            }
            item.ParentId = parent.Id;
            item.Changed = ++_seq;
        }
    }

    /// <summary>The folder at a path, for tests that rename or move it.</summary>
    public FakeItem Folder(FakeDrive drive, string path)
    {
        lock (_sync)
        {
            var current = drive.Root;
            foreach (string name in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
                current = drive.Items.Values.Single(i => i.IsFolder && i.ParentId == current.Id && i.Name == name);
            return current;
        }
    }

    /// <summary>The next request with an older token of this app gets 401 (token expired).</summary>
    public void ExpireTokens(string app) => _tokenGeneration.AddOrUpdate(app, 1, (_, g) => g + 1);

    public int MinTokenGeneration(string app) => _tokenGeneration.GetValueOrDefault(app);

    // ---------------------------------------------------------------- handling

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Task.Yield();
        string url = request.RequestUri!.ToString();
        string? app = null;
        int generation = 0;
        if (request.Headers.Authorization?.Parameter is { } token)
        {
            var parts = token.Split('|');
            app = parts[0];
            generation = int.Parse(parts[1]);
        }
        else if (url.StartsWith(UploadBase, StringComparison.Ordinal))
        {
            app = request.Headers.TryGetValues("X-Test-App", out var v) ? v.First() : null;
        }

        var (driveId, itemId) = ParseIds(url);
        FakeItem? item = null;
        lock (_sync)
        {
            if (driveId is not null && itemId is not null && _drives.TryGetValue(driveId, out var d))
                d.Items.TryGetValue(itemId, out item);
        }
        var fake = new FakeRequest(request.Method.Method, url, app, item?.Name, driveId, itemId);
        Log.Enqueue(fake);

        foreach (var fault in Faults.ToArray())
        {
            if (fault(fake) is { } faulted)
                return faulted;
        }

        if (!url.StartsWith(UploadBase, StringComparison.Ordinal))
        {
            if (!request.Headers.UserAgent.ToString().StartsWith("NONISV|", StringComparison.Ordinal))
                return Error(HttpStatusCode.BadRequest, "badUserAgent");
            if (app is null)
                return Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken");
            if (generation < MinTokenGeneration(app))
                return Error(HttpStatusCode.Unauthorized, "InvalidAuthenticationToken");
        }

        try
        {
            return await Route(request, url, fake, ct);
        }
        catch (KeyNotFoundException)
        {
            return Error(HttpStatusCode.NotFound, "itemNotFound");
        }
    }

    private static (string? Drive, string? Item) ParseIds(string url)
    {
        var path = new Uri(url).AbsolutePath;
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string? drive = null, item = null;
        for (int i = 0; i < segs.Length - 1; i++)
        {
            if (segs[i] == "drives")
                drive = segs[i + 1];
            if (segs[i] == "items")
                item = segs[i + 1];
        }
        return (drive, item);
    }

    private async Task<HttpResponseMessage> Route(HttpRequestMessage request, string url, FakeRequest fake, CancellationToken ct)
    {
        var uri = new Uri(url);
        string path = Uri.UnescapeDataString(uri.AbsolutePath);
        string method = request.Method.Method;

        if (url.StartsWith(UploadBase, StringComparison.Ordinal))
            return await HandleSession(request, url[UploadBase.Length..], fake, ct);

        if (path.Contains("/_api/SP.MoveCopyUtil.", StringComparison.Ordinal))
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            lock (_sync)
            {
                var answer = MoveCopyUtil(path, body);
                if (answer.IsSuccessStatusCode && DropAnswerAfterMove > 0)
                {
                    DropAnswerAfterMove--;
                    return Error(HttpStatusCode.GatewayTimeout, "gatewayTimeout");
                }
                return answer;
            }
        }
        if (path.Contains("/_api/", StringComparison.Ordinal))
        {
            lock (_sync)
                return Rest(uri, path, method);
        }

        if (!path.StartsWith("/v1.0/", StringComparison.Ordinal))
            return Error(HttpStatusCode.NotFound, "badPath");
        path = path["/v1.0".Length..];

        lock (_sync)
        {
            if (path == "/sites" && uri.Query.Contains("search=", StringComparison.Ordinal))
            {
                string q = Uri.UnescapeDataString(uri.Query.Split("search=")[1].Split('&')[0]);
                var found = new JsonArray();
                foreach (var (sitePath, site) in _sites.Where(s => s.Key.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    found.Add(new JsonObject { ["id"] = site.Id, ["webUrl"] = site.WebUrl, ["displayName"] = sitePath });
                return Json(new JsonObject { ["value"] = found });
            }
            // /drives/{d}/root:/{path}:/children (GET, POST) and POST /drives/{d}/root/children
            if (path.EndsWith(":/children", StringComparison.Ordinal) || (method == "POST" && path.EndsWith("/root/children", StringComparison.Ordinal)))
            {
                var cs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var d = _drives[cs[1]];
                var parent = path.EndsWith(":/children", StringComparison.Ordinal)
                    ? FindByPath(d, path[(path.IndexOf("/root:/", StringComparison.Ordinal) + 7)..^":/children".Length]) ?? throw new KeyNotFoundException()
                    : d.Root;
                if (method == "GET")
                {
                    var list = new JsonArray();
                    foreach (var child in d.Items.Values.Where(i => i.ParentId == parent.Id))
                        list.Add(ItemJson(d, child));
                    return Json(new JsonObject { ["value"] = list });
                }
                var json = JsonNode.Parse(request.Content!.ReadAsStringAsync(ct).GetAwaiter().GetResult())!;
                string name = json["name"]!.GetValue<string>();
                if (d.Items.Values.Any(i => i.ParentId == parent.Id && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase)))
                    return Error(HttpStatusCode.Conflict, "nameAlreadyExists");
                var created = new FakeItem { Id = $"f{_nextId++}", Name = name, ParentId = parent.Id, IsFolder = true, Changed = ++_seq };
                d.Items[created.Id] = created;
                var done = Json(ItemJson(d, created));
                done.StatusCode = HttpStatusCode.Created;
                return done;
            }
            if (path.EndsWith("/children", StringComparison.Ordinal))
            {
                var cs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var d = _drives[cs[1]];
                string parentId = cs[2] == "root" ? d.Root.Id : cs[3];
                int skip = uri.Query.Contains("skip=", StringComparison.Ordinal)
                    ? int.Parse(uri.Query.Split("skip=")[1].Split('&')[0], System.Globalization.CultureInfo.InvariantCulture) : 0;
                var children = new JsonArray();
                List<FakeItem> all;
                lock (_sync)
                    all = d.Items.Values.Where(i => i.ParentId == parentId).ToList();
                foreach (var child in all.Skip(skip).Take(ChildrenPageSize))
                    children.Add(ItemJson(d, child));
                var page = new JsonObject { ["value"] = children };
                if (skip + ChildrenPageSize < all.Count)
                    page["@odata.nextLink"] = $"{uri.GetLeftPart(UriPartial.Path)}?skip={skip + ChildrenPageSize}";
                return Json(page);
            }
            // /sites/{host}:/{path}  or /sites/{host}
            if (path.StartsWith("/sites/", StringComparison.Ordinal) && !path.Contains("/drives", StringComparison.Ordinal))
            {
                string rest = path["/sites/".Length..];
                string sitePath = rest.Contains(":/") ? "/" + rest[(rest.IndexOf(":/", StringComparison.Ordinal) + 2)..].Trim('/') : "/";
                if (!_sites.TryGetValue(sitePath, out var site))
                    return Error(HttpStatusCode.NotFound, "itemNotFound");
                return Json(new JsonObject { ["id"] = site.Id, ["webUrl"] = site.WebUrl });
            }
            if (path.StartsWith("/sites/", StringComparison.Ordinal) && path.EndsWith("/drives", StringComparison.Ordinal))
            {
                string siteId = path.Split('/')[2];
                var arr = new JsonArray();
                foreach (var d in _drivesBySite[siteId])
                    arr.Add(new JsonObject { ["id"] = d.Id, ["name"] = d.Name, ["webUrl"] = d.WebUrl });
                return Json(new JsonObject { ["value"] = arr });
            }

            var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var drive = _drives[segs[1]];
            if (path.Contains("/root:/", StringComparison.Ordinal))
            {
                string itemPath = path[(path.IndexOf("/root:/", StringComparison.Ordinal) + 7)..].Trim('/');
                var found = FindByPath(drive, itemPath) ?? throw new KeyNotFoundException();
                if (method == "DELETE")
                {
                    // Into the recycle bin with everything under it.
                    var gone = new List<FakeItem> { found };
                    for (int i = 0; i < gone.Count; i++)
                        gone.AddRange(drive.Items.Values.Where(x => x.ParentId == gone[i].Id));
                    foreach (var x in gone)
                        drive.Items.Remove(x.Id);
                    drive.Deleted.Add((found.Id, found.ParentId, found.IsFolder, ++_seq));
                    Deletes.Add(itemPath);
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                }
                return Json(ItemJson(drive, found));
            }
            if (path.EndsWith("/root/delta", StringComparison.Ordinal))
                return Delta(drive, uri);
            if (segs.Length == 3 && segs[2] == "list")
                return Json(new JsonObject { ["id"] = "list-" + drive.Id });

            var item = drive.Items[segs[3]];
            if (segs.Length == 4 && method == "GET")
                return Json(ItemJson(drive, item, full: true));
            if (segs.Length == 7 && segs[4] == "versions" && segs[6] == "content" && method == "GET")
            {
                int v = int.Parse(segs[5].Split('.')[0], System.Globalization.CultureInfo.InvariantCulture);
                byte[] content = v == item.Version ? item.Content : item.History.TryGetValue(v, out var old) ? old : throw new KeyNotFoundException();
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content.ToArray()) };
            }
            if (segs.Length == 5 && segs[4] == "content" && method == "GET")
            {
                OnDownloaded?.Invoke(fake, item);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(item.Content.ToArray()) };
            }
            if (segs.Length == 5 && segs[4] == "content" && method == "PUT")
            {
                if (Precondition(request, item) is { } failed)
                    return failed;
                byte[] body = request.Content!.ReadAsByteArrayAsync(ct).GetAwaiter().GetResult();
                item.History[item.Version] = item.Content;
                item.Content = body;
                item.Version++;
                item.Uploads++;
                item.Modified = DateTimeOffset.UtcNow;
                item.Changed = Interlocked.Increment(ref _seq);
                OnUploaded?.Invoke(fake, item);
                return Json(ItemJson(drive, item));
            }
            if (segs.Length == 5 && segs[4] == "createUploadSession" && method == "POST")
            {
                if (Precondition(request, item) is { } failed)
                    return failed;
                string id = Guid.NewGuid().ToString("N");
                _sessions[id] = new Session { DriveId = drive.Id, ItemId = item.Id };
                return Json(new JsonObject { ["uploadUrl"] = UploadBase + id });
            }
        }
        return Error(HttpStatusCode.BadRequest, "notImplemented");
    }

    private HttpResponseMessage? Precondition(HttpRequestMessage request, FakeItem item)
    {
        if (item.LockedUploads > 0)
        {
            item.LockedUploads--;
            return Error((HttpStatusCode)423, "resourceLocked");
        }
        if (item.CheckedOutBy is not null)
            return Error((HttpStatusCode)423, "resourceLocked");
        string? ifMatch = request.Headers.TryGetValues("If-Match", out var v) ? v.First() : null;
        if (ifMatch is null)
            return Error(HttpStatusCode.BadRequest, "ifMatchRequiredInTest");
        if (ifMatch != item.ETag)
            return Error(HttpStatusCode.PreconditionFailed, "resourceModified");
        return null;
    }

    private async Task<HttpResponseMessage> HandleSession(HttpRequestMessage request, string id, FakeRequest fake, CancellationToken ct)
    {
        Session session;
        lock (_sync)
        {
            if (!_sessions.TryGetValue(id, out session!))
                return Error(HttpStatusCode.NotFound, "itemNotFound");
        }
        if (request.Method == HttpMethod.Get)
            return Json(new JsonObject { ["nextExpectedRanges"] = new JsonArray($"{session.Data.Length}-") });
        if (request.Method == HttpMethod.Delete)
        {
            lock (_sync)
                _sessions.Remove(id);
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }

        var range = request.Content!.Headers.ContentRange!;
        byte[] body = await request.Content.ReadAsByteArrayAsync(ct);
        lock (_sync)
        {
            if (range.From != session.Data.Length)
                return Error((HttpStatusCode)416, "invalidRange");
            session.Data.Write(body);
            session.Total = range.Length!.Value;
            if (session.Data.Length < session.Total)
            {
                var accepted = new HttpResponseMessage(HttpStatusCode.Accepted)
                {
                    Content = new StringContent(new JsonObject { ["nextExpectedRanges"] = new JsonArray($"{session.Data.Length}-") }.ToJsonString(), Encoding.UTF8, "application/json"),
                };
                return accepted;
            }
            var drive = _drives[session.DriveId];
            var item = drive.Items[session.ItemId];
            item.History[item.Version] = item.Content;
            item.Content = session.Data.ToArray();
            item.Version++;
            item.Uploads++;
            item.Changed = ++_seq;
            _sessions.Remove(id);
            OnUploaded?.Invoke(fake, item);
            var done = Json(ItemJson(drive, item));
            done.StatusCode = HttpStatusCode.Created;
            return done;
        }
    }

    /// <summary>
    /// Graph delta. Without a token: the whole drive, paged ("token=N" — offset). The final deltaLink carries the
    /// change number ("token=sC"); with it, only items changed after C and deletes, paged as "token=sCoN".
    /// </summary>
    private HttpResponseMessage Delta(FakeDrive drive, Uri uri)
    {
        string token = uri.Query.Contains("token=", StringComparison.Ordinal) ? uri.Query.Split("token=")[1].Split('&')[0] : "0";
        int since = -1, start;
        if (token.StartsWith('s'))
        {
            var parts = token[1..].Split('o');
            since = int.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
            start = parts.Length > 1 ? int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) : 0;
        }
        else
        {
            start = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
        }
        var value = new JsonArray();
        bool more;
        int seq;
        lock (_sync)
        {
            seq = _seq;
            var all = drive.Items.Values.Where(i => since < 0 || i.Changed > since)
                .OrderBy(i => i.IsFile() ? 1 : 0).ThenBy(i => int.Parse(new string(i.Id.Where(char.IsDigit).DefaultIfEmpty('0').ToArray())))
                .Select(i => ItemJson(drive, i))
                .ToList();
            if (FilesBeforeFolders)
                all = all.OrderBy(o => o["root"] is not null ? 0 : o["folder"] is not null ? 2 : 1).ToList();
            if (since >= 0)
            {
                foreach (var d in drive.Deleted.Where(d => d.Seq > since))
                {
                    var o = new JsonObject { ["id"] = d.Id, ["name"] = string.Empty, ["deleted"] = new JsonObject { ["state"] = "deleted" } };
                    if (d.ParentId is not null)
                        o["parentReference"] = new JsonObject { ["driveId"] = drive.Id, ["id"] = d.ParentId };
                    if (d.IsFolder)
                        o["folder"] = new JsonObject();
                    else
                        o["file"] = new JsonObject();
                    all.Add(o);
                }
            }
            foreach (var o in all.Skip(start).Take(DeltaPageSize))
                value.Add(o);
            more = start + DeltaPageSize < all.Count;
        }
        var obj = new JsonObject { ["value"] = value };
        string prefix = since >= 0 ? $"s{since}o" : string.Empty;
        if (more)
            obj["@odata.nextLink"] = $"{Base}/drives/{drive.Id}/root/delta?token={prefix}{start + DeltaPageSize}";
        else
            obj["@odata.deltaLink"] = $"{Base}/drives/{drive.Id}/root/delta?token=s{seq}";
        return Json(obj);
    }

    /// <summary>A move done by SharePoint outside the tool (e.g. before the host died); absolute decoded URLs.</summary>
    public bool MoveDirectly(string method, string from, string to)
    {
        var body = new JsonObject
        {
            ["srcPath"] = new JsonObject { ["__metadata"] = new JsonObject { ["type"] = "SP.ResourcePath" }, ["DecodedUrl"] = from },
            ["destPath"] = new JsonObject { ["__metadata"] = new JsonObject { ["type"] = "SP.ResourcePath" }, ["DecodedUrl"] = to },
        };
        lock (_sync)
            return MoveCopyUtil($"/_api/SP.MoveCopyUtil.{method}()", body.ToJsonString()).IsSuccessStatusCode;
    }

    /// <summary>The drive and library-relative path of an absolute (decoded) URL of this tenant.</summary>
    private (FakeDrive Drive, string Path)? Locate(string absoluteUrl)
    {
        foreach (var d in _drives.Values.OrderByDescending(d => d.WebUrl.Length))
        {
            string root = Uri.UnescapeDataString(d.WebUrl).TrimEnd('/');
            if (absoluteUrl.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return (d, absoluteUrl[(root.Length + 1)..]);
        }
        return null;
    }

    private static HttpResponseMessage SpError(HttpStatusCode status, string code, string message) =>
        Json(new JsonObject { ["odata.error"] = new JsonObject { ["code"] = code, ["message"] = new JsonObject { ["lang"] = "uk-UA", ["value"] = message } } }, status);

    /// <summary>
    /// SP.MoveCopyUtil.MoveFolderByPath / MoveFileByPath as observed on the test tenant (API_SPIKE.md, second pass):
    /// the target must not exist (400 -2130575257), its parent must, a folder over the threshold is refused (500), the
    /// item keeps its id and moves to the other library with its subtree.
    /// </summary>
    private HttpResponseMessage MoveCopyUtil(string path, string body)
    {
        string method = path[(path.IndexOf("SP.MoveCopyUtil.", StringComparison.Ordinal) + "SP.MoveCopyUtil.".Length)..].TrimEnd(')').TrimEnd('(');
        var json = JsonNode.Parse(body)!;
        string from = json["srcPath"]!["DecodedUrl"]!.GetValue<string>();
        string to = json["destPath"]!["DecodedUrl"]!.GetValue<string>();
        Moves.Enqueue((method, from, to));
        if (json["srcPath"]!["__metadata"] is null)
            return SpError(HttpStatusCode.BadRequest, "-1, Microsoft.SharePoint.Client.InvalidClientQueryException", "verbose body expected");
        var src = Locate(from);
        var dst = Locate(to);
        var item = src is { } s ? FindByPath(s.Drive, s.Path) : null;
        if (src is null || dst is null || item is null || item.IsRoot || item.IsFolder != (method == "MoveFolderByPath"))
            return SpError(HttpStatusCode.NotFound, "-2147024894, System.IO.FileNotFoundException", "Файл не знайдено.");
        var (sd, _) = src.Value;
        var (dd, dpath) = dst.Value;
        if (FindByPath(dd, dpath) is not null)
            return SpError(HttpStatusCode.BadRequest, "-2130575257, Microsoft.SharePoint.SPException", "Кінцевий файл уже існує.");
        string parentPath = dpath.Contains('/') ? dpath[..dpath.LastIndexOf('/')] : string.Empty;
        var parent = FindByPath(dd, parentPath);
        if (parent is null || !parent.IsFolder)
            return SpError(HttpStatusCode.NotFound, "-2147024893, System.IO.DirectoryNotFoundException", "Папку не знайдено.");

        var subtree = new List<FakeItem> { item };
        for (int i = 0; i < subtree.Count; i++)
            subtree.AddRange(sd.Items.Values.Where(x => x.ParentId == subtree[i].Id));
        if (item.IsFolder && subtree.Count - 1 > ThresholdItems)
            return SpError(HttpStatusCode.InternalServerError, "-2147024860, Microsoft.SharePoint.SPQueryThrottledException",
                "Запитану операцію заборонено, оскільки вона перевищує граничне значення подання списку.");
        var locked = subtree.Where(x => !x.IsFolder && x.CheckedOutBy is not null).ToList();
        if (locked.Count > 0 && (!item.IsFolder || !FolderMoveLeavesLocked))
            return SpError(HttpStatusCode.Locked, "-2147018894, Microsoft.SharePoint.SPFileLockException", $"Файл заблоковано: {locked[0].Name}.");

        string newName = dpath.Contains('/') ? dpath[(dpath.LastIndexOf('/') + 1)..] : dpath;
        if (locked.Count == 0)
        {
            foreach (var x in subtree)
            {
                sd.Items.Remove(x.Id);
                dd.Items[x.Id] = x;
                x.Changed = ++_seq;
            }
            sd.Deleted.Add((item.Id, item.ParentId, item.IsFolder, ++_seq));
            item.ParentId = parent.Id;
            item.Name = newName;
            return Json(new JsonObject { ["odata.null"] = true });
        }

        // A folder with locked files: a copy of the folder chain is created in the target and everything that is not
        // locked (and not on the way to a locked file) moves into it; the locked files stay where they were.
        var keep = new HashSet<string>(locked.Select(x => x.Id));
        foreach (var l in locked)
        {
            for (var p = l.ParentId; p is not null && p != item.ParentId; p = sd.Items.TryGetValue(p, out var pi) ? pi.ParentId : null)
                keep.Add(p);
        }
        var copies = new Dictionary<string, FakeItem>();
        FakeItem CopyOf(FakeItem folder)
        {
            if (copies.TryGetValue(folder.Id, out var c))
                return c;
            var parentCopy = folder.Id == item.Id ? parent : CopyOf(sd.Items[folder.ParentId!]);
            c = new FakeItem { Id = $"f{_nextId++}", Name = folder.Id == item.Id ? newName : folder.Name, ParentId = parentCopy.Id, IsFolder = true, Changed = ++_seq };
            dd.Items[c.Id] = c;
            copies[folder.Id] = c;
            return c;
        }
        foreach (var x in subtree.Where(x => !keep.Contains(x.Id) && x.ParentId is not null && keep.Contains(x.ParentId)).ToList())
        {
            var newParent = CopyOf(sd.Items[x.ParentId!]);
            var moved = new List<FakeItem> { x };
            for (int i = 0; i < moved.Count; i++)
                moved.AddRange(sd.Items.Values.Where(y => y.ParentId == moved[i].Id));
            foreach (var y in moved)
            {
                sd.Items.Remove(y.Id);
                dd.Items[y.Id] = y;
                y.Changed = ++_seq;
            }
            sd.Deleted.Add((x.Id, x.ParentId, x.IsFolder, ++_seq));
            x.ParentId = newParent.Id;
        }
        CopyOf(item);
        return Json(new JsonObject { ["odata.null"] = true });
    }

    /// <summary>SharePoint REST of a list: the files without a published version.</summary>
    private HttpResponseMessage Rest(Uri uri, string path, string method)
    {
        if (!SharePointRest)
            return Json(new JsonObject { ["odata.error"] = new JsonObject { ["code"] = "-2147024891, System.UnauthorizedAccessException", ["message"] = new JsonObject { ["value"] = "Access denied." } } },
                HttpStatusCode.Forbidden);
        int start = path.IndexOf("lists(guid'", StringComparison.Ordinal) + "lists(guid'".Length;
        string listId = path[start..path.IndexOf("')", start, StringComparison.Ordinal)];
        var drive = _drives[listId["list-".Length..]];
        string libraryPath = Uri.UnescapeDataString(new Uri(drive.WebUrl).AbsolutePath);
        if (method == "GET" && path.EndsWith("')", StringComparison.Ordinal))
        {
            if (uri.Query.Contains("ItemCount", StringComparison.Ordinal))
            {
                if (ItemCountFails)
                    return Error(HttpStatusCode.InternalServerError, "itemCountFailed");
                // ItemCount: files and folders of the list (not its root), the unpublished files too.
                return Json(new JsonObject { ["ItemCount"] = drive.Items.Values.Count(i => i.Id != drive.Root.Id) + drive.Unpublished.Count });
            }
            return Json(new JsonObject
            {
                ["Title"] = drive.Name, ["EnableVersioning"] = drive.Versioning, ["EnableMinorVersions"] = drive.MinorVersions,
                ["ForceCheckout"] = drive.ForceCheckout, ["EnableModeration"] = drive.Moderation,
            });
        }
        if (method == "GET" && path.EndsWith("/fields", StringComparison.Ordinal))
        {
            var fields = new JsonArray
            {
                new JsonObject { ["InternalName"] = "Title", ["Title"] = "Заголовок", ["TypeAsString"] = "Text", ["Required"] = false, ["Hidden"] = false, ["ReadOnlyField"] = false, ["FromBaseType"] = false },
                new JsonObject { ["InternalName"] = "Modified", ["Title"] = "Змінено", ["TypeAsString"] = "DateTime", ["Required"] = false, ["Hidden"] = false, ["ReadOnlyField"] = true, ["FromBaseType"] = true },
                new JsonObject { ["InternalName"] = "MediaServiceImageTags", ["Title"] = "Теги зображень", ["TypeAsString"] = "TaxonomyFieldTypeMulti", ["Required"] = false, ["Hidden"] = false, ["ReadOnlyField"] = false, ["FromBaseType"] = false },
            };
            foreach (var (name, title, type, required) in drive.Columns)
                fields.Add(new JsonObject { ["InternalName"] = name, ["Title"] = title, ["TypeAsString"] = type, ["Required"] = required, ["Hidden"] = false, ["ReadOnlyField"] = false, ["FromBaseType"] = false });
            return Json(new JsonObject { ["value"] = fields });
        }
        if (method == "GET" && path.EndsWith("/contenttypes", StringComparison.Ordinal))
        {
            var types = new JsonArray();
            foreach (string t in drive.ContentTypes)
                types.Add(new JsonObject { ["Name"] = t, ["Hidden"] = false });
            return Json(new JsonObject { ["value"] = types });
        }
        if (path.EndsWith("/GetCheckedOutFiles()", StringComparison.Ordinal) && method == "POST")
        {
            if (!FullControl)
            {
                return Json(new JsonObject { ["odata.error"] = new JsonObject { ["code"] = "-2147024891, System.UnauthorizedAccessException", ["message"] = new JsonObject { ["value"] = "Attempted to perform an unauthorized operation." } } },
                    HttpStatusCode.Forbidden);
            }
            var files = new JsonArray();
            foreach (var (p, by) in drive.Unpublished)
                files.Add(new JsonObject { ["ServerRelativePath"] = new JsonObject { ["DecodedUrl"] = libraryPath + "/" + p }, ["CheckedOutBy"] = new JsonObject { ["Title"] = by } });
            return Json(new JsonObject { ["value"] = files });
        }
        return Error(HttpStatusCode.NotFound, "badRestPath");
    }

    private static FakeItem? FindByPath(FakeDrive drive, string path)
    {
        var current = drive.Root;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current = drive.Items.Values.FirstOrDefault(i => i.ParentId == current.Id && string.Equals(i.Name, part, StringComparison.OrdinalIgnoreCase));
            if (current is null)
                return null;
        }
        return current;
    }

    private static JsonObject ItemJson(FakeDrive drive, FakeItem i, bool full = false)
    {
        var o = new JsonObject
        {
            ["id"] = i.Id,
            ["name"] = i.Name,
            ["eTag"] = i.ETag,
            ["cTag"] = i.ETag,
            ["size"] = i.Content.Length,
            ["lastModifiedDateTime"] = i.Modified.ToString("o"),
            ["lastModifiedBy"] = new JsonObject { ["user"] = new JsonObject { ["email"] = "user@contoso.com" } },
        };
        if (i.ParentId is not null)
            o["parentReference"] = new JsonObject { ["id"] = i.ParentId, ["driveId"] = drive.Id };
        if (i.IsRoot)
            o["root"] = new JsonObject();
        if (i.IsFolder)
            o["folder"] = new JsonObject { ["childCount"] = 0 };
        else
            o["file"] = new JsonObject { ["mimeType"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" };
        if (!i.IsFolder)
        {
            // As Graph returns it, in delta too (API_SPIKE.md, result 16).
            o["publication"] = i.CheckedOutBy is null
                ? new JsonObject { ["level"] = "published", ["versionId"] = $"{i.Version}.0" }
                : new JsonObject { ["level"] = "checkout", ["versionId"] = $"{i.Version}.0", ["checkedOutBy"] = new JsonObject { ["user"] = new JsonObject { ["displayName"] = i.CheckedOutBy } } };
        }
        if (full && !i.IsFolder)
        {
            o["listItem"] = new JsonObject
            {
                ["id"] = "1",
                ["fields"] = new JsonObject { ["_UIVersionString"] = $"{i.Version}.0", ["CheckoutUser"] = i.CheckedOutBy is null ? null : new JsonObject { ["LookupValue"] = i.CheckedOutBy } },
            };
        }
        return o;
    }

    public static HttpResponseMessage Json(JsonNode node, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json"),
    };

    public static HttpResponseMessage Error(HttpStatusCode status, string code, int? retryAfterSeconds = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { error = new { code, message = "fake " + code } }), Encoding.UTF8, "application/json"),
        };
        response.Headers.TryAddWithoutValidation("request-id", Guid.NewGuid().ToString());
        if (retryAfterSeconds is int s)
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(s));
        return response;
    }

    public int UploadsOf(FakeItem item)
    {
        lock (_sync)
            return item.Uploads;
    }

    /// <summary>RU spent by every app according to the requests the fake has seen (same cost table as the client).</summary>
    public int CostOf(FakeRequest r) => r.Method switch
    {
        "GET" when r.Path.Contains("/content", StringComparison.Ordinal) => RequestCost.Download,
        "GET" when r.Path.Contains("/delta", StringComparison.Ordinal) => r.Path.Contains("token=", StringComparison.Ordinal) ? RequestCost.DeltaWithToken : RequestCost.List,
        "GET" when r.Path.EndsWith("/drives?$select=id,name,webUrl", StringComparison.Ordinal) => RequestCost.List,
        "GET" when r.Path.Contains("/children", StringComparison.Ordinal) => RequestCost.List,
        "GET" => RequestCost.Get,
        "PUT" when r.Path.StartsWith(UploadBase, StringComparison.Ordinal) => RequestCost.UploadChunk,
        _ => RequestCost.Upload,
    };
}

internal static class FakeItemExtensions
{
    public static bool IsFile(this FakeGraph.FakeItem i) => !i.IsFolder;
}

/// <summary>Token "app|generation"; a forced refresh moves to the current generation of the fake.</summary>
public sealed class FakeTokens(FakeGraph graph, string app) : ITokenProvider
{
    private int _generation;

    public int Refreshes;

    public Task<string> GetTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        if (forceRefresh)
        {
            Interlocked.Increment(ref Refreshes);
            _generation = graph.MinTokenGeneration(app);
        }
        return Task.FromResult($"{app}|{_generation}");
    }
}

public sealed class FakeAppFactory(FakeGraph graph) : ISpoAppFactory
{
    public ConcurrentDictionary<string, SpoApp> Created { get; } = new();

    public ConcurrentDictionary<string, FakeTokens> Tokens { get; } = new();

    public int ChunkSize { get; set; } = 320 * 1024;

    public SpoApp Create(Connection connection, SpoAppSettings settings, RuBudget tenantBudget)
    {
        var (perMinute, perDay, _) = connection.Limits();
        var budget = new RuBudget(connection.Name, perMinute, TimeSpan.FromMinutes(1), perDay) { Scale = settings.BudgetScale };
        var throttle = new ThrottleState(connection.Name, settings.MaxConcurrency, settings.BlockedAfter);
        var tokens = Tokens.GetOrAdd(connection.Name, n => new FakeTokens(graph, n));
        var http = new HttpClient(new AppHeaderHandler(connection.Name) { InnerHandler = graph }, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = new GraphClient(connection.Name, http, tokens, budget, tenantBudget, throttle, new GraphClientOptions
        {
            BaseUrl = FakeGraph.Base,
            RequestAttempts = settings.RequestAttempts,
            RequestTimeout = settings.RequestTimeout,
            BaseBackoff = TimeSpan.FromMilliseconds(10),
            MaxBackoff = TimeSpan.FromMilliseconds(100),
            ChunkSize = ChunkSize,
        }, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, sharePointTokens: _ => tokens);
        var app = new SpoApp(connection.Name, client, budget, throttle);
        Created[connection.Name] = app;
        return app;
    }

    /// <summary>Marks unauthenticated upload-session requests with the app name, for attribution in the fake.</summary>
    private sealed class AppHeaderHandler(string app) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            request.Headers.TryAddWithoutValidation("X-Test-App", app);
            return base.SendAsync(request, cancellationToken);
        }
    }
}
