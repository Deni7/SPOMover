using System.Diagnostics;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor;
using MudBlazor.Services;
using SPOMover.Core.Config;
using SPOMover.Engine;
using SPOMover.Ui.Components.Pages;
using SPOMover.Ui.Components.Shared;
using SPOMover.Ui.Services;
using SPOMover.State;
using SPOMover.Tests.Shared;

namespace SPOMover.Host.Tests;

/// <summary>Components of the GUI rendered with bUnit over a real state store and the engine on the fake Graph.</summary>
public sealed class UiTests : BunitContext, IAsyncLifetime
{
    private const string Host = "https://" + FakeGraph.Host;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spo-mover-ui", Guid.NewGuid().ToString("N"));
    private readonly FakeGraph _graph = new();
    private StateStore _store = null!;
    private RunService _runs = null!;
    private SettingsService _settings = null!;

    /// <summary>Instead of the "Save as" dialog: writes to memory and remembers what was saved.</summary>
    private sealed class MemorySaver : IFileSaver
    {
        public Dictionary<string, byte[]> Saved { get; } = [];

        public async Task<string?> SaveAsync(string suggestedName, Func<Stream, Task> write)
        {
            using var ms = new MemoryStream();
            await write(ms);
            Saved[suggestedName] = ms.ToArray();
            return "memory:" + suggestedName;
        }
    }

    private MemorySaver Saver { get; } = new();

    public Task InitializeAsync()
    {
        _store = new StateStore(Path.Combine(_dir, "state.db"));
        var apps = new FakeAppFactory(_graph);
        _runs = new RunService(_store, apps, NullLoggerFactory.Instance, new EngineOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
        });
        var browser = new SpoBrowser(_runs, apps);
        _settings = new SettingsService(_runs, browser);
        Services.AddMudServices();
        Services.AddSingleton(_store);
        Services.AddSingleton(_runs);
        Services.AddSingleton(browser);
        Services.AddSingleton(_settings);
        Services.AddSingleton(new StatusService(_runs));
        Services.AddSingleton<IFileSaver>(Saver);
        JSInterop.Mode = JSRuntimeMode.Loose;
        return Task.CompletedTask;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await _runs.DisposeAsync();
        await base.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private IRenderedComponent<T> RenderWithProviders<T>(Action<ComponentParameterCollectionBuilder<T>>? parameters = null) where T : IComponent =>
        RenderPage(parameters).FindComponent<T>();

    /// <summary>The component with the providers around it; dialogs and snackbars render here, outside the component.</summary>
    private IRenderedComponent<Providers> RenderPage<T>(Action<ComponentParameterCollectionBuilder<T>>? parameters = null) where T : IComponent =>
        Render<Providers>(p => p.AddChildContent(parameters ?? (_ => { })));

    /// <summary>The MudBlazor providers followed by the child content.</summary>
    private sealed class Providers : ComponentBase
    {
        [Parameter]
        public RenderFragment? ChildContent { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder b)
        {
            b.OpenComponent<MudPopoverProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudDialogProvider>(1);
            b.CloseComponent();
            b.OpenComponent<MudSnackbarProvider>(2);
            b.CloseComponent();
            b.AddContent(3, ChildContent);
        }
    }

    /// <summary>The source library of the profile, and its target library next to it.</summary>
    private FakeGraph.FakeDrive Library()
    {
        _graph.AddLibrary("/sites/archive", "Фінанси", "Finance");
        return _graph.AddLibrary("/sites/archive", "Documents", "Shared Documents");
    }

    private void SeedSettings()
    {
        _store.PutSetting(SettingKinds.Connection, "app-a", new Connection
        {
            Name = "app-a", TenantId = "t", ClientId = Guid.NewGuid().ToString(), CertificateThumbprint = new string('A', 40),
        }.ToJson(), DateTimeOffset.UtcNow);
        var profile = new Profile
        {
            Name = "split",
            Source = new Target { SiteUrl = Host + "/sites/archive", Library = "Documents" },
            TargetLibraries = ["Фінанси"],
            Mapping = [new Core.Mapping.MappingEntry { Source = "Fin", Library = "Фінанси", Path = "/" }],
            Connections = ["app-a"],
        };
        profile.Retry.RequestAttempts = 1;
        _store.PutSetting(SettingKinds.Profile, "split", profile.ToJson(), DateTimeOffset.UtcNow);
    }

    private string Snapshot() => new RunConfig { Profile = _runs.GetProfile("split"), Connections = [.. _runs.Connections()] }.ToJson();

    private async Task<long> InventoryAsync()
    {
        long id = _runs.CreateRun(RunModes.Inventory, "split");
        Assert.Equal(RunStatuses.Completed, await _runs.Start(id).Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        return id;
    }

    [Fact]
    public async Task DashboardCountersMatchTheDatabase()
    {
        SeedSettings();
        var lib = Library();
        for (int i = 0; i < 5; i++)
            _graph.AddFile(lib, $"Fin/a{i}.xlsx", new byte[1024]);
        _graph.AddFile(lib, "HR/b.pdf", new byte[2048]);
        long id = await InventoryAsync();

        var cut = RenderWithProviders<RunDashboard>(p => p.Add(x => x.RunId, id));
        string text = cut.Markup;
        Assert.Contains("завершено", text);
        Assert.Contains("inventory — інвентаризація", text);
        // An inventory has no «processed N of N» (always 100%): it says the listing is done; libraries show their stage.
        Assert.Contains("Перелік завершено: знайдено <b>6</b> файлів", text);
        Assert.DoesNotContain("Оброблено <b>", text);
        Assert.Contains("Знайдено файлів", text);
        Assert.Contains("7 КБ", text);
        // Libraries: the library on its own line with the site under it, not a narrow table column.
        cut.WaitForAssertion(() => Assert.Contains("breakdown-row", cut.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains($">{Host}/sites/archive<", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains(">xlsx<", cut.Markup), TimeSpan.FromSeconds(10));
        // Controls of a finished run: reports and delete; no pause/stop, nothing of the link replacement.
        Assert.Contains("Видалити…", text);
        Assert.DoesNotContain("Пауза", text);
        Assert.DoesNotContain("verify", text);
        Assert.DoesNotContain("Замін", text);
    }

    [Fact]
    public async Task DeleteButtonRemovesTheRun()
    {
        SeedSettings();
        _graph.AddFile(Library(), "Fin/a.xlsx", new byte[10]);
        long id = await InventoryAsync();

        // The confirmation is shown by the dialog provider, outside the dashboard: look in the whole page.
        var root = RenderPage<RunDashboard>(p => p.Add(x => x.RunId, id));
        await root.InvokeAsync(() => root.FindAll("button").First(b => b.TextContent.Contains("Видалити…")).Click());
        root.WaitForAssertion(() => Assert.Contains("Файлів у SharePoint це не змінює", root.Markup));
        Assert.NotNull(_store.GetRun(id));

        await root.InvokeAsync(() => root.FindAll("button").Last(b => b.TextContent.Trim() == "Видалити").Click());
        root.WaitForAssertion(() => Assert.Null(_store.GetRun(id)));
    }

    [Fact]
    public void RenameUpdatesProfilesThatUseIt()
    {
        SeedSettings();
        var updated = _settings.Rename(SettingKinds.Connection, "app-a", "app-main");
        Assert.Equal(["split"], updated);
        Assert.Null(_settings.GetConnection("app-a"));
        Assert.Equal(["app-main"], _settings.GetProfile("split")!.Connections);
        Assert.True(_runs.ValidateProfile(_settings.GetProfile("split")!).IsValid);

        _settings.Rename(SettingKinds.Profile, "split", "Split");
        Assert.Equal("Split", _settings.GetProfile("Split")!.Name);
        _store.PutSetting(SettingKinds.Connection, "other", new Connection { Name = "other" }.ToJson(), DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => _settings.Rename(SettingKinds.Connection, "app-main", "other"));
        Assert.Empty(_settings.Rename(SettingKinds.Profile, "Split", "Split"));
        Assert.Equal(["Split"], _settings.UsedBy(SettingKinds.Connection, "app-main"));
    }

    [Fact]
    public void SortsFilesOnTheServer()
    {
        SeedSettings();
        long id = SyntheticRun.Create(_store, Path.Combine(_dir, "state.db"), "split", Snapshot(), 500);
        var bySize = _store.QueryFiles(id, new FileFilter(), 0, 500, "size", descending: true);
        Assert.Equal(bySize.Select(f => f.Size).OrderByDescending(s => s), bySize.Select(f => f.Size));
        var byPath = _store.QueryFiles(id, new FileFilter(), 0, 50, "path");
        Assert.Equal(byPath.Select(f => f.Path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), byPath.Select(f => f.Path));
        // Unknown column: falls back to id, no SQL injection.
        Assert.Equal(_store.QueryFiles(id, new FileFilter(), 0, 5).Select(f => f.Id), _store.QueryFiles(id, new FileFilter(), 0, 5, "1; DROP TABLE files").Select(f => f.Id));
    }

    [Fact]
    public async Task FilePageShowsMetadataAndTheLinkToSharePoint()
    {
        SeedSettings();
        _graph.AddFile(Library(), "Fin/Звіт 1.xlsx", new byte[3000]);
        long id = await InventoryAsync();
        var file = _store.QueryFiles(id, new FileFilter(), 0, 10).Single();

        var page = RenderWithProviders<FilePage>(p => p.Add(x => x.Id, file.Id));
        Assert.Contains("Fin/Звіт 1.xlsx", page.Markup);
        Assert.Contains("listed", page.Markup);
        Assert.Contains("Відкрити в SharePoint", page.Markup);
        Assert.Contains("Fin/%D0%97%D0%B2%D1%96%D1%82%201.xlsx", page.Markup);
        Assert.DoesNotContain("версію до зміни", page.Markup);
    }

    [Fact]
    public async Task NewRunDialogAppliesOneOffChangesWithoutSavingThem()
    {
        SeedSettings();
        Library();
        var root = Render(b =>
        {
            b.OpenComponent<MudPopoverProvider>(0);
            b.CloseComponent();
            b.OpenComponent<MudDialogProvider>(1);
            b.CloseComponent();
        });
        var dialogs = Services.GetRequiredService<IDialogService>();
        IDialogReference? reference = null;
        await root.InvokeAsync(async () => reference = await dialogs.ShowAsync<NewRunDialog>("Новий прогін", new DialogParameters<NewRunDialog> { { x => x.Profile, "split" } }));
        root.WaitForAssertion(() => Assert.Contains("Разові зміни", root.Markup));
        Assert.Contains("inventory", root.Markup);

        // The first numeric field of the one-off panel is "Запитів одночасно".
        var concurrency = root.FindAll("input").First(i => i.GetAttribute("value") == "8");
        concurrency.Change("2");
        root.WaitForAssertion(() => Assert.Contains("(є зміни)", root.Markup));
        await root.InvokeAsync(() => root.FindAll("button").First(b => b.TextContent.Trim() == "Запустити").Click());

        var result = await reference!.Result;
        long runId = Assert.IsType<long>(result!.Data);
        var snapshot = RunConfig.Parse(_store.GetRun(runId)!.ConfigSnapshot);
        Assert.Equal(2, snapshot.Profile.Performance.Concurrency);
        Assert.Equal(8, _settings.GetProfile("split")!.Performance.Concurrency);
        Assert.Equal(RunModes.Inventory, _store.GetRun(runId)!.Mode);
    }

    [Fact]
    public void ProfileEditorMarksErrorsOnTheirTabsWhenOpened()
    {
        SeedSettings();
        var p = Profile.Parse(_store.GetSetting(SettingKinds.Profile, "split")!);
        p.Connections = ["missing"];
        p.Performance.Concurrency = 0;
        _store.PutSetting(SettingKinds.Profile, "split", p.ToJson(), DateTimeOffset.UtcNow);
        var cut = RenderWithProviders<ProfileEdit>(x => x.Add(y => y.Name, "split"));
        Assert.Contains("Помилок: 2", cut.Markup);
        // Each error is next to its section (the badge with the count is on the tab).
        Assert.Equal(2, cut.FindAll(".mud-tab .mud-badge").Count);
        Assert.Contains("Бібліотеки", cut.Markup);
        Assert.DoesNotContain("Фільтри", cut.Markup);
        Assert.DoesNotContain("Місця заміни", cut.Markup);
    }

    [Fact]
    public async Task ExportsAreSavedFromThePages()
    {
        SeedSettings();
        _graph.AddFile(Library(), "Fin/a.xlsx", new byte[10]);
        long id = await InventoryAsync();

        var table = RenderWithProviders<FilesTable>(p => p.Add(x => x.RunId, id));
        await table.InvokeAsync(() => table.FindAll("button").First(b => b.TextContent.Trim() == "XLSX").Click());
        table.WaitForAssertion(() => Assert.True(Saver.Saved.ContainsKey($"run-{id}-files.xlsx")));
        var rows = Core.Reports.SimpleXlsxReader.ReadFirstSheet(new MemoryStream(Saver.Saved[$"run-{id}-files.xlsx"]));
        Assert.Equal("path", rows[0][2]);
        Assert.Equal("Fin/a.xlsx", rows[1][2]);

        await DisposeComponentsAsync();
        var profile = RenderWithProviders<ProfileEdit>(p => p.Add(x => x.Name, "split"));
        await profile.InvokeAsync(() => profile.FindAll("button").First(b => b.TextContent.Trim() == "JSON").Click());
        profile.WaitForAssertion(() => Assert.True(Saver.Saved.ContainsKey("split.profile.json")));
        Assert.Contains("\"targetLibraries\"", System.Text.Encoding.UTF8.GetString(Saver.Saved["split.profile.json"]));
    }

    [Fact]
    public async Task ProfileEditorSavesAndShowsValidation()
    {
        SeedSettings();
        var cut = RenderWithProviders<ProfileEdit>(p => p.Add(x => x.Name, "split"));
        Assert.Contains("Профіль «split»", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent.Contains("Зберегти")).Click();
        Assert.Contains("Помилок немає", cut.Markup);
        Assert.NotNull(_store.GetSetting(SettingKinds.Profile, "split"));

        // An unknown connection makes the profile invalid; the editor says why.
        var p = Profile.Parse(_store.GetSetting(SettingKinds.Profile, "split")!);
        p.Connections = ["missing"];
        _store.PutSetting(SettingKinds.Profile, "split", p.ToJson(), DateTimeOffset.UtcNow);
        await DisposeComponentsAsync();
        cut = RenderWithProviders<ProfileEdit>(x => x.Add(y => y.Name, "split"));
        cut.FindAll("button").First(b => b.TextContent.Contains("Перевірити")).Click();
        Assert.Contains("Помилок: 1", cut.Markup);
        Assert.Single(cut.FindAll(".mud-tab .mud-badge"));
    }

    [Fact]
    public async Task ErrorsViewGroupsFailures()
    {
        SeedSettings();
        long id = SyntheticRun.Create(_store, Path.Combine(_dir, "state.db"), "split", Snapshot(), 20_000);
        var cut = RenderWithProviders<ErrorsView>(p => p.Add(x => x.RunId, id));
        Assert.Contains("failed:internal", cut.Markup);
        Assert.Contains("помилка інструменту", cut.Markup);
        cut.Render(p => p.Add(x => x.Scope, StateStore.ErrorScope.Failed));
        Assert.Contains("failed:internal", cut.Markup);
        Assert.Equal(_store.CountAttention(id), _store.GroupErrors(id, StateStore.ErrorScope.Attention).Sum(g => g.Count));

        // The "attention" filter value takes exactly what the card counts, alone or with other statuses.
        long attention = _store.CountAttention(id);
        Assert.True(attention > 0);
        Assert.Equal(attention, _store.CountFiles(id, new FileFilter { Statuses = [FileStatuses.Attention] }));
        Assert.True(_store.CountFiles(id, new FileFilter { Statuses = [FileStatuses.Attention, FileStatuses.Listed] }) > attention);

        await DisposeComponentsAsync();
        var dashboard = RenderWithProviders<RunDashboard>(p => p.Add(x => x.RunId, id));
        Assert.Contains($"href=\"runs/{id}?tab=errors&amp;show=failed\"", dashboard.Markup);
        Assert.Contains($"href=\"runs/{id}?tab=errors&amp;show=attention\"", dashboard.Markup);

        // The tab and the filter go to the address, so the same card link works again after switching tabs by hand.
        await DisposeComponentsAsync();
        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        var page = RenderWithProviders<RunPage>(p => p.Add(x => x.Id, id));
        page.WaitForAssertion(() => Assert.Contains(page.FindAll(".mud-tab"), t => t.TextContent.Contains("Помилки й увага")));
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Помилки й увага")).Click();
        page.WaitForAssertion(() => Assert.EndsWith("?tab=errors", nav.Uri));
        page.WaitForAssertion(() => Assert.Contains(page.FindAll("button"), b => b.TextContent.Trim() == "Помилки"));
        page.FindAll("button").First(b => b.TextContent.Trim() == "Помилки").Click();
        page.WaitForAssertion(() => Assert.EndsWith("?tab=errors&show=failed", nav.Uri));
        page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Огляд")).Click();
        page.WaitForAssertion(() => Assert.DoesNotContain("tab=", nav.Uri));
    }

    [Fact]
    public void MappingEditorValidatesWhileTypingAndTestsAPath()
    {
        var rows = new List<Core.Mapping.MappingEntry>
        {
            new() { Source = "Фін", Library = "Фінанси", Path = "/" },
            new() { Source = "Фін", Library = "Кадри" },
        };
        var cut = RenderWithProviders<MappingEditor>(p => p.Add(x => x.Rows, rows).Add(x => x.Libraries, ["Фінанси"]).Add(x => x.SourceLibrary, "Documents"));
        Assert.Contains("не додано до цільових бібліотек", cut.Markup);
        Assert.Contains("помилок: <b class=\"mud-error-text\">1</b>", cut.Markup);

        rows.RemoveAt(1);
        cut.Render(p => p.Add(x => x.Rows, rows));
        Assert.Contains("помилок: <b>0</b>", cut.Markup);
        cut.FindAll("input").Last().Input("Фін/2023/звіт.xlsx");
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Перевірити").Click();
        Assert.Contains("→ Фінанси:/2023/звіт.xlsx (рядок 1)", cut.Markup);
    }

    [Fact]
    public async Task CoveragePageShowsWhatMovesAndWhatStays()
    {
        SeedSettings();
        var lib = Library();
        _graph.AddFile(lib, "Fin/a.xlsx", new byte[100]);
        _graph.AddFile(lib, "HR/b.pdf", new byte[10]);

        var empty = RenderWithProviders<CoveragePage>(p => p.Add(x => x.Name, "split"));
        empty.WaitForAssertion(() => Assert.Contains("ще немає завершеної інвентаризації", empty.Markup));
        await DisposeComponentsAsync();

        await InventoryAsync();
        var page = RenderWithProviders<CoveragePage>(p => p.Add(x => x.Name, "split"));
        page.WaitForAssertion(() => Assert.Contains("Переміщується", page.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains("Без правила: найбільші папки", page.Markup);
        Assert.Contains(">HR<", page.Markup);
        Assert.Contains("Фінанси:/", page.Markup);
    }

    /// <summary>NFR-2: the GUI stays responsive on a large run (criterion: under 1 s). 200 000 files here; the pilot checks 1,5 million.</summary>
    [Fact]
    [Trait("Category", "Performance")]
    public void LargeRunStaysResponsive()
    {
        SeedSettings();
        long id = SyntheticRun.Create(_store, Path.Combine(_dir, "state.db"), "split", Snapshot(), 200_000);

        void Within(string what, Action action)
        {
            action(); // warm-up: first query compiles statements and fills the page cache
            var sw = Stopwatch.StartNew();
            action();
            Assert.True(sw.ElapsedMilliseconds < 1000, $"{what}: {sw.ElapsedMilliseconds} ms");
        }

        var status = new StatusService(_runs);
        Within("стан прогону", () => status.Get(id, fresh: true));
        Within("сторінка файлів", () => _store.QueryFiles(id, new FileFilter { Statuses = ["listed"] }, 5000, 50));
        Within("пошук", () => _store.QueryFiles(id, new FileFilter { Search = "0012" }, 0, 50));
        Within("групи помилок", () => _store.GroupErrors(id));
        Within("розбивки", () => { _store.BreakdownBy(id, "ext"); _store.BreakdownBy(id, "target_idx"); });
        Within("оцінка профілю", () => ProfileInsights.For(_store, _runs.GetProfile("split")));

        // MAP-5 writes a preview row per file: not instant, but it must stay well within a minute on 1,5 million files.
        var profile = _runs.GetProfile("split");
        profile.Mapping = [new() { Source = "Fin", Library = "Фінанси", Path = "/" }, new() { Source = "HR", Library = "Фінанси", Path = "Кадри" }];
        var sw = Stopwatch.StartNew();
        var report = MappingCheck.Run(_store, profile, _store.GetRun(id));
        Assert.Equal(_store.CountFiles(id, new FileFilter { TargetIndex = 0 }), report.Files); // half of the synthetic files are in the source
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"перевірка мапінгу (100 000 файлів джерела з 200 000): {sw.Elapsed.TotalSeconds:0.0} с");
        Console.WriteLine($"MappingCheck 100k of 200k: {sw.Elapsed.TotalSeconds:0.0} s");
        Within("дашборд", () =>
        {
            DisposeComponentsAsync().GetAwaiter().GetResult();
            RenderWithProviders<RunDashboard>(p => p.Add(x => x.RunId, id));
        });
    }

    // ------------------------------------------------------------------ stage 5: plan and move in the window

    [Fact]
    public async Task ThePlanIsBuiltLookedAtAndMovedFromTheWindow()
    {
        SeedSettings();
        var library = Library();
        _graph.AddFile(library, "Fin/2023/a.xlsx", new byte[100]);
        _graph.AddFile(library, "Fin/b.xlsx", new byte[50]);
        _graph.AddFile(library, "HR/c.docx", new byte[10]);
        await InventoryAsync();

        var host = RenderPage<PlanPage>(p => p.Add(x => x.Name, "split"));
        var page = host.FindComponent<PlanPage>();
        Assert.Contains("ще немає плану", page.Markup);
        await page.InvokeAsync(() => page.FindAll("button").First(b => b.TextContent.Contains("Побудувати план")).Click());
        page.WaitForAssertion(() => Assert.Contains("Найбільший пакет", page.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains("Fin → Фінанси:/", page.Markup); // the wave

        // The source tree: Fin is split (its target is the library root), 2023 moves whole, HR has no rule.
        await page.InvokeAsync(() => page.FindAll(".mud-tab").First(t => t.TextContent.Contains("Дерево джерела")).Click());
        page.WaitForAssertion(() => Assert.Contains("розбито", page.Markup));
        await page.InvokeAsync(() => page.FindAll("button[aria-label='Розгорнути']").First().Click());
        page.WaitForAssertion(() => Assert.Contains("папка цілком", page.Markup));
        Assert.Contains("без правила", page.Markup);

        // The move asks for a confirmation and opens the run.
        await page.InvokeAsync(() => page.FindAll("button").First(b => b.TextContent.Contains("Перемістити…")).Click());
        host.WaitForAssertion(() => Assert.Contains("змінює SharePoint", host.Markup));
        // The dialog checks the target libraries first (columns, content types, settings).
        host.WaitForAssertion(() => Assert.Contains("підходять для файлів з джерела", host.Markup), TimeSpan.FromSeconds(10));
        var start = host.FindAll("button").First(b => b.TextContent.Trim() == "Перемістити");
        Assert.True(start.HasAttribute("disabled"));
        await host.InvokeAsync(() => host.Find(".mud-dialog input[type=checkbox]").Change(true));
        await host.InvokeAsync(() => host.FindAll("button").First(b => b.TextContent.Trim() == "Перемістити").Click());
        var nav = Services.GetRequiredService<NavigationManager>();
        host.WaitForAssertion(() => Assert.Matches(@"runs/\d+$", nav.Uri), TimeSpan.FromSeconds(10));
        long run = long.Parse(nav.Uri[(nav.Uri.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(RunStatuses.Completed, await _runs.Active!.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(RunModes.Apply, _store.GetRun(run)!.Mode);
        Assert.All(_store.QueryFiles(run, new FileFilter(), 0, 100), f => Assert.Equal(FileStatuses.Moved, f.Status));

        // The run page of a move: batches tab, moved counter, verify.
        await DisposeComponentsAsync();
        var dialogs = RenderPage<RunPage>(p => p.Add(x => x.Id, run));
        var runPage = dialogs.FindComponent<RunPage>();
        runPage.WaitForAssertion(() => Assert.Contains(runPage.FindAll(".mud-tab"), t => t.TextContent.Contains("Пакети")));
        runPage.WaitForAssertion(() => Assert.Contains("Переміщено", runPage.Markup));
        Assert.Contains("Хвилі", runPage.Markup);
        await runPage.InvokeAsync(() => runPage.FindAll("button").First(b => b.TextContent.Contains("Звірити…")).Click());
        dialogs.WaitForAssertion(() => Assert.Contains("Оновити інвентаризацію і звірити", dialogs.Markup));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").First(b => b.TextContent.Contains("Оновити інвентаризацію і звірити")).Click());
        dialogs.WaitForAssertion(() => Assert.Contains("Усе переміщене на місці", dialogs.Markup), TimeSpan.FromSeconds(30));
        Assert.All(_store.QueryFiles(run, new FileFilter(), 0, 100), f => Assert.Equal(FileStatuses.Verified, f.Status));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").First(b => b.TextContent.Trim() == "Закрити").Click());

        // After verify: the pilot report is in the reports, and the emptied source folders can go.
        runPage.WaitForAssertion(() => Assert.Contains(runPage.FindAll("button"), b => b.TextContent.Contains("Видалити порожні папки…")), TimeSpan.FromSeconds(10));
        Assert.Contains("Звіт пілоту", PilotReport.Text(_store, run));
        await runPage.InvokeAsync(() => runPage.FindAll("button").First(b => b.TextContent.Contains("Видалити порожні папки…")).Click());
        dialogs.WaitForAssertion(() => Assert.Contains("кошик сайту", dialogs.Markup), TimeSpan.FromSeconds(10));
        Assert.Contains("<b>2</b> папок", dialogs.Markup); // Fin/2023 and Fin; never HR, never the root
        await dialogs.InvokeAsync(() => dialogs.Find(".mud-dialog input[type=checkbox]").Change(true));
        await dialogs.InvokeAsync(() => dialogs.FindAll("button").First(b => b.TextContent.Trim() == "Видалити порожні").Click());
        dialogs.WaitForAssertion(() => Assert.Matches(@"runs/\d+$", nav.Uri), TimeSpan.FromSeconds(10));
        long cleanup = long.Parse(nav.Uri[(nav.Uri.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.NotEqual(run, cleanup);
        Assert.Equal(RunStatuses.Completed, await _runs.Active!.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(RunModes.Cleanup, _store.GetRun(cleanup)!.Mode);
        Assert.Contains(_store.QueryFiles(cleanup, new FileFilter(), 0, 100), f => f.Path == "Fin" && f.Status == FileStatuses.Deleted);
    }

    [Fact]
    public void ThePlanningTabShowsItsErrors()
    {
        SeedSettings();
        var p = Profile.Parse(_store.GetSetting(SettingKinds.Profile, "split")!);
        p.Plan.MaxBatchItems = PlanOptions.ThresholdItems;
        _store.PutSetting(SettingKinds.Profile, "split", p.ToJson(), DateTimeOffset.UtcNow);
        var cut = RenderWithProviders<ProfileEdit>(x => x.Add(y => y.Name, "split"));
        Assert.Contains("Помилок: 1", cut.Markup);
        var tab = Assert.Single(cut.FindAll(".mud-tab"), t => t.TextContent.Contains("Планування"));
        Assert.NotNull(tab.QuerySelector(".mud-badge"));
    }

    [Fact]
    public async Task PlansPageShowsWhereEachProfileStands()
    {
        SeedSettings();
        _graph.AddFile(Library(), "Fin/a.xlsx", new byte[10]);
        long inventory = await InventoryAsync();
        Planner.Build(_store, _runs.GetProfile("split"), DateTimeOffset.UtcNow);
        var cut = RenderWithProviders<PlansPage>();
        Assert.Contains($"прогін {inventory}", cut.Markup);
        Assert.Contains("план 1", cut.Markup);
        Assert.Contains("ще не було", cut.Markup);
    }
}
