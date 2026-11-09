using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace SPOMover.State;

/// <summary>
/// STA-1: state of runs in one SQLite file (WAL: the dashboard reads while the engine writes).
/// Also stores profiles and connections (CFG-1) with their change history.
/// Writes are serialized inside the process; every status change is one transaction (STA-2).
/// </summary>
public sealed partial class StateStore
{
    private readonly string _connectionString;
    private readonly object _write = new();

    public StateStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = true,
            DefaultTimeout = 30,
        }.ToString();
        using var c = Open();
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, Schema);
        Migrate(c);
        Exec(c, InventorySchema);
        Exec(c, PlanSchema);
        MigrateApply(c);
        Exec(c, ApplySchema);
    }

    public static string Now(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? Time(object value) =>
        value is string s && s.Length > 0 ? DateTimeOffset.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

    private const string Schema = """
        CREATE TABLE IF NOT EXISTS runs(
            id INTEGER PRIMARY KEY, parent_id INTEGER, mode TEXT NOT NULL, status TEXT NOT NULL, profile_name TEXT,
            config_snapshot TEXT NOT NULL, enumerate INTEGER NOT NULL, created_at TEXT NOT NULL, started_at TEXT,
            finished_at TEXT, stop_reason TEXT);
        CREATE TABLE IF NOT EXISTS targets(
            run_id INTEGER NOT NULL, idx INTEGER NOT NULL, drive_id TEXT, drive_url TEXT, root_item_id TEXT, root_path TEXT,
            next_link TEXT, delta_link TEXT, done INTEGER NOT NULL DEFAULT 0, checkouts INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(run_id, idx));
        CREATE TABLE IF NOT EXISTS folders(
            run_id INTEGER NOT NULL, drive_id TEXT NOT NULL, item_id TEXT NOT NULL, parent_id TEXT, name TEXT NOT NULL,
            is_root INTEGER NOT NULL, PRIMARY KEY(run_id, drive_id, item_id));
        CREATE TABLE IF NOT EXISTS files(
            id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL, target_idx INTEGER NOT NULL, drive_id TEXT NOT NULL,
            item_id TEXT NOT NULL, parent_id TEXT, name TEXT NOT NULL, path TEXT, ext TEXT, size INTEGER NOT NULL DEFAULT 0,
            etag TEXT, modified_at TEXT, modified_by TEXT, status TEXT NOT NULL, status_code TEXT, stage TEXT,
            error_class TEXT, error TEXT, attempts INTEGER NOT NULL DEFAULT 0, not_before TEXT, locked_since TEXT, app TEXT,
            comment TEXT, updated_at TEXT, path_key TEXT, checked_out_by TEXT, UNIQUE(run_id, drive_id, item_id));
        CREATE INDEX IF NOT EXISTS ix_files_queue ON files(run_id, status, not_before);
        CREATE INDEX IF NOT EXISTS ix_files_code ON files(run_id, status_code);
        CREATE INDEX IF NOT EXISTS ix_files_ext ON files(run_id, ext, status);
        CREATE INDEX IF NOT EXISTS ix_files_target ON files(run_id, target_idx, status);
        CREATE TABLE IF NOT EXISTS attempts(
            id INTEGER PRIMARY KEY, file_id INTEGER NOT NULL, at TEXT NOT NULL, attempt INTEGER NOT NULL, stage TEXT NOT NULL,
            error_class TEXT, http_status INTEGER, request_id TEXT, message TEXT, app TEXT);
        CREATE INDEX IF NOT EXISTS ix_attempts_file ON attempts(file_id);
        CREATE TABLE IF NOT EXISTS run_metrics(run_id INTEGER NOT NULL, at TEXT NOT NULL, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS settings(kind TEXT NOT NULL, name TEXT NOT NULL COLLATE NOCASE, json TEXT NOT NULL,
            updated_at TEXT NOT NULL, PRIMARY KEY(kind, name));
        CREATE TABLE IF NOT EXISTS config_history(id INTEGER PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL,
            json TEXT, changed_at TEXT NOT NULL, deleted INTEGER NOT NULL DEFAULT 0);
        """;

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, "PRAGMA busy_timeout=30000; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=OFF;");
        // The same comparison form as PathKey (SQLite lower() changes ASCII only, not Cyrillic).
        c.CreateFunction("ulower", (string? s) => PathKey(s), isDeterministic: true);
        return c;
    }

    private static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null, params (string, object?)[] args)
    {
        using var cmd = Command(c, sql, tx, args);
        cmd.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection c, string sql, SqliteTransaction? tx, params (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }

    private T Write<T>(Func<SqliteConnection, SqliteTransaction, T> action)
    {
        lock (_write)
        {
            using var c = Open();
            using var tx = c.BeginTransaction();
            var result = action(c, tx);
            tx.Commit();
            return result;
        }
    }

    private void Write(Action<SqliteConnection, SqliteTransaction> action) => Write<bool>((c, tx) =>
    {
        action(c, tx);
        return true;
    });

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, null, args);
        using var r = cmd.ExecuteReader();
        var list = new List<T>();
        while (r.Read())
            list.Add(map(r));
        return list;
    }

    private object? Scalar(string sql, params (string, object?)[] args)
    {
        using var c = Open();
        using var cmd = Command(c, sql, null, args);
        return cmd.ExecuteScalar();
    }

    private static string? S(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static long L(SqliteDataReader r, string column)
    {
        int i = r.GetOrdinal(column);
        return r.IsDBNull(i) ? 0 : r.GetInt64(i);
    }

    // ---------------- settings (CFG-1) ----------------

    public IReadOnlyList<(string Name, string Json, DateTimeOffset UpdatedAt)> ListSettings(string kind) =>
        Query("SELECT name, json, updated_at FROM settings WHERE kind=$k ORDER BY name",
            r => (S(r, "name")!, S(r, "json")!, Time(S(r, "updated_at")!)!.Value), ("$k", kind));

    public string? GetSetting(string kind, string name) =>
        Scalar("SELECT json FROM settings WHERE kind=$k AND name=$n", ("$k", kind), ("$n", name)) as string;

    /// <summary>Saves a profile or a connection; the previous version goes to config_history.</summary>
    public void PutSetting(string kind, string name, string json, DateTimeOffset now) => Write((c, tx) =>
    {
        Exec(c, "INSERT INTO config_history(kind, name, json, changed_at) VALUES($k,$n,$j,$t)", tx,
            ("$k", kind), ("$n", name), ("$j", json), ("$t", Now(now)));
        Exec(c, "INSERT INTO settings(kind, name, json, updated_at) VALUES($k,$n,$j,$t) ON CONFLICT(kind, name) DO UPDATE SET json=$j, updated_at=$t", tx,
            ("$k", kind), ("$n", name), ("$j", json), ("$t", Now(now)));
    });

    public bool DeleteSetting(string kind, string name, DateTimeOffset now) => Write((c, tx) =>
    {
        Exec(c, "INSERT INTO config_history(kind, name, json, changed_at, deleted) VALUES($k,$n,NULL,$t,1)", tx,
            ("$k", kind), ("$n", name), ("$t", Now(now)));
        using var cmd = Command(c, "DELETE FROM settings WHERE kind=$k AND name=$n", tx, ("$k", kind), ("$n", name));
        return cmd.ExecuteNonQuery() > 0;
    });

    // ---------------- runs ----------------

    public long CreateRun(string mode, long? parentId, string? profileName, string snapshot, bool enumerate, int targets, DateTimeOffset now) =>
        Write((c, tx) =>
        {
            using var cmd = Command(c, """
                INSERT INTO runs(parent_id, mode, status, profile_name, config_snapshot, enumerate, created_at)
                VALUES($p,$m,$s,$n,$c,$e,$t); SELECT last_insert_rowid();
                """, tx, ("$p", parentId), ("$m", mode), ("$s", RunStatuses.Created), ("$n", profileName), ("$c", snapshot),
                ("$e", enumerate ? 1 : 0), ("$t", Now(now)));
            long id = (long)cmd.ExecuteScalar()!;
            for (int i = 0; i < targets; i++)
                Exec(c, "INSERT INTO targets(run_id, idx) VALUES($r,$i)", tx, ("$r", id), ("$i", i));
            return id;
        });

    public RunRow? GetRun(long id) => Query("SELECT * FROM runs WHERE id=$id", MapRun, ("$id", id)).FirstOrDefault();

    public IReadOnlyList<RunRow> ListRuns(int take = 50) => Query("SELECT * FROM runs ORDER BY id DESC LIMIT $t", MapRun, ("$t", take));

    /// <summary>
    /// Removes a run with everything it recorded: files, attempts, listing, metrics.
    /// Runs made from it keep their own files and lose only the reference to the parent. False if there is no such run.
    /// </summary>
    public bool DeleteRun(long id) => Write((c, tx) =>
    {
        const string files = "SELECT id FROM files WHERE run_id=$r";
        Exec(c, $"DELETE FROM attempts WHERE file_id IN ({files})", tx, ("$r", id));
        foreach (string table in new[] { "files", "folders", "targets", "run_metrics", "folder_stats", "unpublished_files", "run_notes", "mapping_preview" })
            Exec(c, $"DELETE FROM {table} WHERE run_id=$r", tx, ("$r", id));
        // Plans built from this inventory refer to its files.
        foreach (string table in new[] { "plan_batches", "plan_batch_files", "plan_folders", "plan_conflicts", "plan_waves" })
            Exec(c, $"DELETE FROM {table} WHERE plan_id IN (SELECT id FROM plans WHERE run_id=$r)", tx, ("$r", id));
        Exec(c, "DELETE FROM plans WHERE run_id=$r", tx, ("$r", id));
        Exec(c, "UPDATE runs SET parent_id=NULL WHERE parent_id=$r", tx, ("$r", id));
        using var cmd = Command(c, "DELETE FROM runs WHERE id=$r", tx, ("$r", id));
        return cmd.ExecuteNonQuery() > 0;
    });

    private static RunRow MapRun(SqliteDataReader r) => new(
        L(r, "id"),
        r.IsDBNull(r.GetOrdinal("parent_id")) ? null : L(r, "parent_id"),
        S(r, "mode")!,
        S(r, "status")!,
        S(r, "profile_name"),
        S(r, "config_snapshot")!,
        L(r, "enumerate") == 1,
        Time(S(r, "created_at")!)!.Value,
        Time(S(r, "started_at") ?? string.Empty),
        Time(S(r, "finished_at") ?? string.Empty),
        S(r, "stop_reason"));

    /// <summary>Changes the status only if the run is in one of <paramref name="from"/> (no race with completion).</summary>
    public bool SetRunStatusIf(long id, string status, DateTimeOffset now, params string[] from) => Write((c, tx) =>
    {
        using var cmd = Command(c, $"UPDATE runs SET status=$s WHERE id=$id AND status IN ({string.Join(",", from.Select((_, i) => "$f" + i))})", tx,
            [("$s", status), ("$id", id), .. from.Select((f, i) => ("$f" + i, (object?)f))]);
        return cmd.ExecuteNonQuery() > 0;
    });

    public void SetRunStatus(long id, string status, DateTimeOffset now, string? reason = null) => Write((c, tx) =>
    {
        string extra = status == RunStatuses.Running ? ", started_at=COALESCE(started_at,$t), finished_at=NULL, stop_reason=NULL"
            : RunStatuses.IsFinished(status) || status is RunStatuses.Stopped or RunStatuses.Aborted ? ", finished_at=$t, stop_reason=$why"
            : string.Empty;
        Exec(c, $"UPDATE runs SET status=$s{extra} WHERE id=$id", tx, ("$s", status), ("$t", Now(now)), ("$why", reason), ("$id", id));
    });

    public void AddMetrics(long runId, string json, DateTimeOffset now) => Write((c, tx) =>
        Exec(c, "INSERT INTO run_metrics(run_id, at, json) VALUES($r,$t,$j)", tx, ("$r", runId), ("$t", Now(now)), ("$j", json)));

    public IReadOnlyList<(DateTimeOffset At, string Json)> GetMetrics(long runId) =>
        Query("SELECT at, json FROM run_metrics WHERE run_id=$r ORDER BY at", r => (Time(S(r, "at")!)!.Value, S(r, "json")!), ("$r", runId));

    // ---------------- targets and enumeration ----------------

    public IReadOnlyList<TargetRow> GetTargets(long runId) => Query("SELECT * FROM targets WHERE run_id=$r ORDER BY idx", r => new TargetRow(
        L(r, "run_id"), (int)L(r, "idx"), S(r, "drive_id"), S(r, "drive_url"), S(r, "root_item_id"), S(r, "root_path"),
        S(r, "next_link"), S(r, "delta_link"), L(r, "done") == 1, (int)L(r, "checkouts"), L(r, "incremental") == 1), ("$r", runId));

    public void SetTargetDrive(long runId, int idx, string driveId, string driveUrl, string? rootItemId, string rootPath) => Write((c, tx) =>
        Exec(c, "UPDATE targets SET drive_id=$d, drive_url=$u, root_item_id=$ri, root_path=$rp WHERE run_id=$r AND idx=$i", tx,
            ("$d", driveId), ("$u", driveUrl), ("$ri", rootItemId), ("$rp", rootPath), ("$r", runId), ("$i", idx)));

    public IReadOnlyList<FolderRow> GetFolders(long runId) =>
        Query("SELECT drive_id, item_id, parent_id, name, is_root FROM folders WHERE run_id=$r",
            r => new FolderRow(S(r, "drive_id")!, S(r, "item_id")!, S(r, "parent_id"), S(r, "name")!, L(r, "is_root") == 1), ("$r", runId));

    /// <summary>ENU-1: one delta page — folders, new files and the position to continue from — in one transaction.</summary>
    public int SaveDeltaPage(long runId, int idx, IEnumerable<FolderRow> folders, IEnumerable<FileRow> files, string? nextLink, string? deltaLink,
        bool done, DateTimeOffset now) => Write((c, tx) =>
    {
        foreach (var f in folders)
        {
            Exec(c, "INSERT INTO folders(run_id, drive_id, item_id, parent_id, name, is_root) VALUES($r,$d,$i,$p,$n,$root) " +
                    "ON CONFLICT(run_id, drive_id, item_id) DO UPDATE SET parent_id=$p, name=$n, is_root=$root", tx,
                ("$r", runId), ("$d", f.DriveId), ("$i", f.ItemId), ("$p", f.ParentId), ("$n", f.Name), ("$root", f.IsRoot ? 1 : 0));
        }
        int inserted = 0;
        foreach (var f in files)
            inserted += InsertFile(c, tx, f, now);
        Exec(c, "UPDATE targets SET next_link=$n, delta_link=COALESCE($dl, delta_link), done=$done WHERE run_id=$r AND idx=$i", tx,
            ("$n", nextLink), ("$dl", deltaLink), ("$done", done ? 1 : 0), ("$r", runId), ("$i", idx));
        return inserted;
    });

    private static int InsertFile(SqliteConnection c, SqliteTransaction tx, FileRow f, DateTimeOffset now)
    {
        using var cmd = Command(c, """
            INSERT INTO files(run_id, target_idx, drive_id, item_id, parent_id, name, path, path_key, ext, size, etag, modified_at, modified_by,
                              checked_out_by, status, status_code, error, updated_at)
            VALUES($r,$t,$d,$i,$p,$n,$path,$pk,$ext,$size,$etag,$m,$mb,$co,$s,$s,$err,$u)
            ON CONFLICT(run_id, drive_id, item_id) DO UPDATE SET
                parent_id=$p, name=$n, path=COALESCE($path, path), path_key=COALESCE($pk, path_key), size=$size, etag=$etag, modified_at=$m, modified_by=$mb,
                checked_out_by=$co
            WHERE files.status IN ('waiting_path','pending','listed')
            """, tx,
            ("$r", f.RunId), ("$t", f.TargetIndex), ("$d", f.DriveId), ("$i", f.ItemId), ("$p", f.ParentId), ("$n", f.Name),
            ("$path", f.Path), ("$pk", PathKey(f.Path)), ("$ext", System.IO.Path.GetExtension(f.Name).ToLowerInvariant()), ("$size", f.Size), ("$etag", f.ETag),
            ("$m", f.Modified is { } m ? Now(m) : null), ("$mb", f.ModifiedBy), ("$co", f.CheckedOutBy), ("$s", f.Status), ("$err", f.Error), ("$u", Now(now)));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Files whose folder path was not known when they arrived.</summary>
    public IReadOnlyList<FileRow> GetWaitingPath(long runId) =>
        Query($"SELECT * FROM files WHERE run_id=$r AND status='{FileStatuses.WaitingPath}'", MapFile, ("$r", runId));

    public void ResolvePath(long fileId, string? path, string status, string? error, DateTimeOffset now) => Write((c, tx) =>
        Exec(c, "UPDATE files SET path=$p, path_key=$pk, status=$s, status_code=$s, error=$e, updated_at=$u WHERE id=$id", tx,
            ("$p", path), ("$pk", PathKey(path)), ("$s", status), ("$e", error), ("$u", Now(now)), ("$id", fileId)));

    /// <summary>A file that turned out to be outside the target is not part of the run.</summary>
    public void DeleteFile(long fileId) => Write((c, tx) => Exec(c, "DELETE FROM files WHERE id=$id", tx, ("$id", fileId)));

    // ---------------- queue ----------------

    /// <summary>STA-3: after a crash, files that were in progress start over.</summary>
    public int RecoverInProgress(long runId, DateTimeOffset now) => Write((c, tx) =>
    {
        using var cmd = Command(c, $"UPDATE files SET status='{FileStatuses.Pending}', status_code='{FileStatuses.Pending}', stage=NULL, updated_at=$u " +
                                   $"WHERE run_id=$r AND status='{FileStatuses.InProgress}'", tx, ("$u", Now(now)), ("$r", runId));
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Takes up to <paramref name="limit"/> files whose time has come and marks them in progress.</summary>
    public IReadOnlyList<FileRow> ClaimReady(long runId, int limit, DateTimeOffset now) => Write((c, tx) =>
    {
        var rows = new List<FileRow>();
        using (var cmd = Command(c, $"""
            SELECT * FROM files WHERE run_id=$r AND status IN ('{FileStatuses.Pending}','{FileStatuses.Deferred}')
              AND (not_before IS NULL OR not_before <= $now)
            ORDER BY CASE status WHEN '{FileStatuses.Pending}' THEN 0 ELSE 1 END, not_before, id LIMIT $l
            """, tx, ("$r", runId), ("$now", Now(now)), ("$l", limit)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
                rows.Add(MapFile(r));
        }
        foreach (var row in rows)
        {
            // A wave of a deferred (locked) file is not a new attempt: level 3 must not use up level 2.
            Exec(c, $"UPDATE files SET status='{FileStatuses.InProgress}', status_code='{FileStatuses.InProgress}', stage=NULL, " +
                    $"attempts=attempts+(CASE WHEN status='{FileStatuses.Deferred}' AND attempts>0 THEN 0 ELSE 1 END), updated_at=$u WHERE id=$id",
                tx, ("$u", Now(now)), ("$id", row.Id));
        }
        return rows.Select(x => x with
        {
            Status = FileStatuses.InProgress,
            Attempts = x.Status == FileStatuses.Deferred && x.Attempts > 0 ? x.Attempts : x.Attempts + 1,
        }).ToList();
    });

    /// <summary>STA-4: a file interrupted by a stop before it was written goes back to the queue; the attempt is not counted.</summary>
    public void Release(long fileId, DateTimeOffset now) => Write((c, tx) =>
        Exec(c, $"UPDATE files SET status='{FileStatuses.Pending}', status_code='{FileStatuses.Pending}', stage=NULL, " +
                $"attempts=MAX(attempts-1,0), updated_at=$u WHERE id=$id AND status='{FileStatuses.InProgress}'", tx, ("$u", Now(now)), ("$id", fileId)));

    public DateTimeOffset? NextNotBefore(long runId) =>
        Time(Scalar($"SELECT MIN(not_before) FROM files WHERE run_id=$r AND status IN ('{FileStatuses.Pending}','{FileStatuses.Deferred}')",
            ("$r", runId)) as string ?? string.Empty);

    public void SetStage(long fileId, string stage, string? app, DateTimeOffset now, string? etag = null) => Write((c, tx) =>
        Exec(c, "UPDATE files SET stage=$s, app=COALESCE($a, app), etag=COALESCE($e, etag), updated_at=$u WHERE id=$id", tx,
            ("$s", stage), ("$a", app), ("$e", etag), ("$u", Now(now)), ("$id", fileId)));

    /// <summary>Final status, or back to the queue with not_before (levels 2 and 3).</summary>
    public void Finish(long fileId, FileOutcome o, AttemptRow? attempt, DateTimeOffset now) => Write((c, tx) =>
    {
        Exec(c, """
            UPDATE files SET status=$s, status_code=$sc, error_class=$ec, error=$e, not_before=$nb, locked_since=$ls,
                etag=COALESCE($etag, etag), stage=NULL, updated_at=$u
            WHERE id=$id
            """, tx,
            ("$s", o.Status), ("$sc", o.StatusCode ?? o.Status), ("$ec", o.ErrorClass), ("$e", o.Error),
            ("$nb", o.NotBefore is { } nb ? Now(nb) : null), ("$ls", o.LockedSince is { } ls ? Now(ls) : null),
            ("$etag", o.ETag), ("$u", Now(now)), ("$id", fileId));
        if (attempt is not null)
            AddAttempt(c, tx, fileId, attempt);
    });

    private static void AddAttempt(SqliteConnection c, SqliteTransaction tx, long fileId, AttemptRow a) =>
        Exec(c, "INSERT INTO attempts(file_id, at, attempt, stage, error_class, http_status, request_id, message, app) VALUES($f,$t,$n,$s,$c,$h,$r,$m,$a)", tx,
            ("$f", fileId), ("$t", Now(a.At)), ("$n", a.Attempt), ("$s", a.Stage), ("$c", a.ErrorClass), ("$h", a.HttpStatus),
            ("$r", a.RequestId), ("$m", a.Message), ("$a", a.App));

    public IReadOnlyList<AttemptRow> GetAttempts(long fileId) =>
        Query("SELECT * FROM attempts WHERE file_id=$f ORDER BY id", r => new AttemptRow(
            Time(S(r, "at")!)!.Value, (int)L(r, "attempt"), S(r, "stage")!, S(r, "error_class"),
            r.IsDBNull(r.GetOrdinal("http_status")) ? null : (int)L(r, "http_status"), S(r, "request_id"), S(r, "message") ?? string.Empty, S(r, "app")),
            ("$f", fileId));

    // ---------------- files: queries for UI, reports, retry ----------------

    public int CountNonFinal(long runId) => Convert.ToInt32(Scalar(
        $"SELECT COUNT(*) FROM files WHERE run_id=$r AND status IN ({string.Join(',', FileStatuses.NonFinal.Select(s => $"'{s}'"))})", ("$r", runId)),
        CultureInfo.InvariantCulture);

    /// <summary>REP-3: number of files per status code.</summary>
    public IReadOnlyDictionary<string, long> CountByStatus(long runId) =>
        Query("SELECT status_code, COUNT(*) n FROM files WHERE run_id=$r GROUP BY status_code", r => (S(r, "status_code") ?? "?", L(r, "n")), ("$r", runId))
            .ToDictionary(x => x.Item1, x => x.Item2);

    public (long Files, long Bytes) Totals(long runId) => Query(
        "SELECT COUNT(*) f, COALESCE(SUM(size),0) b FROM files WHERE run_id=$r",
        r => (L(r, "f"), L(r, "b")), ("$r", runId))[0];

    /// <summary>Files that need a person (SQL, for large runs). Stage 4 adds classes like locked:checkout and conflict:name.</summary>
    public const string AttentionSql = "(status_code LIKE 'failed:%')";

    /// <summary>Attempts of the run's files by error class, including attempts after which the file later succeeded.</summary>
    public IReadOnlyDictionary<string, long> CountAttemptsByClass(long runId) =>
        Query("SELECT a.error_class AS c, COUNT(*) AS n FROM attempts a JOIN files f ON f.id=a.file_id WHERE f.run_id=$r AND a.error_class IS NOT NULL GROUP BY a.error_class",
            r => (S(r, "c")!, L(r, "n")), ("$r", runId)).ToDictionary(x => x.Item1, x => x.Item2);

    public long CountAttention(long runId) =>
        Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM files WHERE run_id=$r AND {AttentionSql}", ("$r", runId)), CultureInfo.InvariantCulture);

    public sealed record ErrorGroup(string Status, string? ErrorClass, string? Error, long Count);

    /// <summary>Which files <see cref="GroupErrors"/> takes: the dashboard cards "Помилки" and "Потребують уваги" open these.</summary>
    public enum ErrorScope { All, Failed, Attention }

    /// <summary>GUI-7: failed, deferred and attention files grouped by class and message.</summary>
    public IReadOnlyList<ErrorGroup> GroupErrors(long runId, ErrorScope scope = ErrorScope.All, int take = 200)
    {
        string where = scope switch
        {
            ErrorScope.Failed => "status_code LIKE 'failed:%'",
            ErrorScope.Attention => AttentionSql,
            _ => $"(status_code LIKE 'failed:%' OR status = '{FileStatuses.Deferred}' OR {AttentionSql})",
        };
        return Query($"""
            SELECT status_code, error_class, error, COUNT(*) n FROM files
            WHERE run_id=$r AND {where}
            GROUP BY status_code, error_class, error ORDER BY n DESC LIMIT $t
            """, r => new ErrorGroup(S(r, "status_code")!, S(r, "error_class"), S(r, "error"), L(r, "n")), ("$r", runId), ("$t", take));
    }

    public IReadOnlyList<long> FileIdsOfGroup(long runId, string statusCode, string? error) => Query(
        "SELECT id FROM files WHERE run_id=$r AND status_code=$s AND (error=$e OR ($e IS NULL AND error IS NULL))",
        r => L(r, "id"), ("$r", runId), ("$s", statusCode), ("$e", error));

    public sealed record Breakdown(string Key, long Total, long Bytes, long Finished, long Failed);

    /// <summary>Files, size and progress by format (ext) or by library (target_idx).</summary>
    public IReadOnlyList<Breakdown> BreakdownBy(long runId, string column)
    {
        if (column is not ("ext" or "target_idx" or "dest_idx"))
            throw new ArgumentException("ext, target_idx or dest_idx", nameof(column));
        string open = string.Join(',', FileStatuses.NonFinal.Select(s => $"'{s}'"));
        return Query($"""
            SELECT CAST({column} AS TEXT) k, COUNT(*) total,
                   SUM(CASE WHEN status NOT IN ({open}) THEN 1 ELSE 0 END) finished,
                   COALESCE(SUM(size),0) bytes,
                   SUM(CASE WHEN status LIKE 'failed:%' THEN 1 ELSE 0 END) failed
            FROM files WHERE run_id=$r GROUP BY {column} ORDER BY total DESC
            """, r => new Breakdown(S(r, "k") ?? "?", L(r, "total"), L(r, "bytes"), L(r, "finished"), L(r, "failed")), ("$r", runId));
    }

    /// <summary>
    /// GUI-1 charts, from the stored attempts, so they survive a stop and continue: when each file now failed got its
    /// last attempt, and when each failed attempt was followed by another try (a retry, a wait for a locked file).
    /// </summary>
    public (IReadOnlyList<DateTimeOffset> FailedFiles, IReadOnlyList<DateTimeOffset> Retries) ErrorTimeline(long runId)
    {
        var failed = Query("""
            SELECT (SELECT MAX(a.at) FROM attempts a WHERE a.file_id = f.id) at FROM files f
            WHERE f.run_id=$r AND f.status LIKE 'failed:%' ORDER BY at
            """, r => Time(S(r, "at") ?? string.Empty), ("$r", runId)).OfType<DateTimeOffset>().ToList();
        var retries = Query("""
            SELECT a.at FROM attempts a JOIN files f ON f.id = a.file_id
            WHERE f.run_id=$r AND a.error_class IS NOT NULL
              AND NOT (f.status LIKE 'failed:%' AND a.id = (SELECT MAX(b.id) FROM attempts b WHERE b.file_id = f.id))
            ORDER BY a.at
            """, r => Time(S(r, "at")!)!.Value, ("$r", runId));
        return (failed, retries);
    }

    /// <summary>RTY-6 for a group of files.</summary>
    public int IgnoreMany(IEnumerable<long> ids, string comment, DateTimeOffset now) => ids.Count(id => Ignore(id, comment, now));

    /// <summary>Last run of the profile that listed its targets — the base of estimates (GUI-17, GUI-18).</summary>
    public RunRow? LastListingRun(string profileName) =>
        Query("SELECT * FROM runs WHERE profile_name=$p AND enumerate=1 ORDER BY id DESC LIMIT 1", MapRun, ("$p", profileName)).FirstOrDefault();

    public FileRow? GetFile(long id) => Query("SELECT * FROM files WHERE id=$id", MapFile, ("$id", id)).FirstOrDefault();

    /// <summary>Columns the files table can be sorted by (GUI-5); anything else falls back to id.</summary>
    public static readonly IReadOnlyDictionary<string, string> SortColumns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["id"] = "id",
        ["path"] = "path COLLATE NOCASE",
        ["size"] = "size",
        ["status"] = "status_code",
        ["attempts"] = "attempts",
        ["error"] = "error",
    };

    public IReadOnlyList<FileRow> QueryFiles(long runId, FileFilter filter, int skip, int take, string? sortBy = null, bool descending = false)
    {
        var (where, args) = Where(runId, filter);
        args.Add(("$skip", skip));
        args.Add(("$take", take));
        string order = sortBy is not null && SortColumns.TryGetValue(sortBy, out var column) ? column : "id";
        string direction = descending ? "DESC" : "ASC";
        // id as the second key keeps the order stable between pages.
        return Query($"SELECT * FROM files WHERE {where} ORDER BY {order} {direction}, id {direction} LIMIT $take OFFSET $skip", MapFile, args.ToArray());
    }

    /// <summary>Keyset paging for exports: files with id greater than <paramref name="afterId"/>, in id order.</summary>
    public IReadOnlyList<FileRow> QueryFilesAfter(long runId, FileFilter filter, long afterId, int take)
    {
        var (where, args) = Where(runId, filter);
        args.Add(("$after", afterId));
        args.Add(("$take", take));
        return Query($"SELECT * FROM files WHERE {where} AND id > $after ORDER BY id LIMIT $take", MapFile, args.ToArray());
    }

    public long CountFiles(long runId, FileFilter filter)
    {
        var (where, args) = Where(runId, filter);
        return Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM files WHERE {where}", args.ToArray()), CultureInfo.InvariantCulture);
    }

    private static (string Where, List<(string, object?)> Args) Where(long runId, FileFilter f)
    {
        var sb = new StringBuilder("run_id=$run");
        var args = new List<(string, object?)> { ("$run", runId) };
        void In(string column, IReadOnlyList<string> values)
        {
            if (values.Count == 0)
                return;
            var parts = new List<string>();
            for (int i = 0; i < values.Count; i++)
            {
                string p = $"${column}{i}";
                parts.Add($"{column} = {p}");
                args.Add((p, values[i]));
            }
            sb.Append(" AND (").Append(string.Join(" OR ", parts)).Append(')');
        }
        // A status like "failed" or "done" also takes "failed:transient", "done:residual".
        if (f.Statuses.Count > 0)
        {
            var parts = new List<string>();
            for (int i = 0; i < f.Statuses.Count; i++)
            {
                if (f.Statuses[i] == FileStatuses.Attention)
                {
                    parts.Add(AttentionSql);
                    continue;
                }
                parts.Add($"status_code = $st{i} OR status_code LIKE $stp{i}");
                args.Add(($"$st{i}", f.Statuses[i]));
                args.Add(($"$stp{i}", f.Statuses[i] + ":%"));
            }
            sb.Append(" AND (").Append(string.Join(" OR ", parts)).Append(')');
        }
        In("error_class", f.ErrorClasses);
        In("ext", f.Extensions.Select(e => e.StartsWith('.') ? e.ToLowerInvariant() : "." + e.ToLowerInvariant()).ToList());
        if (f.FileIds.Count > 0)
            sb.Append(" AND id IN (").Append(string.Join(',', f.FileIds)).Append(')');
        In("path", f.Paths);
        if (f.TargetIndex is int t)
        {
            sb.Append(" AND target_idx=$tidx");
            args.Add(("$tidx", t));
        }
        if (!string.IsNullOrWhiteSpace(f.Search))
        {
            sb.Append(" AND path LIKE $search");
            args.Add(("$search", "%" + f.Search + "%"));
        }
        if (!string.IsNullOrWhiteSpace(f.PathMask))
        {
            // Mask is applied in SQL with GLOB (* and ?); ** is the same as * here.
            sb.Append(" AND path GLOB $glob");
            args.Add(("$glob", f.PathMask.Replace("**", "*")));
        }
        return (sb.ToString(), args);
    }

    /// <summary>RTY-6: the file is not taken into later retries and is counted separately.</summary>
    public bool Ignore(long fileId, string comment, DateTimeOffset now) => Write((c, tx) =>
    {
        using var cmd = Command(c, $"UPDATE files SET status='{FileStatuses.Ignored}', status_code='{FileStatuses.Ignored}', comment=$c, updated_at=$u " +
                                   $"WHERE id=$id AND status NOT IN ('{FileStatuses.InProgress}')", tx, ("$c", comment), ("$u", Now(now)), ("$id", fileId));
        return cmd.ExecuteNonQuery() > 0;
    });

    public IEnumerable<FileRow> AllFiles(long runId)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT * FROM files WHERE run_id=$r ORDER BY id", null, ("$r", runId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return MapFile(r);
    }

    private static FileRow MapFile(SqliteDataReader r) => new()
    {
        Id = L(r, "id"),
        RunId = L(r, "run_id"),
        TargetIndex = (int)L(r, "target_idx"),
        DriveId = S(r, "drive_id")!,
        ItemId = S(r, "item_id")!,
        ParentId = S(r, "parent_id"),
        Name = S(r, "name")!,
        Path = S(r, "path"),
        Size = L(r, "size"),
        ETag = S(r, "etag"),
        Modified = Time(S(r, "modified_at") ?? string.Empty),
        ModifiedBy = S(r, "modified_by"),
        Status = S(r, "status")!,
        StatusCode = S(r, "status_code"),
        Stage = S(r, "stage"),
        ErrorClass = S(r, "error_class"),
        Error = S(r, "error"),
        Attempts = (int)L(r, "attempts"),
        LockedSince = Time(S(r, "locked_since") ?? string.Empty),
        App = S(r, "app"),
        Comment = S(r, "comment"),
        CheckedOutBy = S(r, "checked_out_by"),
    };
}
