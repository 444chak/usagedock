using System.Diagnostics;
using System.Text.Json;
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

    /// <summary>Loads, mutates and writes settings.json, keeping a one time backup.</summary>
    private bool Edit(Func<JsonObject, bool> mutate)
    {
        try
        {
            var text = JsonIo.TryReadAllText(_settingsPath);
            JsonObject root;

            if (string.IsNullOrWhiteSpace(text))
            {
                root = new JsonObject();
            }
            else
            {
                root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
                BackupOnce(text);
            }

            if (!mutate(root))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var temp = _settingsPath + ".usagedock.tmp";
            File.WriteAllText(temp, root.ToJsonString(options));

            if (File.Exists(_settingsPath))
            {
                File.Replace(temp, _settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, _settingsPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed editing Claude Code settings", ex);
            return false;
        }
    }

    private void BackupOnce(string original)
    {
        try
        {
            var backup = _settingsPath + ".usagedock-bak";
            if (!File.Exists(backup))
            {
                File.WriteAllText(backup, original);
            }
        }
        catch (IOException)
        {
            // A missing backup must not block the edit.
        }
    }
}
