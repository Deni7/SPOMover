using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SPOMover.Cli;
using SPOMover.Engine;
using SPOMover.Spo;
using SPOMover.State;
using SPOMover.Tests.Shared;

namespace SPOMover.Host.Tests;

/// <summary>CLI → host REST API → engine → fake SharePoint: the way an administrator runs the tool.</summary>
public sealed class HostTests : IAsyncLifetime
{
    private const string Host = "https://" + FakeGraph.Host;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spo-mover-host", Guid.NewGuid().ToString("N"));
    private readonly FakeGraph _graph = new();
    private FakeGraph.FakeDrive _library = null!;
    private WebApplicationFactory<HostEntry> _factory = null!;
    private readonly StringWriter _out = new();

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        _library = _graph.AddLibrary("/sites/archive", "Documents", "Shared Documents");
        _graph.AddLibrary("/sites/archive", "Фінанси", "Finance");
        _factory = CreateFactory(continueInterrupted: false);
        return Task.CompletedTask;
    }

    private WebApplicationFactory<HostEntry> CreateFactory(bool continueInterrupted) => new WebApplicationFactory<HostEntry>().WithWebHostBuilder(b =>
    {
        b.UseSetting("data", _dir);
        b.UseSetting("continueInterrupted", continueInterrupted ? "true" : "false");
        b.ConfigureTestServices(s =>
        {
            s.AddSingleton<ISpoAppFactory>(new FakeAppFactory(_graph));
            s.AddSingleton(new EngineOptions { PollInterval = TimeSpan.FromMilliseconds(20) });
        });
    });

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, true);
        }
        catch (IOException)
        {
        }
    }

    private HostCommands Cli(WebApplicationFactory<HostEntry>? factory = null) =>
        new(new HostClient(Http(factory)), _out);

    /// <summary>A client with the key of the instance, as the CLI of the same user has it.</summary>
    private HttpClient Http(WebApplicationFactory<HostEntry>? factory = null)
    {
        var f = factory ?? _factory;
        var http = f.CreateClient();
        http.DefaultRequestHeaders.Add(Core.Hosting.ApiEndpoint.KeyHeader, f.Services.GetRequiredService<ApiAccess>().Key);
        return http;
    }

    [Fact]
    public async Task ApiRefusesRequestsWithoutTheKeyOfThisInstance()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/runs")).StatusCode);
        anonymous.DefaultRequestHeaders.Add(Core.Hosting.ApiEndpoint.KeyHeader, new string('0', 64));
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/runs")).StatusCode);
        Assert.True((await Http().GetAsync("api/runs")).IsSuccessStatusCode);
    }

    [Fact]
    public void EndpointFileIsReadOnlyWhileItsProcessRuns()
    {
        new Core.Hosting.ApiEndpoint("http://127.0.0.1:5123", "K", Environment.ProcessId).Write(_dir);
        Assert.Equal("http://127.0.0.1:5123", Core.Hosting.ApiEndpoint.Read(_dir)?.Url);
        Core.Hosting.ApiEndpoint.Delete(_dir, Environment.ProcessId + 1); // another instance's file is left alone
        Assert.NotNull(Core.Hosting.ApiEndpoint.Read(_dir));
        Core.Hosting.ApiEndpoint.Delete(_dir, Environment.ProcessId);
        Assert.Null(Core.Hosting.ApiEndpoint.Read(_dir));

        new Core.Hosting.ApiEndpoint("http://127.0.0.1:5123", "K", int.MaxValue).Write(_dir); // a crashed instance
        Assert.Null(Core.Hosting.ApiEndpoint.Read(_dir));
    }

    private string Write(string name, string json)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private async Task ImportSettings(HostCommands cli)
    {
        Assert.Equal(0, await cli.PutSetting("connections", Write("app.json", $$"""
            { "name": "app-a", "tenantId": "contoso.onmicrosoft.com", "clientId": "{{Guid.NewGuid()}}",
              "certificateThumbprint": "{{new string('C', 40)}}", "licenses": "upTo5000" }
            """), null));
        Assert.Equal(0, await cli.PutSetting("profiles", Write("split.profile.json", $$"""
            {
              // as exported from the GUI
              "source": { "siteUrl": "{{Host}}/sites/archive", "library": "Documents" },
              "targetLibraries": [ "Фінанси" ],
              "connections": [ "app-a" ],
              "retry": { "requestAttempts": 2 }
            }
            """), null));
    }

    private FakeGraph.FakeItem AddFile(string path, int size = 100) => _graph.AddFile(_library, path, new byte[size]);

    [Fact]
    public async Task FullCycleFromTheCli()
    {
        AddFile("Fin/a.xlsx", 1000);
        AddFile("Fin/b.pdf", 500);
        AddFile("HR/c.docx", 250);
        var cli = Cli();
        await ImportSettings(cli);
        Assert.Equal(0, await cli.ValidateProfile("split"));

        Assert.Equal(ExitCodes.Ok, await cli.StartRun("inventory", "split", wait: true, CancellationToken.None));
        Assert.Contains("Файлів: 3", _out.ToString());
        Assert.Equal(0, await cli.Status(1));
        Assert.Contains("перелік завершено", _out.ToString());
        Assert.DoesNotContain("готово 3 (100%)", _out.ToString());
        // While listing: the share of the items SharePoint counted, not «done N of N».
        var listing = System.Text.Json.Nodes.JsonNode.Parse("""
            {"run":{"status":"running"},"files":40,"bytes":0,"nonFinal":0,"attention":0,"paused":false,"counts":{"listed":40},
             "targets":[{"index":0,"done":false,"expected":100,"files":40,"folders":10},{"index":1,"done":false,"expected":10,"files":0,"folders":0}]}
            """)!;
        Assert.Contains("переглянуто ≈ 45.5% (50 з ≈ 110 елементів), бібліотека 1 з 2", HostCommands.ProgressLine(listing));
        var store = _factory.Services.GetRequiredService<StateStore>();
        Assert.All(store.QueryFiles(1, new FileFilter(), 0, 100), f => Assert.Equal(FileStatuses.Listed, f.StatusCode));

        string reports = Path.Combine(_dir, "reports");
        Assert.Equal(0, await cli.Export(1, reports));
        Assert.Contains("HR/c.docx", File.ReadAllText(Path.Combine(reports, "files.csv")));
        Assert.True(File.Exists(Path.Combine(reports, "attention.csv")));
        Assert.Contains("За бібліотеками", File.ReadAllText(Path.Combine(reports, "summary.txt")));

        Assert.Equal(0, await cli.Files(1, "listed", null, "Fin", 10));
        Assert.Contains("Усього за фільтром: 2", _out.ToString());
        Assert.Equal(0, await cli.Runs(10));
        Assert.Contains("inventory", _out.ToString());
    }

    [Fact]
    public async Task MappingFromTheCli()
    {
        AddFile("Fin/2023/a.xlsx", 1000);
        AddFile("Fin/Чернетки/b.docx", 10);
        AddFile("HR/c.pdf", 5);
        var cli = Cli();
        await ImportSettings(cli);
        Assert.Equal(0, await cli.StartRun("inventory", "split", wait: true, CancellationToken.None));

        string csv = Write("map.csv", "Джерело;Бібліотека;Шлях\nFin;Фінанси;/\nFin/Чернетки;-;\n");
        Assert.Equal(0, await cli.MapImport("split", csv, append: false));
        Assert.Contains("Імпортовано рядків: 2", _out.ToString());
        Assert.Equal(0, await cli.MapTest("split", ["Fin/2023/a.xlsx", "Fin/Чернетки/b.docx", "HR/c.pdf"]));
        string text = _out.ToString();
        Assert.Contains("Fin/2023/a.xlsx → Фінанси:/2023/a.xlsx (рядок 1)", text);
        Assert.Contains("Fin/Чернетки/b.docx → лишається в джерелі (рядок 2)", text);
        Assert.Contains("HR/c.pdf → жодне правило не підходить", text);

        string reports = Path.Combine(_dir, "map");
        Assert.Equal(0, await cli.MapCheck("split", reports, CancellationToken.None));
        Assert.Contains("Переміщується: 1 файлів", _out.ToString());
        Assert.Contains("HR,1,5", File.ReadAllText(Path.Combine(reports, "mapping-unmapped.csv")));
        Assert.Contains("Fin/2023/a.xlsx,1000,1,Фінанси,2023/a.xlsx", File.ReadAllText(Path.Combine(reports, "mapping-preview.csv")));

        // PLN-1…PLN-5: the plan through the API, a second look at the saved plan.
        string planDir = Path.Combine(_dir, "plan");
        Assert.Equal(0, await cli.PlanBuild("split", planDir, CancellationToken.None));
        Assert.Contains("Пакетів: 1 (папок цілком 1", _out.ToString());
        string planCsv = Directory.GetFiles(planDir, "*-batches.csv").Single();
        Assert.Contains("1,1,folder,1,Fin/2023,Фінанси,2023,1,1000,1,0", File.ReadAllText(planCsv));
        long planId = long.Parse(Path.GetFileName(planCsv)["plan".Length..].Split('-')[0], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, await cli.PlanShow(planId, null, CancellationToken.None));
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await Http().GetAsync($"api/plans/{planId}/reports/nope")).StatusCode);

        // Stage 4: the plan is moved; the draft stays in the source.
        Assert.Equal(0, await cli.Apply("split", planId, wait: true, CancellationToken.None));
        Assert.Contains("apply, план", _out.ToString());
        Assert.Null(_library.Items.Values.FirstOrDefault(i => i.Name == "a.xlsx"));
        Assert.NotNull(_library.Items.Values.FirstOrDefault(i => i.Name == "b.docx"));
        long applyRun = long.Parse(System.Text.RegularExpressions.Regex.Match(_out.ToString(), @"Прогін (\d+) \(apply").Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, await cli.Verify(applyRun, update: true, Path.Combine(_dir, "verify"), CancellationToken.None));
        Assert.Contains("Підтверджено: 1", _out.ToString());
        string rules = Path.Combine(_dir, "rules.csv");
        Assert.Equal(0, await cli.ExportRules(applyRun, rules, CancellationToken.None));
        Assert.Contains("/sites/archive/Shared Documents/Fin/2023,https://contoso.sharepoint.com/sites/archive/Finance/2023", File.ReadAllText(rules));

        string exported = Path.Combine(_dir, "back.csv");
        Assert.Equal(0, await cli.MapExport("split", exported));
        Assert.Contains("Fin/Чернетки,'-", File.ReadAllText(exported));

        // A mapping error is reported, but it does not stop an inventory.
        Assert.Equal(ExitCodes.InvalidInput, await cli.MapImport("split", Write("bad.csv", "Fin;Невідома\n"), append: true));
        Assert.Equal(ExitCodes.Attention, await cli.MapCheck("split", null, CancellationToken.None));
        Assert.Equal(0, await cli.StartRun("inventory", "split", wait: true, CancellationToken.None));
    }

    [Fact]
    public async Task UnknownModesAndApplyWithoutAPlanAreRefused()
    {
        var cli = Cli();
        await ImportSettings(cli);
        var ex = await Assert.ThrowsAsync<HostException>(() => cli.StartRun("verify", "split", false, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains("inventory", ex.Message);
        ex = await Assert.ThrowsAsync<HostException>(() => cli.Apply("split", null, false, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, ex.Status);
        Assert.Contains("плану", ex.Message);
    }

    [Fact]
    public async Task InvalidProfileIsReportedWithIssues()
    {
        var cli = Cli();
        string file = Write("bad.json", """{ "source": { "siteUrl": "relative/path" }, "connections": [ "missing" ] }""");
        Assert.Equal(ExitCodes.InvalidInput, await cli.PutSetting("profiles", file, "bad"));
        Assert.Contains("ПОМИЛКА", _out.ToString());

        var ex = await Assert.ThrowsAsync<HostException>(() => cli.StartRun("inventory", "bad", false, CancellationToken.None));
        Assert.Equal(HttpStatusCode.BadRequest, ex.Status);
        Assert.Contains(ex.Issues, i => i.Contains("missing"));

        var notFound = await Assert.ThrowsAsync<HostException>(() => cli.Status(999));
        Assert.Equal(HttpStatusCode.NotFound, notFound.Status);
    }

    [Fact]
    public async Task OnlyOneRunAtATimeAndPauseStop()
    {
        for (int i = 0; i < 10; i++)
            AddFile($"f{i}.xlsx");
        var cli = Cli();
        await ImportSettings(cli);
        _graph.DeltaPageSize = 1;
        _graph.Faults.Add(r => { Thread.Sleep(100); return null; });

        Assert.Equal(0, await cli.StartRun("inventory", "split", wait: false, CancellationToken.None));
        var busy = await Assert.ThrowsAsync<HostException>(() => cli.StartRun("inventory", "split", false, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, busy.Status);

        Assert.Equal(0, await cli.Control(1, "pause", false, CancellationToken.None));
        Assert.Equal(0, await cli.Control(1, "resume", false, CancellationToken.None));
        Assert.Equal(HostCommands.RunNotFinished, await cli.Control(1, "stop", wait: true, CancellationToken.None));

        // continue the stopped run to the end
        _graph.Faults.Clear();
        Assert.Equal(0, await cli.Control(1, "start", wait: true, CancellationToken.None));
    }

    [Fact]
    public async Task RunIsDeletedFromTheCliOnlyWhenNotRunning()
    {
        for (int i = 0; i < 10; i++)
            AddFile($"f{i}.xlsx");
        var cli = Cli();
        await ImportSettings(cli);
        _graph.DeltaPageSize = 1;
        _graph.Faults.Add(r => { Thread.Sleep(100); return null; });
        Assert.Equal(0, await cli.StartRun("inventory", "split", wait: false, CancellationToken.None));

        var busy = await Assert.ThrowsAsync<HostException>(() => cli.DeleteRun(1, yes: true, CancellationToken.None));
        Assert.Equal(HttpStatusCode.Conflict, busy.Status);

        Assert.Equal(HostCommands.RunNotFinished, await cli.Control(1, "stop", wait: true, CancellationToken.None));
        Assert.Equal(0, await cli.DeleteRun(1, yes: true, CancellationToken.None));
        Assert.Contains("Прогін 1 видалено", _out.ToString());
        var gone = await Assert.ThrowsAsync<HostException>(() => cli.Status(1));
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
        var again = await Assert.ThrowsAsync<HostException>(() => cli.DeleteRun(1, yes: true, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, again.Status);
    }

    [Fact]
    public async Task InterruptedRunIsContinuedWhenTheHostStarts()
    {
        for (int i = 0; i < 3; i++)
            AddFile($"f{i}.xlsx");
        await ImportSettings(Cli());
        var service = _factory.Services.GetRequiredService<RunService>();
        long id = service.CreateRun(RunModes.Inventory, "split");
        // The host died while the run was going: the state says "running".
        service.Store.SetRunStatus(id, RunStatuses.Running, DateTimeOffset.UtcNow);
        await _factory.DisposeAsync();

        _factory = CreateFactory(continueInterrupted: true);
        var cli = Cli();
        Assert.Equal(0, await cli.Wait(id, CancellationToken.None));
        Assert.Equal(3, service.Store.CountFiles(id, new FileFilter()));
    }

    [Fact]
    public async Task ExportsForTheGui()
    {
        AddFile("Fin/a.xlsx");
        AddFile("Fin/b.pdf");
        var cli = Cli();
        await ImportSettings(cli);
        Assert.Equal(0, await cli.StartRun("inventory", "split", wait: true, CancellationToken.None));
        var http = Http();

        var xlsx = await http.GetAsync("api/runs/1/files/export?format=xlsx&ext=.xlsx");
        Assert.Equal("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", xlsx.Content.Headers.ContentType!.MediaType);
        var rows = Core.Reports.SimpleXlsxReader.ReadFirstSheet(await xlsx.Content.ReadAsStreamAsync());
        Assert.Equal(2, rows.Count); // header + the one xlsx file
        Assert.Equal("Fin/a.xlsx", rows[1][2]);

        var csv = await http.GetStringAsync("api/runs/1/files/export?format=csv");
        Assert.Equal(3, csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries).Length);

        var report = await http.GetAsync("api/runs/1/reports/attention?format=xlsx");
        Assert.True(report.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("api/runs/1/reports/links")).StatusCode);

        var json = await http.GetAsync("api/profiles/split?download=true");
        Assert.Equal("attachment", json.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("\"targetLibraries\"", await json.Content.ReadAsStringAsync());

    }
}
