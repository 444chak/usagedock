using System.Text.Json.Nodes;

namespace UsageDock.Services;

/// <summary>
/// Installs and removes the status line feed in Claude Code's settings.json.
///
/// Claude Code allows a single status line, and the user may already have one from another tool.
/// Rather than replace it, the installer chains in front of it: UsageDock reads the payload,
/// passes it on unchanged, and the existing command keeps drawing the line exactly as before.
/// The original command is kept verbatim inside the chained one, which is what lets uninstall
/// put it back without a side file that could drift out of step.
/// </summary>
public sealed class StatusLineInstaller
{
    /// <summary>Every status line command written by this installer contains this token.</summary>
    public const string Marker = "--usagedock-statusline";

    /// <summary>Passes stdin through to stdout instead of drawing a line of its own.</summary>
    public const string TeeFlag = "--tee";

    // The previous command is wrapped in a subshell on lines of its own, so a trailing comment
    // or an exit in it cannot swallow or cut short the wrapper.
    private const string ChainOpener = "| (\n";
    private const string ChainCloser = "\n)";

    private readonly string _settingsPath;
    private readonly string _executablePath;

    public StatusLineInstaller(string? settingsPath = null, string? executablePath = null)
    {
        _settingsPath = settingsPath ?? AppPaths.ClaudeSettingsFile;
        _executablePath = executablePath ?? HookInstaller.CurrentExecutablePath();
    }

    /// <summary>
    /// The command to install. Chained, a missing executable (the app uninstalled, say) falls
    /// through to <c>cat</c> so the other status line still gets its input instead of going
    /// blank on every turn.
    /// </summary>
    internal string BuildCommand(string? previous) => previous is null
        ? $"\"{_executablePath}\" {Marker}"
        : $"{{ \"{_executablePath}\" {Marker} {TeeFlag} 2>/dev/null || cat; }} {ChainOpener}{previous}{ChainCloser}";

    /// <summary>
    /// The command that was chained behind ours, or null when ours stands alone or the command
    /// is not one this installer wrote.
    /// </summary>
    internal static string? ExtractPrevious(string command)
    {
        var tee = command.IndexOf(Marker + " " + TeeFlag, StringComparison.Ordinal);
        if (tee < 0)
        {
            return null;
        }

        var open = command.IndexOf(ChainOpener, tee, StringComparison.Ordinal);
        if (open < 0 || !command.EndsWith(ChainCloser, StringComparison.Ordinal))
        {
            return null;
        }

        var start = open + ChainOpener.Length;
        var end = command.Length - ChainCloser.Length;
        return end > start ? command[start..end] : null;
    }

    public bool IsInstalled() => CurrentCommand(ClaudeSettingsEditor.TryRead(_settingsPath)) is { } command
                                 && command.Contains(Marker, StringComparison.Ordinal);

    /// <summary>True when the feed is installed but points at another copy of the app.</summary>
    public bool NeedsRepointing() => CurrentCommand(ClaudeSettingsEditor.TryRead(_settingsPath)) is { } command
                                     && command.Contains(Marker, StringComparison.Ordinal)
                                     && !command.Contains(_executablePath, StringComparison.OrdinalIgnoreCase);

    public bool Install()
    {
        return ClaudeSettingsEditor.Edit(_settingsPath, root =>
        {
            if (root["statusLine"] is null)
            {
                root["statusLine"] = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = BuildCommand(previous: null)
                };

                return true;
            }

            if (root["statusLine"] is not JsonObject statusLine
                || statusLine["type"] is not JsonValue type
                || !type.TryGetValue<string>(out var kind)
                || kind != "command"
                || CurrentCommand(root) is not { } current
                || string.IsNullOrWhiteSpace(current))
            {
                // Not a shape this installer understands. Leaving it alone beats guessing.
                Log.Warn("Claude Code status line has an unexpected shape, not installing the feed");
                return false;
            }

            var previous = current.Contains(Marker, StringComparison.Ordinal)
                ? ExtractPrevious(current)
                : current;

            var command = BuildCommand(previous);
            if (command == current)
            {
                return false;
            }

            statusLine["command"] = command;
            return true;
        });
    }

    public bool Uninstall()
    {
        return ClaudeSettingsEditor.Edit(_settingsPath, root =>
        {
            if (CurrentCommand(root) is not { } current || !current.Contains(Marker, StringComparison.Ordinal))
            {
                return false;
            }

            if (ExtractPrevious(current) is { } previous)
            {
                ((JsonObject)root["statusLine"]!)["command"] = previous;
            }
            else
            {
                root.Remove("statusLine");
            }

            return true;
        });
    }

    private static string? CurrentCommand(JsonObject? root) =>
        root?["statusLine"] is JsonObject statusLine
        && statusLine["command"] is JsonValue value
        && value.TryGetValue<string>(out var command)
            ? command
            : null;
}
