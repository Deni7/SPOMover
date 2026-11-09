using SPOMover.State;
using SPOMover.Tests.Shared;
using static SPOMover.Engine.Tests.EngineEnv;

namespace SPOMover.Engine.Tests;

public class RunDeleteTests
{
    [Fact]
    public async Task DeletedRunLeavesNothing()
    {
        await using var env = new EngineEnv();
        env.AddFile("a.xlsx");
        env.AddFile("Fin/b.xlsx");
        long run = env.Create();
        await env.RunAsync(run);
        var fileIds = env.Files(run).Select(f => f.Id).ToList();
        Assert.Equal(2, fileIds.Count);
        Assert.NotNull(ProfileInsights.For(env.Store, env.Service.GetProfile(NewProfile().Name!)));

        env.Service.DeleteRun(run);

        Assert.Null(env.Store.GetRun(run));
        Assert.Empty(env.Files(run));
        Assert.All(fileIds, id => Assert.Empty(env.Store.GetAttempts(id)));
        Assert.Empty(env.Store.GetTargets(run));
        Assert.Empty(env.Store.GetFolders(run));
        Assert.Empty(env.Store.GetMetrics(run));
        // The estimate of a new run no longer comes from the deleted listing.
        Assert.Null(ProfileInsights.For(env.Store, env.Service.GetProfile(NewProfile().Name!)));
        Assert.Throws<KeyNotFoundException>(() => env.Service.DeleteRun(run));
    }

    [Fact]
    public async Task RunningRunCannotBeDeleted()
    {
        await using var env = new EngineEnv();
        env.AddFile("a.xlsx");
        using var gate = new ManualResetEventSlim();
        env.Graph.Faults.Add(r =>
        {
            if (r.Path.Contains("/delta", StringComparison.Ordinal))
                gate.Wait(TimeSpan.FromSeconds(30));
            return null;
        });
        long run = env.Create();
        var active = env.Service.Start(run);
        for (int i = 0; i < 100 && env.Store.GetRun(run)!.Status != RunStatuses.Running; i++)
            await Task.Delay(50);

        var ex = Assert.Throws<InvalidOperationException>(() => env.Service.DeleteRun(run));
        Assert.Contains("виконується", ex.Message);
        gate.Set();
        await active.Completion.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.NotNull(env.Store.GetRun(run));
    }

    [Fact]
    public async Task RunLeftRunningByAKilledHostCannotBeDeleted()
    {
        await using var env = new EngineEnv();
        long run = env.Create();
        env.Store.SetRunStatus(run, RunStatuses.Running, DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => env.Service.DeleteRun(run));

        env.Store.SetRunStatus(run, RunStatuses.Stopped, DateTimeOffset.UtcNow, "Зупинено");
        env.Service.DeleteRun(run);
        Assert.Null(env.Store.GetRun(run));
    }
}
