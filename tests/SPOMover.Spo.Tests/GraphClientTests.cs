using System.Net;
using SPOMover.Core.Config;
using SPOMover.Core;
using SPOMover.Spo.Throttling;
using SPOMover.Tests.Shared;

namespace SPOMover.Spo.Tests;

public class GraphClientTests
{
    private static (FakeGraph Graph, SpoApp App, FakeGraph.FakeDrive Drive, FakeGraph.FakeItem Item) Setup(int attempts = 6)
    {
        var graph = new FakeGraph();
        var drive = graph.AddLibrary("/sites/x", "Documents", "Shared Documents");
        var item = graph.AddFile(drive, "a.xlsx", [1, 2, 3]);
        var factory = new FakeAppFactory(graph);
        var connection = new Connection { Name = "a", Licenses = LicenseTier.Over50000 };
        var app = factory.Create(connection, new SpoAppSettings(4, attempts, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), 0.8),
            new RuBudget("tenant", 10_000, TimeSpan.FromMinutes(5), 0));
        return (graph, app, drive, item);
    }

    [Fact]
    public async Task ResolvesSiteDrivesAndFolder()
    {
        var (graph, app, drive, _) = Setup();
        graph.AddFile(drive, "Fin/Звіт 1.xlsx", [1]);
        var site = await app.Client.GetSiteAsync("https://contoso.sharepoint.com/sites/x/", CancellationToken.None);
        var drives = await app.Client.GetDrivesAsync(site.Id, CancellationToken.None);
        Assert.Equal("Documents", Assert.Single(drives).Name);
        var folder = await app.Client.GetItemByPathAsync(drive.Id, "Fin", CancellationToken.None);
        Assert.True(folder.IsFolder);
    }

    [Fact]
    public async Task FakeGraphSurvivesSaveAndLoad()
    {
        var (graph, _, drive, item) = Setup();
        graph.AddFile(drive, "Fin/b.xlsx", [4]).CheckedOutBy = "ivan";
        item.History[1] = [9];
        item.Version = 2;
        string path = Path.GetTempFileName();
        graph.Save(path);

        var loaded = FakeGraph.Load(path);
        var app = new FakeAppFactory(loaded).Create(new Connection { Name = "a", Licenses = LicenseTier.Over50000 },
            new SpoAppSettings(4, 2, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(15), 0.8), new RuBudget("tenant", 10_000, TimeSpan.FromMinutes(5), 0));
        var site = await app.Client.GetSiteAsync("https://contoso.sharepoint.com/sites/x", CancellationToken.None);
        Assert.Equal(drive.Id, Assert.Single(await app.Client.GetDrivesAsync(site.Id, CancellationToken.None)).Id);
        Assert.Equal(item.ETag, (await app.Client.GetItemAsync(drive.Id, item.Id, CancellationToken.None)).ETag);
        string old = Path.GetTempFileName();
        await app.Client.DownloadVersionAsync(drive.Id, item.Id, "1.0", old, CancellationToken.None);
        Assert.Equal([9], await File.ReadAllBytesAsync(old));
        Assert.True((await app.Client.GetItemByPathAsync(drive.Id, "Fin", CancellationToken.None)).IsFolder);
        // New ids do not clash with the saved ones.
        Assert.DoesNotContain(loaded.AddFile(loaded.AddLibrary("/sites/x", "Архів", "Archive"), "c.xlsx", []).Id, drive.Items.Keys);
    }

    [Fact]
    public async Task RetryAfterPausesTheWholeApp()
    {
        var (graph, app, drive, item) = Setup();
        int calls = 0;
        graph.Faults.Add(r => r.Path.EndsWith("/content", StringComparison.Ordinal) && Interlocked.Increment(ref calls) == 1
            ? FakeGraph.Error(HttpStatusCode.TooManyRequests, "throttled", 1)
            : null);
        string dir = Directory.CreateTempSubdirectory().FullName;
        var started = DateTimeOffset.UtcNow;
        var first = app.Client.DownloadAsync(drive.Id, item.Id, Path.Combine(dir, "1"), CancellationToken.None);
        await Task.Delay(100);
        // A request started during the pause also waits for it.
        var meta = await app.Client.GetItemAsync(drive.Id, item.Id, CancellationToken.None);
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(0.9));
        await first;
        Assert.Equal(item.ETag, meta.ETag);
        Assert.Equal("1.0", meta.Version);
        Assert.Equal(1, app.Throttle.Throttled429);
    }

    [Fact]
    public async Task ConflictAndLockSurfaceWithClassAndRequestId()
    {
        var (graph, app, drive, item) = Setup();
        string file = Path.GetTempFileName();
        File.WriteAllBytes(file, [9, 9]);

        var conflict = await Assert.ThrowsAsync<SpoException>(() => app.Client.UploadAsync(drive.Id, item.Id, file, "\"stale\"", 1 << 20, CancellationToken.None));
        Assert.Equal(ErrorClass.Conflict, conflict.ErrorClass);
        Assert.NotNull(conflict.RequestId);

        item.LockedUploads = 1;
        var locked = await Assert.ThrowsAsync<SpoException>(() => app.Client.UploadAsync(drive.Id, item.Id, file, item.ETag, 1 << 20, CancellationToken.None));
        Assert.Equal(ErrorClass.Locked, locked.ErrorClass);

        var ok = await app.Client.UploadAsync(drive.Id, item.Id, file, item.ETag, 1 << 20, CancellationToken.None);
        Assert.Equal(item.ETag, ok.ETag);
        Assert.Equal([9, 9], item.Content);
    }

    [Fact]
    public async Task NetworkErrorsAreRetriedThenTransient()
    {
        var (graph, app, drive, item) = Setup(attempts: 3);
        int calls = 0;
        graph.Faults.Add(_ =>
        {
            Interlocked.Increment(ref calls);
            throw new HttpRequestException("connection reset");
        });
        var ex = await Assert.ThrowsAsync<SpoException>(() => app.Client.GetItemAsync(drive.Id, item.Id, CancellationToken.None));
        Assert.Equal(ErrorClass.Transient, ex.ErrorClass);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task SpendsBudgetByCostTable()
    {
        var (_, app, drive, item) = Setup();
        await app.Client.GetItemAsync(drive.Id, item.Id, CancellationToken.None);
        await app.Client.GetDeltaPageAsync(drive.Id, null, CancellationToken.None);
        Assert.Equal(3, app.Budget.SpentTotal);
    }
}
