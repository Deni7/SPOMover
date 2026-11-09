// Fills a data folder with demo settings and runs, made by the real engine against the fake Graph,
// so the app can be looked at without SharePoint.
// Usage: DemoSeed <dataDir> [--large N] [--open] [--port PORT]
//        DemoSeed <dataDir> --bench N   — a synthetic inventory of N files with a folder tree, then the plan, the plan tree,
//                                         an apply run and the dashboard queries on it, with the time of each step (stage 5).
// --open starts the desktop app on these data with the same fake Graph, so new runs can be started from its window;
// --port is the local API port for the CLI (default 5098).
// The fake SharePoint is saved to <dataDir>\fake-sharepoint.json when the app closes, so the same folder can be
// opened again later (e.g. to continue a stopped run).
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using SPOMover.Core.Config;
using SPOMover.Core.Mapping;
using SPOMover.Engine;
using SPOMover.State;
using SPOMover.Tests.Shared;

string dir = Path.GetFullPath(args.Length > 0 ? args[0] : "out/demo-data");
int large = Option("--large") is { } l ? int.Parse(l) : 0;
bool open = args.Contains("--open") || args.Contains("--serve");
int port = (Option("--serve") ?? Option("--port")) is { } s && int.TryParse(s, out int parsed) ? parsed : 5098;
string dbPath = Path.Combine(dir, "state.db");
if (Option("--bench") is { } benchFiles)
    return Bench(int.Parse(benchFiles, System.Globalization.CultureInfo.InvariantCulture));
string graphPath = Path.Combine(dir, "fake-sharepoint.json");
Directory.CreateDirectory(dir);

FakeGraph graph;
if (File.Exists(dbPath))
{
    if (!File.Exists(graphPath))
    {
        Console.Error.WriteLine($"Folder {dir} has demo data without a saved emulated SharePoint (made by an old version of this tool). Specify a new folder.");
        return 2;
    }
    if (!open)
    {
        Console.Error.WriteLine($"Folder {dir} already has demo data. To open it, add --open; to create new data, specify another folder.");
        return 2;
    }
    graph = FakeGraph.Load(graphPath);
    Console.WriteLine($"Opening the existing demo data from {dir}…");
}
else
{
    graph = await Seed();
    graph.Save(graphPath);
}

if (!open)
    return 0;

graph.Faults.Clear();
Console.WriteLine("Opening SPOMover on these data (SharePoint is emulated)…");
// WPF needs an STA thread; the top-level program is not one.
int exit = 0;
var ui = new Thread(() => exit = SPOMover.Desktop.DesktopApp.Run(
    ["--data", dir, "--urls", $"http://localhost:{port}", "--continueInterrupted", "false", .. Option("--selftest") is { } st ? new[] { "--selftest", st } : []],
    services =>
    {
        services.AddSingleton<SPOMover.Spo.ISpoAppFactory>(new FakeAppFactory(graph));
        services.AddSingleton(new EngineOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(50),
            MetricsInterval = TimeSpan.FromSeconds(5),
        });
    }));
ui.SetApartmentState(ApartmentState.STA);
ui.Start();
ui.Join();
// What the runs wrote to the fake SharePoint is kept for the next opening of this folder.
graph.Save(graphPath);
Console.WriteLine($"Emulated SharePoint saved: {graphPath}");
return exit;

async Task<FakeGraph> Seed()
{
    string withLarge = large > 0 ? $" (with a synthetic inventory of {large} files)" : "";
    Console.WriteLine($"Preparing demo data in {dir}{withLarge}…");
    const string Host = "https://" + FakeGraph.Host;
    var store = new StateStore(dbPath);
    var g = new FakeGraph { DeltaPageSize = 50 };
    // The source library to split and two target libraries of the same site (FEATURES: one site, move).
    var source = g.AddLibrary("/sites/archive", "Documents", "Shared Documents");
    var finance = g.AddLibrary("/sites/archive", "Finance", "Finance");
    g.AddLibrary("/sites/archive", "HR", "HR");
    var now = DateTimeOffset.UtcNow;

    store.PutSetting(SettingKinds.Connection, "demo-app", new Connection
    {
        Name = "demo-app",
        TenantId = "contoso.onmicrosoft.com",
        ClientId = "11111111-2222-3333-4444-555555555555",
        CertificateThumbprint = new string('A', 40),
        Licenses = LicenseTier.UpTo5000,
    }.ToJson(), now);
    var profile = new Profile
    {
        Name = "archive",
        Source = new Target { SiteUrl = Host + "/sites/archive", Library = "Documents" },
        TargetLibraries = ["Finance", "HR"],
        Mapping =
        [
            new MappingEntry { Source = "Finance", Library = "Finance", Path = "/" },
            new MappingEntry { Source = "HR", Library = "HR", Path = "/" },
            new MappingEntry { Source = "HR/Personnel files", Library = MappingEntry.Stay, Comment = "until a separate decision" },
            new MappingEntry { Source = "Contracts/Archive", Library = "Finance", Path = "Contracts" },
        ],
        Connections = ["demo-app"],
    };
    profile.Retry.RequestAttempts = 2;
    store.PutSetting(SettingKinds.Profile, "archive", profile.ToJson(), now);

    string[] folders = ["Finance/2023", "Finance/2024/Quarter 1", "HR/Orders", "HR/Personnel files", "Contracts", "Contracts/Archive", "Legal"];
    string[] exts = [".xlsx", ".docx", ".pdf", ".pdf", ".msg", ".jpg", ".xlsb", ".zip"];
    var rnd = new Random(7);
    for (int i = 0; i < 600; i++)
    {
        string folder = folders[rnd.Next(folders.Length)];
        var item = g.AddFile(source, $"{folder}/Document {i:0000}{exts[rnd.Next(exts.Length)]}", new byte[1_000 + rnd.Next(400_000)]);
        if (i % 97 == 0)
            item.CheckedOutBy = "Ivan Petrenko";
    }
    // Uploaded but never checked in: only GetCheckedOutFiles sees it (INV-3).
    source.Unpublished.Add(("Finance/2024/Budget draft.xlsx", "Olena Koval"));
    // The same name in Contracts/Archive and in the target Finance/Contracts: a conflict for the coverage check (MAP-4).
    g.AddFile(source, "Contracts/Archive/Contract 001.pdf", new byte[5_000]);
    g.AddFile(finance, "Contracts/Contract 001.pdf", new byte[5_000]);
    // Something already lies in a target library.
    for (int i = 0; i < 25; i++)
        g.AddFile(finance, $"2025/Report {i:00}.xlsx", new byte[20_000 + rnd.Next(100_000)]);
    // A short hiccup of SharePoint: the listing waits and goes on.
    int busy = 0;
    g.Faults.Add(r => r.Path.Contains("/delta", StringComparison.Ordinal) && Interlocked.Increment(ref busy) == 4
        ? FakeGraph.Error(HttpStatusCode.TooManyRequests, "activityLimitReached", 1)
        : null);

    var service = new RunService(store, new FakeAppFactory(g), NullLoggerFactory.Instance, new EngineOptions
    {
        PollInterval = TimeSpan.FromMilliseconds(20),
        MetricsInterval = TimeSpan.FromMilliseconds(300),
    });
    long id = service.CreateRun(RunModes.Inventory, "archive");
    Console.WriteLine($"inventory: run {id} — {await service.Start(id).Completion}");

    // Stage 5: a plan and a move of it, so the plan page and the run page of a move have something to show.
    g.Faults.Clear();
    var demoPlan = Planner.Build(store, profile, DateTimeOffset.UtcNow);
    Console.WriteLine($"plan {demoPlan.Plan.Id}: {demoPlan.Plan.Batches} batches, {demoPlan.Plan.Files} files");
    // Files on check-out wait during the move, then the run finishes with them as failed:locked (attention, retry).
    profile.Retry.LockedWaveMinutes = 0.01;
    profile.Retry.LockedLimitMinutes = 0.03;
    profile.Retry.FilePausesMinutes = [0];
    store.PutSetting(SettingKinds.Profile, "archive", profile.ToJson(), DateTimeOffset.UtcNow);
    long apply = service.CreateApplyRun("archive", demoPlan.Plan.Id);
    Console.WriteLine($"apply: run {apply} — {await service.Start(apply).Completion}");
    profile.Retry = new RetryOptions { RequestAttempts = 2 };
    store.PutSetting(SettingKinds.Profile, "archive", profile.ToJson(), DateTimeOffset.UtcNow);

    if (large > 0)
    {
        var config = new RunConfig { Profile = profile, Connections = [.. service.Connections()] };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        long synthetic = SyntheticRun.Create(store, dbPath, "archive-large", config.ToJson(), large);
        Console.WriteLine($"synthetic inventory {synthetic}: {large} files in {sw.Elapsed.TotalSeconds:0} s");
    }
    await service.DisposeAsync();
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Console.WriteLine("Done: " + dir);
    return g;
}

// Stage 5: how long the steps take on a large library (NFR-2). Nothing goes to SharePoint.
int Bench(int files)
{
    Directory.CreateDirectory(dir);
    if (File.Exists(dbPath))
    {
        Console.Error.WriteLine($"Folder {dir} already has a database; specify an empty folder for the benchmark.");
        return 2;
    }
    const string Host = "https://" + FakeGraph.Host;
    var store = new StateStore(dbPath);
    var targets = SyntheticTree.Departments.Take(10).Select(d => d + " (lib)").ToList();
    var profile = new Profile
    {
        Name = "bench",
        Source = new Target { SiteUrl = Host + "/sites/archive", Library = "Documents" },
        TargetLibraries = targets,
        // Ten departments into their own libraries; two of them keep a year in the source; scans go to one library.
        Mapping = [.. SyntheticTree.Departments.Take(10).Select((d, i) => new MappingEntry { Source = d, Library = targets[i], Path = "/" }),
            new MappingEntry { Source = SyntheticTree.Departments[1] + "/2005", Library = MappingEntry.Stay }, new MappingEntry { Source = SyntheticTree.Departments[2] + "/2024", Library = MappingEntry.Stay },
            new MappingEntry { Source = "Сканування", Library = targets[0], Path = "Сканування" }], // i18n-ignore: a folder of the synthetic tree (tests/Shared)
        Connections = ["demo-app"],
    };
    store.PutSetting(SettingKinds.Connection, "demo-app", new Connection { Name = "demo-app", TenantId = "t", ClientId = Guid.NewGuid().ToString(), CertificateThumbprint = new string('A', 40) }.ToJson(), DateTimeOffset.UtcNow);
    store.PutSetting(SettingKinds.Profile, "bench", profile.ToJson(), DateTimeOffset.UtcNow);
    var config = new RunConfig { Profile = profile, Connections = [Connection.Parse(store.GetSetting(SettingKinds.Connection, "demo-app")!)] };
    var results = new List<(string Step, TimeSpan Time, string Detail)>();
    T Step<T>(string name, Func<T> action, Func<T, string> detail)
    {
        GC.Collect();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = action();
        sw.Stop();
        results.Add((name, sw.Elapsed, detail(result)));
        Console.WriteLine($"{name,-44} {sw.Elapsed.TotalSeconds,8:0.0} s  {detail(result)}  (memory {Environment.WorkingSet / 1024 / 1024} MB)");
        return result;
    }

    long inventory = Step("Synthetic inventory", () => SyntheticTree.Create(store, dbPath, "bench", config.ToJson(), profile.Libraries().Count, files,
        Host + "/sites/archive/Shared%20Documents"), id => $"run {id}, {files:N0} files");
    var coverage = Step("Coverage check (MAP-5)", () => MappingCheck.Run(store, profile), r => $"moving {r.MovedFiles:N0}, staying {r.StayFiles + r.UnmappedFiles:N0}");
    var plan = Step("Plan (PLN-1…3)", () => Planner.Build(store, profile, DateTimeOffset.UtcNow), r => $"{r.Plan.Batches:N0} batches, {r.Plan.Folders:N0} folders to create, largest {r.LargestBatchItems:N0}");
    var view = Step("Plan tree: load", () => new PlanView(store, plan.Plan.Id), v => $"{v.Batches.Count:N0} batches in memory");
    Step("Plan tree: root and a department", () => view.Children("").Count + view.Children(SyntheticTree.Departments[0]).Count, n => $"{n} nodes");
    Step("Plan report (summary, waves)", () => Planner.Report(store, plan.Plan.Id), r => $"{r.Waves.Count} waves, forecast {r.Duration.TotalHours:0.0} h");
    var service = new RunService(store, new FakeAppFactory(new FakeGraph()), NullLoggerFactory.Instance, new EngineOptions());
    long apply = Step("Apply run: create", () => service.CreateApplyRun("bench", plan.Plan.Id), id => $"run {id}");
    var status = new SPOMover.Ui.Services.StatusService(service);
    Step("Apply run dashboard (counters)", () => status.Get(apply, fresh: true)!, s => $"{s.Files:N0} files in the run");
    Step("Dashboard: waves", () => store.GetWaveProgress(apply), w => $"{w.Count} waves");
    Step("Run batches (tab)", () => store.GetApplyBatches(apply), b => $"{b.Count:N0} batches");
    Step("File table: first page", () => store.QueryFiles(apply, new FileFilter(), 0, 50, "path"), f => $"{f.Count} rows");
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Console.WriteLine($"Database: {new FileInfo(dbPath).Length / 1024 / 1024:N0} MB ({dbPath})");
    return 0;
}

string? Option(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
