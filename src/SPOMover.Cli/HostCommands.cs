using System.Globalization;
using System.Text.Json.Nodes;
using SPOMover.Core.Localization;

namespace SPOMover.Cli;

/// <summary>Commands that work through the host: settings, runs, reports.</summary>
internal sealed class HostCommands(HostClient host, TextWriter output)
{
    public const int RunNotFinished = 3;

    // ------------------------------------------------------------------ settings

    public async Task<int> ListSettings(string kind)
    {
        var items = (await host.GetAsync($"api/{kind}"))!.AsArray();
        if (items.Count == 0)
            output.WriteLine(L.T("(empty)"));
        foreach (var i in items)
            output.WriteLine($"{i!["name"],-30} {i["updatedAt"]}");
        return ExitCodes.Ok;
    }

    public async Task<int> GetSetting(string kind, string name, string? file)
    {
        string json = await host.GetTextAsync($"api/{kind}/{Uri.EscapeDataString(name)}");
        if (file is null)
            output.WriteLine(json);
        else
            await File.WriteAllTextAsync(file, json);
        return ExitCodes.Ok;
    }

    /// <summary>Import from JSON (CFG-1). The name comes from --name, the "name" field, or the file name.</summary>
    public async Task<int> PutSetting(string kind, string file, string? name)
    {
        string json = await File.ReadAllTextAsync(file);
        name ??= (JsonNode.Parse(json, documentOptions: new() { CommentHandling = System.Text.Json.JsonCommentHandling.Skip, AllowTrailingCommas = true })?["name"]?.GetValue<string>())
            ?? Path.GetFileNameWithoutExtension(file).Split('.')[0];
        var result = await host.PutJsonTextAsync($"api/{kind}/{Uri.EscapeDataString(name)}", json);
        output.WriteLine(kind == "profiles" ? L.T($"Saved profile \"{name}\".") : L.T($"Saved connection \"{name}\"."));
        return PrintIssues(result?["issues"]?.AsArray());
    }

    public async Task<int> DeleteSetting(string kind, string name)
    {
        await host.DeleteAsync($"api/{kind}/{Uri.EscapeDataString(name)}");
        output.WriteLine(kind == "profiles" ? L.T($"Deleted profile \"{name}\".") : L.T($"Deleted connection \"{name}\"."));
        return ExitCodes.Ok;
    }

    public async Task<int> ValidateProfile(string name)
    {
        var result = (await host.PostAsync($"api/profiles/{Uri.EscapeDataString(name)}/validate"))!;
        int code = PrintIssues(result["issues"]?.AsArray());
        output.WriteLine(result["valid"]!.GetValue<bool>() ? L.T("The profile is ready to run.") : L.T("The profile has errors."));
        return result["valid"]!.GetValue<bool>() ? ExitCodes.Ok : Math.Max(code, ExitCodes.InvalidInput);
    }

    public async Task<int> TestConnection(string name, string? profile)
    {
        string query = profile is null ? string.Empty : "?profile=" + Uri.EscapeDataString(profile);
        var checks = (await host.PostAsync($"api/connections/{Uri.EscapeDataString(name)}/test{query}"))!.AsArray();
        bool ok = true;
        foreach (var c in checks)
        {
            bool passed = c!["ok"]!.GetValue<bool>();
            ok &= passed;
            output.WriteLine($"{(passed ? "OK  " : "FAIL")} {c["what"]}: {c["message"]}");
        }
        return ok ? ExitCodes.Ok : ExitCodes.Attention;
    }

    private int PrintIssues(JsonArray? issues)
    {
        bool errors = false;
        foreach (var i in issues ?? [])
        {
            string severity = i!["severity"]!.GetValue<string>();
            errors |= severity == "error";
            string rule = i["rule"] is { } r ? " " + L.T($"[rule {r}]") : string.Empty;
            string label = severity == "error" ? L.T("ERROR") : L.T("Warning");
            output.WriteLine($"{label}{rule}: {i["message"]}");
        }
        return errors ? ExitCodes.InvalidInput : ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ mapping (MAP-5…MAP-7)

    /// <summary>MAP-7: where each path goes.</summary>
    public async Task<int> MapTest(string profile, IReadOnlyList<string> paths)
    {
        string query = string.Join('&', paths.Select(p => "path=" + Uri.EscapeDataString(p)));
        foreach (var r in (await host.GetAsync($"api/profiles/{Uri.EscapeDataString(profile)}/mapping/test?{query}"))!.AsArray())
            output.WriteLine($"{r!["path"]} → {r["text"]}");
        return ExitCodes.Ok;
    }

    /// <summary>MAP-5: coverage against the last inventory; with a folder, the four mapping reports go there too.</summary>
    public async Task<int> MapCheck(string profile, string? directory, CancellationToken ct)
    {
        string p = Uri.EscapeDataString(profile);
        var result = (await host.PostAsync($"api/profiles/{p}/mapping/check", null, ct))!;
        output.Write(result["summary"]!.GetValue<string>());
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            foreach (string name in new[] { "coverage", "unmapped", "problems", "preview" })
            {
                await using var source = await host.GetStreamAsync($"api/profiles/{p}/mapping/reports/{name}", ct);
                await using var target = File.Create(Path.Combine(directory, $"mapping-{name}.csv"));
                await source.CopyToAsync(target, ct);
            }
            output.WriteLine(L.T($"Mapping reports: {Path.GetFullPath(directory)}"));
        }
        bool errors = result["issues"]!.AsArray().Any(i => i!["severity"]!.GetValue<string>() == "error");
        return errors ? ExitCodes.Attention : ExitCodes.Ok;
    }

    /// <summary>Before a move: are the target libraries ready (columns, content types, settings)? Code 2 with warnings.</summary>
    public async Task<int> CheckTargets(string profile, string? file, CancellationToken ct)
    {
        var result = (await host.GetAsync($"api/profiles/{Uri.EscapeDataString(profile)}/target-check", ct))!;
        output.Write(result["text"]!.GetValue<string>());
        if (file is not null)
        {
            var lines = new List<string> { "library,severity,kind,message" };
            foreach (var f in result["findings"]!.AsArray())
                lines.Add(string.Join(',', new[] { "libraryName", "severity", "kind", "message" }.Select(k => "\"" + f![k]!.GetValue<string>().Replace("\"", "\"\"", StringComparison.Ordinal) + "\"")));
            await File.WriteAllLinesAsync(file, lines, new System.Text.UTF8Encoding(true), ct);
            output.WriteLine(L.T($"Target check report: {Path.GetFullPath(file)}"));
        }
        return result["ok"]!.GetValue<bool>() ? ExitCodes.Ok : ExitCodes.Attention;
    }

    // ------------------------------------------------------------------ plan (PLN-1…PLN-5)

    /// <summary>Builds a plan from the last inventory; with a folder, the plan reports go there too.</summary>
    public async Task<int> PlanBuild(string profile, string? directory, CancellationToken ct)
    {
        var result = (await host.PostAsync($"api/profiles/{Uri.EscapeDataString(profile)}/plan", null, ct))!;
        output.Write(result["summary"]!.GetValue<string>());
        if (directory is not null)
            await PlanReports(result["plan"]!.GetValue<long>(), directory, ct);
        bool errors = result["issues"]!.AsArray().Any(i => i!["severity"]!.GetValue<string>() == "error");
        return errors ? ExitCodes.Attention : ExitCodes.Ok;
    }

    /// <summary>A saved plan: its summary and, with a folder, its reports.</summary>
    public async Task<int> PlanShow(long plan, string? directory, CancellationToken ct)
    {
        var result = (await host.GetAsync($"api/plans/{plan}"))!;
        output.Write(result["summary"]!.GetValue<string>());
        if (directory is not null)
            await PlanReports(plan, directory, ct);
        return ExitCodes.Ok;
    }

    private async Task PlanReports(long plan, string directory, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        foreach (string name in new[] { "batches", "waves", "folders", "conflicts", "files", "changes" })
        {
            await using var source = await host.GetStreamAsync($"api/plans/{plan}/reports/{name}", ct);
            await using var target = File.Create(Path.Combine(directory, $"plan{plan}-{name}.csv"));
            await source.CopyToAsync(target, ct);
        }
        output.WriteLine(L.T($"Plan reports: {Path.GetFullPath(directory)}"));
    }

    /// <summary>MAP-6: rows from CSV/TSV/XLSX into the profile (replace, or append with --append).</summary>
    public async Task<int> MapImport(string profile, string file, bool append)
    {
        string query = $"file={Uri.EscapeDataString(Path.GetFileName(file))}&append={(append ? "true" : "false")}";
        var result = (await host.PostBytesAsync($"api/profiles/{Uri.EscapeDataString(profile)}/mapping/import?{query}", await File.ReadAllBytesAsync(file)))!;
        foreach (var w in result["warnings"]!.AsArray())
            output.WriteLine(L.T($"Warning: {w}"));
        var (rows, total) = (result["rows"], result["total"]);
        output.WriteLine(L.T($"Rows imported: {rows}, total in the mapping: {total}."));
        return PrintIssues(result["issues"]?.AsArray());
    }

    public async Task<int> MapExport(string profile, string file)
    {
        await using var source = await host.GetStreamAsync($"api/profiles/{Uri.EscapeDataString(profile)}/mapping/csv");
        await using var target = File.Create(file);
        await source.CopyToAsync(target);
        output.WriteLine(L.T($"Mapping of profile \"{profile}\": {Path.GetFullPath(file)}"));
        return ExitCodes.Ok;
    }

    // ------------------------------------------------------------------ runs

    /// <summary>A run of a whole profile (stage 1: inventory).</summary>
    public async Task<int> StartRun(string mode, string profile, bool wait, CancellationToken ct, bool update = false)
    {
        var created = (await host.PostAsync("api/runs", new { mode, profile, start = true, update }, ct))!;
        long id = created["id"]!.GetValue<long>();
        output.WriteLine(update ? L.T($"Run {id} ({mode}, changes only) started.") : L.T($"Run {id} ({mode}) started."));
        return wait ? await Wait(id, ct) : ExitCodes.Ok;
    }

    /// <summary>Stage 4: moves a plan of the profile (the newest when <paramref name="plan"/> is null).</summary>
    public async Task<int> Apply(string profile, long? plan, bool wait, CancellationToken ct)
    {
        var created = (await host.PostAsync("api/runs", new { mode = "apply", profile, start = true, plan }, ct))!;
        long id = created["id"]!.GetValue<long>();
        output.WriteLine(plan is null ? L.T($"Run {id} (apply, newest plan) started.") : L.T($"Run {id} (apply, plan {plan}) started."));
        return wait ? await Wait(id, ct) : ExitCodes.Ok;
    }

    public async Task<int> Control(long runId, string action, bool wait, CancellationToken ct)
    {
        await host.PostAsync($"api/runs/{runId}/{action}", null, ct);
        output.WriteLine(action switch
        {
            "start" => L.T($"Run {runId} continued."),
            "pause" => L.T($"Run {runId} paused: no new files are taken."),
            "resume" => L.T($"Run {runId} continued after the pause."),
            _ => L.T($"Run {runId}: stopping after the current files finish."),
        });
        return wait ? await Wait(runId, ct) : ExitCodes.Ok;
    }

    /// <summary>Progress until the run reaches a final status. Ctrl+C asks the host for a graceful stop (STA-4).</summary>
    public async Task<int> Wait(long runId, CancellationToken ct)
    {
        bool stopSent = false;
        DateTimeOffset lastLine = DateTimeOffset.MinValue;
        while (true)
        {
            if (ct.IsCancellationRequested && !stopSent)
            {
                stopSent = true;
                output.WriteLine(L.T("Stopping: the current listing page is finishing…"));
                try
                {
                    await host.PostAsync($"api/runs/{runId}/stop");
                }
                catch (HostException)
                {
                }
            }
            var status = (await host.GetAsync($"api/runs/{runId}"))!;
            string state = status["run"]!["status"]!.GetValue<string>();
            bool active = status["active"]!.GetValue<bool>();
            if (DateTimeOffset.UtcNow - lastLine > TimeSpan.FromSeconds(5) || !active)
            {
                output.WriteLine(ProgressLine(status));
                lastLine = DateTimeOffset.UtcNow;
            }
            if (!active && state is not ("created" or "running" or "paused"))
            {
                output.WriteLine();
                output.Write(await host.GetTextAsync($"api/runs/{runId}/reports/summary"));
                return state switch
                {
                    "completed" => status["attention"]!.GetValue<long>() > 0 ? ExitCodes.Attention : ExitCodes.Ok,
                    "completed_with_errors" => ExitCodes.Attention,
                    _ => RunNotFinished,
                };
            }
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stopSent ? CancellationToken.None : ct);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    internal static string ProgressLine(JsonNode status)
    {
        var run = status["run"]!;
        long files = status["files"]!.GetValue<long>();
        long open = status["nonFinal"]!.GetValue<long>();
        long done = files - open;
        var counts = status["counts"]!.AsObject();
        long failed = counts.Where(c => c.Key.StartsWith("failed:", StringComparison.Ordinal)).Sum(c => c.Value!.GetValue<long>());
        long deferred = counts.TryGetPropertyValue("deferred", out var d) ? d!.GetValue<long>() : 0;
        string percent = files > 0 ? (100.0 * done / files).ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
        double gb = status["bytes"]!.GetValue<long>() / 1024.0 / 1024.0 / 1024.0;
        // An inventory: every listed file is final, so «done N (100%)» says nothing; the share of the libraries' items
        // (ItemCount from SharePoint) does, when it is known.
        string progress = L.T($"done {done} ({percent})");
        if (status["targets"] is JsonArray { Count: > 0 } targets)
        {
            var listing = targets.Where(t => !t!["done"]!.GetValue<bool>()).ToList();
            long Seen(JsonNode t) => t["files"]!.GetValue<long>() + t["folders"]!.GetValue<long>();
            if (listing.Count == 0)
                progress = L.T("listing complete");
            else if (listing.All(t => t!["expected"] is JsonValue e && e.GetValue<long>() > 0))
            {
                long seen = targets.Sum(t => t!["done"]!.GetValue<bool>() ? Seen(t) : Math.Min(Seen(t), t["expected"]!.GetValue<long>()));
                long total = targets.Sum(t => t!["done"]!.GetValue<bool>() ? Seen(t) : t["expected"]!.GetValue<long>());
                string share = Math.Min(99.9, 100.0 * seen / Math.Max(1, total)).ToString("0.0", CultureInfo.InvariantCulture);
                int library = listing[0]!["index"]!.GetValue<int>() + 1;
                progress = L.T($"scanned ≈ {share}% ({seen} of ≈ {total} items), library {library} of {targets.Count}");
            }
            else
            {
                int library = listing[0]!["index"]!.GetValue<int>() + 1;
                progress = L.T($"listing, library {library} of {targets.Count}");
            }
        }
        string state = status["paused"]!.GetValue<bool>() ? $"{run["status"]}, {L.T("paused")}" : $"{run["status"]}";
        string size = gb.ToString("0.00", CultureInfo.InvariantCulture);
        var attention = status["attention"];
        string line = L.T($"[{state}] files {files}, {size} GB, {progress}, errors {failed}, deferred {deferred}, need attention {attention}");
        if (status["live"] is JsonObject live)
        {
            double perMinute = live["filesPerMinute"]!.GetValue<double>();
            line += ", " + L.T($"{perMinute:0} files/min");
            foreach (var app in live["apps"]!.AsArray())
            {
                string paused = app!["pausedUntil"] is null ? string.Empty : " " + L.T("paused");
                line += $" | {app["name"]}: {app["ruSpent"]} RU, 429/503 {app["throttled429"]}/{app["throttled503"]}{paused}";
            }
        }
        return line;
    }

    public async Task<int> Status(long runId)
    {
        var status = (await host.GetAsync($"api/runs/{runId}"))!;
        output.WriteLine(ProgressLine(status));
        if (status["run"]!["stopReason"] is { } reason)
            output.WriteLine(L.T($"Stop reason: {reason}"));
        foreach (var (code, n) in status["counts"]!.AsObject().OrderBy(c => c.Key, StringComparer.Ordinal))
            output.WriteLine($"  {code,-28} {n}");
        return ExitCodes.Ok;
    }

    public async Task<int> Runs(int take)
    {
        foreach (var r in (await host.GetAsync($"api/runs?take={take}"))!.AsArray())
        {
            output.WriteLine($"{r!["id"],6} {r["mode"],-10} {r["status"],-22} {r["profileName"],-20} {r["createdAt"]}");
        }
        return ExitCodes.Ok;
    }

    public async Task<int> Files(long runId, string? status, string? errorClass, string? search, int take)
    {
        string query = $"take={take}";
        if (status is not null)
            query += "&status=" + Uri.EscapeDataString(status);
        if (errorClass is not null)
            query += "&errorClass=" + Uri.EscapeDataString(errorClass);
        if (search is not null)
            query += "&search=" + Uri.EscapeDataString(search);
        var page = (await host.GetAsync($"api/runs/{runId}/files?{query}"))!;
        foreach (var f in page["items"]!.AsArray())
            output.WriteLine($"{f!["id"],8} {f["statusCode"],-24} {f["path"]}{(f["error"] is { } e ? "  — " + e : string.Empty)}");
        var matching = page["total"];
        output.WriteLine(L.T($"Total matching the filter: {matching}"));
        return ExitCodes.Ok;
    }

    /// <summary>Deletes a run that is not running, with everything it recorded; asks first unless --yes.</summary>
    public async Task<int> DeleteRun(long runId, bool yes, CancellationToken ct)
    {
        var status = (await host.GetAsync($"api/runs/{runId}", ct))!;
        var run = status["run"]!;
        long written = status["counts"]!.AsObject().TryGetPropertyValue("done", out var d) ? d!.GetValue<long>() : 0;
        var (mode, state, files) = (run["mode"], run["status"], status["files"]);
        string question = L.T($"Delete run {runId} ({mode}, {state}, {files} files)?");
        if (written > 0)
            question += " " + L.T($"It wrote {written} files to SharePoint: the log of these changes and the versions before writing (for rollback) will be lost.");
        if (!Confirm(question, yes))
            return ExitCodes.InvalidInput;
        await host.DeleteAsync($"api/runs/{runId}", ct);
        output.WriteLine(L.T($"Run {runId} deleted."));
        return ExitCodes.Ok;
    }

    public async Task<int> Ignore(long fileId, string comment)
    {
        await host.PostAsync($"api/files/{fileId}/ignore", new { comment });
        output.WriteLine(L.T($"File {fileId} marked as ignored."));
        return ExitCodes.Ok;
    }

    /// <summary>
    /// verify: the apply run against an inventory made after it; with <paramref name="update"/> an inventory with
    /// changes only runs first. With a folder, the verify report goes there.
    /// </summary>
    public async Task<int> Verify(long runId, bool update, string? directory, CancellationToken ct)
    {
        if (update)
        {
            string profile = (await host.GetAsync($"api/runs/{runId}"))!["run"]!["profileName"]!.GetValue<string>();
            int code = await StartRun("inventory", profile, wait: true, ct, update: true);
            if (code is not (ExitCodes.Ok or ExitCodes.Attention))
                return code;
        }
        var result = (await host.PostAsync($"api/runs/{runId}/verify", null, ct))!;
        output.Write(result["summary"]!.GetValue<string>());
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
            await using var source = await host.GetStreamAsync($"api/runs/{runId}/apply-reports/verify", ct);
            await using var target = File.Create(Path.Combine(directory, $"run-{runId}-verify.csv"));
            await source.CopyToAsync(target, ct);
            output.WriteLine(L.T($"Verify report: {Path.GetFullPath(directory)}"));
        }
        return result["ok"]!.GetValue<bool>() ? ExitCodes.Ok : ExitCodes.Attention;
    }

    /// <summary>retry: chosen failed or deferred files of an apply run are moved again.</summary>
    public async Task<int> Retry(long runId, IReadOnlyList<string> classes, IReadOnlyList<long> files, bool wait, CancellationToken ct)
    {
        var result = (await host.PostAsync($"api/runs/{runId}/retry", new { classes, files, start = true }, ct))!;
        var retried = result["files"];
        output.WriteLine(L.T($"Run {runId}: retry of {retried} files started."));
        return wait ? await Wait(runId, ct) : ExitCodes.Ok;
    }

    /// <summary>
    /// EXE-7: after a verified move, the source folders it emptied go to the site recycle bin (a cleanup run).
    /// Asks first unless <paramref name="yes"/>.
    /// </summary>
    public async Task<int> CleanupFolders(long runId, bool yes, bool wait, CancellationToken ct)
    {
        var preview = (await host.GetAsync($"api/runs/{runId}/cleanup", ct))!;
        if (preview["blocker"] is JsonValue blocker)
        {
            output.WriteLine(blocker.GetValue<string>());
            return ExitCodes.Attention;
        }
        var folders = preview["folders"];
        if (!Confirm(L.T($"Check {folders} source folders of run {runId} and delete the empty ones to the site recycle bin?"), yes))
            return ExitCodes.InvalidInput;
        long id = (await host.PostAsync($"api/runs/{runId}/cleanup", null, ct))!["id"]!.GetValue<long>();
        string cleanup = "cleanup";
        output.WriteLine(L.T($"Run {id} ({cleanup}) started."));
        return wait ? await Wait(id, ct) : ExitCodes.Ok;
    }

    /// <summary>Stage 6: the pilot report of an apply run (speed, load, locks, forecast); printed, and saved with a file.</summary>
    public async Task<int> PilotReport(long runId, string? file, CancellationToken ct)
    {
        string text = await host.GetTextAsync($"api/runs/{runId}/apply-reports/pilot", ct);
        output.Write(text.TrimStart('﻿'));
        if (file is not null)
        {
            await File.WriteAllTextAsync(file, text.TrimStart('﻿'), new System.Text.UTF8Encoding(true), ct);
            output.WriteLine(L.T($"Pilot report: {Path.GetFullPath(file)}"));
        }
        return ExitCodes.Ok;
    }

    /// <summary>MAP-8: rules for SPOReplaceLink from an apply run, as CSV (old,new,name).</summary>
    public async Task<int> ExportRules(long runId, string file, CancellationToken ct)
    {
        await using (var source = await host.GetStreamAsync($"api/runs/{runId}/apply-reports/rules", ct))
        await using (var target = File.Create(file))
            await source.CopyToAsync(target, ct);
        int rules = File.ReadLines(file).Count() - 1;
        output.WriteLine(L.T($"Rules for SPOReplaceLink: {rules} → {Path.GetFullPath(file)}"));
        return ExitCodes.Ok;
    }

    /// <summary>REP-2, REP-4, INV-6: all reports of a run into a folder.</summary>
    public async Task<int> Export(long runId, string directory)
    {
        Directory.CreateDirectory(directory);
        // Same list as ReportExporter.Names on the host.
        foreach (string name in new[] { "files", "folders", "checkouts", "attention", "summary" })
        {
            string file = Path.Combine(directory, name + (name is "summary" ? ".txt" : ".csv"));
            await using var source = await host.GetStreamAsync($"api/runs/{runId}/reports/{name}");
            await using var target = File.Create(file);
            await source.CopyToAsync(target);
        }
        output.WriteLine(L.T($"Reports of run {runId}: {Path.GetFullPath(directory)}"));
        return ExitCodes.Ok;
    }

    private bool Confirm(string question, bool yes)
    {
        if (yes)
            return true;
        if (Console.IsInputRedirected)
        {
            output.WriteLine(question + " " + L.T("To run without the question, add --yes."));
            return false;
        }
        output.Write(question + " [y/N] ");
        string? answer = Console.ReadLine();
        return answer is not null && (answer.Trim().Equals("y", StringComparison.OrdinalIgnoreCase) || answer.Trim().Equals("yes", StringComparison.OrdinalIgnoreCase)
            || answer.Trim().Equals("так", StringComparison.OrdinalIgnoreCase)); // i18n-ignore: a Ukrainian answer is accepted too
    }

    /// <summary>RTY-4: paths from a CSV — the "path" column, or the first column.</summary>
}
