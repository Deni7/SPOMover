using System.Text.RegularExpressions;
using Bunit;
using MudBlazor.Services;
using SPOMover.Ui.Components.Pages;
using SPOMover.Ui.Services;

namespace SPOMover.Host.Tests;

/// <summary>The user guide as in-app help: sections, links between them and the "?" buttons of the screens.</summary>
public sealed partial class HelpTests : BunitContext
{
    private static readonly UserGuide Guide = UserGuide.Instance;

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SPOMover.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Корінь репозиторію не знайдено.");
    }

    [Fact]
    public void GuideIsSplitIntoPartsAndSections()
    {
        Assert.Equal(3, Guide.Parts.Count);
        Assert.NotNull(Guide.Intro);
        Assert.DoesNotContain(Guide.Pages, s => s.Title == "Зміст");
        var profile = Guide.Find("33-профіль")!;
        Assert.Equal("Частина 3. Довідник", profile.Group);
        Assert.Contains(profile.Children, c => c.Id == "вкладка-продуктивність");
        var tab = Guide.Find("вкладка-продуктивність")!;
        Assert.Equal("33-профіль", tab.ParentId);
        Assert.Contains("<table>", tab.Html);
        Assert.Contains("performance.budgetPercent", tab.Html);
        // Code blocks with "#" inside are not headings.
        Assert.DoesNotContain(Guide.Pages, s => s.Title.StartsWith("include", StringComparison.Ordinal));
        Assert.True(Guide.Pages.Count > 30, $"{Guide.Pages.Count} сторінок");
        Assert.Equal(["вкладка-бібліотеки", "вкладка-мапінг", "вкладка-планування", "вкладка-продуктивність", "вкладка-повтори", "вкладка-підключення"], profile.Children.Select(c => c.Id));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public void EveryLinkInsideTheGuideLeadsToASection(string language)
    {
        var guide = UserGuide.For(language);
        string markdown = File.ReadAllText(Path.Combine(Root(), "docs", UserGuide.FileName(language)));
        var anchors = AnchorRegex().Matches(markdown).Select(m => m.Groups[1].Value).Distinct().ToList();
        Assert.NotEmpty(anchors);
        Assert.All(anchors, a => Assert.True(guide.Find(a) is not null, $"{language}: посилання #{a} не веде на розділ"));
        // In the app they are links to help pages; links to other repository files keep only their text.
        Assert.All(guide.Pages, s => Assert.DoesNotContain("href=\"#", s.Html));
        Assert.All(guide.Pages, s => Assert.DoesNotContain("href=\"../", s.Html));
        // The link to the other language's file is for GitHub only.
        Assert.All(guide.Pages, s => Assert.DoesNotContain("USER_GUIDE", s.Html));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("uk")]
    public void EveryHelpButtonOfTheScreensLeadsToASection(string language)
    {
        var guide = UserGuide.For(language);
        var ids = Directory.EnumerateFiles(Path.Combine(Root(), "src", "SPOMover.Ui"), "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => ButtonRegex().Matches(File.ReadAllText(f)).Select(m => (File: Path.GetFileName(f), Id: m.Groups.Values.Skip(1).First(g => g.Success).Value)))
            .Where(x => !x.Id.StartsWith('@'))
            .ToList();
        Assert.True(ids.Count >= 12, $"кнопок довідки: {ids.Count}");
        Assert.All(ids, x => Assert.True(guide.Resolve(x.Id) is not null, $"{language}, {x.File}: розділ «{x.Id}» не знайдено"));
    }

    [Fact]
    public void BothLanguagesHaveTheSameSections()
    {
        var en = UserGuide.For("en");
        var uk = UserGuide.For("uk");
        Assert.Equal("en", en.Language);
        Assert.Equal("uk", uk.Language);
        Assert.Equal(uk.Parts.Count, en.Parts.Count);
        Assert.Equal(uk.Pages.Count, en.Pages.Count);
        Assert.Equal(uk.Numbers.Order(StringComparer.Ordinal), en.Numbers.Order(StringComparer.Ordinal));
        Assert.Equal("About this guide", en.Intro!.Title);
        Assert.Equal("Про інструкцію", uk.Intro!.Title);
        Assert.DoesNotContain(en.Pages, s => s.Title == "Contents");
        Assert.Equal(["libraries-tab", "mapping-tab", "planning-tab", "performance-tab", "retries-tab", "connection-tab"], en.Find("33-profile")!.Children.Select(c => c.Id));
        Assert.Contains("performance.budgetPercent", en.Find("performance-tab")!.Html);
    }

    [Theory]
    [InlineData("3.1", "31-connection", "31-підключення")]
    [InlineData("3.1.", "31-connection", "31-підключення")]
    [InlineData("3.7", "37-file-table-errors-file-page", "37-таблиця-файлів-помилки-картка-файлу")]
    [InlineData("3.15", "315-mapping-coverage-check", "315-перевірка-покриття-мапінгу")]
    [InlineData("3.16", "316-move-plan", "316-план-переміщення")]
    [InlineData("S1", "s1-first-time-setup-of-access-to-sharepoint", "с1-перше-налаштування-доступу-до-sharepoint")]
    [InlineData("3.3.4", "performance-tab", "вкладка-продуктивність")]
    public void SectionsAreFoundByNumberInBothLanguages(string number, string english, string ukrainian)
    {
        Assert.Equal(english, UserGuide.For("en").FindByNumber(number)?.Id);
        Assert.Equal(ukrainian, UserGuide.For("uk").FindByNumber(number)?.Id);
        Assert.Equal(english, UserGuide.For("en").Resolve(number)?.Id);
        Assert.Equal(ukrainian, UserGuide.For("uk").Resolve(number)?.Id);
    }

    [Fact]
    public void AnIdOfTheOtherLanguageLeadsToTheSameSection()
    {
        Assert.Equal("316-move-plan", UserGuide.For("en").Resolve("316-план-переміщення")?.Id);
        Assert.Equal("вкладка-мапінг", UserGuide.For("uk").Resolve("mapping-tab")?.Id);
        Assert.Equal("intro", UserGuide.For("en").Resolve("intro")?.Id);
        Assert.Null(UserGuide.For("en").FindByNumber("3.99"));
        Assert.Null(UserGuide.For("en").Resolve("nope"));
    }

    [Fact]
    public void SearchFindsSectionsByWords()
    {
        var found = Guide.Search("бюджет RU");
        Assert.NotEmpty(found);
        Assert.Contains(found, s => s.Id == "вкладка-продуктивність");
        Assert.Empty(Guide.Search("неіснуюче-слово-xyz"));
    }

    [Fact]
    public void HelpPageShowsTheSectionAndItsNeighbours()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
        var page = Render<HelpPage>(p => p.Add(x => x.Id, "вкладка-продуктивність"));
        Assert.Contains("Вкладка «Продуктивність»", page.Markup);
        Assert.Contains("performance.concurrency", page.Markup);
        Assert.Contains("help/вкладка-мапінг", page.Markup); // previous
        Assert.Contains("help/вкладка-повтори", page.Markup); // next

        var missing = Render<HelpPage>(p => p.Add(x => x.Id, "nope"));
        Assert.Contains("Розділ не знайдено", missing.Markup);
    }

    [GeneratedRegex(@"\]\(#([^)\s]+)\)")]
    private static partial Regex AnchorRegex();

    [GeneratedRegex("<HelpButton[^>]*Section=\"([^\"]+)\"|\\sHelp=\"([^\"]+)\"|href=\"help/([^\"@]+)\"")]
    private static partial Regex ButtonRegex();
}
