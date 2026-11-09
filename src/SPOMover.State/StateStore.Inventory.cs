using System.Globalization;
using Microsoft.Data.Sqlite;

namespace SPOMover.State;

/// <summary>INV-2 folder totals, INV-3 check-outs, run notes, MAP-5 mapping preview.</summary>
public sealed partial class StateStore
{
    private const string InventorySchema = """
        CREATE INDEX IF NOT EXISTS ix_files_pathkey ON files(run_id, target_idx, path_key);
        CREATE INDEX IF NOT EXISTS ix_files_checkout ON files(run_id, checked_out_by) WHERE checked_out_by IS NOT NULL;
        CREATE TABLE IF NOT EXISTS folder_stats(
            run_id INTEGER NOT NULL, target_idx INTEGER NOT NULL, path TEXT NOT NULL, depth INTEGER NOT NULL,
            files INTEGER NOT NULL, bytes INTEGER NOT NULL, direct_files INTEGER NOT NULL, max_path INTEGER NOT NULL,
            PRIMARY KEY(run_id, target_idx, path));
        CREATE TABLE IF NOT EXISTS unpublished_files(
            run_id INTEGER NOT NULL, target_idx INTEGER NOT NULL, path TEXT NOT NULL, path_key TEXT NOT NULL, checked_out_by TEXT,
            PRIMARY KEY(run_id, target_idx, path_key));
        CREATE TABLE IF NOT EXISTS run_notes(id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL, at TEXT NOT NULL, level TEXT NOT NULL, message TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_run_notes ON run_notes(run_id);
        CREATE TABLE IF NOT EXISTS mapping_preview(
            run_id INTEGER NOT NULL, file_id INTEGER NOT NULL, rule_row INTEGER, target_idx INTEGER, new_path TEXT, new_key TEXT,
            PRIMARY KEY(run_id, file_id));
        CREATE INDEX IF NOT EXISTS ix_preview_target ON mapping_preview(run_id, target_idx, new_key);
        """;

    /// <summary>Columns added after a data folder was created: SQLite keeps the old table, so they are added in place.</summary>
    private static void Migrate(SqliteConnection c)
    {
        void Ensure(string table, string column, string definition)
        {
            using var cmd = Command(c, $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name=$n", null, ("$n", column));
            if (Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
                Exec(c, $"ALTER TABLE {table} ADD COLUMN {column} {definition}");
        }
        Ensure("files", "path_key", "TEXT");
        Ensure("files", "checked_out_by", "TEXT");
        Ensure("targets", "checkouts", "INTEGER NOT NULL DEFAULT 0");
        Ensure("targets", "incremental", "INTEGER NOT NULL DEFAULT 0");
        Ensure("targets", "expected_items", "INTEGER");
        // Rows written before path_key existed: ASCII-only lower() is enough to find them again, the next listing rewrites them.
        Exec(c, "UPDATE files SET path_key = lower(path) WHERE path_key IS NULL AND path IS NOT NULL");
    }

    /// <summary>MAP-3: the comparison form of a library-relative path (case-insensitive, also for Cyrillic).</summary>
    public static string? PathKey(string? path) => path?.ToLowerInvariant();

    // ---------------- run notes ----------------

    public sealed record RunNote(DateTimeOffset At, string Level, string Message);

    /// <summary>Something the user should know about a run that is not a file status: a skipped step, a limitation.</summary>
    public void AddNote(long runId, string level, string message, DateTimeOffset now) => Write((c, tx) =>
        Exec(c, "INSERT INTO run_notes(run_id, at, level, message) VALUES($r,$t,$l,$m)", tx, ("$r", runId), ("$t", Now(now)), ("$l", level), ("$m", message)));

    public IReadOnlyList<RunNote> GetNotes(long runId) =>
        Query("SELECT at, level, message FROM run_notes WHERE run_id=$r ORDER BY id", r => new RunNote(Time(S(r, "at")!)!.Value, S(r, "level")!, S(r, "message")!), ("$r", runId));

    // ---------------- INV-2: folders ----------------

    public sealed record FolderStat(int TargetIndex, string Path, int Depth, long Files, long Bytes, long DirectFiles, int MaxPath);

    /// <summary>Paths and sizes of the listed files of one library, for the folder totals.</summary>
    public IEnumerable<(string Path, long Size)> FilePaths(long runId, int targetIndex)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT path, size FROM files WHERE run_id=$r AND target_idx=$t AND path IS NOT NULL", null, ("$r", runId), ("$t", targetIndex));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return (r.GetString(0), r.GetInt64(1));
    }

    public void SaveFolderStats(long runId, int targetIndex, IEnumerable<FolderStat> stats) => Write((c, tx) =>
    {
        Exec(c, "DELETE FROM folder_stats WHERE run_id=$r AND target_idx=$t", tx, ("$r", runId), ("$t", targetIndex));
        using var cmd = Command(c, "INSERT INTO folder_stats(run_id, target_idx, path, depth, files, bytes, direct_files, max_path) VALUES($r,$t,$p,$d,$f,$b,$df,$m)", tx,
            ("$r", runId), ("$t", targetIndex), ("$p", null), ("$d", null), ("$f", null), ("$b", null), ("$df", null), ("$m", null));
        foreach (var s in stats)
        {
            cmd.Parameters["$p"].Value = s.Path;
            cmd.Parameters["$d"].Value = s.Depth;
            cmd.Parameters["$f"].Value = s.Files;
            cmd.Parameters["$b"].Value = s.Bytes;
            cmd.Parameters["$df"].Value = s.DirectFiles;
            cmd.Parameters["$m"].Value = s.MaxPath;
            cmd.ExecuteNonQuery();
        }
    });

    /// <summary>Folder totals of a run; <paramref name="maxDepth"/> limits the depth (0 — the library root only).</summary>
    public IReadOnlyList<FolderStat> GetFolderStats(long runId, int? targetIndex = null, int? maxDepth = null, string orderBy = "path", int take = int.MaxValue)
    {
        string order = orderBy == "bytes" ? "bytes DESC, path" : "target_idx, path";
        return Query($"""
            SELECT * FROM folder_stats WHERE run_id=$r AND ($t IS NULL OR target_idx=$t) AND ($d IS NULL OR depth <= $d)
            ORDER BY {order} LIMIT $take
            """, r => new FolderStat((int)L(r, "target_idx"), S(r, "path")!, (int)L(r, "depth"), L(r, "files"), L(r, "bytes"), L(r, "direct_files"), (int)L(r, "max_path")),
            ("$r", runId), ("$t", targetIndex), ("$d", maxDepth), ("$take", take));
    }

    // ---------------- INV-3: check-outs ----------------

    /// <summary>
    /// How many items (files and folders) SharePoint says the library has, taken before its listing: the denominator of
    /// the listing progress. Graph delta itself never says how many items are left.
    /// </summary>
    public void SetExpectedItems(long runId, int targetIndex, long items) => Write((c, tx) =>
        Exec(c, "UPDATE targets SET expected_items=$n WHERE run_id=$r AND idx=$i", tx, ("$n", items), ("$r", runId), ("$i", targetIndex)));

    /// <summary>One library of an inventory run: where its listing stands and what it found so far.</summary>
    /// <param name="Expected">Items of the library according to SharePoint (null: not known, e.g. a listing of a folder or an update).</param>
    /// <param name="Folders">Folders listed so far, without the root.</param>
    public sealed record TargetProgress(int Index, bool Done, int Checkouts, bool Incremental, long? Expected, long Files, long Bytes, long Folders, long Failed)
    {
        /// <summary>Share of the library listed, by items; null when the total is not known. Never 100 before the listing is done.</summary>
        public double? Percent => Done ? 100 : Expected is > 0 ? Math.Min(99.9, 100.0 * (Files + Folders) / Expected.Value) : null;
    }

    public IReadOnlyList<TargetProgress> GetTargetProgress(long runId)
    {
        var files = Query("SELECT target_idx, COUNT(*) f, COALESCE(SUM(size),0) b, SUM(status_code LIKE 'failed:%') e FROM files WHERE run_id=$r GROUP BY target_idx",
            r => ((int)L(r, "target_idx"), (F: L(r, "f"), B: L(r, "b"), E: L(r, "e"))), ("$r", runId)).ToDictionary(x => x.Item1, x => x.Item2);
        var folders = Query("SELECT drive_id, COUNT(*) - SUM(is_root) n FROM folders WHERE run_id=$r GROUP BY drive_id",
            r => (S(r, "drive_id")!, L(r, "n")), ("$r", runId)).ToDictionary(x => x.Item1, x => x.Item2, StringComparer.Ordinal);
        return Query("SELECT idx, drive_id, done, checkouts, incremental, expected_items FROM targets WHERE run_id=$r ORDER BY idx", r =>
        {
            int idx = (int)L(r, "idx");
            files.TryGetValue(idx, out var f);
            long dirs = S(r, "drive_id") is { } d && folders.TryGetValue(d, out long n) ? n : 0;
            return new TargetProgress(idx, L(r, "done") == 1, (int)L(r, "checkouts"), L(r, "incremental") == 1,
                r.IsDBNull(r.GetOrdinal("expected_items")) ? null : L(r, "expected_items"), f.F, f.B, dirs, f.E);
        }, ("$r", runId));
    }

    public void SetCheckoutState(long runId, int targetIndex, int state) => Write((c, tx) =>
        Exec(c, "UPDATE targets SET checkouts=$s WHERE run_id=$r AND idx=$i", tx, ("$s", state), ("$r", runId), ("$i", targetIndex)));

    /// <summary>INV-3: files without a published version, which the listing does not see (replaces the earlier list of the library).</summary>
    public void SaveUnpublished(long runId, int targetIndex, IEnumerable<(string Path, string? By)> unpublished) => Write((c, tx) =>
    {
        Exec(c, "DELETE FROM unpublished_files WHERE run_id=$r AND target_idx=$t", tx, ("$r", runId), ("$t", targetIndex));
        using var cmd = Command(c, "INSERT OR REPLACE INTO unpublished_files(run_id, target_idx, path, path_key, checked_out_by) VALUES($r,$t,$p,$k,$b)", tx,
            ("$r", runId), ("$t", targetIndex), ("$p", null), ("$k", null), ("$b", null));
        foreach (var (path, by) in unpublished)
        {
            cmd.Parameters["$p"].Value = path;
            cmd.Parameters["$k"].Value = PathKey(path);
            cmd.Parameters["$b"].Value = (object?)by ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    });

    public sealed record CheckoutRow(int TargetIndex, string Path, string? CheckedOutBy, bool Published, long Size);

    /// <summary>INV-3, LCK-1: checked-out files (from the listing) and files without a published version, of all libraries of the run.</summary>
    public IReadOnlyList<CheckoutRow> GetCheckouts(long runId) => Query("""
        SELECT target_idx, path, checked_out_by, 1 published, size FROM files WHERE run_id=$r AND checked_out_by IS NOT NULL
        UNION ALL
        SELECT target_idx, path, checked_out_by, 0, 0 FROM unpublished_files WHERE run_id=$r
        ORDER BY 1, 2
        """, r => new CheckoutRow((int)L(r, "target_idx"), S(r, "path")!, S(r, "checked_out_by"), L(r, "published") == 1, L(r, "size")), ("$r", runId));

    /// <summary>Check-outs of the source only: those block the move; check-outs in the targets are reported, not counted.</summary>
    public long CountCheckouts(long runId) => Convert.ToInt64(Scalar("""
        SELECT (SELECT COUNT(*) FROM files WHERE run_id=$r AND target_idx=0 AND checked_out_by IS NOT NULL)
             + (SELECT COUNT(*) FROM unpublished_files WHERE run_id=$r AND target_idx=0)
        """, ("$r", runId)), CultureInfo.InvariantCulture);

    // ---------------- MAP-5: mapping preview ----------------

    /// <summary>Where each file of the source goes under the current mapping. Replaces the previous preview of the run.</summary>
    public sealed record PreviewRow(long FileId, int? RuleRow, int? TargetIndex, string? NewPath);

    public void SavePreview(long runId, IEnumerable<PreviewRow> rows) => Write((c, tx) =>
    {
        Exec(c, "DELETE FROM mapping_preview WHERE run_id=$r", tx, ("$r", runId));
        using var cmd = Command(c, "INSERT INTO mapping_preview(run_id, file_id, rule_row, target_idx, new_path, new_key) VALUES($r,$f,$rr,$t,$p,$k)", tx,
            ("$r", runId), ("$f", null), ("$rr", null), ("$t", null), ("$p", null), ("$k", null));
        foreach (var row in rows)
        {
            cmd.Parameters["$f"].Value = row.FileId;
            cmd.Parameters["$rr"].Value = (object?)row.RuleRow ?? DBNull.Value;
            cmd.Parameters["$t"].Value = (object?)row.TargetIndex ?? DBNull.Value;
            cmd.Parameters["$p"].Value = (object?)row.NewPath ?? DBNull.Value;
            cmd.Parameters["$k"].Value = (object?)PathKey(row.NewPath) ?? DBNull.Value;
            cmd.ExecuteNonQuery();
        }
    });

    /// <summary>Files of the source with id, path and size, for the preview.</summary>
    public IEnumerable<(long Id, string Path, long Size)> SourceFiles(long runId)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT id, path, size FROM files WHERE run_id=$r AND target_idx=0 AND path IS NOT NULL ORDER BY id", null, ("$r", runId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return (r.GetInt64(0), r.GetString(1), r.GetInt64(2));
    }

    public sealed record ConflictRow(int TargetIndex, string NewPath, string SourcePath, string With);

    /// <summary>
    /// MAP-4 with the inventory: moved files that would land on the same path in a target library — on each other
    /// or on a file already there.
    /// </summary>
    public IReadOnlyList<ConflictRow> PreviewConflicts(long runId, int take = 100_000) => Query("""
        WITH dup AS (
            SELECT target_idx, new_key FROM mapping_preview
            WHERE run_id=$r AND target_idx > 0 GROUP BY target_idx, new_key HAVING COUNT(*) > 1)
        SELECT m.target_idx t, m.new_path np, f.path sp, 'moved' w
        FROM dup CROSS JOIN mapping_preview m CROSS JOIN files f
        WHERE m.run_id=$r AND m.target_idx=dup.target_idx AND m.new_key=dup.new_key AND f.id=m.file_id
        UNION ALL
        SELECT m.target_idx, m.new_path, f.path, 'existing'
        FROM files e CROSS JOIN mapping_preview m CROSS JOIN files f
        WHERE e.run_id=$r AND e.target_idx > 0 AND e.path_key IS NOT NULL
          AND m.run_id=$r AND m.target_idx=e.target_idx AND m.new_key=e.path_key AND f.id=m.file_id
        ORDER BY 1, 2 LIMIT $take
        """, r => new ConflictRow((int)L(r, "t"), S(r, "np")!, S(r, "sp")!, S(r, "w")!), ("$r", runId), ("$take", take));

    /// <summary>Preview rows with the source path, in file order (keyset paging for exports).</summary>
    public IEnumerable<(string SourcePath, long Size, int? RuleRow, int? TargetIndex, string? NewPath)> PreviewRows(long runId)
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT f.path, f.size, m.rule_row, m.target_idx, m.new_path FROM mapping_preview m JOIN files f ON f.id = m.file_id
            WHERE m.run_id=$r ORDER BY m.file_id
            """, null, ("$r", runId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            yield return (r.GetString(0), r.GetInt64(1), r.IsDBNull(2) ? null : r.GetInt32(2), r.IsDBNull(3) ? null : r.GetInt32(3),
                r.IsDBNull(4) ? null : r.GetString(4));
        }
    }

    public long CountPreview(long runId) =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM mapping_preview WHERE run_id=$r", ("$r", runId)), CultureInfo.InvariantCulture);

    // ---------------- INV-5: inventory updated with changes only ----------------

    /// <summary>
    /// Starts an update of <paramref name="fromRun"/>: copies its listed files and folders of the libraries in
    /// <paramref name="libraries"/> (new index → index in the earlier run) and sets each to continue from the earlier
    /// delta link, so only changes are listed. Files without a published version are scanned again.
    /// </summary>
    public void CopyInventory(long fromRun, long toRun, IReadOnlyDictionary<int, int> libraries) => Write((c, tx) =>
    {
        foreach (var (to, from) in libraries)
        {
            Exec(c, """
                UPDATE targets SET (drive_id, drive_url, root_item_id, root_path, next_link, delta_link, done, checkouts, incremental) =
                    (SELECT drive_id, drive_url, root_item_id, root_path, delta_link, NULL, 0, 0, 1 FROM targets WHERE run_id=$f AND idx=$fi)
                WHERE run_id=$t AND idx=$ti
                """, tx, ("$f", fromRun), ("$fi", from), ("$t", toRun), ("$ti", to));
            Exec(c, """
                INSERT INTO files(run_id, target_idx, drive_id, item_id, parent_id, name, path, path_key, ext, size, etag, modified_at, modified_by,
                                  checked_out_by, status, status_code, updated_at)
                SELECT $t, $ti, drive_id, item_id, parent_id, name, path, path_key, ext, size, etag, modified_at, modified_by,
                       checked_out_by, status, status_code, updated_at
                FROM files WHERE run_id=$f AND target_idx=$fi AND status=$listed AND path IS NOT NULL
                """, tx, ("$f", fromRun), ("$fi", from), ("$t", toRun), ("$ti", to), ("$listed", FileStatuses.Listed));
            Exec(c, """
                INSERT OR IGNORE INTO folders(run_id, drive_id, item_id, parent_id, name, is_root)
                SELECT $t, drive_id, item_id, parent_id, name, is_root FROM folders
                WHERE run_id=$f AND drive_id=(SELECT drive_id FROM targets WHERE run_id=$f AND idx=$fi)
                """, tx, ("$f", fromRun), ("$fi", from), ("$t", toRun));
        }
    });

    /// <summary>Items reported as deleted: files and folders with these ids leave the run.</summary>
    public void DeleteItems(long runId, string driveId, IReadOnlyCollection<string> itemIds) => Write((c, tx) =>
    {
        foreach (string id in itemIds)
        {
            Exec(c, "DELETE FROM files WHERE run_id=$r AND drive_id=$d AND item_id=$i", tx, ("$r", runId), ("$d", driveId), ("$i", id));
            Exec(c, "DELETE FROM folders WHERE run_id=$r AND drive_id=$d AND item_id=$i", tx, ("$r", runId), ("$d", driveId), ("$i", id));
        }
    });

    /// <summary>Files of a library with their parent folder, name and stored path (streamed).</summary>
    public IEnumerable<(long Id, string? ParentId, string Name, string? Path)> FileNames(long runId, int targetIndex)
    {
        using var c = Open();
        using var cmd = Command(c, "SELECT id, parent_id, name, path FROM files WHERE run_id=$r AND target_idx=$t", null, ("$r", runId), ("$t", targetIndex));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return (r.GetInt64(0), r.IsDBNull(1) ? null : r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    /// <summary>New paths of files under renamed or moved folders; null — the file leaves the run (its folder is gone or outside the listed folder).</summary>
    public void SetPaths(IEnumerable<(long Id, string? Path)> paths, DateTimeOffset now) => Write((c, tx) =>
    {
        using var update = Command(c, "UPDATE files SET path=$p, path_key=$k, updated_at=$u WHERE id=$id", tx, ("$p", null), ("$k", null), ("$u", Now(now)), ("$id", null));
        using var delete = Command(c, "DELETE FROM files WHERE id=$id", tx, ("$id", null));
        foreach (var (id, path) in paths)
        {
            if (path is null)
            {
                delete.Parameters["$id"].Value = id;
                delete.ExecuteNonQuery();
                continue;
            }
            update.Parameters["$p"].Value = path;
            update.Parameters["$k"].Value = PathKey(path);
            update.Parameters["$id"].Value = id;
            update.ExecuteNonQuery();
        }
    });
}
