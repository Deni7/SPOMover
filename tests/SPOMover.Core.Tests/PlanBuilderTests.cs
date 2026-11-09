using SPOMover.Core.Mapping;
using SPOMover.Core.Planning;

namespace SPOMover.Core.Tests;

public class PlanBuilderTests
{
    private static readonly string[] Targets = ["Фінанси", "Кадри", "Архів"];

    private static MappingEntry E(string source, string library, string? path = null) => new() { Source = source, Library = library, Path = path };

    /// <summary>A source library and target contents for one test.</summary>
    private sealed class Lib
    {
        private readonly List<(string Path, long Size, string? By)> _files = [];
        private readonly List<string> _emptyFolders = [];
        public Dictionary<string, List<string>> TargetFolders { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, List<string>> TargetFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Lib File(string path, long size = 10, string? checkedOutBy = null)
        {
            _files.Add((path, size, checkedOutBy));
            return this;
        }

        public Lib Files(string folder, int count)
        {
            for (int i = 1; i <= count; i++)
                File($"{folder}/f{i:0000}.txt");
            return this;
        }

        public Lib Folder(string path)
        {
            _emptyFolders.Add(path);
            return this;
        }

        public Lib InTarget(string library, string filePath)
        {
            if (!TargetFiles.TryGetValue(library, out var files))
                TargetFiles[library] = files = [];
            files.Add(filePath);
            if (!TargetFolders.TryGetValue(library, out var folders))
                TargetFolders[library] = folders = [];
            for (string p = PlanBuilder.Parent(filePath); p.Length > 0; p = PlanBuilder.Parent(p))
                folders.Add(p);
            return this;
        }

        public IReadOnlyList<PlanFolder> Stats()
        {
            var stats = new Dictionary<string, (long Files, long Bytes)>(StringComparer.Ordinal) { [string.Empty] = (0, 0) };
            foreach (string f in _emptyFolders)
            {
                for (string p = f; p.Length > 0; p = PlanBuilder.Parent(p))
                    stats.TryAdd(p, (0, 0));
            }
            foreach (var (path, size, _) in _files)
            {
                for (string p = PlanBuilder.Parent(path); ; p = PlanBuilder.Parent(p))
                {
                    stats.TryGetValue(p, out var s);
                    stats[p] = (s.Files + 1, s.Bytes + size);
                    if (p.Length == 0)
                        break;
                }
            }
            return stats.Select(x => new PlanFolder(x.Key, x.Value.Files, x.Value.Bytes)).ToList();
        }

        public IEnumerable<PlanFile> SourceFiles() =>
            _files.Select((f, i) => new PlanFile(i + 1, f.Path, f.Size, f.By)).OrderBy(f => MappingSet.Key(f.Path), StringComparer.Ordinal);

        public PlanResult Plan(int limit, params MappingEntry[] mapping) => Plan(limit, shuffle: false, mapping);

        public PlanResult Plan(int limit, bool shuffle, params MappingEntry[] mapping)
        {
            var build = MappingSet.Build(mapping, "Documents", Targets);
            Assert.True(build.IsValid, string.Join("\n", build.Issues));
            var stats = Stats();
            if (shuffle)
                stats = stats.OrderBy(s => s.Path.GetHashCode(StringComparison.Ordinal)).ToList();
            return PlanBuilder.Build(build.Set!, stats, SourceFiles(),
                TargetFolders.ToDictionary(x => x.Key, x => (IReadOnlyCollection<string>)x.Value),
                TargetFiles.ToDictionary(x => x.Key, x => (IReadOnlyCollection<string>)x.Value),
                new PlanLimits(limit));
        }
    }

    private static string Show(PlanBatch b) =>
        b.Kind == BatchKind.Folder ? $"folder {b.Source} → {b.Library}:/{b.TargetPath}" : $"files {b.Source} ({b.Files}) → {b.Library}:/{b.TargetPath}";

    private static string[] Shows(PlanResult r) => r.Batches.Select(Show).ToArray();

    private static string[] Created(PlanResult r) => r.Folders.Select(f => $"{f.Library}:/{f.Path}").ToArray();

    [Fact]
    public void AFolderThatFitsMovesWholeAndItsTargetParentIsCreated()
    {
        var lib = new Lib().Files("Фін/2023", 3).Files("Фін/2024", 2);
        var r = lib.Plan(100, E("Фін", "Фінанси", "Старе/Фін"));
        var b = Assert.Single(r.Batches);
        Assert.Equal("folder Фін → Фінанси:/Старе/Фін", Show(b));
        Assert.Equal(5, b.Files);
        Assert.Equal(7, b.Items); // 5 files + 2 subfolders
        Assert.Equal(["Фінанси:/Старе"], Created(r));
        Assert.Empty(r.Conflicts);
    }

    [Fact]
    public void AFolderExactlyAtTheLimitMovesWholeAndOneMoreSplitsIt()
    {
        var lib = new Lib().Files("Фін/2023", 4).Files("Фін", 1); // 5 files + 1 subfolder = 6 items
        Assert.Equal(["folder Фін → Фінанси:/Фін"], Shows(lib.Plan(6, E("Фін", "Фінанси"))));

        var r = lib.Plan(5, E("Фін", "Фінанси"));
        Assert.Equal(["files Фін (1) → Фінанси:/Фін", "folder Фін/2023 → Фінанси:/Фін/2023"], Shows(r));
        Assert.Equal(["Фінанси:/Фін"], Created(r));
    }

    [Fact]
    public void FilesOfASplitFolderAreChunkedByTheLimit()
    {
        var lib = new Lib().Files("Фін", 7);
        var r = lib.Plan(3, E("Фін", "Фінанси"));
        Assert.Equal([3L, 3L, 1L], r.Batches.Select(b => b.Files));
        Assert.All(r.Batches, b => Assert.Equal(BatchKind.Files, b.Kind));
        Assert.Equal(7, r.Batches.SelectMany(b => b.FileList).Select(f => f.FileId).Distinct().Count());
    }

    [Fact]
    public void ARuleInsideThatSendsElsewhereSplitsTheFolderAndARepeatingRuleDoesNot()
    {
        var lib = new Lib().Files("Фін/2023", 2).Files("Фін/Архів", 2);
        var r = lib.Plan(100, E("Фін", "Фінанси"), E("Фін/Архів", "Архів", "Фін"));
        Assert.Equal(["folder Фін/2023 → Фінанси:/Фін/2023", "folder Фін/Архів → Архів:/Фін"], Shows(r));
        Assert.Equal(["Фінанси:/Фін"], Created(r));

        var same = lib.Plan(100, E("Фін", "Фінанси"), E("Фін/Архів", "Фінанси", "Фін/Архів"));
        Assert.Equal(["folder Фін → Фінанси:/Фін"], Shows(same));
    }

    [Fact]
    public void ARuleForOneFileInsideAFolderSendsOnlyThatFileElsewhere()
    {
        var lib = new Lib().File("Фін/a.xlsx").File("Фін/b.xlsx").Files("Фін/2023", 2);
        var r = lib.Plan(100, E("Фін", "Фінанси"), E("Фін/b.xlsx", "Архів", "b-2019.xlsx"));
        Assert.Equal(["files Фін (1) → Фінанси:/Фін", "folder Фін/2023 → Фінанси:/Фін/2023", "files Фін (1) → Архів:/"], Shows(r));
        var renamed = r.Batches[2].FileList.Single();
        // Batches of a wave go by source path: a folder's own files before its subfolders.
        Assert.Equal(("Фін/b.xlsx", "b-2019.xlsx"), (renamed.Path, renamed.NewPath));
        Assert.Equal([1, 1, 2], r.Batches.Select(b => b.Wave));
    }

    [Fact]
    public void AStayRuleInsideKeepsThatSubtreeAndTheRestMoves()
    {
        var lib = new Lib().Files("Фін/2023", 2).Files("Фін/Чернетки", 3).File("HR/x.docx");
        var r = lib.Plan(100, E("Фін", "Фінанси"), E("Фін/Чернетки", MappingEntry.Stay));
        Assert.Equal(["folder Фін/2023 → Фінанси:/Фін/2023"], Shows(r));
        Assert.Equal(4, r.StayFiles); // 3 drafts + the unmapped HR file
    }

    [Fact]
    public void AnExistingTargetFolderIsNotReplacedItsFilesGoInOneByOneAndCollisionsAreConflicts()
    {
        var lib = new Lib().Files("Фін/2023", 2).File("Фін/звіт.xlsx").File("Фін/новий.xlsx")
            .InTarget("Фінанси", "Фін/звіт.xlsx");
        var r = lib.Plan(100, E("Фін", "Фінанси"));
        Assert.Equal(["files Фін (1) → Фінанси:/Фін", "folder Фін/2023 → Фінанси:/Фін/2023"], Shows(r));
        Assert.Empty(r.Folders); // Фін is already there
        var c = Assert.Single(r.Conflicts);
        Assert.Equal(("Фін/звіт.xlsx", "Фінанси", "Фін/звіт.xlsx"), (c.Path, c.Library, c.NewPath));
    }

    [Fact]
    public void TwoSourcesIntoOneFolderAreMergedThroughACreatedFolder()
    {
        var lib = new Lib().Files("Бух/2023", 1).Files("Каса/2024", 1);
        var r = lib.Plan(100, E("Бух", "Фінанси", "Спільне"), E("Каса", "Фінанси", "Спільне"));
        Assert.Equal(["folder Бух/2023 → Фінанси:/Спільне/2023", "folder Каса/2024 → Фінанси:/Спільне/2024"], Shows(r));
        Assert.Equal(["Фінанси:/Спільне"], Created(r));
    }

    [Fact]
    public void AWholeFolderIsSplitWhenAnotherBatchNeedsAFolderInsideItsTarget()
    {
        var lib = new Lib().Files("А", 2).Files("Б", 1);
        var r = lib.Plan(100, E("А", "Фінанси", "Т"), E("Б", "Фінанси", "Т/Б"));
        // Б needs Т to exist, so А cannot be moved as the folder Т: Т is created and А's files go into it.
        Assert.Equal(["files А (2) → Фінанси:/Т", "folder Б → Фінанси:/Т/Б"], Shows(r));
        Assert.Equal(["Фінанси:/Т"], Created(r));
    }

    [Fact]
    public void TheWholeLibraryIntoTheTargetRootMovesTopFoldersAndRootFiles()
    {
        var lib = new Lib().File("readme.txt").Files("Фін", 2).Folder("Порожня");
        var r = lib.Plan(100, E("/", "Архів"));
        Assert.Equal(["files  (1) → Архів:/", "folder Порожня → Архів:/Порожня", "folder Фін → Архів:/Фін"], Shows(r));
        Assert.Equal(0, r.Batches[1].Items);
        Assert.Empty(r.Folders);
    }

    [Fact]
    public void UnmappedAndStayingFoldersAreNotPlanned()
    {
        var lib = new Lib().Files("HR", 3).Files("Фін", 1);
        var r = lib.Plan(100, E("Фін", "Фінанси"), E("HR", MappingEntry.Stay));
        Assert.Equal(["folder Фін → Фінанси:/Фін"], Shows(r));
        Assert.Equal(3, r.StayFiles);
        Assert.Equal(30, r.StayBytes);
    }

    [Fact]
    public void CheckedOutFilesAreCountedPerBatch()
    {
        var lib = new Lib().File("Фін/2023/a.xlsx", checkedOutBy: "Іван").File("Фін/2023/b.xlsx").File("Фін/c.xlsx", checkedOutBy: "Олена");
        var r = lib.Plan(3, E("Фін", "Фінанси"));
        Assert.Equal(["files Фін (1) → Фінанси:/Фін", "folder Фін/2023 → Фінанси:/Фін/2023"], Shows(r));
        Assert.Equal([1, 1], r.Batches.Select(b => b.CheckedOut));
    }

    [Fact]
    public void WavesFollowTheMappingRows()
    {
        var lib = new Lib().Files("Фін", 1).Files("HR", 1).Files("Арх", 1);
        var r = lib.Plan(100, E("HR", "Кадри"), E("Фін", "Фінанси"), E("Арх", "Архів"));
        Assert.Equal([(1, 1), (2, 2), (3, 3)], r.Waves.Select(w => (w.Number, w.RuleRow)));
        Assert.Equal(["folder HR → Кадри:/HR", "folder Фін → Фінанси:/Фін", "folder Арх → Архів:/Арх"], Shows(r));
    }

    [Fact]
    public void ThePlanDoesNotDependOnTheOrderOfTheInventory()
    {
        var lib = new Lib().Files("Фін/2023", 5).Files("Фін/2024", 4).File("Фін/b.xlsx").Files("HR/Накази", 3).Folder("Фін/Порожня");
        var mapping = new[] { E("Фін", "Фінанси"), E("Фін/b.xlsx", "Архів"), E("HR", "Кадри", "/") };
        var a = lib.Plan(4, false, mapping);
        var b = lib.Plan(4, true, mapping);
        Assert.Equal(Shows(a), Shows(b));
        Assert.Equal(Created(a), Created(b));
        Assert.Equal(a.Batches.Select(x => x.Seq), Enumerable.Range(1, a.Batches.Count));
    }
}
