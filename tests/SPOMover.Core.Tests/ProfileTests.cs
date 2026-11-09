using SPOMover.Core.Config;
using SPOMover.Core;
using SPOMover.Core.Reports;

namespace SPOMover.Core.Tests;

public class ProfileTests
{
    [Fact]
    public void SampleProfileIsValid()
    {
        string path = Path.Combine(FindRepoRoot(), "samples", "split.profile.json");
        var profile = Profile.Load(path);
        var connection = Connection.Parse(File.ReadAllText(Path.Combine(FindRepoRoot(), "samples", "connection.example.json")));
        var validation = profile.ValidateForRun([connection]);
        Assert.True(validation.IsValid, string.Join("\n", validation.Issues));
        Assert.Equal(3, profile.TargetLibraries.Count);
        Assert.NotEmpty(profile.Mapping);
    }

    [Fact]
    public void UnknownPropertyIsRejected()
    {
        var ex = Assert.Throws<ProfileException>(() => Profile.Parse("""{ "source": null, "rules": [] }"""));
        Assert.Contains("rules", ex.Message);
    }

    [Fact]
    public void RoundTripsThroughJson()
    {
        var profile = Profile.Parse("""
            // comment
            { "name": "x", "source": { "siteUrl": "https://h/sites/a", "library": "Documents", "folder": "Fin" }, "targetLibraries": [ "Фінанси", ],
              "mapping": [ { "source": "Fin/2023", "library": "Фінанси", "path": "/" } ], "performance": { "concurrency": 4 } }
            """);
        var again = Profile.Parse(profile.ToJson());
        Assert.Equal("x", again.Name);
        Assert.Equal("Fin", again.Source!.Folder);
        Assert.Equal("/", again.Mapping[0].Path);
        Assert.Equal(4, again.Performance.Concurrency);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=HYPERLINK(1)", "'=HYPERLINK(1)")]
    [InlineData(null, "")]
    public void EscapesCsv(string? value, string expected) => Assert.Equal(expected, CsvReport.Escape(value, ','));

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SPOMover.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корінь репозиторію не знайдено.");
    }
}
