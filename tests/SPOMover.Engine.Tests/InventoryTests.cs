using SPOMover.Core.Config;
using SPOMover.Core.Mapping;
using SPOMover.State;
using static SPOMover.Engine.Tests.EngineEnv;

namespace SPOMover.Engine.Tests;

/// <summary>Stage 2: folder totals (INV-2), check-outs (INV-3), target libraries (INV-4), mapping coverage (MAP-5).</summary>
public class InventoryTests
{
    private static MappingEntry E(string source, string library, string? path = null) => new() { Source = source, Library = library, Path = path };

    [Fact]
    public async Task FolderTotalsCoverTheWholeSubtree()
    {
        await using var env = new EngineEnv();
        env.Graph.FilesBeforeFolders = true;
        env.AddFile("Фін/2023/a.xlsx", 100);
        env.AddFile("Фін/2023/Q1/b.xlsx", 200);
        env.AddFile("Фін/c.pdf", 50);
        env.AddFile("HR/d.docx", 10);
        env.AddFile("root.txt", 1);
        env.Graph.AddFile(env.Library, "Порожня/.keep", []);
        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));

        var stats = env.Store.GetFolderStats(run, 0).ToDictionary(s => s.Path);
        Assert.Equal((5 + 1, 361L), (stats[""].Files, stats[""].Bytes));
        Assert.Equal((3, 350L, 1L), (stats["Фін"].Files, stats["Фін"].Bytes, stats["Фін"].DirectFiles));
        Assert.Equal((2, 300L, 1L), (stats["Фін/2023"].Files, stats["Фін/2023"].Bytes, stats["Фін/2023"].DirectFiles));
        Assert.Equal("Фін/2023/Q1/b.xlsx".Length, stats["Фін"].MaxPath);
        Assert.Equal(3, stats["Фін/2023/Q1"].Depth);
        // The target library is listed too (INV-4), with its own totals.
        Assert.Contains(env.Store.GetFolderStats(run, 1), s => s.Path.Length == 0 && s.Files == 0);

        string dir = Path.Combine(env.Dir, "r");
        ReportExporter.ExportAll(env.Store, run, dir);
        Assert.Contains("1,Фін/2023,2,2,300,1,", File.ReadAllText(Path.Combine(dir, "folders.csv")));
        Assert.Contains("Джерело, папки першого рівня", File.ReadAllText(Path.Combine(dir, "summary.txt")));
    }

    [Fact]
    public async Task CheckedOutAndUnpublishedFilesAreFound()
    {
        await using var env = new EngineEnv();
        env.AddFile("Фін/a.xlsx");
        env.AddFile("Фін/b.xlsx").CheckedOutBy = "Іван Петренко";
        env.AddFile("HR/c.docx");
        env.AddFile("HR/Накази/d.docx").CheckedOutBy = "Олена";
        env.Library.Unpublished.Add(("Фін/новий звіт.xlsx", "Іван Петренко"));
        env.Graph.AddFile(env.Finance, "x.xlsx", new byte[1]).CheckedOutBy = "хтось"; // reported, not counted

        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        var checkouts = env.Store.GetCheckouts(run).Where(c => c.TargetIndex == 0);
        Assert.Equal(
            [("Фін/b.xlsx", "Іван Петренко", true), ("HR/Накази/d.docx", "Олена", true), ("Фін/новий звіт.xlsx", "Іван Петренко", false)],
            checkouts.Select(c => (c.Path, c.CheckedOutBy, c.Published)).OrderBy(c => c.Published ? 0 : 1).ThenBy(c => c.Path.Length));
        Assert.Contains(env.Store.GetCheckouts(run), c => c.TargetIndex == 1 && c.CheckedOutBy == "хтось");
        Assert.Equal(3, env.Store.CountCheckouts(run)); // the source only
        Assert.Equal(CheckoutScan.Done, env.Store.GetTargets(run)[0].Checkouts);
        Assert.Empty(env.Store.GetNotes(run));
        // Checked-out files come with the listing: the only REST requests are the list of unpublished files and,
        // for the listing progress, the item count of each library (a list path without a method).
        Assert.Equal(1, env.Graph.Log.Count(r => r.Path.Contains("/_api/", StringComparison.Ordinal) && !r.Path.Split('?')[0].EndsWith("')", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task WithoutSharePointPermissionCheckoutsStillComeFromTheListing()
    {
        await using var env = new EngineEnv();
        env.Graph.SharePointRest = false;
        env.AddFile("a.xlsx").CheckedOutBy = "Іван";
        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(CheckoutScan.Skipped, env.Store.GetTargets(run)[0].Checkouts);
        var note = Assert.Single(env.Store.GetNotes(run));
        Assert.Contains("Файли без опублікованої версії не перевірено", note.Message);
        Assert.Equal("Іван", Assert.Single(env.Store.GetCheckouts(run)).CheckedOutBy);
    }

    [Fact]
    public async Task WithTheWriteRoleUnpublishedFilesAreSkippedAndTheNoteSaysWhy()
    {
        await using var env = new EngineEnv();
        env.Graph.FullControl = false;
        env.AddFile("a.xlsx").CheckedOutBy = "Іван";
        env.Library.Unpublished.Add(("b.xlsx", "Олена"));
        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Contains("fullcontrol", Assert.Single(env.Store.GetNotes(run)).Message);
        Assert.Equal(1, env.Store.CountCheckouts(run));
    }

    [Fact]
    public async Task MappingCoverageConflictsAndLongPaths()
    {
        await using var env = new EngineEnv();
        var hr = env.Graph.AddLibrary("/sites/archive", "Кадри", "HR");
        env.AddFile("Фін/2023/a.xlsx", 100);
        env.AddFile("Фін/2023/b.xlsx", 100);
        env.AddFile("Фін/Чернетки/draft.docx", 5);
        env.AddFile("Бух/2023/a.xlsx", 40);           // collides with Фін/2023/a.xlsx in Фінанси:/2023
        env.AddFile("HR/Накази/1.pdf", 7);
        env.AddFile("HR/" + new string('д', 370) + ".pdf", 1); // too long under Документи/Персонал, fits in the root
        env.AddFile("Інше/x.txt", 3);
        env.AddFile("Інше/Глибше/y.txt", 3);
        env.Graph.AddFile(env.Finance, "2023/b.xlsx", new byte[1]); // already in the target
        env.AddFile("Фін/2023/c.xlsx", 1).CheckedOutBy = "Іван";
        var p = NewProfile();
        p.TargetLibraries = ["Фінанси", "Кадри"];
        p.Mapping =
        [
            E("Фін", "Фінанси", "/"),
            E("Фін/Чернетки", MappingEntry.Stay),
            E("Бух/2023", "Фінанси", "2023"),
            E("HR", "Кадри", "Документи/Персонал"),
            E("Немає такої", "Архів"),
        ];
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        _ = hr;
        // A target library added to the profile after the inventory: its contents are unknown to the check.
        p.TargetLibraries.Add("Архів");

        var report = MappingCheck.Run(env.Store, p);
        Assert.Equal(run, report.RunId);
        Assert.Equal(9, report.Files);
        Assert.Equal((6L, 249L), (report.MovedFiles, report.MovedBytes)); // 3 from Фін, 1 from Бух, 2 from HR
        Assert.Equal((1L, 5L), (report.StayFiles, report.StayBytes));
        Assert.Equal((2L, 6L), (report.UnmappedFiles, report.UnmappedBytes));
        Assert.Equal(["Інше", "Інше/Глибше"], report.UnmappedFolders.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3, report.Rules.Single(r => r.Rule.Row == 1).Files);
        Assert.Equal(0, report.Rules.Single(r => r.Rule.Row == 5).Files);
        Assert.Contains(report.Issues, i => i.Row == 5 && i.Message.Contains("не збігається"));
        // Фінанси:/2023/a.xlsx twice (moved and moved) and Фінанси:/2023/b.xlsx already there.
        Assert.Equal(3, report.Conflicts);
        Assert.Equal(1, report.LongPaths);
        Assert.Equal(1, report.CheckedOutMoved);
        Assert.True(report.HasErrors);
        Assert.Contains(report.Issues, i => i.Message.Contains("«Архів» не перелічено"));
        Assert.Equal([("Фінанси", 4L), ("Кадри", 2L), ("Архів", 0L)], report.ByLibrary.Select(l => (l.Library, l.Files)));

        var problems = MappingCheck.Rows(env.Store, p, report, "problems").Skip(1).ToList();
        Assert.Equal(2, problems.Count(r => r[0] == "conflict"));
        Assert.Contains(problems, r => r[0] == "exists" && r[1] == "Фін/2023/b.xlsx" && r[3] == "2023/b.xlsx");
        Assert.Contains(problems, r => r[0] == "long_path" && r[2] == "Кадри");
        Assert.Contains(problems, r => r[0] == "checked_out" && r[1] == "Фін/2023/c.xlsx" && r[4] == "Іван");

        var preview = MappingCheck.Rows(env.Store, p, report, "preview").Skip(1).ToDictionary(r => r[0]!);
        Assert.Equal(["Кадри", "Документи/Персонал/Накази/1.pdf"], preview["HR/Накази/1.pdf"].Skip(3));
        Assert.Equal(MappingEntry.Stay, preview["Фін/Чернетки/draft.docx"][3]);
        Assert.Null(preview["Інше/x.txt"][2]);
        Assert.Contains("Переміщується: 6 файлів", MappingCheck.Summary(report));

        // Fixed mapping: no errors left.
        p.Mapping = [E("Фін", "Фінанси", "/"), E("Бух/2023", "Фінанси", "2023 Бух"), E("HR", "Кадри", "/"), E("Інше", MappingEntry.Stay)];
        env.Graph.Remove(env.Finance, env.Finance.Items.Values.Single(i => i.Name == "b.xlsx"));
        var fixedReport = MappingCheck.Run(env.Store, p);
        Assert.Equal(1, fixedReport.Conflicts); // the inventory still has b.xlsx in Фінанси: a new inventory would clear it
        Assert.Equal(0, fixedReport.LongPaths);
        Assert.Equal(0, fixedReport.UnmappedFiles);
    }

    [Fact]
    public async Task CheckNeedsAnInventoryOfTheSameSource()
    {
        await using var env = new EngineEnv();
        var p = NewProfile();
        env.SaveProfile(p);
        Assert.Throws<InvalidOperationException>(() => MappingCheck.Run(env.Store, p));
        long run = env.Create(p);
        await env.RunAsync(run);
        Assert.Equal(run, MappingCheck.LastInventory(env.Store, p)!.Id);
        p.Source = new Target { SiteUrl = p.Source!.SiteUrl, Library = "Фінанси" };
        p.TargetLibraries = ["Documents"];
        Assert.Null(MappingCheck.LastInventory(env.Store, p));
    }
}
