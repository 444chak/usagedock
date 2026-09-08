using UsageDock.Models;

namespace UsageDock.Services;

/// <summary>
/// Current Working / Waiting / Idle state per provider.
///
/// Claude is driven by Claude Code hooks writing small activity files. Codex has no hook system,
/// so its state is inferred from how recently the CLI wrote to its session log; the same
/// interface covers both, which leaves room for a better Codex signal later.
/// </summary>
public sealed class ActivityService
{
    /// <summary>A Codex session log written this recently means a turn is in flight.</summary>
    public static readonly TimeSpan CodexActiveWindow = TimeSpan.FromSeconds(90);

    private readonly string _activityDirectory;
    private readonly Func<DateTimeOffset> _now;

    public ActivityService(string? activityDirectory = null, Func<DateTimeOffset>? now = null)
    {
        _activityDirectory = activityDirectory ?? AppPaths.ActivityDirectory;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Reads the hook written state for a provider.</summary>
    public ActivityState ReadHookState(ProviderId provider)
    {
        var path = Path.Combine(_activityDirectory, provider.Key() + ".json");
        var record = JsonIo.TryReadFile<ActivityRecord>(path);
        return record?.ResolveState(_now()) ?? ActivityState.Idle;
    }

    /// <summary>Derives Codex activity from the freshness of its session log.</summary>
    public ActivityState InferFromLastWrite(DateTimeOffset? lastWriteUtc)
    {
        if (lastWriteUtc is not { } last)
        {
            return ActivityState.Idle;
        }

        return _now() - last <= CodexActiveWindow ? ActivityState.Working : ActivityState.Idle;
    }

    /// <summary>Writes a state file. Used by the hook entry point.</summary>
    public static void WriteState(ProviderId provider, ActivityState state, string? source)
    {
        AppPaths.EnsureCreated();

        var record = new ActivityRecord
        {
            State = state switch
            {
                ActivityState.Working => "working",
                ActivityState.Waiting => "waiting",
                _ => "idle"
            },
            At = DateTimeOffset.UtcNow,
            Source = source
        };

        JsonIo.TryWriteFile(AppPaths.ActivityFile(provider.Key()), record);
    }
}
