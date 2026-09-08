namespace UsageDock.Models;

public enum ActivityState
{
    /// <summary>Nothing running.</summary>
    Idle,

    /// <summary>The agent is doing work.</summary>
    Working,

    /// <summary>The agent is blocked on the user (permission prompt, question).</summary>
    Waiting,

    /// <summary>No signal available for this provider.</summary>
    Unknown
}

public static class ActivityStateExtensions
{
    public static string DisplayName(this ActivityState s) => s switch
    {
        ActivityState.Idle => "Idle",
        ActivityState.Working => "Working",
        ActivityState.Waiting => "Waiting for user",
        _ => "Unknown"
    };
}
