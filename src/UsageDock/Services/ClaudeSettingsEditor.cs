using System.Text.Json;
using System.Text.Json.Nodes;

namespace UsageDock.Services;

/// <summary>
/// Load, mutate and write Claude Code's settings.json, shared by everything that installs into it.
///
/// The file belongs to the user and to every other tool they run, so it is backed up once before
/// the first write and replaced atomically, never truncated in place.
/// </summary>
internal static class ClaudeSettingsEditor
{
    /// <summary>
    /// Applies <paramref name="mutate"/> and writes the result if it reports a change. Returns
    /// whether something was written.
    /// </summary>
    public static bool Edit(string settingsPath, Func<JsonObject, bool> mutate)
    {
        try
        {
            var text = JsonIo.TryReadAllText(settingsPath);
            JsonObject root;

            if (string.IsNullOrWhiteSpace(text))
            {
                root = new JsonObject();
            }
            else
            {
                root = JsonNode.Parse(text) as JsonObject ?? new JsonObject();
                BackupOnce(settingsPath, text);
            }

            if (!mutate(root))
            {
                return false;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            var options = new JsonSerializerOptions { WriteIndented = true };
            var temp = settingsPath + ".usagedock.tmp";
            File.WriteAllText(temp, root.ToJsonString(options));

            if (File.Exists(settingsPath))
            {
                File.Replace(temp, settingsPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, settingsPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Failed editing Claude Code settings", ex);
            return false;
        }
    }

    /// <summary>Parses the settings file for inspection, or null when it is absent or unreadable.</summary>
    public static JsonObject? TryRead(string settingsPath)
    {
        var text = JsonIo.TryReadAllText(settingsPath);

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void BackupOnce(string settingsPath, string original)
    {
        try
        {
            var backup = settingsPath + ".usagedock-bak";
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
