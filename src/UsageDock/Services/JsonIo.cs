using System.Text.Json;

namespace UsageDock.Services;

/// <summary>
/// Shared JSON options plus tolerant read and atomic write helpers.
/// </summary>
public static class JsonIo
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true
    };

    /// <summary>
    /// Reads and deserialises a file, returning null on any failure. Opens with full sharing so
    /// that a file another process is writing does not throw.
    /// </summary>
    public static T? TryReadFile<T>(string path) where T : class
    {
        var text = TryReadAllText(path);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Reads a file as text without locking it, returning null on failure.</summary>
    public static string? TryReadAllText(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes via a temporary file then replaces, so readers never see a half file.</summary>
    public static bool TryWriteFile<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));

            if (File.Exists(path))
            {
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed writing {Path.GetFileName(path)}", ex);
            return false;
        }
    }
}
