using SPOMover.Core.Config;
using SPOMover.Core.Mapping;
using SPOMover.State;
using static SPOMover.Engine.Tests.EngineEnv;

namespace SPOMover.Engine.Tests;

/// <summary>Stage 3: the plan from a real inventory run (PLN-1…PLN-5).</summary>
public class PlannerTests
{
    private static MappingEntry E(string source, string library, string? path = null) => new() { Source = source, Library = library, Path = path };

    [Fact]
    public async Task ThePlanUsesTheInventoryOfSourceAndTargetsAndIsKeptAsASnapshot()
    {
        await using var env = new EngineEnv();
        for (int i = 1; i <= 3; i++)
            env.AddFile($"Фін/2023/a{i}.xlsx", 100);
        env.AddFile("Фін/2024/b.xlsx", 50).CheckedOutBy = "Іван";
        env.AddFile("Фін/звіт.xlsx", 10);
        env.AddFile("Фін/новий.xlsx", 10);
        env.AddFile("HR/x.docx", 7);
        env.Graph.AddFile(env.Finance, "Фін/звіт.xlsx", new byte[1]); // the folder and one name already in the target
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        p.Plan.MaxBatchItems = 3;
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));

        var report = Planner.Build(env.Store, p, DateTimeOffset.UtcNow);
        var plan = report.Plan;
        Assert.Equal(run, plan.RunId);
        var batches = env.Store.GetPlanBatches(plan.Id);
        Assert.Equal(
            [("files", "Фін", "Фін", 1L), ("folder", "Фін/2023", "Фін/2023", 3L), ("folder", "Фін/2024", "Фін/2024", 1L)],
            batches.Select(b => (b.Kind == Core.Planning.BatchKind.Folder ? "folder" : "files", b.Source, b.TargetPath, b.Files)));
        Assert.Equal(1, batches.Single(b => b.Source == "Фін/2024").CheckedOut);
        Assert.Empty(env.Store.GetPlanFolders(plan.Id)); // Фін exists in Фінанси
        var conflict = Assert.Single(env.Store.GetPlanConflicts(plan.Id));
        Assert.Equal("Фін/звіт.xlsx", conflict.Path);
        Assert.Equal((5L, 1L, 7L), (plan.Files, plan.StayFiles, plan.StayBytes)); // the conflict is in neither
        Assert.Contains(report.Issues, i => i.Message.Contains("check-out"));
        Assert.Contains(report.Issues, i => i.Message.Contains("зайнятий шлях"));
        Assert.Equal(1, Assert.Single(report.Waves).RuleRow);
        Assert.Equal(TimeSpan.FromSeconds(5 / (2.2 * 4)), report.Duration);

        var files = Planner.Rows(env.Store, plan.Id, "files").Skip(1).ToList();
        Assert.Equal(["1", "Фін/новий.xlsx", "Фін/новий.xlsx", "10"], Assert.Single(files));
        Assert.Equal("seq,wave,kind,rule,source,library,target,files,bytes,items,checked_out",
            string.Join(',', Planner.Rows(env.Store, plan.Id, "batches").First()));
        Assert.Contains("Пакетів: 3", Planner.Summary(report));

        // A second build is a new snapshot; the first stays.
        p.Plan.MaxBatchItems = 100;
        var second = Planner.Build(env.Store, p, DateTimeOffset.UtcNow);
        Assert.NotEqual(plan.Id, second.Plan.Id);
        Assert.Equal(second.Plan.Id, Planner.Latest(env.Store, p)!.Id);
        Assert.Equal(3, env.Store.GetPlanBatches(plan.Id).Count);
    }

    [Fact]
    public async Task ThePlanIsRefusedWhileTheMappingHasErrorsOrThereIsNoInventory()
    {
        await using var env = new EngineEnv();
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси")];
        Assert.Contains("інвентаризації", Assert.Throws<InvalidOperationException>(() => Planner.Build(env.Store, p, DateTimeOffset.UtcNow)).Message);

        env.AddFile("Фін/a.xlsx");
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        p.Mapping = [E("Фін", "Немає такої")];
        Assert.Contains("помилки", Assert.Throws<InvalidOperationException>(() => Planner.Build(env.Store, p, DateTimeOffset.UtcNow)).Message);

        p.Mapping = [E("Фін", "Фінанси")];
        p.Plan.MaxBatchItems = PlanOptions.ThresholdItems;
        Assert.Contains("plan.maxBatchItems", Assert.Throws<InvalidOperationException>(() => Planner.Build(env.Store, p, DateTimeOffset.UtcNow)).Message);
    }

    [Fact]
    public async Task ThePlanTreeTellsWhatHappensToEveryFolder()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/2023/Q1/a.xlsx");
        env.AddFile("Фін/b.xlsx");
        env.AddFile("Фін/Чернетки/c.docx");
        env.AddFile("Інше/Договори/d.pdf");
        env.AddFile("HR/e.docx");
        var p = NewProfile();
        p.Mapping = [E("Фін", "Фінанси", "Нове"), E("Фін/Чернетки", MappingEntry.Stay), E("Інше/Договори", "Фінанси", "Договори")];
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        var report = Planner.Build(env.Store, p, DateTimeOffset.UtcNow);

        var view = new PlanView(env.Store, report.Plan.Id);
        var top = view.Children("").ToDictionary(n => n.Name);
        Assert.Equal(FolderFate.Split, top["Фін"].Fate);          // a stay row inside
        Assert.Equal(FolderFate.Partly, top["Інше"].Fate);       // not moved, a row inside moves Договори
        Assert.Equal(FolderFate.Unmapped, top["HR"].Fate);
        var fin = view.Children("Фін").ToDictionary(n => n.Name);
        Assert.Equal(FolderFate.Batch, fin["2023"].Fate);
        Assert.Equal("Нове/2023", fin["2023"].Batch!.TargetPath);
        Assert.Equal(FolderFate.Stays, fin["Чернетки"].Fate);
        Assert.Single(top["Фін"].FileBatches);                   // b.xlsx
        var q1 = Assert.Single(view.Children("Фін/2023"));
        Assert.Equal((FolderFate.InBatch, "Нове/2023/Q1"), (q1.Fate, q1.Target));
        Assert.Equal(FolderFate.Batch, Assert.Single(view.Children("Інше")).Fate);
    }
}
