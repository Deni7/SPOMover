using System.Globalization;
using Microsoft.Data.Sqlite;
using static SPOMover.Core.Localization.L;

namespace SPOMover.State;

/// <summary>
/// Stage 4: an apply run moves the batches of one plan. Its batches, the folders to create and a row for every planned
/// file (copied from the inventory, so the run's file table, filters and reports work as for any run) live here.
/// </summary>
public sealed partial class StateStore
{
    private const string ApplySchema = """
        CREATE TABLE IF NOT EXISTS apply_batches(
            run_id INTEGER NOT NULL, seq INTEGER NOT NULL, wave INTEGER NOT NULL, kind TEXT NOT NULL, library TEXT NOT NULL, dest_idx INTEGER NOT NULL,
            source TEXT NOT NULL, target TEXT NOT NULL, files INTEGER NOT NULL, items INTEGER NOT NULL,
            status TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0, not_before TEXT, locked_since TEXT,
            error_class TEXT, error TEXT, started_at TEXT, finished_at TEXT,
            PRIMARY KEY(run_id, seq));
        CREATE TABLE IF NOT EXISTS apply_folders(
            run_id INTEGER NOT NULL, ord INTEGER NOT NULL, library TEXT NOT NULL, dest_idx INTEGER NOT NULL, path TEXT NOT NULL, done INTEGER NOT NULL DEFAULT 0,
            PRIMARY KEY(run_id, ord));
        CREATE INDEX IF NOT EXISTS ix_files_batch ON files(run_id, batch);
        """;

    private static void MigrateApply(SqliteConnection c)
    {
        void Ensure(string table, string column, string definition)
        {
            using var cmd = Command(c, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$n", null, ("$n", column));
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }
        Ensure("runs", "plan_id", "INTEGER");
        Ensure("files", "batch", "INTEGER");
        Ensure("files", "dest_idx", "INTEGER");
        Ensure("files", "new_path", "TEXT");
    }

    public static class BatchStatuses
    {
        public const string Pending = "pending";
        public const string InProgress = "in_progress";
        /// <summary>Waits for locked files (LCK-2) until <c>not_before</c>.</summary>
        public const string Deferred = "deferred";
        /// <summary>Everything planned is in the target and the source subtree is empty.</summary>
        public const string Done = "done";
        /// <summary>Final, but some files did not move (conflict, lock past the limit, error): they need attention.</summary>
        public const string Partial = "partial";

        public static string Failed(string errorClass) => "failed:" + errorClass;

        public static bool IsFinal(string s) => s is Done or Partial || s.StartsWith("failed:", StringComparison.Ordinal);
    }

    public sealed record ApplyBatch(
        long RunId, int Seq, int Wave, string Kind, string Library, int DestIndex, string Source, string Target, long Files, long Items,
        string Status, int Attempts, DateTimeOffset? NotBefore, DateTimeOffset? LockedSince, string? ErrorClass, string? Error,
        DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt)
    {
        public bool IsFolder => Kind == "folder";
    }

    public sealed record ApplyFolder(int Ord, string Library, int DestIndex, string Path, bool Done);

    /// <summary>The plan an apply run moves.</summary>
    public long? GetRunPlan(long runId) =>
        Scalar("SELECT plan_id FROM runs WHERE id=$r", ("$r", runId)) is long id ? id : null;

    /// <summary>
    /// Creates an apply run of a plan: copies the library targets of the plan's inventory, the batches, the folders to
    /// create, and a pending row for every planned file with its batch and new path.
    /// </summary>
    /// <param name="destIndex">Library name → index of the library in the run (as in the profile's Libraries()).</param>
    public long CreateApplyRun(long planId, string? profileName, string snapshot, int libraries, IReadOnlyDictionary<string, int> destIndex, DateTimeOffset now) => Write((c, tx) =>
    {
        long inventory;
        using (var p = Command(c, "SELECT run_id FROM plans WHERE id=$p", tx, ("$p", planId)))
            inventory = p.ExecuteScalar() is long r ? r : throw new ArgumentException(T($"Plan {planId} not found."));
        long id;
        using (var cmd = Command(c, """
            INSERT INTO runs(parent_id, mode, status, profile_name, config_snapshot, enumerate, created_at, plan_id)
            VALUES($inv,$m,$s,$n,$c,0,$t,$p) RETURNING id
            """, tx, ("$inv", inventory), ("$m", RunModes.Apply), ("$s", RunStatuses.Created), ("$n", profileName), ("$c", snapshot), ("$t", Now(now)), ("$p", planId)))
            id = (long)cmd.ExecuteScalar()!;
        for (int i = 0; i < libraries; i++)
            Exec(c, "INSERT INTO targets(run_id, idx, done, checkouts) VALUES($r,$i,1,1)", tx, ("$r", id), ("$i", i));
        // Drives of the inventory, by library name: the indexes of the plan's inventory may differ from the run's.
        Exec(c, """
            UPDATE targets SET (drive_id, drive_url, root_item_id, root_path) =
                (SELECT drive_id, drive_url, root_item_id, root_path FROM targets t WHERE t.run_id=$inv AND t.idx=0)
            WHERE run_id=$r AND idx=0
            """, tx, ("$inv", inventory), ("$r", id));

        int Dest(string library) => destIndex.TryGetValue(library, out int d) ? d : throw new ArgumentException(T($"Library «{library}» is not in the profile."));
        var batches = new List<(int Seq, int Wave, string Kind, string Library, string Source, string Target, long Files, long Items)>();
        using (var q = Command(c, "SELECT seq, wave, kind, library, source, target, files, items FROM plan_batches WHERE plan_id=$p ORDER BY seq", tx, ("$p", planId)))
        using (var r = q.ExecuteReader())
        {
            while (r.Read())
                batches.Add((r.GetInt32(0), r.GetInt32(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetInt64(6), r.GetInt64(7)));
        }
        foreach (var b in batches)
        {
            int dest = Dest(b.Library);
            Exec(c, """
                INSERT INTO apply_batches(run_id, seq, wave, kind, library, dest_idx, source, target, files, items, status)
                VALUES($r,$s,$w,$k,$l,$d,$src,$tg,$f,$i,$st)
                """, tx, ("$r", id), ("$s", b.Seq), ("$w", b.Wave), ("$k", b.Kind), ("$l", b.Library), ("$d", dest), ("$src", b.Source), ("$tg", b.Target),
                ("$f", b.Files), ("$i", b.Items), ("$st", BatchStatuses.Pending));
            const string columns = "run_id, target_idx, drive_id, item_id, parent_id, name, path, path_key, ext, size, etag, modified_at, modified_by, checked_out_by, status, status_code, batch, dest_idx, new_path, updated_at";
            if (b.Kind == "folder")
            {
                string key = PathKey(b.Source)!;
                Exec(c, $"""
                    INSERT INTO files({columns})
                    SELECT $r, 0, drive_id, item_id, parent_id, name, path, path_key, ext, size, etag, modified_at, modified_by, checked_out_by, $pending, $pending,
                           $s, $d, $tg || substr(path, length($src) + 1), $t
                    FROM files WHERE run_id=$inv AND target_idx=0 AND path_key > $k1 AND path_key < $k2
                    """, tx, ("$r", id), ("$pending", FileStatuses.Pending), ("$s", b.Seq), ("$d", dest), ("$tg", b.Target), ("$src", b.Source), ("$t", Now(now)),
                    ("$inv", inventory), ("$k1", key + "/"), ("$k2", key + "0"));
            }
            else
            {
                Exec(c, $"""
                    INSERT INTO files({columns})
                    SELECT $r, 0, f.drive_id, f.item_id, f.parent_id, f.name, f.path, f.path_key, f.ext, f.size, f.etag, f.modified_at, f.modified_by, f.checked_out_by,
                           $pending, $pending, $s, $d, pf.new_path, $t
                    FROM plan_batch_files pf JOIN files f ON f.id = pf.file_id
                    WHERE pf.plan_id=$p AND pf.seq=$s
                    """, tx, ("$r", id), ("$pending", FileStatuses.Pending), ("$s", b.Seq), ("$d", dest), ("$t", Now(now)), ("$p", planId));
            }
        }
        int ord = 0;
        using (var q = Command(c, "SELECT library, path FROM plan_folders WHERE plan_id=$p ORDER BY ord", tx, ("$p", planId)))
        using (var r = q.ExecuteReader())
        {
            var folders = new List<(string, string)>();
            while (r.Read())
                folders.Add((r.GetString(0), r.GetString(1)));
            r.Close();
            foreach (var (library, path) in folders)
            {
                Exec(c, "INSERT INTO apply_folders(run_id, ord, library, dest_idx, path) VALUES($r,$o,$l,$d,$p)", tx,
                    ("$r", id), ("$o", ++ord), ("$l", library), ("$d", Dest(library)), ("$p", path));
            }
        }
        return id;
    });

    private static ApplyBatch MapBatch(SqliteDataReader r) => new(
        L(r, "run_id"), (int)L(r, "seq"), (int)L(r, "wave"), S(r, "kind")!, S(r, "library")!, (int)L(r, "dest_idx"), S(r, "source")!, S(r, "target")!,
        L(r, "files"), L(r, "items"), S(r, "status")!, (int)L(r, "attempts"), Time(S(r, "not_before") ?? string.Empty), Time(S(r, "locked_since") ?? string.Empty),
        S(r, "error_class"), S(r, "error"), Time(S(r, "started_at") ?? string.Empty), Time(S(r, "finished_at") ?? string.Empty));

    public IReadOnlyList<ApplyBatch> GetApplyBatches(long runId) =>
        Query("SELECT * FROM apply_batches WHERE run_id=$r ORDER BY seq", MapBatch, ("$r", runId));

    public ApplyBatch? GetApplyBatch(long runId, int seq) =>
        Query("SELECT * FROM apply_batches WHERE run_id=$r AND seq=$s", MapBatch, ("$r", runId), ("$s", seq)).FirstOrDefault();

    /// <summary>
    /// Batches that can start now: pending, or deferred whose time has come. Wave by wave, the largest first: a big batch
    /// started last would run alone at the end (on fs-test the last 584-file batch took 288 s on its own).
    /// </summary>
    public IReadOnlyList<ApplyBatch> DueBatches(long runId, DateTimeOffset now, int take) => Query($"""
        SELECT * FROM apply_batches WHERE run_id=$r AND (status='{BatchStatuses.Pending}' OR status='{BatchStatuses.Deferred}')
          AND (not_before IS NULL OR not_before <= $now) ORDER BY wave, items DESC, seq LIMIT $take
        """, MapBatch, ("$r", runId), ("$now", Now(now)), ("$take", take));

    /// <summary>When the next deferred or delayed batch becomes due, if any.</summary>
    public DateTimeOffset? NextDue(long runId) =>
        Scalar($"SELECT MIN(not_before) FROM apply_batches WHERE run_id=$r AND status IN ('{BatchStatuses.Pending}','{BatchStatuses.Deferred}')", ("$r", runId)) is string s
            ? Time(s) : null;

    public long CountOpenBatches(long runId) => Convert.ToInt64(Scalar($"""
        SELECT COUNT(*) FROM apply_batches WHERE run_id=$r AND status IN ('{BatchStatuses.Pending}','{BatchStatuses.Deferred}','{BatchStatuses.InProgress}')
        """, ("$r", runId)), CultureInfo.InvariantCulture);

    public void StartBatch(long runId, int seq, DateTimeOffset now) => Write((c, tx) =>
        Exec(c, $"UPDATE apply_batches SET status='{BatchStatuses.InProgress}', attempts=attempts+1, started_at=COALESCE(started_at,$t), not_before=NULL WHERE run_id=$r AND seq=$s", tx,
            ("$t", Now(now)), ("$r", runId), ("$s", seq)));

    /// <summary>Batch outcome. Final statuses set finished_at; <paramref name="notBefore"/> delays a pending or deferred batch.</summary>
    public void FinishBatch(long runId, int seq, string status, DateTimeOffset now, string? errorClass = null, string? error = null,
        DateTimeOffset? notBefore = null, DateTimeOffset? lockedSince = null) => Write((c, tx) =>
        Exec(c, """
            UPDATE apply_batches SET status=$st, error_class=$ec, error=$e, not_before=$nb, locked_since=COALESCE($ls, locked_since),
                finished_at=CASE WHEN $final=1 THEN $t ELSE NULL END
            WHERE run_id=$r AND seq=$s
            """, tx, ("$st", status), ("$ec", errorClass), ("$e", error), ("$nb", notBefore is { } nb ? Now(nb) : null),
            ("$ls", lockedSince is { } ls ? Now(ls) : null), ("$final", BatchStatuses.IsFinal(status) ? 1 : 0), ("$t", Now(now)), ("$r", runId), ("$s", seq)));

    /// <summary>STA-3: batches left in progress by a crash go back to pending; the next attempt starts by looking at source and target.</summary>
    public int RecoverBatches(long runId) => Write((c, tx) =>
    {
        using var cmd = Command(c, $"UPDATE apply_batches SET status='{BatchStatuses.Pending}' WHERE run_id=$r AND status='{BatchStatuses.InProgress}'", tx, ("$r", runId));
        return cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<ApplyFolder> GetApplyFolders(long runId) =>
        Query("SELECT * FROM apply_folders WHERE run_id=$r ORDER BY ord", r => new ApplyFolder((int)L(r, "ord"), S(r, "library")!, (int)L(r, "dest_idx"), S(r, "path")!, L(r, "done") == 1), ("$r", runId));

    public void SetApplyFolderDone(long runId, int ord) => Write((c, tx) =>
        Exec(c, "UPDATE apply_folders SET done=1 WHERE run_id=$r AND ord=$o", tx, ("$r", runId), ("$o", ord)));

    /// <summary>Pending (not final) files of a batch: id, source path, new path, size.</summary>
    public IReadOnlyList<(long Id, string Path, string NewPath, long Size, string Status)> GetBatchFiles(long runId, int seq) => Query(
        "SELECT id, path, new_path, size, status FROM files WHERE run_id=$r AND batch=$s ORDER BY path_key",
        r => (L(r, "id"), S(r, "path")!, S(r, "new_path")!, L(r, "size"), S(r, "status")!), ("$r", runId), ("$s", seq));

    /// <summary>Every not yet final file of a batch gets <paramref name="status"/> (a whole folder moved).</summary>
    public int SetBatchFilesStatus(long runId, int seq, string status, DateTimeOffset now) => Write((c, tx) =>
    {
        using var cmd = Command(c, $"""
            UPDATE files SET status=$s, status_code=$s, error_class=NULL, error=NULL, updated_at=$u
            WHERE run_id=$r AND batch=$b AND status IN ('{FileStatuses.Pending}','{FileStatuses.InProgress}','{FileStatuses.Deferred}')
            """, tx, ("$s", status), ("$u", Now(now)), ("$r", runId), ("$b", seq));
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Not yet final files of a batch under a source folder get <paramref name="status"/> (a subfolder moved whole).</summary>
    public int SetBatchFilesStatusUnder(long runId, int seq, string folder, string status, DateTimeOffset now) => Write((c, tx) =>
    {
        string key = PathKey(folder)!;
        using var cmd = Command(c, $"""
            UPDATE files SET status=$s, status_code=$s, error_class=NULL, error=NULL, updated_at=$u
            WHERE run_id=$r AND batch=$b AND path_key > $k1 AND path_key < $k2
              AND status IN ('{FileStatuses.Pending}','{FileStatuses.InProgress}','{FileStatuses.Deferred}')
            """, tx, ("$s", status), ("$u", Now(now)), ("$r", runId), ("$b", seq), ("$k1", key + "/"), ("$k2", key + "0"));
        return cmd.ExecuteNonQuery();
    });

    /// <summary>Not yet final files of a batch with one status get another (e.g. deferred → failed:locked past the limit).</summary>
    public int ReplaceBatchFilesStatus(long runId, int seq, string from, string to, string? error, DateTimeOffset now) => Write((c, tx) =>
    {
        using var cmd = Command(c, """
            UPDATE files SET status=$to, status_code=$to, error_class=CASE WHEN $to LIKE 'failed:%' THEN substr($to, 8) ELSE error_class END,
                error=COALESCE($e, error), updated_at=$u
            WHERE run_id=$r AND batch=$b AND status=$from
            """, tx, ("$to", to), ("$e", error), ("$u", Now(now)), ("$r", runId), ("$b", seq), ("$from", from));
        return cmd.ExecuteNonQuery();
    });

    /// <summary>GUI-1: batches of an apply run by wave and status, with their files.</summary>
    public sealed record WaveProgress(int Wave, long Batches, long Done, long Partial, long Deferred, long Failed, long InProgress, long Files, long FilesDone);

    public IReadOnlyList<WaveProgress> GetWaveProgress(long runId) => Query($"""
        SELECT b.wave,
               COUNT(*) batches,
               SUM(CASE WHEN b.status='{BatchStatuses.Done}' THEN 1 ELSE 0 END) done,
               SUM(CASE WHEN b.status='{BatchStatuses.Partial}' THEN 1 ELSE 0 END) partial,
               SUM(CASE WHEN b.status='{BatchStatuses.Deferred}' THEN 1 ELSE 0 END) deferred,
               SUM(CASE WHEN b.status LIKE 'failed:%' THEN 1 ELSE 0 END) failed,
               SUM(CASE WHEN b.status='{BatchStatuses.InProgress}' THEN 1 ELSE 0 END) running,
               SUM(b.files) files,
               COALESCE((SELECT COUNT(*) FROM files f JOIN apply_batches x ON x.run_id=f.run_id AND x.seq=f.batch
                         WHERE f.run_id=$r AND x.wave=b.wave AND f.status IN ('{FileStatuses.Moved}','{FileStatuses.Verified}')), 0) filesDone
        FROM apply_batches b WHERE b.run_id=$r GROUP BY b.wave ORDER BY b.wave
        """, r => new WaveProgress((int)L(r, "wave"), L(r, "batches"), L(r, "done"), L(r, "partial"), L(r, "deferred"), L(r, "failed"), L(r, "running"),
            L(r, "files"), L(r, "filesDone")), ("$r", runId));

    /// <summary>Source paths of the files an apply run moved (moved or verified).</summary>
    public IReadOnlyList<string> MovedPaths(long runId) =>
        Query($"SELECT path FROM files WHERE run_id=$r AND status IN ('{FileStatuses.Moved}','{FileStatuses.Verified}') AND path IS NOT NULL",
            r => S(r, "path")!, ("$r", runId));

    /// <summary>
    /// EXE-7: a cleanup run of an apply run: its library targets, and a pending row for every folder to look at
    /// (the "files" of the run are folders: path, name, a synthetic item id).
    /// </summary>
    public long CreateCleanupRun(long applyRun, string? profileName, string snapshot, IEnumerable<string> folders, DateTimeOffset now) => Write((c, tx) =>
    {
        long id;
        using (var cmd = Command(c, """
            INSERT INTO runs(parent_id, mode, status, profile_name, config_snapshot, enumerate, created_at)
            VALUES($a,$m,$s,$n,$c,0,$t) RETURNING id
            """, tx, ("$a", applyRun), ("$m", RunModes.Cleanup), ("$s", RunStatuses.Created), ("$n", profileName), ("$c", snapshot), ("$t", Now(now))))
            id = (long)cmd.ExecuteScalar()!;
        Exec(c, """
            INSERT INTO targets(run_id, idx, drive_id, drive_url, root_item_id, root_path, done, checkouts)
            SELECT $r, idx, drive_id, drive_url, root_item_id, root_path, 1, 1 FROM targets WHERE run_id=$a
            """, tx, ("$r", id), ("$a", applyRun));
        string drive;
        using (var q = Command(c, "SELECT drive_id FROM targets WHERE run_id=$a AND idx=0", tx, ("$a", applyRun)))
            drive = q.ExecuteScalar() as string ?? throw new InvalidOperationException(T($"Run {applyRun} has no source library."));
        foreach (string path in folders)
        {
            Exec(c, """
                INSERT INTO files(run_id, target_idx, drive_id, item_id, name, path, path_key, status, status_code, updated_at)
                VALUES($r,0,$d,$i,$n,$p,$k,$s,$s,$t)
                """, tx, ("$r", id), ("$d", drive), ("$i", "dir:" + path), ("$n", path[(path.LastIndexOf('/') + 1)..]), ("$p", path),
                ("$k", PathKey(path)), ("$s", FileStatuses.Pending), ("$t", Now(now)));
        }
        return id;
    });

    /// <summary>EXE-7: the outcome for one folder of a cleanup run.</summary>
    public void SetCleanupOutcome(long fileId, string status, string? errorClass, string? error, string? comment, DateTimeOffset now) => Write((c, tx) =>
        Exec(c, "UPDATE files SET status=$s, status_code=$s, error_class=$ec, error=$e, comment=$cm, attempts=attempts+1, updated_at=$u WHERE id=$id", tx,
            ("$s", status), ("$ec", errorClass), ("$e", error), ("$cm", comment), ("$u", Now(now)), ("$id", fileId)));

    public long CountMoved(long runId) =>
        Convert.ToInt64(Scalar($"SELECT COUNT(*) FROM files WHERE run_id=$r AND status='{FileStatuses.Moved}'", ("$r", runId)), CultureInfo.InvariantCulture);

    /// <summary>Outcome of one file of a batch, found by its source path; true if there was such a row.</summary>
    public bool SetBatchFileOutcome(long runId, int seq, string path, string status, string? errorClass, string? error, DateTimeOffset now) => Write((c, tx) =>
    {
        using var cmd = Command(c, """
            UPDATE files SET status=$s, status_code=$s, error_class=$ec, error=$e, attempts=attempts+1, updated_at=$u
            WHERE run_id=$r AND batch=$b AND path_key=$k
            """, tx, ("$s", status), ("$ec", errorClass), ("$e", error), ("$u", Now(now)), ("$r", runId), ("$b", seq), ("$k", PathKey(path)));
        return cmd.ExecuteNonQuery() > 0;
    });

    /// <summary>Files of a batch by status, for the batch summary.</summary>
    public Dictionary<string, long> CountBatchFiles(long runId, int seq) =>
        Query("SELECT status, COUNT(*) n FROM files WHERE run_id=$r AND batch=$b GROUP BY status", r => (S(r, "status")!, L(r, "n")), ("$r", runId), ("$b", seq))
            .ToDictionary(x => x.Item1, x => x.Item2);
}
