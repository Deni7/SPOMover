using System.Globalization;
using SPOMover.Core.Planning;

namespace SPOMover.State;

/// <summary>PLN-1…PLN-5: plans built from an inventory run. Every build is kept as a snapshot (PLN-4).</summary>
public sealed partial class StateStore
{
    private const string PlanSchema = """
        CREATE TABLE IF NOT EXISTS plans(
            id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL, profile_name TEXT, created_at TEXT NOT NULL,
            mapping_json TEXT NOT NULL, options_json TEXT NOT NULL,
            batches INTEGER NOT NULL, files INTEGER NOT NULL, bytes INTEGER NOT NULL,
            stay_files INTEGER NOT NULL, stay_bytes INTEGER NOT NULL, conflicts INTEGER NOT NULL, folders INTEGER NOT NULL);
        CREATE INDEX IF NOT EXISTS ix_plans_profile ON plans(profile_name, id);
        CREATE TABLE IF NOT EXISTS plan_batches(
            plan_id INTEGER NOT NULL, seq INTEGER NOT NULL, wave INTEGER NOT NULL, kind TEXT NOT NULL, rule_row INTEGER NOT NULL,
            library TEXT NOT NULL, source TEXT NOT NULL, target TEXT NOT NULL,
            files INTEGER NOT NULL, bytes INTEGER NOT NULL, items INTEGER NOT NULL, checked_out INTEGER NOT NULL,
            PRIMARY KEY(plan_id, seq));
        CREATE TABLE IF NOT EXISTS plan_batch_files(
            plan_id INTEGER NOT NULL, seq INTEGER NOT NULL, file_id INTEGER NOT NULL, path TEXT NOT NULL, new_path TEXT NOT NULL, size INTEGER NOT NULL,
            PRIMARY KEY(plan_id, file_id));
        CREATE INDEX IF NOT EXISTS ix_plan_batch_files ON plan_batch_files(plan_id, seq);
        CREATE TABLE IF NOT EXISTS plan_folders(plan_id INTEGER NOT NULL, ord INTEGER NOT NULL, library TEXT NOT NULL, path TEXT NOT NULL, PRIMARY KEY(plan_id, ord));
        CREATE TABLE IF NOT EXISTS plan_conflicts(
            plan_id INTEGER NOT NULL, file_id INTEGER NOT NULL, path TEXT NOT NULL, library TEXT NOT NULL, new_path TEXT NOT NULL, reason TEXT NOT NULL,
            PRIMARY KEY(plan_id, file_id));
        CREATE TABLE IF NOT EXISTS plan_waves(
            plan_id INTEGER NOT NULL, number INTEGER NOT NULL, rule_row INTEGER NOT NULL, title TEXT NOT NULL,
            batches INTEGER NOT NULL, files INTEGER NOT NULL, bytes INTEGER NOT NULL, PRIMARY KEY(plan_id, number));
        """;

    public sealed record PlanRow(
        long Id, long RunId, string? ProfileName, DateTimeOffset CreatedAt, string MappingJson, string OptionsJson,
        long Batches, long Files, long Bytes, long StayFiles, long StayBytes, long Conflicts, long Folders);

    /// <summary>Files of the source for the planner: in path order (stable result), with check-outs.</summary>
    public IEnumerable<PlanFile> PlanSourceFiles(long runId)
    {
        using var c = Open();
        using var cmd = Command(c, """
            SELECT id, path, size, checked_out_by FROM files WHERE run_id=$r AND target_idx=0 AND path IS NOT NULL ORDER BY path_key, id
            """, null, ("$r", runId));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            yield return new PlanFile(r.GetInt64(0), r.GetString(1), r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3));
    }

    /// <summary>Saves a built plan as a new snapshot and returns its id.</summary>
    public long SavePlan(long runId, string? profileName, string mappingJson, string optionsJson, PlanResult plan, DateTimeOffset now) => Write((c, tx) =>
    {
        using (var head = Command(c, """
            INSERT INTO plans(run_id, profile_name, created_at, mapping_json, options_json, batches, files, bytes, stay_files, stay_bytes, conflicts, folders)
            VALUES($r,$p,$t,$m,$o,$b,$f,$by,$sf,$sb,$c,$fo) RETURNING id
            """, tx, ("$r", runId), ("$p", profileName), ("$t", Now(now)), ("$m", mappingJson), ("$o", optionsJson),
            ("$b", plan.Batches.Count), ("$f", plan.Batches.Sum(b => b.Files)), ("$by", plan.Batches.Sum(b => b.Bytes)),
            ("$sf", plan.StayFiles), ("$sb", plan.StayBytes), ("$c", plan.Conflicts.Count), ("$fo", plan.Folders.Count)))
        {
            long id = (long)head.ExecuteScalar()!;
            using var batch = Command(c, """
                INSERT INTO plan_batches(plan_id, seq, wave, kind, rule_row, library, source, target, files, bytes, items, checked_out)
                VALUES($p,$s,$w,$k,$rr,$l,$src,$tg,$f,$b,$i,$co)
                """, tx, ("$p", id), ("$s", null), ("$w", null), ("$k", null), ("$rr", null), ("$l", null), ("$src", null), ("$tg", null),
                ("$f", null), ("$b", null), ("$i", null), ("$co", null));
            using var file = Command(c, "INSERT INTO plan_batch_files(plan_id, seq, file_id, path, new_path, size) VALUES($p,$s,$f,$pa,$n,$z)", tx,
                ("$p", id), ("$s", null), ("$f", null), ("$pa", null), ("$n", null), ("$z", null));
            foreach (var b in plan.Batches)
            {
                batch.Parameters["$s"].Value = b.Seq;
                batch.Parameters["$w"].Value = b.Wave;
                batch.Parameters["$k"].Value = b.Kind == BatchKind.Folder ? "folder" : "files";
                batch.Parameters["$rr"].Value = b.RuleRow;
                batch.Parameters["$l"].Value = b.Library;
                batch.Parameters["$src"].Value = b.Source;
                batch.Parameters["$tg"].Value = b.TargetPath;
                batch.Parameters["$f"].Value = b.Files;
                batch.Parameters["$b"].Value = b.Bytes;
                batch.Parameters["$i"].Value = b.Items;
                batch.Parameters["$co"].Value = b.CheckedOut;
                batch.ExecuteNonQuery();
                foreach (var f in b.FileList)
                {
                    file.Parameters["$s"].Value = b.Seq;
                    file.Parameters["$f"].Value = f.FileId;
                    file.Parameters["$pa"].Value = f.Path;
                    file.Parameters["$n"].Value = f.NewPath;
                    file.Parameters["$z"].Value = f.Size;
                    file.ExecuteNonQuery();
                }
            }
            using var folder = Command(c, "INSERT INTO plan_folders(plan_id, ord, library, path) VALUES($p,$o,$l,$pa)", tx,
                ("$p", id), ("$o", null), ("$l", null), ("$pa", null));
            int ord = 0;
            foreach (var f in plan.Folders)
            {
                folder.Parameters["$o"].Value = ++ord;
                folder.Parameters["$l"].Value = f.Library;
                folder.Parameters["$pa"].Value = f.Path;
                folder.ExecuteNonQuery();
            }
            using var conflict = Command(c, "INSERT INTO plan_conflicts(plan_id, file_id, path, library, new_path, reason) VALUES($p,$f,$pa,$l,$n,$r)", tx,
                ("$p", id), ("$f", null), ("$pa", null), ("$l", null), ("$n", null), ("$r", null));
            foreach (var x in plan.Conflicts)
            {
                conflict.Parameters["$f"].Value = x.FileId;
                conflict.Parameters["$pa"].Value = x.Path;
                conflict.Parameters["$l"].Value = x.Library;
                conflict.Parameters["$n"].Value = x.NewPath;
                conflict.Parameters["$r"].Value = x.Reason;
                conflict.ExecuteNonQuery();
            }
            using var wave = Command(c, "INSERT INTO plan_waves(plan_id, number, rule_row, title, batches, files, bytes) VALUES($p,$n,$rr,$t,$b,$f,$by)", tx,
                ("$p", id), ("$n", null), ("$rr", null), ("$t", null), ("$b", null), ("$f", null), ("$by", null));
            foreach (var w in plan.Waves)
            {
                wave.Parameters["$n"].Value = w.Number;
                wave.Parameters["$rr"].Value = w.RuleRow;
                wave.Parameters["$t"].Value = w.Title;
                wave.Parameters["$b"].Value = w.Batches;
                wave.Parameters["$f"].Value = w.Files;
                wave.Parameters["$by"].Value = w.Bytes;
                wave.ExecuteNonQuery();
            }
            return id;
        }
    });

    private static PlanRow MapPlan(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        L(r, "id"), L(r, "run_id"), S(r, "profile_name"), Time(S(r, "created_at")!)!.Value, S(r, "mapping_json")!, S(r, "options_json")!,
        L(r, "batches"), L(r, "files"), L(r, "bytes"), L(r, "stay_files"), L(r, "stay_bytes"), L(r, "conflicts"), L(r, "folders"));

    public PlanRow? GetPlan(long planId) =>
        Query("SELECT * FROM plans WHERE id=$p", MapPlan, ("$p", planId)).FirstOrDefault();

    /// <summary>Snapshots of a profile, newest first.</summary>
    public IReadOnlyList<PlanRow> ListPlans(string profileName, int take = 50) =>
        Query("SELECT * FROM plans WHERE profile_name=$n COLLATE NOCASE ORDER BY id DESC LIMIT $t", MapPlan, ("$n", profileName), ("$t", take));

    public IReadOnlyList<PlanBatch> GetPlanBatches(long planId) => Query("SELECT * FROM plan_batches WHERE plan_id=$p ORDER BY seq",
        r => new PlanBatch((int)L(r, "seq"), (int)L(r, "wave"), S(r, "kind") == "folder" ? BatchKind.Folder : BatchKind.Files, (int)L(r, "rule_row"),
            S(r, "library")!, S(r, "source")!, S(r, "target")!, L(r, "files"), L(r, "bytes"), L(r, "items"), (int)L(r, "checked_out"), []),
        ("$p", planId));

    /// <summary>Files of the files batches; <paramref name="seq"/> limits them to one batch.</summary>
    public IReadOnlyList<(int Seq, PlanBatchFile File)> GetPlanBatchFiles(long planId, int? seq = null) => Query(
        "SELECT * FROM plan_batch_files WHERE plan_id=$p AND ($s IS NULL OR seq=$s) ORDER BY seq, path",
        r => ((int)L(r, "seq"), new PlanBatchFile(L(r, "file_id"), S(r, "path")!, S(r, "new_path")!, L(r, "size"))), ("$p", planId), ("$s", seq));

    public IReadOnlyList<PlanTargetFolder> GetPlanFolders(long planId) =>
        Query("SELECT library, path FROM plan_folders WHERE plan_id=$p ORDER BY ord", r => new PlanTargetFolder(S(r, "library")!, S(r, "path")!), ("$p", planId));

    public IReadOnlyList<PlanConflict> GetPlanConflicts(long planId) => Query("SELECT * FROM plan_conflicts WHERE plan_id=$p ORDER BY path",
        r => new PlanConflict(L(r, "file_id"), S(r, "path")!, S(r, "library")!, S(r, "new_path")!, S(r, "reason")!), ("$p", planId));

    public IReadOnlyList<PlanWave> GetPlanWaves(long planId) => Query("SELECT * FROM plan_waves WHERE plan_id=$p ORDER BY number",
        r => new PlanWave((int)L(r, "number"), (int)L(r, "rule_row"), S(r, "title")!, (int)L(r, "batches"), L(r, "files"), L(r, "bytes")), ("$p", planId));

    public long CountPlans() => Convert.ToInt64(Scalar("SELECT COUNT(*) FROM plans"), CultureInfo.InvariantCulture);
}
