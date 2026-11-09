using System.Net;
using SPOMover.Core.Config;
using SPOMover.State;
using SPOMover.Tests.Shared;
using static SPOMover.Engine.Tests.EngineEnv;

namespace SPOMover.Engine.Tests;

/// <summary>Stage 1: the inventory mode (INV-1) on the run framework inherited from SPOReplaceLink.</summary>
public class RunEngineTests
{
    [Fact]
    public async Task InventoryListsEveryFileWithPathSizeAndMetadata()
    {
        await using var env = new EngineEnv();
        env.AddFile("Fin/2024/звіт за рік.xlsx", 1500);
        env.AddFile("Fin/a.docx", 20);
        env.AddFile("HR/Накази/b.pdf", 300);
        env.AddFile("readme.txt", 5);

        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        var files = env.Files(run).ToDictionary(f => f.Path!);
        Assert.Equal(["Fin/2024/звіт за рік.xlsx", "Fin/a.docx", "HR/Накази/b.pdf", "readme.txt"], files.Keys.Order(StringComparer.Ordinal));
        Assert.All(files.Values, f => Assert.Equal(FileStatuses.Listed, f.StatusCode));
        Assert.Equal(1500, files["Fin/2024/звіт за рік.xlsx"].Size);
        Assert.NotNull(files["readme.txt"].ETag);
        Assert.Equal((4, 1825), env.Store.Totals(run));
        // Nothing but listing: no content was read and nothing was written (the one POST is the read-only GetCheckedOutFiles).
        Assert.DoesNotContain(env.Graph.Log, r => r.Path.Contains("/content", StringComparison.Ordinal)
                                                  || (r.Method != "GET" && !r.Path.EndsWith("/GetCheckedOutFiles()?$expand=CheckedOutBy", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task TheListingProgressGetsItsTotalFromSharePoint()
    {
        await using var env = new EngineEnv();
        env.AddFile("Fin/2024/a.xlsx", 10);
        env.AddFile("Fin/b.docx", 20);
        env.AddFile("HR/Накази/c.pdf", 30);
        env.AddFile("readme.txt", 5);

        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        var source = env.Store.GetTargetProgress(run)[0];
        // 4 files and 4 folders (Fin, Fin/2024, HR, HR/Накази): what ItemCount says is what the listing found.
        Assert.Equal(8, source.Expected);
        Assert.Equal((4L, 4L, 65L), (source.Files, source.Folders, source.Bytes));
        Assert.Equal(100, source.Percent);
        Assert.Equal(50, (new StateStore.TargetProgress(0, false, 0, false, 8, 2, 0, 2, 0)).Percent);
        Assert.Equal(99.9, (new StateStore.TargetProgress(0, false, 0, false, 8, 9, 0, 0, 0)).Percent);
        Assert.Null((new StateStore.TargetProgress(0, false, 0, false, null, 9, 0, 0, 0)).Percent);
    }

    [Fact]
    public async Task WithoutTheTotalTheListingGoesOnWithoutAPercentage()
    {
        await using var env = new EngineEnv();
        env.Graph.ItemCountFails = true;
        env.AddFile("Fin/a.xlsx");
        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Null(env.Store.GetTargetProgress(run)[0].Expected);

        // A folder of a library: the library's total says nothing about it.
        await using var env2 = new EngineEnv();
        env2.AddFile("Fin/a.xlsx");
        var p = NewProfile();
        p.Source!.Folder = "Fin";
        long run2 = env2.Create(p);
        Assert.Equal(RunStatuses.Completed, await env2.RunAsync(run2));
        Assert.Null(env2.Store.GetTargetProgress(run2)[0].Expected);
        Assert.Equal(1, env2.Store.GetTargetProgress(run2)[0].Files);
    }

    [Fact]
    public async Task FolderTargetTakesOnlyItsFilesAndLateFoldersAreResolved()
    {
        await using var env = new EngineEnv();
        env.Graph.FilesBeforeFolders = true;
        env.AddFile("Fin/2024/a.xlsx");
        env.AddFile("Fin/Archive/b.xlsx");
        env.AddFile("Other/c.xlsx");
        var p = NewProfile();
        p.Source!.Folder = "Fin";

        long run = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(["Fin/2024/a.xlsx", "Fin/Archive/b.xlsx"], env.Files(run).Select(f => f.Path!).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(env.Files(run), f => f.Status == FileStatuses.WaitingPath);
    }

    [Fact]
    public async Task SourceAndTargetLibrariesAreListedSeparately()
    {
        await using var env = new EngineEnv();
        env.AddFile("a.xlsx", 10);
        env.Graph.AddFile(env.Finance, "b.xlsx", new byte[30]);
        env.Graph.AddFile(env.Finance, "Q1/c.xlsx", new byte[30]);
        var p = NewProfile();

        long run = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        var byLibrary = env.Store.BreakdownBy(run, "target_idx").ToDictionary(b => b.Key);
        Assert.Equal((1, 10), (byLibrary["0"].Total, byLibrary["0"].Bytes));
        Assert.Equal((2, 60), (byLibrary["1"].Total, byLibrary["1"].Bytes));
        var estimate = ProfileInsights.For(env.Store, p)!;
        Assert.Equal((1L, 10L), estimate.Of("documents"));
        Assert.Equal((2L, 60L), estimate.Of("Фінанси"));
        Assert.Equal(1, estimate.SourceFiles);
    }

    [Fact]
    public async Task EnumerationContinuesFromSavedPosition()
    {
        await using var env = new EngineEnv();
        env.Graph.DeltaPageSize = 2;
        for (int i = 0; i < 7; i++)
            env.AddFile($"f{i}.xlsx");
        int deltaPages = 0;
        int fromStart = 0;
        long run = env.Create();
        RunService.ActiveRun? active = null;
        env.Graph.Faults.Add(r =>
        {
            if (!r.Path.Contains("/delta", StringComparison.Ordinal))
                return null;
            if (!r.Path.Contains("token=", StringComparison.Ordinal))
                Interlocked.Increment(ref fromStart);
            if (Interlocked.Increment(ref deltaPages) == 3)
                active!.Stop.Cancel();
            return null;
        });
        active = env.Service.Start(run);
        Assert.Equal(RunStatuses.Stopped, await active.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.NotNull(env.Store.GetTargets(run)[0].NextLink);

        var continued = env.Service.Start(run);
        Assert.Equal(RunStatuses.Completed, await continued.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(7, env.Files(run).Count);
        // Continued from the saved nextLink: each library (source and target) was listed from the beginning only once.
        Assert.Equal(2, fromStart);
        Assert.InRange(deltaPages, 5, 6);
        // The count goes on from the files listed before the stop.
        Assert.Equal(7, continued.Engine.Metrics.Found);
    }

    [Fact]
    public async Task PauseHoldsTheListingAndResumeFinishesIt()
    {
        await using var env = new EngineEnv();
        env.Graph.DeltaPageSize = 1;
        for (int i = 0; i < 4; i++)
            env.AddFile($"f{i}.xlsx");
        using var firstPage = new ManualResetEventSlim();
        env.Graph.Faults.Add(r =>
        {
            if (r.Path.Contains("/delta", StringComparison.Ordinal))
                firstPage.Set();
            return null;
        });
        long run = env.Create();
        var active = env.Service.Start(run);
        Assert.True(firstPage.Wait(TimeSpan.FromSeconds(30)));
        env.Service.Pause(run);
        await Task.Delay(300);
        int pages = env.Graph.Log.Count(r => r.Path.Contains("/delta", StringComparison.Ordinal));
        await Task.Delay(500);
        Assert.Equal(pages, env.Graph.Log.Count(r => r.Path.Contains("/delta", StringComparison.Ordinal)));
        Assert.Equal(RunStatuses.Paused, env.Store.GetRun(run)!.Status);

        env.Service.Resume(run);
        Assert.Equal(RunStatuses.Completed, await active.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(4, env.Files(run).Count);
    }

    [Fact]
    public async Task TransientListingFailureIsRetriedFromTheSamePage()
    {
        await using var env = new EngineEnv();
        env.Graph.DeltaPageSize = 2;
        for (int i = 0; i < 5; i++)
            env.AddFile($"f{i}.xlsx");
        int failures = 0;
        env.Graph.Faults.Add(r => r.Path.Contains("/delta", StringComparison.Ordinal) && r.Path.Contains("token=2", StringComparison.Ordinal)
                                  && Interlocked.Increment(ref failures) <= 1
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ \"value\": [", System.Text.Encoding.UTF8, "application/json") }
            : null);
        var p = NewProfile();
        p.Retry.RequestAttempts = 1;
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(5, env.Files(run).Count);
    }

    [Fact]
    public async Task BrokenListingAbortsInsteadOfHangingOrCompleting()
    {
        await using var env = new EngineEnv();
        env.Graph.DeltaPageSize = 2;
        for (int i = 0; i < 5; i++)
            env.AddFile($"f{i}.xlsx");
        env.Graph.Faults.Add(r => r.Path.Contains("/delta", StringComparison.Ordinal) && r.Path.Contains("token=2", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{ \"value\": [", System.Text.Encoding.UTF8, "application/json") }
            : null);
        var p = NewProfile();
        p.Retry.RequestAttempts = 1;
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Aborted, await env.RunAsync(run));
        Assert.Contains("Перелік файлів", env.Store.GetRun(run)!.StopReason);
    }

    [Fact]
    public async Task ThrottlingPausesTheAppAndTheListingFinishes()
    {
        await using var env = new EngineEnv();
        env.Graph.DeltaPageSize = 2;
        for (int i = 0; i < 6; i++)
            env.AddFile($"f{i}.xlsx");
        int throttled = 0;
        env.Graph.Faults.Add(r => r.Path.Contains("/delta", StringComparison.Ordinal) && Interlocked.Increment(ref throttled) <= 2
            ? FakeGraph.Error(HttpStatusCode.TooManyRequests, "activityLimitReached", retryAfterSeconds: 1)
            : null);
        var started = DateTimeOffset.UtcNow;
        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromSeconds(1));
        Assert.Equal(6, env.Files(run).Count);
        Assert.Equal(2, env.Apps.Created["app-a"].Throttle.Throttled429);
    }

    [Fact]
    public async Task ExpiredTokenIsRefreshed()
    {
        await using var env = new EngineEnv();
        env.Graph.DeltaPageSize = 1;
        for (int i = 0; i < 3; i++)
            env.AddFile($"f{i}.xlsx");
        int pages = 0;
        env.Graph.Faults.Add(r =>
        {
            if (r.Path.Contains("/delta", StringComparison.Ordinal) && Interlocked.Increment(ref pages) == 2)
                env.Graph.ExpireTokens("app-a");
            return null;
        });
        long run = env.Create();
        Assert.Equal(RunStatuses.Completed, await env.RunAsync(run));
        Assert.Equal(3, env.Files(run).Count);
        Assert.Equal(1, env.Apps.Tokens["app-a"].Refreshes);
    }

    [Fact]
    public async Task Only503MeansBlockedAndStops()
    {
        await using var env = new EngineEnv();
        env.AddFile("a.xlsx");
        var p = NewProfile();
        p.Retry.BlockedMinutes = 0.02;
        p.Retry.RequestAttempts = 20;
        long run = env.Create(p);
        env.Graph.Faults.Add(r => r.Path.Contains("/delta", StringComparison.Ordinal) ? FakeGraph.Error(HttpStatusCode.ServiceUnavailable, "serverBusy", 1) : null);
        Assert.Equal(RunStatuses.Aborted, await env.RunAsync(run));
        Assert.Contains("блокування", env.Store.GetRun(run)!.StopReason);
    }

    [Fact]
    public async Task OutsideWindowsTheRunWaits()
    {
        await using var env = new EngineEnv();
        env.AddFile("a.xlsx");
        var p = NewProfile();
        var now = DateTime.Now;
        // A one-minute window two hours from now.
        p.Performance.Windows = [new ScheduleWindow { From = now.AddHours(2).ToString("HH:mm"), To = now.AddHours(2).AddMinutes(1).ToString("HH:mm") }];
        p.Performance.OnlyInWindows = true;
        long run = env.Create(p);
        var active = env.Service.Start(run);
        await Task.Delay(1500);
        Assert.DoesNotContain(env.Graph.Log, r => r.Path.Contains("/delta", StringComparison.Ordinal));
        active.Stop.Cancel();
        Assert.Equal(RunStatuses.Stopped, await active.Completion.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task SpentDailyBudgetPausesInsteadOfFailing()
    {
        await using var env = new EngineEnv();
        env.AddConnection("app-a", perDay: 25); // 20 RU at 80%
        env.Graph.DeltaPageSize = 1;
        for (int i = 0; i < 20; i++)
            env.AddFile($"f{i}.xlsx");
        long run = env.Create();
        var active = env.Service.Start(run);
        await Task.Delay(2000);
        Assert.Equal(RunStatuses.Running, env.Store.GetRun(run)!.Status);
        active.Stop.Cancel();
        Assert.Equal(RunStatuses.Stopped, await active.Completion.WaitAsync(TimeSpan.FromSeconds(90)));
        Assert.InRange(env.Files(run).Count, 1, 19);
        Assert.True(env.Apps.Created["app-a"].Budget.SpentLast24h <= 25);
    }

    [Fact]
    public async Task InvalidProfileIsRejectedBeforeTheRun()
    {
        await using var env = new EngineEnv();
        var p = NewProfile("app-a", "app-b");
        env.AddConnection("app-b");
        var ex = Assert.Throws<RunValidationException>(() => env.Create(p));
        Assert.Contains(ex.Issues, i => i.Message.Contains("pool.enabled"));
    }

    [Fact]
    public async Task AppWithoutAccessToTargetStopsBeforeAnyWork()
    {
        await using var env = new EngineEnv();
        env.AddFile("a.xlsx");
        env.AddConnection("app-b");
        var p = NewProfile("app-a", "app-b");
        p.Pool.Enabled = true;
        env.Graph.Faults.Add(r => r.App == "app-b" && r.Path.Contains("/sites/", StringComparison.Ordinal) ? FakeGraph.Error(HttpStatusCode.Forbidden, "accessDenied") : null);
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Aborted, await env.RunAsync(run));
        Assert.Contains("app-b", env.Store.GetRun(run)!.StopReason);
        Assert.Empty(env.Files(run));
    }

    [Fact]
    public async Task UnknownLibraryStopsTheRunWithItsName()
    {
        await using var env = new EngineEnv();
        var p = NewProfile();
        p.Source!.Library = "Немає такої";
        long run = env.Create(p);
        Assert.Equal(RunStatuses.Aborted, await env.RunAsync(run));
        Assert.Contains("Немає такої", env.Store.GetRun(run)!.StopReason);
    }

    [Fact]
    public async Task ReportsExport()
    {
        await using var env = new EngineEnv();
        env.AddFile("Fin/a.xlsx", 2048);
        env.AddFile("b.pdf", 10);
        long run = env.Create();
        await env.RunAsync(run);
        string dir = Path.Combine(env.Dir, "reports");
        ReportExporter.ExportAll(env.Store, run, dir);
        string files = File.ReadAllText(Path.Combine(dir, "files.csv"));
        Assert.StartsWith("﻿id,library,path,format,size", files);
        Assert.Contains("Fin/a.xlsx,xlsx,2048", files);
        Assert.Contains("listed", files);
        Assert.Single(File.ReadAllLines(Path.Combine(dir, "attention.csv")));
        string summary = File.ReadAllText(Path.Combine(dir, "summary.txt"));
        Assert.Contains("Файлів: 2", summary);
        Assert.Contains("За бібліотеками", summary);

        using var xlsx = new MemoryStream();
        ReportExporter.WriteXlsx(env.Store, run, "files", xlsx);
        Assert.True(xlsx.Length > 0);
    }
}
