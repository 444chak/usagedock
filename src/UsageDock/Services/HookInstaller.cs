using System.Diagnostics;
using System.Text.Json.Nodes;

namespace UsageDock.Services;

/// <summary>
/// Installs and removes the Claude Code hooks that drive the activity indicator.
///
/// Edits are surgical: only entries carrying the UsageDock marker are added or removed, so hooks
/// belonging to other tools in the same settings file are left untouched. The previous file is
/// backed up before the first write.
/// </summary>
public sealed class HookInstaller
{
    /// <summary>Every command written by this installer contains this token.</summary>
    public const string Marker = "--usagedock-hook";

    private readonly string _settingsPath;
    private readonly string _executablePath;

    public HookInstaller(string? settingsPath = null, string? executablePath = null)
    {
        _settingsPath = settingsPath ?? AppPaths.ClaudeSettingsFile;
        _executablePath = executablePath ?? CurrentExecutablePath();
    }

    /// <summary>Hook event mapped to the activity state it implies.</summary>
    internal static readonly (string Event, string State, bool NeedsMatcher)[] HookMap =
    [
        ("SessionStart", "idle", false),
        ("UserPromptSubmit", "working", false),
        ("PreToolUse", "working", true),
        ("PostToolUse", "working", true),
        ("Notification", "waiting", false),
        ("Stop", "idle", false),
        ("SessionEnd", "idle", false)
    ];

    public static string CurrentExecutablePath()
    {
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path))
        {
            return path;
        }

        return Process.GetCurrentProcess().MainModule?.FileName ?? "UsageDock.exe";
    }

    public string BuildCommand(string state) => $"\"{_executablePath}\" {Marker} {state}";

    public bool IsInstalled()
    {
        var text = JsonIo.TryReadAllText(_settingsPath);
        return text is not null && text.Contains(Marker, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when hooks are installed but point at a different copy of the app than this one.
    ///
    /// Hooks store an absolute path, so moving or reinstalling the app leaves Claude Code
    /// launching an executable that is no longer there. Windows then shows its "install .NET"
    /// dialog on every single hook event, which is as unpleasant as it sounds.
    /// </summary>
    public bool NeedsRepointing()
    {
        var commands = OurCommands().ToList();

        // Compared per command rather than on the whole file: the status line feed carries the
        // same path, and a current one there must not hide a stale one here.
        return commands.Count > 0
               && commands.Any(c => !c.Contains(_executablePath, StringComparison.OrdinalIgnoreCase));
    }

    private IEnumerable<string> OurCommands()
    {
        if (ClaudeSettingsEditor.TryRead(_settingsPath)?["hooks"] is not JsonObject hooks)
        {
            yield break;
        }

        var commands = hooks
            .Select(static p => p.Value)
            .OfType<JsonArray>()
            .SelectMany(static list => list.OfType<JsonObject>())
            .Select(static entry => entry["hooks"])
            .OfType<JsonArray>()
            .SelectMany(static list => list.OfType<JsonObject>());

        foreach (var command in commands)
        {
            if (command["command"] is JsonValue value
                && value.TryGetValue<string>(out var text)
                && text.Contains(Marker, StringComparison.Ordinal))
            {
                yield return text;
            }
        }
    }

    /// <summary>
    /// True when this copy of the app is running out of a build output directory. Hooks pointing
    /// there break on the next clean build, so it is worth warning about.
    /// </summary>
    public bool IsRunningFromBuildOutput() =>
        _executablePath.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase)
        || _executablePath.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase);

    public bool Install()
    {
        return Edit(root =>
        {
            var hooks = GetOrCreateObject(root, "hooks");

            foreach (var (eventName, state, needsMatcher) in HookMap)
            {
                var list = GetOrCreateArray(hooks, eventName);
                RemoveOurEntries(list);
                list.Add(BuildEntry(state, needsMatcher));
            }

            return true;
        });
    }

    public bool Uninstall()
    {
        return Edit(root =>
        {
            if (root["hooks"] is not JsonObject hooks)
            {
                return false;
            }

            var changed = false;

            foreach (var property in hooks.ToList())
            {
                if (property.Value is not JsonArray list)
                {
                    continue;
                }

                if (RemoveOurEntries(list) > 0)
                {
                    changed = true;
                }

                if (list.Count == 0)
                {
                    hooks.Remove(property.Key);
                }
            }

            if (hooks.Count == 0)
            {
                root.Remove("hooks");
            }

            return changed;
        });
    }

    private JsonObject BuildEntry(string state, bool needsMatcher)
    {
        var entry = new JsonObject();

        if (needsMatcher)
        {
            entry["matcher"] = "*";
        }

        entry["hooks"] = new JsonArray(new JsonObject
        {
            ["type"] = "command",
            ["command"] = BuildCommand(state),
            ["timeout"] = 5
        });

        return entry;
    }

    /// <summary>Removes every entry whose command carries our marker. Returns how many went.</summary>
    private static int RemoveOurEntries(JsonArray list)
    {
        var removed = 0;

        for (var i = list.Count - 1; i >= 0; i--)
        {
            if (list[i] is JsonObject entry && ContainsMarker(entry))
            {
                list.RemoveAt(i);
                removed++;
            }
        }

        return removed;
    }

    private static bool ContainsMarker(JsonObject entry)
    {
        if (entry["hooks"] is not JsonArray commands)
        {
            return false;
        }

        return commands
            .OfType<JsonObject>()
            .Any(c => c["command"]?.GetValue<string>()?.Contains(Marker, StringComparison.Ordinal) == true);
    }

    private static JsonObject GetOrCreateObject(JsonObject parent, string name)
    {
        if (parent[name] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[name] = created;
        return created;
    }

    private static JsonArray GetOrCreateArray(JsonObject parent, string name)
    {
        if (parent[name] is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        parent[name] = created;
        return created;
    }

    private bool Edit(Func<JsonObject, bool> mutate) => ClaudeSettingsEditor.Edit(_settingsPath, mutate);
}
