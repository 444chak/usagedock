using System.Text.Json;

namespace UsageDock.Services;

/// <summary>
/// Shared JSON options plus tolerant read and atomic write helpers.
/// </summary>
public static class JsonIo
{
    private static int _writeSequence;

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

    /// <summary>
    /// Writes via a temporary file then moves it into place, so a reader never sees a half
    /// written file.
    ///
    /// The temporary name is unique per call, combining the process id with a counter. Claude
    /// Code fires hooks concurrently, so several processes can write the same activity file at
    /// the same moment, and a shared temporary name would make them clobber each other.
    /// </summary>
    public static bool TryWriteFile<T>(string path, T value)
    {
        var ticket = Interlocked.Increment(ref _writeSequence);
        var temp = $"{path}.{Environment.ProcessId:x}-{ticket:x}.tmp";
        Exception? last = null;

        // Two writers can still collide on the destination rename itself. Losing that race is
        // transient, so retry briefly before treating it as a real failure.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(temp, JsonSerializer.Serialize(value, Options));

                // Atomic replace on the same volume, and unlike File.Replace it does not need
                // the destination to already exist.
                File.Move(temp, path, overwrite: true);

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                Thread.Sleep(15 * (attempt + 1));
            }
            catch (Exception ex)
            {
                last = ex;
                break;
            }
        }

        Log.Error($"Failed writing {Path.GetFileName(path)}", last);
        TryDelete(temp);
        return false;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Leaving a stray temp file behind is not worth a second failure.
        }
    }
}
