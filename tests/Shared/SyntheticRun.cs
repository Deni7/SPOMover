using Microsoft.Data.Sqlite;
using SPOMover.State;

namespace SPOMover.Tests.Shared;

/// <summary>
/// A large finished inventory written straight into the state database — for checking how the GUI and the API
/// behave on hundreds of thousands of files (NFR-2) without running SharePoint traffic.
/// </summary>
public static class SyntheticRun
{
    public static long Create(StateStore store, string dbPath, string profileName, string snapshot, int files, int seed = 1)
    {
        var started = DateTimeOffset.UtcNow.AddHours(-6);
        long runId = store.CreateRun(RunModes.Inventory, null, profileName, snapshot, enumerate: true, targets: 2, started);
        store.SetRunStatus(runId, RunStatuses.Running, started);
        var rnd = new Random(seed);
        string[] exts = [".xlsx", ".xlsx", ".docx", ".docx", ".pdf", ".pdf", ".pdf", ".msg", ".jpg", ".xlsb", ".zip", ""];
        string[] folders = ["Fin/2023", "Fin/2024", "Analitic", "Analitic/Q1", "Бухгалтерія/Звіти", "HR", "HR/Накази/2022", "Архів/Договори"];

        using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString());
        c.Open();
        using var tx = c.BeginTransaction();
        using var insertFile = c.CreateCommand();
        insertFile.Transaction = tx;
        insertFile.CommandText = """
            INSERT INTO files(run_id, target_idx, drive_id, item_id, name, path, ext, size, etag, modified_at, modified_by,
                              status, status_code, error_class, error, attempts, app, updated_at)
            VALUES($r,$t,$d,$i,$n,$p,$e,$s,'"{x},1"',$m,'Олена Коваль',$st,$st,$ec,$err,$a,'demo-app',$u)
            """;
        foreach (string p in new[] { "$r", "$t", "$d", "$i", "$n", "$p", "$e", "$s", "$m", "$st", "$ec", "$err", "$a", "$u" })
            insertFile.Parameters.Add(new SqliteParameter(p, DBNull.Value));

        for (int n = 0; n < files; n++)
        {
            string ext = exts[rnd.Next(exts.Length)];
            string folder = folders[rnd.Next(folders.Length)];
            string name = $"Документ {n:000000}{ext}";
            int target = rnd.Next(2);
            bool failed = rnd.Next(10_000) < 3;
            Set(insertFile, ("$r", runId), ("$t", target), ("$d", $"drive-{target + 1}"), ("$i", $"i{n}"), ("$n", name), ("$p", $"{folder}/{name}"),
                ("$e", ext), ("$s", 2_000 + rnd.Next(5_000_000)), ("$m", StateStore.Now(started.AddDays(-rnd.Next(2000)))),
                ("$st", failed ? "failed:internal" : FileStatuses.Listed), ("$ec", failed ? "internal" : null),
                ("$err", failed ? "Не вдалося визначити папку файлу з delta." : null), ("$a", 0), ("$u", StateStore.Now(DateTimeOffset.UtcNow)));
            insertFile.ExecuteNonQuery();
        }

        using var metrics = c.CreateCommand();
        metrics.Transaction = tx;
        metrics.CommandText = "INSERT INTO run_metrics(run_id, at, json) VALUES($r,$at,$j)";
        metrics.Parameters.Add(new SqliteParameter("$r", runId));
        metrics.Parameters.Add(new SqliteParameter("$at", DBNull.Value));
        metrics.Parameters.Add(new SqliteParameter("$j", DBNull.Value));
        for (int m = 0; m <= 360; m += 5)
        {
            long found = (long)(files * Math.Min(1.0, m / 360.0));
            long pages = found / 200;
            metrics.Parameters["$at"].Value = StateStore.Now(started.AddMinutes(m));
            metrics.Parameters["$j"].Value = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $$"""{"at":"{{started.AddMinutes(m):o}}","found":{{found}},"filesPerMinute":{{(m == 0 ? 0 : files / 360.0 * (0.8 + (rnd.NextDouble() * 0.4))):0.0}},"errors":{"transient":{{m / 60}}},"apps":[{"name":"demo-app","ruSpent":{{pages}},"ruLast24h":{{pages}},"minuteUtilization":0.2,"minuteCapacity":1000,"throttled429":{{m / 120}},"throttled503":0,"requests":{{pages}},"pausedUntil":null,"concurrencyLimit":8,"blocked":false}],"tenantSpent":{{pages}},"tenantUtilization":0.05}""");
            metrics.ExecuteNonQuery();
        }
        tx.Commit();
        store.SetRunStatus(runId, RunStatuses.CompletedWithErrors, DateTimeOffset.UtcNow);
        return runId;
    }

    private static void Set(SqliteCommand cmd, params (string Name, object? Value)[] values)
    {
        foreach (var (name, value) in values)
            cmd.Parameters[name].Value = value ?? DBNull.Value;
    }
}
