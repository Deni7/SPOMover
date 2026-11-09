using System.Globalization;
using static SPOMover.Core.Localization.L;

namespace SPOMover.State;

/// <summary>Stage 4: verify (files of an apply run against a later inventory), retry of chosen files, rules for SPOReplaceLink.</summary>
public sealed partial class StateStore
{
    /// <summary>
    /// verify: every moved file of the apply run must be in its target library at its new path and no longer in the
    /// source, both according to the inventory <paramref name="inventoryRun"/>. Files that pass get <c>verified</c>,
    /// the rest <c>failed:verify</c> with the reason. <paramref name="libraries"/>: library index in the apply run →
    /// its index in the inventory.
    /// </summary>
    public void VerifyApply(long applyRun, long inventoryRun, IReadOnlyDictionary<int, int> libraries, DateTimeOffset now) => Write((c, tx) =>
    {
        string stillInSource = T("Verify: the file is still in the source and not in the target.");
        string notInTarget = T("Verify: the file is not in the target at its new path.");
        string inBoth = T("Verify: the file is both in the target and in the source.");
        foreach (var (dest, inv) in libraries)
        {
            Exec(c, $"""
                UPDATE files SET
                    status = CASE WHEN x.there = 1 AND x.stays = 0 THEN '{FileStatuses.Verified}' ELSE 'failed:verify' END,
                    status_code = CASE WHEN x.there = 1 AND x.stays = 0 THEN '{FileStatuses.Verified}' ELSE 'failed:verify' END,
                    error_class = CASE WHEN x.there = 1 AND x.stays = 0 THEN NULL ELSE 'verify' END,
                    error = CASE
                        WHEN x.there = 1 AND x.stays = 0 THEN NULL
                        WHEN x.there = 0 AND x.stays = 1 THEN $stillInSource
                        WHEN x.there = 0 THEN $notInTarget
                        ELSE $inBoth END,
                    updated_at = $u
                FROM (SELECT f.id,
                        EXISTS(SELECT 1 FROM files i WHERE i.run_id=$inv AND i.target_idx=$ti AND i.path_key = ulower(f.new_path)) there,
                        EXISTS(SELECT 1 FROM files i WHERE i.run_id=$inv AND i.target_idx=0 AND i.path_key = f.path_key) stays
                      FROM files f WHERE f.run_id=$r AND f.dest_idx=$d) x
                WHERE files.id = x.id
                  AND files.status IN ('{FileStatuses.Moved}', '{FileStatuses.Verified}', 'failed:verify')
                """, tx, ("$u", Now(now)), ("$inv", inventoryRun), ("$ti", inv), ("$r", applyRun), ("$d", dest),
                ("$stillInSource", stillInSource), ("$notInTarget", notInTarget), ("$inBoth", inBoth));
        }
    });

    /// <summary>
    /// verify: files in the source inventory under folders that moved whole, which the apply run did not know (added
    /// after the plan): the source subtree is not empty. Path only, up to <paramref name="take"/>.
    /// </summary>
    public IReadOnlyList<(int Seq, string Path)> NewInSource(long applyRun, long inventoryRun, int take = 10_000) => Query($"""
        SELECT b.seq, i.path FROM apply_batches b JOIN files i
          ON i.run_id=$inv AND i.target_idx=0 AND i.path_key > ulower(b.source) || '/' AND i.path_key < ulower(b.source) || '0'
        WHERE b.run_id=$r AND b.kind='folder'
          AND NOT EXISTS (SELECT 1 FROM files f WHERE f.run_id=$r AND f.batch=b.seq AND f.path_key = i.path_key)
        ORDER BY b.seq, i.path_key LIMIT $take
        """, r => ((int)L(r, "seq"), S(r, "path")!), ("$inv", inventoryRun), ("$r", applyRun), ("$take", take));

    /// <summary>
    /// retry: chosen files of a finished apply run go back to pending, their batches too (attempts start over), and the
    /// run can be continued. Without classes or ids — every failed and deferred file. Returns how many files.
    /// </summary>
    public int ReopenApply(long applyRun, IReadOnlyCollection<string> errorClasses, IReadOnlyCollection<long> fileIds, DateTimeOffset now) => Write((c, tx) =>
    {
        string where = $"run_id=$r AND (status LIKE 'failed:%' OR status='{FileStatuses.Deferred}')";
        var args = new List<(string, object?)> { ("$r", applyRun), ("$u", Now(now)) };
        if (errorClasses.Count > 0)
        {
            where += $" AND error_class IN ({string.Join(",", errorClasses.Select((_, i) => "$c" + i))})";
            args.AddRange(errorClasses.Select((e, i) => ("$c" + i, (object?)e)));
        }
        if (fileIds.Count > 0)
        {
            where += $" AND id IN ({string.Join(",", fileIds.Select((_, i) => "$f" + i))})";
            args.AddRange(fileIds.Select((id, i) => ("$f" + i, (object?)id)));
        }
        Exec(c, $"""
            UPDATE apply_batches SET status='{BatchStatuses.Pending}', attempts=0, not_before=NULL, locked_since=NULL, error_class=NULL, error=NULL, finished_at=NULL
            WHERE run_id=$r AND seq IN (SELECT DISTINCT batch FROM files WHERE {where})
            """, tx, [.. args]);
        using var cmd = Command(c, $"UPDATE files SET status='{FileStatuses.Pending}', status_code='{FileStatuses.Pending}', error_class=NULL, error=NULL, updated_at=$u WHERE {where}", tx, [.. args]);
        int n = cmd.ExecuteNonQuery();
        if (n > 0)
            Exec(c, $"UPDATE runs SET status='{RunStatuses.Stopped}', stop_reason=$why, finished_at=NULL WHERE id=$r", tx, ("$r", applyRun), ("$why", T("Retry of chosen files")));
        return n;
    });

    /// <summary>MAP-8: moved units of an apply run — whole folders (batch done) and single moved files — with source and target paths.</summary>
    public IReadOnlyList<(int Seq, bool Folder, int DestIndex, string Source, string Target)> MovedUnits(long applyRun) => Query($"""
        SELECT seq, 1 folder, dest_idx, source, target FROM apply_batches
        WHERE run_id=$r AND kind='folder' AND status='{BatchStatuses.Done}'
        UNION ALL
        SELECT f.batch, 0, f.dest_idx, f.path, f.new_path FROM files f JOIN apply_batches b ON b.run_id=f.run_id AND b.seq=f.batch
        WHERE f.run_id=$r AND f.status IN ('{FileStatuses.Moved}', '{FileStatuses.Verified}') AND NOT (b.kind='folder' AND b.status='{BatchStatuses.Done}')
        ORDER BY 1, 4
        """, r => ((int)L(r, "seq"), L(r, "folder") == 1, (int)L(r, "dest_idx"), S(r, "source")!, S(r, "target")!), ("$r", applyRun));

    public long CountRunFiles(long runId, string status) =>
        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM files WHERE run_id=$r AND status=$s", ("$r", runId), ("$s", status)), CultureInfo.InvariantCulture);
}
