using System.Text;
using SPOMover.Core.Config;
using SPOMover.Core.Mapping;
using SPOMover.Core.Reports;

namespace SPOMover.Core.Tests;

public class MappingTests
{
    private static readonly string[] Targets = ["Фінанси", "Кадри", "Архів"];

    private static MappingEntry E(string source, string library, string? path = null) => new() { Source = source, Library = library, Path = path };

    private static MappingBuild Build(params MappingEntry[] entries) => MappingSet.Build(entries, "Documents", Targets);

    private static MappingSet Set(params MappingEntry[] entries)
    {
        var build = Build(entries);
        Assert.True(build.IsValid, string.Join("\n", build.Issues));
        return build.Set!;
    }

    [Fact]
    public void LongestSourceWinsWhateverTheOrder()
    {
        var set = Set(E("Фін/Архів", "Архів", "Фін"), E("Фін", "Фінанси", "/"));
        Assert.Equal(("Фінанси", "2023/звіт.xlsx"), Target(set, "Фін/2023/звіт.xlsx"));
        Assert.Equal(("Архів", "Фін/2019/a.pdf"), Target(set, "Фін/Архів/2019/a.pdf"));
        Assert.Equal(("Архів", "Фін"), Target(set, "Фін/Архів"));
        Assert.Null(set.Resolve("HR/b.docx"));
    }

    [Fact]
    public void TheSampleCsvImportsWithoutIssuesIntoTheProfileLibraries()
    {
        var result = MappingImport.Parse("mapping-sample.csv", new MemoryStream(MappingSet.SampleCsv(["Фінанси", "Кадри"])));
        Assert.Empty(result.Warnings);
        Assert.Equal(5, result.Entries.Count);
        Assert.All(result.Entries, e => Assert.False(string.IsNullOrEmpty(e.Comment)));
        var build = MappingSet.Build(result.Entries, "Documents", ["Фінанси", "Кадри"]);
        Assert.Empty(build.Issues);
        var set = build.Set!;
        Assert.Equal(("Фінанси", "Фінанси/2023/a.xlsx"), Target(set, "Фінанси/2023/a.xlsx"));
        Assert.Null(set.Resolve("Фінанси/Чернетки/b.docx")!.Library);
        Assert.Equal(("Кадри", "2023/c.pdf"), Target(set, "Кадри/2023/c.pdf"));
        Assert.Equal(("Кадри", "Договори/d.pdf"), Target(set, "Юридичний/Договори/d.pdf"));
        Assert.Equal(("Фінанси", "Довідники/Положення.docx"), Target(set, "Загальне/Положення.docx"));
        // Without target libraries the sample still has two placeholders.
        Assert.Equal(2, MappingImport.Parse("s.csv", new MemoryStream(MappingSet.SampleCsv([]))).Entries.Where(e => e.Library != MappingEntry.Stay).Select(e => e.Library).Distinct().Count());
    }

    [Theory]
    [InlineData("фін/2023/a.xlsx")]
    [InlineData("%D0%A4%D1%96%D0%BD/2023/a.xlsx")]
    [InlineData(@"Фін\2023\a.xlsx")]
    [InlineData("/Фін/2023/a.xlsx/")]
    public void MatchingIgnoresCaseEncodingAndSlashes(string path) =>
        Assert.Equal(("Фінанси", "Фін/2023/a.xlsx"), Target(Set(E("Фін", "Фінанси")), path));

    [Fact]
    public void MatchesOnlyAtFolderBoundaries()
    {
        var set = Set(E("Фін", "Фінанси"));
        Assert.Null(set.Resolve("Фінанси/a.xlsx"));
        Assert.Null(set.Resolve("Фін.xlsx"));
    }

    [Fact]
    public void EmptyPathKeepsTheSourcePathAndSlashIsTheRoot()
    {
        Assert.Equal(("Кадри", "HR/Накази/1.pdf"), Target(Set(E("HR/Накази", "Кадри")), "HR/Накази/1.pdf"));
        Assert.Equal(("Кадри", "1.pdf"), Target(Set(E("HR/Накази", "Кадри", "/")), "HR/Накази/1.pdf"));
        Assert.Equal(("Кадри", "Накази 2024/1.pdf"), Target(Set(E("HR/Накази", "Кадри", "Накази 2024")), "HR/Накази/1.pdf"));
    }

    [Fact]
    public void FileRuleRenamesTheFile()
    {
        var set = Set(E("Фін/звіт.xlsx", "Фінанси", "Звіти/звіт 2023.xlsx"));
        Assert.Equal(("Фінанси", "Звіти/звіт 2023.xlsx"), Target(set, "Фін/звіт.xlsx"));
    }

    [Fact]
    public void StayKeepsASubtreeInsideAMovedFolder()
    {
        var set = Set(E("Фін", "Фінанси"), E("Фін/Чернетки", MappingEntry.Stay));
        var match = set.Resolve("Фін/Чернетки/a.docx")!;
        Assert.True(match.Stays);
        Assert.Null(match.NewPath);
        Assert.False(set.Resolve("Фін/a.docx")!.Stays);
    }

    [Fact]
    public void RootRuleCoversTheWholeLibrary()
    {
        var set = Set(E("/", "Архів", "Старе"), E("Фін", "Фінанси"));
        Assert.Equal(("Архів", "Старе/HR/a.pdf"), Target(set, "HR/a.pdf"));
        Assert.Equal(("Архів", "Старе/a.pdf"), Target(set, "a.pdf"));
        Assert.Equal(("Фінанси", "Фін/a.pdf"), Target(set, "Фін/a.pdf"));
    }

    [Theory]
    [InlineData("", "Фінанси", null, "не задано джерело")]
    [InlineData("https://h/sites/x/Shared Documents/Фін", "Фінанси", null, "а не адреса")]
    [InlineData("Фін", "", null, "не задано бібліотеку")]
    [InlineData("Фін", "Невідома", null, "не додано до цільових бібліотек")]
    [InlineData("Фін", "documents", null, "це джерело")]
    [InlineData("Фін", "Фінанси", "Звіти:2023", "недопустимий символ")]
    [InlineData("Фін", "Фінанси", "Звіти.", "закінчується крапкою")]
    [InlineData("Фін", "Фінанси", "Forms/x", "службова папка")]
    [InlineData("Фін/../HR", "Фінанси", null, "«..»")]
    [InlineData("Фін", "Фінанси", "a/_vti_bin", "_vti_")]
    public void InvalidRowsAreErrorsWithTheirRow(string source, string library, string? path, string message)
    {
        var build = Build(E("HR", "Кадри"), E(source, library, path));
        var issue = Assert.Single(build.Issues, i => i.Severity == IssueSeverity.Error);
        Assert.Equal(2, issue.Row);
        Assert.Equal(MappingSet.Area, issue.Area);
        Assert.Contains(message, issue.Message);
        Assert.False(build.IsValid);
        // The valid rows still work: the editor and the coverage report show them.
        Assert.Equal(("Кадри", "HR/a.pdf"), Target(build.Set!, "HR/a.pdf"));
    }

    [Fact]
    public void SameSourceTwice()
    {
        var duplicate = Build(E("Фін", "Фінанси"), E("фін/", "фінанси"));
        Assert.True(duplicate.IsValid);
        Assert.Contains(duplicate.Issues, i => i.Severity == IssueSeverity.Warning && i.Row == 2 && i.Message.Contains("дублює рядок 1"));

        var conflict = Build(E("Фін", "Фінанси"), E("Фін", "Архів"));
        Assert.Contains(conflict.Issues, i => i.Severity == IssueSeverity.Error && i.Row == 2 && i.Message.Contains("рядку 1"));
        Assert.Null(conflict.Set!.Resolve("Фін/a.xlsx"));
    }

    [Fact]
    public void RowThatRepeatsItsParentIsAWarning()
    {
        var build = Build(E("Фін", "Фінанси", "Old"), E("Фін/2023", "Фінанси", "Old/2023"), E("Фін/2024", "Фінанси", "2024"));
        Assert.True(build.IsValid);
        Assert.Contains(build.Issues, i => i.Row == 2 && i.Message.Contains("нічого не змінює"));
        Assert.DoesNotContain(build.Issues, i => i.Row == 3);
    }

    [Fact]
    public void SeveralSourcesIntoOneFolderAreAWarning()
    {
        var build = Build(E("Фін/2023", "Фінанси", "Звіти"), E("Бух/2023", "Фінанси", "звіти"));
        Assert.True(build.IsValid);
        Assert.Contains(build.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("вміст об'єднається"));
    }

    [Fact]
    public void EmptyRowsOfTheEditorAreIgnored()
    {
        var build = Build(E("Фін", "Фінанси"), new MappingEntry(), new MappingEntry { Source = " " });
        Assert.True(build.IsValid);
        Assert.Single(build.Set!.Rules);
    }

    [Fact]
    public void ProfileReportsMappingIssuesButInventoryRunsWithThem()
    {
        var profile = new Profile
        {
            Source = new Target { SiteUrl = "https://h/sites/a", Library = "Documents" },
            TargetLibraries = ["Фінанси", "фінанси", "Documents"],
            Mapping = [E("Фін", "Невідома")],
            Connections = ["c"],
        };
        var connection = new Connection { Name = "c", TenantId = "t", ClientId = Guid.NewGuid().ToString(), CertificateThumbprint = new string('A', 40) };
        var v = profile.ValidateForRun([connection]);
        Assert.Contains(v.Issues, i => i.Message.Contains("кілька разів"));
        Assert.Contains(v.Issues, i => i.Message.Contains("бібліотека-джерело"));
        Assert.False(v.IsValidForInventory);

        profile.TargetLibraries = ["Фінанси"];
        v = profile.ValidateForRun([connection]);
        Assert.False(v.IsValid);
        Assert.True(v.IsValidForInventory);
        Assert.Equal(["Documents", "Фінанси"], profile.Libraries().Select(l => l.Library));
        Assert.All(profile.Libraries(), l => Assert.Equal("https://h/sites/a", l.SiteUrl));
    }

    [Theory]
    [InlineData("source,library,path\r\nФін,Фінанси,/\r\n")]
    [InlineData("Джерело;Бібліотека;Шлях\nФін;Фінанси;/")]
    [InlineData("Фін\tФінанси\t/")]
    [InlineData("Фін|Фінанси|/")]
    public void ImportsTables(string text)
    {
        var result = MappingImport.Parse("map.csv", new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var entry = Assert.Single(result.Entries);
        Assert.Equal(("Фін", "Фінанси", "/"), (entry.Source, entry.Library, entry.Path));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void TabsKeepCommasInFolderNames()
    {
        var result = MappingImport.Parse("map.tsv", new MemoryStream(Encoding.UTF8.GetBytes("Фін, 2023\tФінанси\t\tпогоджено, Іван")));
        var entry = Assert.Single(result.Entries);
        Assert.Equal("Фін, 2023", entry.Source);
        Assert.Null(entry.Path);
        Assert.Equal("погоджено, Іван", entry.Comment);
    }

    [Fact]
    public void BadLinesAreReported()
    {
        var result = MappingImport.Parse("map.csv", new MemoryStream(Encoding.UTF8.GetBytes("Фін;Фінанси\nтільки-одне\n\nHR;-\n")));
        Assert.Equal(2, result.Entries.Count);
        Assert.Contains("Рядок 2", Assert.Single(result.Warnings));
    }

    [Fact]
    public void CsvExportImportsBackIncludingStay()
    {
        List<MappingEntry> entries = [E("Фін, 2023", "Фінанси", "/"), E("Фін/Чернетки", MappingEntry.Stay), E("=HR", "Кадри", "HR")];
        entries[0].Comment = "погоджено";
        var back = MappingImport.Parse("map.csv", new MemoryStream(MappingSet.ToCsv(entries))).Entries;
        Assert.Equal(entries.Select(e => (e.Source, e.Library, e.Path, e.Comment)), back.Select(e => (e.Source, e.Library, e.Path, e.Comment)));
    }

    [Fact]
    public void XlsxImport()
    {
        using var ms = new MemoryStream();
        SimpleXlsxWriter.Write(ms, "mapping", [["Джерело", "Бібліотека", "Шлях"], ["Фін", "Фінанси", "/"], ["HR/Накази", "Кадри", null]]);
        ms.Position = 0;
        var entries = MappingImport.Parse("map.xlsx", ms).Entries;
        Assert.Equal(2, entries.Count);
        Assert.Equal("HR/Накази", entries[1].Source);
        Assert.Null(entries[1].Path);
    }

    private static (string? Library, string? Path) Target(MappingSet set, string path) =>
        set.Resolve(path) is { } m ? (m.Library, m.NewPath) : (null, null);
}
