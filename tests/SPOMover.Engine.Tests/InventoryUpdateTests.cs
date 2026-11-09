using SPOMover.State;
using static SPOMover.Engine.Tests.EngineEnv;

namespace SPOMover.Engine.Tests;

/// <summary>INV-5: an inventory updated with the changes since the last one gives what a full listing would.</summary>
public class InventoryUpdateTests
{
    private static List<(int, string?, long)> Listed(EngineEnv env, long run) =>
        env.Files(run).Select(f => (f.TargetIndex, f.Path, f.Size)).OrderBy(x => x.TargetIndex).ThenBy(x => x.Path, StringComparer.Ordinal).ToList();

    private static List<(int, string, long, long)> Stats(EngineEnv env, long run) =>
        env.Store.GetFolderStats(run).Select(s => (s.TargetIndex, s.Path, s.Files, s.Bytes)).ToList();

    [Fact]
    public async Task AnUpdateAppliesAddsRenamesMovesAndDeletesLikeAFullListing()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx", 10);
        env.AddFile("Фін/2023/Q1/b.xlsx", 20);
        env.AddFile("Фін/c.pdf", 30);
        var gone = env.AddFile("HR/d.docx", 40);
        env.AddFile("Старе/x.txt", 5);
        env.AddFile("Старе/Глибше/y.txt", 6);
        env.AddFile("Переїзд/z.txt", 7);
        var renamed = env.AddFile("root.txt", 1);
        env.Graph.AddFile(env.Finance, "Вже/є.txt", new byte[3]);
        var p = NewProfile();
        long first = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(first));

        // Changes in SharePoint after the first inventory.
        env.AddFile("Фін/2024/new.xlsx", 50);                                  // new file in a new folder
        env.Graph.Rename(env.Graph.Folder(env.Library, "Фін/2023"), "2023 рік"); // the files under it are not reported
        env.Graph.Move(env.Library, env.Graph.Folder(env.Library, "Переїзд"), "Архів/2020");
        env.Graph.Remove(env.Library, env.Graph.Folder(env.Library, "Старе"));   // children not reported either
        env.Graph.Remove(env.Library, gone);
        env.Graph.Rename(renamed, "readme.txt");
        env.Graph.AddFile(env.Finance, "Вже/нове.txt", new byte[4]);           // target libraries are updated too

        long update = env.Service.CreateRun(RunModes.Inventory, p.Name, update: true);
        Assert.Equal(first, env.Store.GetRun(update)!.ParentId);
        int before = env.Graph.Log.Count;
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(update));

        long full = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(full));

        Assert.Equal(Listed(env, full), Listed(env, update));
        Assert.Equal(Stats(env, full), Stats(env, update));
        Assert.Contains(("Фін/2023 рік/Q1/b.xlsx"), Listed(env, update).Select(x => x.Item2));
        Assert.Contains(("Архів/2020/Переїзд/z.txt"), Listed(env, update).Select(x => x.Item2));
        Assert.DoesNotContain(Listed(env, update), x => x.Item2!.StartsWith("Старе", StringComparison.Ordinal));
        var note = Assert.Single(env.Store.GetNotes(update), n => n.Message.Contains("Бібліотека 1 оновлена лише змінами"));
        Assert.Contains($"з прогону {first}", note.Message);
        // The first inventory is untouched.
        Assert.Contains(Listed(env, first), x => x.Item2 == "Фін/2023/a.xlsx");
        // Only changes were listed: delta requests of the update carry the token of the first inventory.
        Assert.All(env.Graph.Log.Skip(before).Where(r => r.Path.Contains("/delta", StringComparison.Ordinal)).Take(2),
            r => Assert.Contains("token=s", r.Path));
    }

    [Fact]
    public async Task AnUpdateStoppedHalfwayContinuesAndStillFixesPathsUnderARenamedFolder()
    {
        await using var env = new EngineEnv();
        env.Graph.DeltaPageSize = 2;
        for (int i = 0; i < 4; i++)
            env.AddFile($"Фін/2023/f{i}.xlsx");
        var p = NewProfile();
        long first = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(first));

        env.Graph.Rename(env.Graph.Folder(env.Library, "Фін/2023"), "2023 рік");
        for (int i = 0; i < 5; i++)
            env.AddFile($"Нове/n{i}.txt");
        long update = env.Service.CreateRun(RunModes.Inventory, p.Name, update: true);
        RunService.ActiveRun? active = null;
        int pages = 0;
        env.Graph.Faults.Add(r =>
        {
            if (r.Path.Contains("/delta", StringComparison.Ordinal) && r.Path.Contains("token=s", StringComparison.Ordinal) && Interlocked.Increment(ref pages) == 2)
                active!.Stop.Cancel();
            return null;
        });
        active = env.Service.Start(update);
        Assert.Equal(RunStatuses.Stopped, await active.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        var continued = env.Service.Start(update);
        Assert.Equal(RunStatuses.Completed, await continued.Completion.WaitAsync(TimeSpan.FromSeconds(30)));

        var paths = env.Files(update).Where(f => f.TargetIndex == 0).Select(f => f.Path).Order(StringComparer.Ordinal).ToList();
        Assert.Equal([.. Enumerable.Range(0, 4).Select(i => $"Фін/2023 рік/f{i}.xlsx"), .. Enumerable.Range(0, 5).Select(i => $"Нове/n{i}.txt")],
            paths.OrderBy(x => x!.StartsWith("Фін", StringComparison.Ordinal) ? 0 : 1).ThenBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task AnUpdateNeedsAnEarlierInventoryAndListsNewLibrariesInFull()
    {
        await using var env = new EngineEnv();
        var p = NewProfile();
        Assert.Contains("повну інвентаризацію", Assert.Throws<InvalidOperationException>(() => env.Service.CreateRun(RunModes.Inventory, p.Name ?? "test", p, update: true)).Message);

        env.AddFile("Фін/a.xlsx");
        long first = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(first));

        var hr = env.Graph.AddLibrary("/sites/archive", "Кадри", "HR");
        env.Graph.AddFile(hr, "Накази/1.pdf", new byte[2]);
        p.TargetLibraries.Add("Кадри");
        env.SaveProfile(p);
        long update = env.Service.CreateRun(RunModes.Inventory, p.Name, update: true);
        var targets = env.Store.GetTargets(update);
        Assert.Equal([true, true, false], targets.Select(t => t.Incremental));
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(update));
        Assert.Contains(env.Files(update), f => f.TargetIndex == 2 && f.Path == "Накази/1.pdf");
        Assert.Contains(env.Files(update), f => f.TargetIndex == 0 && f.Path == "Фін/a.xlsx");
    }

    [Fact]
    public async Task APlanCanBeBuiltFromAnUpdatedInventory()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/a.xlsx");
        var p = NewProfile();
        p.Mapping = [new() { Source = "Фін", Library = "Фінанси" }];
        long first = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(first));
        p.Plan.MaxBatchItems = 2;
        var before = Planner.Build(env.Store, p, DateTimeOffset.UtcNow);
        Assert.Null(before.Diff);
        Assert.Equal(["Фін"], env.Store.GetPlanBatches(before.Plan.Id).Select(b => b.Source)); // 1 file + 1 folder

        env.AddFile("Фін/2024/b.xlsx");
        env.AddFile("Фін/2023/c.xlsx");
        long update = env.Service.CreateRun(RunModes.Inventory, p.Name, update: true);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(update));

        // PLN-4: Фін no longer fits, so its subfolders become batches; the change is shown against the previous plan.
        var report = Planner.Build(env.Store, p, DateTimeOffset.UtcNow);
        Assert.Equal(update, report.Plan.RunId);
        Assert.Equal(3, report.Plan.Files);
        var diff = report.Diff!;
        Assert.Equal(before.Plan.Id, diff.PreviousPlanId);
        Assert.Equal((2, 1, 0), (diff.Added, diff.Removed, diff.Changed));
        Assert.Equal((1L, 3L), (diff.FilesBefore, diff.FilesAfter));
        Assert.Contains("пакетів додано 2, прибрано 1", Planner.Summary(report));
        var rows = Planner.Rows(env.Store, report.Plan.Id, "changes").Skip(1).ToList();
        Assert.Equal(["removed,folder,Фін", "added,folder,Фін/2023", "added,folder,Фін/2024"], rows.Select(r => string.Join(',', r.Take(3))));

        var same = Planner.Build(env.Store, p, DateTimeOffset.UtcNow);
        Assert.True(same.Diff!.Same);
    }
}
