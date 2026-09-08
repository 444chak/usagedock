using System.Text.Json.Serialization;
using UsageDock.Models;

namespace UsageDock.Services;

/// <summary>
/// The tiny document a hook writes to signal what an agent is doing. Contains no secrets and
/// no prompt content, only a state and a timestamp.
/// </summary>
public sealed class ActivityRecord
{
    [JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [JsonPropertyName("at")]
    public DateTimeOffset At { get; set; }

    /// <summary>The hook event that produced this record, for troubleshooting.</summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    public ActivityState ToState() => ParseState(State);

    public static ActivityState ParseState(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "working" or "running" or "busy" => ActivityState.Working,
        "waiting" or "attention" or "blocked" => ActivityState.Waiting,
        "idle" or "done" or "finished" => ActivityState.Idle,
        _ => ActivityState.Unknown
    };

    /// <summary>
    /// A hook can be missed, for example when a terminal is killed. A stale Working or Waiting
    /// record therefore decays to Idle instead of pinning the dock in a busy state forever.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public ActivityState ResolveState(DateTimeOffset nowUtc)
    {
        var state = ToState();

        if (state is ActivityState.Working or ActivityState.Waiting && nowUtc - At > StaleAfter)
        {
            return ActivityState.Idle;
        }

        return state;
    }
}
