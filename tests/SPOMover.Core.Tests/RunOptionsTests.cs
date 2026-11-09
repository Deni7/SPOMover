using SPOMover.Core.Config;

namespace SPOMover.Core.Tests;

public class RunOptionsTests
{
    private static Connection Conn(string name) => new()
    {
        Name = name,
        TenantId = "t",
        ClientId = Guid.NewGuid().ToString(),
        CertificateThumbprint = new string('B', 40),
    };

    private static Profile Valid() => new()
    {
        Source = new Target { SiteUrl = "https://h/sites/x", Library = "Documents" },
        TargetLibraries = ["Фінанси"],
        Connections = ["c1"],
    };

    [Fact]
    public void ValidProfileForRun() => Assert.True(Valid().ValidateForRun([Conn("c1")]).IsValid);

    [Theory]
    [InlineData(1, true)]
    [InlineData(64, true)]
    [InlineData(0, false)]
    [InlineData(65, false)]
    public void ConcurrencyIsChecked(int concurrency, bool valid)
    {
        var p = Valid();
        p.Performance.Concurrency = concurrency;
        Assert.Equal(valid, p.ValidateForRun([Conn("c1")]).IsValid);
    }

    [Fact]
    public void SourceNeedsSiteAndLibrary()
    {
        var p = Valid();
        p.Source = new Target { SiteUrl = "sites/x" };
        var v = p.ValidateForRun([Conn("c1")]);
        Assert.Contains(v.Issues, i => i.Message.StartsWith("source.siteUrl", StringComparison.Ordinal));
        Assert.Contains(v.Issues, i => i.Message.StartsWith("source.library", StringComparison.Ordinal));
        Assert.False(v.IsValidForInventory);
    }

    [Fact]
    public void SeveralConnectionsNeedPool()
    {
        var p = Valid();
        p.Connections = ["c1", "c2"];
        var v = p.ValidateForRun([Conn("c1"), Conn("c2")]);
        Assert.False(v.IsValid);
        p.Pool.Enabled = true;
        v = p.ValidateForRun([Conn("c1"), Conn("c2")]);
        Assert.True(v.IsValid);
        Assert.Contains(v.Issues, i => i.Severity == IssueSeverity.Warning && i.Message.Contains("пул"));
    }

    [Fact]
    public void MissingTargetsAndConnectionsAreErrors()
    {
        var p = Valid();
        p.Source = null;
        p.TargetLibraries.Clear();
        p.Connections = ["nope"];
        var v = p.ValidateForRun([]);
        Assert.Contains(v.Issues, i => i.Message.StartsWith("source:", StringComparison.Ordinal));
        Assert.Contains(v.Issues, i => i.Message.StartsWith("targetLibraries:", StringComparison.Ordinal));
        Assert.Contains(v.Issues, i => i.Message.Contains("nope"));
    }

    [Fact]
    public void ConnectionValidation()
    {
        var c = Conn("c");
        Assert.Empty(c.Validate());
        c.CertificateThumbprint = "short";
        c.ClientId = "x";
        Assert.Equal(2, c.Validate().Count());
    }

    [Theory]
    [InlineData("Mon", "20:00", "07:00", "2026-10-26 21:00", true)] // Monday evening
    [InlineData("Mon", "20:00", "07:00", "2026-10-27 06:59", true)] // Tuesday morning belongs to Monday's window
    [InlineData("Mon", "20:00", "07:00", "2026-10-27 21:00", false)]
    [InlineData("", "09:00", "18:00", "2026-10-25 10:00", true)]
    [InlineData("Sat,Sun", "00:00", "24:00", "2026-10-26 10:00", false)]
    public void WindowContains(string days, string from, string to, string at, bool expected)
    {
        var w = new ScheduleWindow { Days = days.Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(), From = from, To = to };
        Assert.Equal(expected, w.Contains(DateTime.Parse(at, System.Globalization.CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void BudgetFollowsWindows()
    {
        var perf = new PerformanceOptions
        {
            BudgetPercent = 50,
            TimeZone = "UTC",
            Windows = [new ScheduleWindow { From = "20:00", To = "07:00", BudgetPercent = 90 }],
        };
        Assert.Equal(90, perf.BudgetAt(new DateTimeOffset(2026, 10, 23, 22, 0, 0, TimeSpan.Zero)));
        Assert.Equal(50, perf.BudgetAt(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)));
        perf.OnlyInWindows = true;
        Assert.Null(perf.BudgetAt(new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void LimitsByTier()
    {
        var c = Conn("c");
        c.Licenses = LicenseTier.UpTo5000;
        Assert.Equal((2_500, 2_400_000, 37_500), c.Limits());
        c.AppPerMinuteOverride = 1000;
        Assert.Equal(1000, c.Limits().PerMinute);
    }
}
