using UsageDock.Services;
using Xunit;

namespace UsageDock.Tests;

public sealed class UsageFileWatcherTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "usagedock-watch-" + Guid.NewGuid().ToString("N"));

    private static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(100);

    private string MissingDirectory => Path.Combine(_root, "not-there-yet");

    private string TargetFile => Path.Combine(MissingDirectory, "plan-usage-history.json");

    public UsageFileWatcherTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void A_missing_folder_leaves_the_watcher_detached()
    {
        using var watcher = new UsageFileWatcher(TargetFile, Retry);

        Assert.False(watcher.IsWatching);
    }

    [Fact]
    public async Task The_watcher_attaches_once_the_folder_appears()
    {
        using var watcher = new UsageFileWatcher(TargetFile, Retry);
        Assert.False(watcher.IsWatching);

        // This is the Windows login case: the app can start before the folder it wants exists,
        // and must not stay dead for the rest of the session.
        Directory.CreateDirectory(MissingDirectory);

        await WaitUntil(() => watcher.IsWatching);

        Assert.True(watcher.IsWatching);
    }

    [Fact]
    public async Task A_write_after_a_late_attach_still_raises_Changed()
    {
        using var watcher = new UsageFileWatcher(TargetFile, Retry);
        var raised = new TaskCompletionSource();
        watcher.Changed += () => raised.TrySetResult();

        Directory.CreateDirectory(MissingDirectory);
        await WaitUntil(() => watcher.IsWatching);

        await File.WriteAllTextAsync(TargetFile, "{}");

        var completed = await Task.WhenAny(raised.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(raised.Task, completed);
    }

    [Fact]
    public void An_existing_folder_attaches_immediately()
    {
        Directory.CreateDirectory(MissingDirectory);

        using var watcher = new UsageFileWatcher(TargetFile, Retry);

        Assert.True(watcher.IsWatching);
    }

    [Fact]
    public void Disposing_stops_the_watcher()
    {
        Directory.CreateDirectory(MissingDirectory);
        var watcher = new UsageFileWatcher(TargetFile, Retry);
        Assert.True(watcher.IsWatching);

        watcher.Dispose();

        Assert.False(watcher.IsWatching);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
