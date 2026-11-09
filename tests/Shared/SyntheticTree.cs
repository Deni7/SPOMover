using Microsoft.Data.Sqlite;
using SPOMover.State;

namespace SPOMover.Tests.Shared;

/// <summary>
/// A finished inventory of a large library with a realistic folder tree (departments, years, months, a few huge
/// folders), written straight into the state database with path keys and folder totals — what the planner, the plan
/// page and an apply run need. For checking the response on 1.5 million files (stage 5) without SharePoint.
/// </summary>
public static class SyntheticTree
{
    public static readonly string[] Departments =
        ["Фінанси", "Кадри", "Юридичний", "Закупівлі", "ІТ", "Логістика", "Продажі", "Маркетинг", "Виробництво", "Якість", "Безпека", "Адміністрація"];

    /// <param name="libraries">Libraries of the run: the source first, then the targets (as the profile's Libraries()).</param>
    public static long Create(StateStore store, string dbPath, string profileName, string snapshot, int libraries, int files, string sourceDriveUrl, int seed = 1)
    {
        var started = DateTimeOffset.UtcNow.AddHours(-8);
        long runId = store.CreateRun(RunModes.Inventory, null, profileName, snapshot, enumerate: true, targets: libraries, started);
        store.SetRunStatus(runId, RunStatuses.Running, started);
        store.SetTargetDrive(runId, 0, "drive-src", sourceDriveUrl, "root", string.Empty);
        for (int i = 1; i < libraries; i++)
            store.SetTargetDrive(runId, i, $"drive-t{i}", sourceDriveUrl[..sourceDriveUrl.LastIndexOf('/')] + $"/Target{i}", "root", string.Empty);

        var rnd = new Random(seed);
        string[] exts = [".xlsx", ".xlsx", ".docx", ".pdf", ".pdf", ".msg", ".jpg", ".xls", ".zip"];
        var folders = new List<string>();
        foreach (string d in Departments)
        {
            for (int y = 2005; y <= 2024; y++)
            {
                for (int m = 1; m <= 12; m++)
                    folders.Add($"{d}/{y}/{m:00}");
                folders.Add($"{d}/{y}/Звіти");
            }
        }
        // Folders with tens of thousands of files right in them: they become files batches.
        string[] huge = ["Сканування/Вхідні", "Сканування/Вихідні", "Фінанси/Первинні документи"];
        var stats = new Dictionary<string, (long Files, long Bytes, long Direct, int MaxPath)>(StringComparer.Ordinal) { [string.Empty] = (0, 0, 0, 0) };

        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        c.Open();
        using var tx = c.BeginTransaction();
        using var insert = c.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = """
            INSERT INTO files(run_id, target_idx, drive_id, item_id, parent_id, name, path, path_key, ext, size, etag, modified_at, modified_by, status, status_code, updated_at)
            VALUES($r,0,'drive-src',$i,$p,$n,$path,$key,$e,$s,'"{x},1"',$m,'Олена Коваль','listed','listed',$u)
            """;
        foreach (string p in new[] { "$r", "$i", "$p", "$n", "$path", "$key", "$e", "$s", "$m", "$u" })
            insert.Parameters.Add(new SqliteParameter(p, DBNull.Value));
        insert.Parameters["$r"].Value = runId;
        insert.Parameters["$u"].Value = StateStore.Now(DateTimeOffset.UtcNow);

        for (int n = 0; n < files; n++)
        {
            string folder = rnd.Next(100) < 4 ? huge[rnd.Next(huge.Length)] : folders[rnd.Next(folders.Count)];
            string ext = exts[rnd.Next(exts.Length)];
            string name = $"Документ {n:0000000}{ext}";
            string path = folder + "/" + name;
            long size = 5_000 + rnd.Next(800_000);
            insert.Parameters["$i"].Value = "i" + n;
            insert.Parameters["$p"].Value = "f:" + folder;
            insert.Parameters["$n"].Value = name;
            insert.Parameters["$path"].Value = path;
            insert.Parameters["$key"].Value = StateStore.PathKey(path);
            insert.Parameters["$e"].Value = ext;
            insert.Parameters["$s"].Value = size;
            insert.Parameters["$m"].Value = StateStore.Now(started.AddDays(-rnd.Next(5000)));
            insert.ExecuteNonQuery();
            bool direct = true;
            for (string dir = folder; ; dir = dir.Contains('/') ? dir[..dir.LastIndexOf('/')] : string.Empty)
            {
                stats.TryGetValue(dir, out var st);
                stats[dir] = (st.Files + 1, st.Bytes + size, st.Direct + (direct ? 1 : 0), Math.Max(st.MaxPath, path.Length));
                direct = false;
                if (dir.Length == 0)
                    break;
            }
        }
        tx.Commit();
        store.SaveFolderStats(runId, 0, stats.Select(x => new StateStore.FolderStat(0, x.Key, x.Key.Length == 0 ? 0 : x.Key.Count(ch => ch == '/') + 1,
            x.Value.Files, x.Value.Bytes, x.Value.Direct, x.Value.MaxPath)));
        for (int i = 1; i < libraries; i++)
            store.SaveFolderStats(runId, i, [new StateStore.FolderStat(i, string.Empty, 0, 0, 0, 0, 0)]);
        store.SaveDeltaPage(runId, 0, [], [], null, "delta", done: true, DateTimeOffset.UtcNow);
        for (int i = 1; i < libraries; i++)
            store.SaveDeltaPage(runId, i, [], [], null, "delta", done: true, DateTimeOffset.UtcNow);
        store.SetCheckoutState(runId, 0, CheckoutScan.Done);
        store.SetRunStatus(runId, RunStatuses.Completed, DateTimeOffset.UtcNow);
        return runId;
    }
}
