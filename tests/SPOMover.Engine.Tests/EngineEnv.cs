using Microsoft.Extensions.Logging.Abstractions;
using SPOMover.Core.Config;
using SPOMover.State;
using SPOMover.Tests.Shared;

namespace SPOMover.Engine.Tests;

/// <summary>A fake tenant, a state store in a temp folder and a run service over them.</summary>
internal sealed class EngineEnv : IAsyncDisposable
{
    public const string Host = "https://" + FakeGraph.Host;

    public EngineEnv()
    {
        Dir = Path.Combine(Path.GetTempPath(), "spo-mover-engine", Guid.NewGuid().ToString("N"));
        Store = new StateStore(Path.Combine(Dir, "state.db"));
        Apps = new FakeAppFactory(Graph);
        Service = new RunService(Store, Apps, NullLoggerFactory.Instance, new EngineOptions
        {
            PollInterval = TimeSpan.FromMilliseconds(20),
            EnumerationRetryDelay = TimeSpan.FromMilliseconds(50),
        });
        Library = Graph.AddLibrary("/sites/archive", "Documents", "Shared Documents");
        Finance = Graph.AddLibrary("/sites/archive", "Фінанси", "Finance");
        AddConnection("app-a");
    }

    public string Dir { get; }
    public FakeGraph Graph { get; } = new();
    public FakeAppFactory Apps { get; }
    public StateStore Store { get; }
    public RunService Service { get; }
    /// <summary>The source library.</summary>
    public FakeGraph.FakeDrive Library { get; }

    /// <summary>The target library of <see cref="NewProfile"/>.</summary>
    public FakeGraph.FakeDrive Finance { get; }

    public void AddConnection(string name, LicenseTier tier = LicenseTier.UpTo1000, int? tenantOverride = null, int? perDay = null)
    {
        var c = new Connection
        {
            Name = name,
            TenantId = "contoso.onmicrosoft.com",
            ClientId = Guid.NewGuid().ToString(),
            CertificateThumbprint = new string('A', 40),
            Licenses = tier,
            TenantPer5MinOverride = tenantOverride,
            AppPerDayOverride = perDay,
        };
        Store.PutSetting(SettingKinds.Connection, name, c.ToJson(), DateTimeOffset.UtcNow);
    }

    public static Profile NewProfile(params string[] connections) => new()
    {
        Name = "test",
        Source = new Target { SiteUrl = Host + "/sites/archive", Library = "Documents" },
        TargetLibraries = ["Фінанси"],
        Connections = connections.Length == 0 ? ["app-a"] : [.. connections],
    };

    public void SaveProfile(Profile p) => Store.PutSetting(SettingKinds.Profile, p.Name!, p.ToJson(), DateTimeOffset.UtcNow);

    public long Create(Profile? profile = null)
    {
        var p = profile ?? NewProfile();
        SaveProfile(p);
        return Service.CreateRun(RunModes.Inventory, p.Name);
    }

    public async Task<string> RunAsync(long runId, int timeoutSeconds = 60) =>
        await Service.Start(runId).Completion.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));

    /// <summary>A file of <paramref name="size"/> bytes; content does not matter for the inventory.</summary>
    public FakeGraph.FakeItem AddFile(string path, int size = 100) => Graph.AddFile(Library, path, new byte[size]);

    public IReadOnlyList<FileRow> Files(long runId) => Store.QueryFiles(runId, new FileFilter(), 0, 10_000);

    public async ValueTask DisposeAsync()
    {
        await Service.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(Dir, true);
        }
        catch (IOException)
        {
        }
    }
}
