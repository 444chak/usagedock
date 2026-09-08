using UsageDock.Models;

namespace UsageDock.Services;

/// <summary>
/// Handles the <c>--usagedock-hook &lt;state&gt;</c> invocation. Claude Code runs this on every
/// hook event, so it must do the least possible work and exit immediately.
/// </summary>
public static class HookEntryPoint
{
    public static bool TryHandle(string[] args)
    {
        var index = Array.FindIndex(
            args,
            a => string.Equals(a, HookInstaller.Marker, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            return false;
        }

        var stateArg = index + 1 < args.Length ? args[index + 1] : "idle";
        var state = ActivityRecord.ParseState(stateArg);

        if (state == ActivityState.Unknown)
        {
            state = ActivityState.Idle;
        }

        try
        {
            ActivityService.WriteState(ProviderId.Claude, state, stateArg);
        }
        catch
        {
            // A hook must never fail the Claude Code turn it is attached to.
        }

        return true;
    }
}
