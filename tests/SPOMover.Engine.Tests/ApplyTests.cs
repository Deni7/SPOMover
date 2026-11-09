using SPOMover.Core.Config;
using SPOMover.Core.Mapping;
using SPOMover.Spo.Graph;
using SPOMover.State;
using SPOMover.Tests.Shared;
using static SPOMover.Engine.Tests.EngineEnv;

namespace SPOMover.Engine.Tests;

/// <summary>Stage 4: the apply mode moves the batches of a plan (EXE-1…EXE-8) on the fake tenant.</summary>
public class ApplyTests
{
    private static MappingEntry E(string source, string library, string? path = null) => new() { Source = source, Library = library, Path = path };

    /// <summary>Inventory, plan and an apply run of it.</summary>
    private static async Task<long> Prepare(EngineEnv env, Profile p)
    {
        p.Retry.FilePausesMinutes = [0, 0, 0];
        p.Retry.LockedWaveMinutes = 0.001;
        long inventory = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(inventory));
        Planner.Build(env.Store, p, DateTimeOffset.UtcNow);
        return env.Service.CreateApplyRun(p.Name!);
    }

    private static string? PathIn(FakeGraph.FakeDrive drive, FakeGraph.FakeItem item)
    {
        var parts = new List<string>();
        for (var x = item; !x.IsRoot; x = drive.Items[x.ParentId!])
            parts.Insert(0, x.Name);
        return string.Join('/', parts);
    }

    private static List<string> Files(FakeGraph.FakeDrive drive) =>
        drive.Items.Values.Where(i => !i.IsFolder).Select(i => PathIn(drive, i)!).Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task APlanIsMovedWholeFoldersInOneRequestFilesOneByOneWithTheirNewNames()
    {
        await using var env = new EngineEnv();
        var hr = env.Graph.AddLibrary("/sites/archive", "Кадри", "HR");
        env.AddFile("Фін/2023/a.xlsx");
        env.AddFile("Фін/2023/Q1/b.xlsx");
        env.AddFile("Фін/c.pdf");
        env.AddFile("Фін/старе.docx");
        env.AddFile("HR/Накази/1.pdf");
        env.AddFile("Інше/x.txt");
        var p = NewProfile();
        p.TargetLibraries = ["Фінанси", "Кадри"];
        p.Mapping = [E("Фін", "Фінанси", "Документи"), E("Фін/старе.docx", "Кадри", "Архів/старе 2019.docx"), E("HR", "Кадри", "/")];
        long run = await Prepare(env, p);
        Assert.Equal(RunModes.Apply, env.Store.GetRun(run)!.Mode);

        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(["Документи/2023/Q1/b.xlsx", "Документи/2023/a.xlsx", "Документи/c.pdf"], Files(env.Finance));
        Assert.Equal(["Архів/старе 2019.docx", "Накази/1.pdf"], Files(hr));
        Assert.Equal(["Інше/x.txt"], Files(env.Library));
        Assert.All(env.Store.GetApplyBatches(run), b => Assert.Equal(StateStore.BatchStatuses.Done, b.Status));
        Assert.All(env.Files(run), f => Assert.Equal(FileStatuses.Moved, f.Status));
        Assert.Equal(5, env.Files(run).Count);
        // Folders 2023 and Накази went in one request each; c.pdf and the renamed file one by one.
        Assert.Equal(["/HR/Накази", "/Фін/2023"], env.Graph.Moves.Where(m => m.Method == "MoveFolderByPath").Select(m => m.Source[m.Source.IndexOf("/Shared Documents", StringComparison.Ordinal)..]["/Shared Documents".Length..]).Order(StringComparer.Ordinal));
        Assert.Equal(2, env.Graph.Moves.Count(m => m.Method == "MoveFileByPath"));
        Assert.All(env.Store.GetApplyFolders(run), f => Assert.True(f.Done));
    }

    [Fact]
    public async Task AFolderOverTheThresholdIsMovedItemByItem()
    {
        await using var env = new EngineEnv();
        for (int i = 0; i < 3; i++)
            env.AddFile($"Фін/2023/f{i}.xlsx");
        env.AddFile("Фін/2023/Q1/b.xlsx");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        long run = await Prepare(env, p);
        env.Graph.ThresholdItems = 2; // the folder grew or the tenant counts differently: SharePoint refuses it

        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(["Фін/2023/Q1/b.xlsx", "Фін/2023/f0.xlsx", "Фін/2023/f1.xlsx", "Фін/2023/f2.xlsx"], Files(env.Finance));
        Assert.Empty(Files(env.Library));
        Assert.All(env.Files(run), f => Assert.Equal(FileStatuses.Moved, f.Status));
        Assert.True(env.Graph.Moves.Count(m => m.Method == "MoveFileByPath") >= 3);
    }

    [Fact]
    public async Task ANameTakenAfterThePlanIsAConflictAndNothingIsOverwritten()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/a.xlsx", 10);
        env.AddFile("Фін/b.xlsx", 10);
        env.Graph.AddFile(env.Finance, "Фін/інше.txt", new byte[1]); // Фін exists in the target: files go one by one
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        long run = await Prepare(env, p);
        var taken = env.Graph.AddFile(env.Finance, "Фін/a.xlsx", new byte[3]);

        Assert.Equal(RunStatuses.CompletedWithErrors, await env.RunAsync(run));
        Assert.Equal(3, taken.Content.Length);
        var files = env.Files(run).ToDictionary(f => f.Path!);
        Assert.Equal(FileStatuses.Failed("conflict"), files["Фін/a.xlsx"].Status);
        Assert.Equal(FileStatuses.Moved, files["Фін/b.xlsx"].Status);
        Assert.Equal(StateStore.BatchStatuses.Partial, Assert.Single(env.Store.GetApplyBatches(run)).Status);
        Assert.Contains("Фін/a.xlsx", Files(env.Library));
    }

    [Fact]
    public async Task ALockedFileIsLeftBehindDeferredAndMovedWhenItIsFree()
    {
        await using var env = new EngineEnv();
        env.Graph.FolderMoveLeavesLocked = true;
        env.AddFile("Фін/2023/a.xlsx");
        var locked = env.AddFile("Фін/2023/b.xlsx");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        long run = await Prepare(env, p);
        locked.CheckedOutBy = "Іван";
        int waves = 0;
        env.Graph.Faults.Add(r =>
        {
            // The owner checks the file in after the second try.
            if (r.Method == "POST" && r.Path.Contains("MoveFileByPath", StringComparison.Ordinal) && Interlocked.Increment(ref waves) == 2)
                locked.CheckedOutBy = null;
            return null;
        });

        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(["Фін/2023/a.xlsx", "Фін/2023/b.xlsx"], Files(env.Finance));
        Assert.Empty(Files(env.Library));
        Assert.All(env.Files(run), f => Assert.Equal(FileStatuses.Moved, f.Status));
        Assert.True(waves >= 2);
        Assert.Equal(StateStore.BatchStatuses.Done, Assert.Single(env.Store.GetApplyBatches(run)).Status);
    }

    [Fact]
    public async Task ALockThatDoesNotGoAwayEndsAsFailedLocked()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/a.xlsx");
        env.AddFile("Фін/b.xlsx").CheckedOutBy = "Іван";
        env.Graph.AddFile(env.Finance, "Фін/x.txt", new byte[1]);
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        p.Retry.LockedLimitMinutes = 0.002;
        long run = await Prepare(env, p);

        Assert.Equal(RunStatuses.CompletedWithErrors, await env.RunAsync(run));
        var files = env.Files(run).ToDictionary(f => f.Path!);
        Assert.Equal(FileStatuses.Failed("locked"), files["Фін/b.xlsx"].Status);
        Assert.Equal(FileStatuses.Moved, files["Фін/a.xlsx"].Status);
    }

    [Fact]
    public async Task AMoveWhoseAnswerWasLostIsCheckedInsteadOfRepeated()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx");
        env.AddFile("Фін/2024/b.xlsx");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси", "Нове")];
        long run = await Prepare(env, p);
        env.Graph.DropAnswerAfterMove = 1;

        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(["Нове/2023/a.xlsx", "Нове/2024/b.xlsx"], Files(env.Finance));
        Assert.All(env.Files(run), f => Assert.Equal(FileStatuses.Moved, f.Status));
        // One request for the folder: the lost answer was settled by looking at source and target.
        Assert.Single(env.Graph.Moves);
    }

    [Fact]
    public async Task ABatchLeftInProgressByACrashIsCheckedOnTheNextStart()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси", "Нове")];
        long run = await Prepare(env, p);
        // The host died right after SharePoint moved the folder.
        env.Store.StartBatch(run, 1, DateTimeOffset.UtcNow);
        env.Store.SetRunStatus(run, RunStatuses.Running, DateTimeOffset.UtcNow);
        Assert.True(env.Graph.MoveDirectly("MoveFolderByPath", Host + "/sites/archive/Shared Documents/Фін", Host + "/sites/archive/Finance/Нове"));

        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(["Нове/2023/a.xlsx"], Files(env.Finance));
        Assert.Equal(FileStatuses.Moved, Assert.Single(env.Files(run)).Status);
        Assert.Single(env.Graph.Moves); // only the move "before the crash"
    }

    [Fact]
    public async Task AnApplyRunNeedsAPlanOfTheProfileAndAValidMapping()
    {
        await using var env = new EngineEnv();
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        env.SaveProfile(p);
        Assert.Contains("плану", Assert.Throws<InvalidOperationException>(() => env.Service.CreateApplyRun(p.Name!)).Message);
        p.Mapping = [E("Фін", "Немає")];
        env.SaveProfile(p);
        Assert.Throws<KeyNotFoundException>(() => env.Service.CreateApplyRun(p.Name!, 999));
    }

    [Fact]
    public async Task VerifyNeedsALaterInventoryAndFindsWhatIsNotWhereItShouldBe()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx");
        env.AddFile("Фін/2023/b.xlsx");
        env.AddFile("Фін/c.pdf");
        env.Graph.AddFile(env.Finance, "Фін/x.txt", new byte[1]);
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        long run = await Prepare(env, p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Contains("після переміщення", Assert.Throws<InvalidOperationException>(() => ApplyResults.Verify(env.Store, run, DateTimeOffset.UtcNow)).Message);

        // After the move: someone deletes a moved file in the target and drops a new file into a moved folder's old place.
        env.Graph.Remove(env.Finance, env.Finance.Items.Values.Single(i => i.Name == "b.xlsx"));
        env.AddFile("Фін/2023/новий.xlsx");
        long update = env.Service.CreateRun(RunModes.Inventory, p.Name, update: true);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(update));

        var report = ApplyResults.Verify(env.Store, run, DateTimeOffset.UtcNow);
        Assert.Equal((2L, 1L), (report.Verified, report.Failed));
        Assert.Equal("Фін/2023/новий.xlsx", Assert.Single(report.NewInSource).Path);
        Assert.False(report.Ok);
        var files = env.Files(run).ToDictionary(f => f.Path!);
        Assert.Equal("failed:verify", files["Фін/2023/b.xlsx"].Status);
        Assert.Contains("у цілі за новим шляхом файлу немає", files["Фін/2023/b.xlsx"].Error);
        Assert.Equal(FileStatuses.Verified, files["Фін/c.pdf"].Status);
        var rows = ApplyResults.VerifyRows(env.Store, run, report).Skip(1).Select(r => (r[0], r[1])).ToList();
        Assert.Equal([("not_verified", "Фін/2023/b.xlsx"), ("new_in_source", "Фін/2023/новий.xlsx")], rows);
    }

    [Fact]
    public async Task CleanupDeletesOnlyTheSourceFoldersTheMoveEmptiedAndOnlyAfterVerify()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx");
        env.AddFile("Фін/2023/Q1/b.xlsx");
        env.AddFile("Фін/c.pdf");
        env.AddFile("Фін/Чернетки/d.docx");
        env.AddFile("Інше/порожня/x.txt");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси"), E("Фін/Чернетки", MappingEntry.Stay)];
        long run = await Prepare(env, p);
        // The folder 2023 cannot move whole: its files go one by one and the folder stays behind, empty.
        env.Graph.ThresholdItems = 1;
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Contains(env.Library.Items.Values, i => i.IsFolder && i.Name == "2023");

        // Not before verify: a folder is deleted only if empty, but an unconfirmed file may still be in it.
        Assert.Contains("Звірено не все", env.Service.PreviewCleanup(run).Blocker);
        Assert.Throws<InvalidOperationException>(() => env.Service.CreateCleanupRun(run));
        long update = env.Service.CreateRun(RunModes.Inventory, p.Name, update: true);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(update));
        Assert.True(ApplyResults.Verify(env.Store, run, DateTimeOffset.UtcNow).Ok);

        // Folders above moved files, up to the rule's folder: never «Інше», never the library root.
        Assert.Equal(["Фін/2023/Q1", "Фін/2023", "Фін"], RunEngine.CleanupCandidates(env.Store, run));
        var preview = env.Service.PreviewCleanup(run);
        Assert.Null(preview.Blocker);
        Assert.Equal(3, preview.Folders);
        long cleanup = env.Service.CreateCleanupRun(run);
        Assert.Equal((RunModes.Cleanup, (long?)run), (env.Store.GetRun(cleanup)!.Mode, env.Store.GetRun(cleanup)!.ParentId));
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(cleanup));

        var rows = env.Files(cleanup).ToDictionary(f => f.Path!);
        Assert.Equal(FileStatuses.SkippedNotFound, rows["Фін/2023/Q1"].Status); // moved whole with its files
        Assert.Equal(FileStatuses.Deleted, rows["Фін/2023"].Status);
        Assert.Equal(FileStatuses.SkippedNotEmpty, rows["Фін"].Status);           // Чернетки stayed by the rule
        Assert.Contains("папок 1", rows["Фін"].Comment);
        Assert.Equal(["Фін/2023"], env.Graph.Deletes);
        Assert.Equal(["Інше/порожня/x.txt", "Фін/Чернетки/d.docx"], Files(env.Library));
    }

    [Fact]
    public async Task CleanupLeavesAFolderThatGotANewFileAndSkipsWhatMovedWhole()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx");
        env.AddFile("Фін/2023/Q1/b.xlsx");
        env.AddFile("Фін/c.pdf");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси", "Нове"), E("Фін/c.pdf", "Фінанси", "окремо/c.pdf")];
        long run = await Prepare(env, p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        long update = env.Service.CreateRun(RunModes.Inventory, p.Name, update: true);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(update));
        Assert.True(ApplyResults.Verify(env.Store, run, DateTimeOffset.UtcNow).Ok);
        // After verify somebody puts a file into the emptied folder.
        env.AddFile("Фін/пізній.docx");

        long cleanup = env.Service.CreateCleanupRun(run);
        int requests = env.Graph.Log.Count;
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(cleanup));
        var rows = env.Files(cleanup).ToDictionary(f => f.Path!);
        Assert.Equal(FileStatuses.SkippedNotEmpty, rows["Фін"].Status);
        Assert.Contains("файлів 1", rows["Фін"].Comment);
        Assert.Empty(env.Graph.Deletes);
        // Folders that moved whole with their batch cost no request of their own.
        Assert.Equal("переміщено разом з папкою пакета", rows["Фін/2023/Q1"].Comment);
        Assert.DoesNotContain(env.Graph.Log.Skip(requests), r => r.Path.Contains("2023/Q1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheMoveNotesWhatTheTargetLibraryCannotHold()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/a.xlsx");
        env.Library.Columns.Add(("Contractor", "Контрагент", "Text", false));
        env.Library.Columns.Add(("Amount", "Сума", "Number", false));
        env.Finance.Columns.Add(("Amount", "Сума", "Text", false));
        env.Finance.ForceCheckout = true;
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        long run = await Prepare(env, p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        var notes = env.Store.GetNotes(run).Select(n => (n.Level, n.Message)).ToList();
        Assert.Contains(notes, n => n.Level == "warning" && n.Message.Contains("«Контрагент»", StringComparison.Ordinal) && n.Message.Contains("буде втрачено", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Level == "warning" && n.Message.Contains("у джерелі — Number, у цілі — Text", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Message.Contains("Вимагати check-out", StringComparison.Ordinal));
        Assert.Contains(notes, n => n.Level == "info" && n.Message.Contains("попереджень 3", StringComparison.Ordinal));
        // Service columns (image tags) are the same everywhere and never reported; the move itself is not held back.
        Assert.DoesNotContain(notes, n => n.Message.Contains("MediaService", StringComparison.Ordinal));
        Assert.Equal(["Фін/a.xlsx"], Files(env.Finance));
    }

    [Fact]
    public void TheTargetCheckComparesColumnsTypesAndSettings()
    {
        ListField F(string name, string type = "Text", bool required = false, bool hidden = false, bool fromBase = false) => new(name, name, type, required, hidden, false, fromBase);
        var source = new SPOMover.Spo.Graph.ListSchema("Джерело", true, false, false, false,
            [F("Title"), F("Client"), F("Secret", hidden: true), F("Created", "DateTime", fromBase: true), F("_ExtendedDescription", "Note")], ["Документ", "Папка", "Договір"]);
        var same = TargetCheck.Compare(source, source with { Title = "Ціль" }, 1, "Ціль").ToList();
        Assert.Empty(same);
        var target = new SPOMover.Spo.Graph.ListSchema("Ціль", false, true, false, true, [F("Title"), F("Code", required: true)], ["Документ", "Папка"]);
        var found = TargetCheck.Compare(source, target, 1, "Ціль").Select(f => (f.Kind, f.Severity)).ToList();
        Assert.Equal([("missing_column", "warning"), ("required_column", "warning"), ("missing_content_type", "warning"), ("moderation", "warning"),
            ("minor_versions", "warning"), ("no_versioning", "info")], found);
        var report = new TargetCheckReport(DateTimeOffset.UtcNow, TargetCheck.Compare(source, target, 1, "Ціль").ToList(), ["Ціль"]);
        Assert.Equal(5, report.Warnings);
        Assert.Contains("«Договір»", TargetCheck.Text(report));
    }

    [Fact]
    public async Task ThePilotReportMeasuresSpeedLoadAndForecastsTheRest()
    {
        await using var env = new EngineEnv();
        for (int i = 0; i < 30; i++)
            env.AddFile($"Фін/2023/f{i:00}.xlsx", 1024);
        env.AddFile("Фін/c.pdf");
        env.AddFile("Інше/x.txt");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси", "Нове")];
        long run = await Prepare(env, p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        string text = PilotReport.Text(env.Store, run);
        Assert.Contains("Звіт пілоту: прогін", text);
        Assert.Contains("переміщено: 31", text);
        Assert.Contains("3. Навантаження на SharePoint", text);
        Assert.Contains("ще не звірено", text);
        Assert.Contains("6. Прогноз для решти", text);
        Assert.Contains("у джерелі лишається ≈ 1 файлів", text); // the inventory was before the move: moved files left out
        Assert.Throws<InvalidOperationException>(() => PilotReport.Text(env.Store, env.Store.GetRun(run)!.ParentId!.Value));

        var t0 = DateTimeOffset.UtcNow;
        // Two overlapping batches and one after a pause: 0–10 s and 5–15 s, then 100–110 s → 25 s of moving.
        Assert.Equal(TimeSpan.FromSeconds(25), PilotReport.ActiveTime([(t0, t0.AddSeconds(10)), (t0.AddSeconds(5), t0.AddSeconds(15)), (t0.AddSeconds(100), t0.AddSeconds(110))]));
        Assert.Equal(2.5, PilotReport.Median([1, 4, 2, 3]));
        Assert.Equal(0, PilotReport.Median([]));
    }

    [Fact]
    public async Task RetryMovesChosenFilesAgainAfterTheCauseIsGone()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/a.xlsx", 10);
        env.AddFile("Фін/b.xlsx", 10);
        env.Graph.AddFile(env.Finance, "Фін/інше.txt", new byte[1]);
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        long run = await Prepare(env, p);
        var taken = env.Graph.AddFile(env.Finance, "Фін/a.xlsx", new byte[3]);
        Assert.Equal(RunStatuses.CompletedWithErrors, await env.RunAsync(run));
        Assert.Throws<InvalidOperationException>(() => env.Service.Retry(run, ["locked"]));

        env.Graph.Remove(env.Finance, taken); // the owner removed the old copy
        Assert.Equal(1, env.Service.Retry(run, ["conflict"]));
        Assert.Equal(RunStatuses.Stopped, env.Store.GetRun(run)!.Status);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.All(env.Files(run), f => Assert.Equal(FileStatuses.Moved, f.Status));
        Assert.Equal(10, env.Finance.Items.Values.Single(i => i.Name == "a.xlsx").Content.Length);
    }

    [Fact]
    public async Task RulesForSpoReplaceLinkCoverWholeFoldersAndSingleFilesOnly()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx");
        env.AddFile("Фін/c.pdf", 10);
        env.AddFile("Фін/d.pdf", 10);
        env.Graph.AddFile(env.Finance, "Нове/c.pdf", new byte[1]); // c.pdf is a conflict of the plan and does not move: no rule for it
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси", "Нове")];
        long run = await Prepare(env, p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));

        var rows = ApplyResults.RuleRows(env.Store, run).ToList();
        Assert.Equal(["old", "new", "name"], rows[0]);
        string src = Host + "/sites/archive/Shared Documents/", dst = Host + "/sites/archive/Finance/";
        Assert.Equal("Фін/d.pdf → Нове/d.pdf | Фін/2023 → Нове/2023", string.Join(" | ", rows.Skip(1).Select(r => r[0]!.Replace(src, "") + " → " + r[1]!.Replace(dst, ""))));
    }
}
