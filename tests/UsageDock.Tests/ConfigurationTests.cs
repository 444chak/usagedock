using UsageDock.Models;
using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-tests-" + Guid.NewGuid().ToString("N"));

    private string ConfigPath => Path.Combine(_directory, "config.json");

    public ConfigServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void A_missing_file_yields_defaults()
    {
        var config = new ConfigService(ConfigPath).Load();

        Assert.Equal(0.5d, config.VerticalAnchor);
        Assert.Equal(8d, config.EdgeMargin);
        Assert.Equal(60, config.PollSeconds);
        Assert.Equal(string.Empty, config.MonitorDeviceName);
        Assert.False(config.StartWithWindows);
    }

    [Fact]
    public void Round_trips_through_disk()
    {
        var service = new ConfigService(ConfigPath);
        service.Save(new AppConfig
        {
            MonitorDeviceName = @"\\.\DISPLAY2",
            VerticalAnchor = 0.25d,
            EdgeMargin = 16d,
            PollSeconds = 120,
            StartWithWindows = true
        });

        var loaded = service.Load();

        Assert.Equal(@"\\.\DISPLAY2", loaded.MonitorDeviceName);
        Assert.Equal(0.25d, loaded.VerticalAnchor);
        Assert.Equal(16d, loaded.EdgeMargin);
        Assert.Equal(120, loaded.PollSeconds);
        Assert.True(loaded.StartWithWindows);
    }

    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_instead_of_throwing()
    {
        File.WriteAllText(ConfigPath, "{ this is not json");

        var config = new ConfigService(ConfigPath).Load();

        Assert.Equal(60, config.PollSeconds);
    }

    [Fact]
    public void Hand_edited_out_of_range_values_are_repaired_on_load()
    {
        File.WriteAllText(ConfigPath, """
        { "verticalAnchor": 4.2, "edgeMargin": -30, "pollSeconds": 1 }
        """);

        var config = new ConfigService(ConfigPath).Load();

        Assert.Equal(1d, config.VerticalAnchor);
        Assert.Equal(0d, config.EdgeMargin);
        Assert.Equal(15, config.PollSeconds);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        File.WriteAllText(ConfigPath, """
        {
          // where to dock
          "edgeMargin": 12,
        }
        """);

        Assert.Equal(12d, new ConfigService(ConfigPath).Load().EdgeMargin);
    }

    [Fact]
    public void Normalize_repairs_non_finite_values()
    {
        var config = new AppConfig { VerticalAnchor = double.NaN, EdgeMargin = double.PositiveInfinity }
            .Normalized();

        Assert.Equal(0.5d, config.VerticalAnchor);
        Assert.Equal(8d, config.EdgeMargin);
    }

    [Fact]
    public void Poll_interval_is_clamped_to_a_sane_band()
    {
        Assert.Equal(15, new AppConfig { PollSeconds = -10 }.Normalized().PollSeconds);
        Assert.Equal(3600, new AppConfig { PollSeconds = 999999 }.Normalized().PollSeconds);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public sealed class CacheServiceTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "usagedock-cache-" + Guid.NewGuid().ToString("N"));

    private string CachePath => Path.Combine(_directory, "cache.json");

    public CacheServiceTests() => Directory.CreateDirectory(_directory);

    private static UsageSnapshot Sample() => new()
    {
        Provider = ProviderId.Codex,
        Status = SnapshotStatus.Ok,
        Plan = "plus",
        UpdatedAt = new DateTimeOffset(2026, 9, 8, 12, 0, 0, TimeSpan.Zero),
        Windows =
        [
            QuotaWindow.Create("5 hour", 6d, DateTimeOffset.FromUnixTimeSeconds(1788913590), TimeSpan.FromMinutes(300)),
            QuotaWindow.Create("Weekly", 19d)
        ]
    };

    [Fact]
    public void Stores_and_reloads_a_snapshot()
    {
        new CacheService(CachePath).Store(Sample());

        var restored = new CacheService(CachePath).Get(ProviderId.Codex);

        Assert.NotNull(restored);
        Assert.True(restored!.FromCache);
        Assert.Equal("plus", restored.Plan);
        Assert.Equal(2, restored.Windows.Count);
        Assert.Equal(6d, restored.Windows[0].UsedPercent);
        Assert.Equal(TimeSpan.FromMinutes(300), restored.Windows[0].WindowLength);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1788913590), restored.Windows[0].ResetsAt);
    }

    [Fact]
    public void Failed_readings_are_not_cached()
    {
        var cache = new CacheService(CachePath);

        cache.Store(UsageSnapshot.Failure(ProviderId.Claude, SnapshotStatus.RateLimited, "429"));

        Assert.Null(cache.Get(ProviderId.Claude));
    }

    [Fact]
    public void An_unknown_provider_reads_back_as_null() =>
        Assert.Null(new CacheService(CachePath).Get(ProviderId.Claude));

    [Fact]
    public void Clear_empties_the_cache()
    {
        var cache = new CacheService(CachePath);
        cache.Store(Sample());

        cache.Clear();

        Assert.Null(cache.Get(ProviderId.Codex));
        Assert.Null(new CacheService(CachePath).Get(ProviderId.Codex));
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
