using System.Text;

namespace UsageDock.Services;

/// <summary>
/// Reads the last chunk of a possibly large append only file without loading all of it.
/// </summary>
public static class FileTail
{
    public const int DefaultTailBytes = 256 * 1024;

    /// <summary>
    /// Returns the complete lines contained in the last <paramref name="maxBytes"/> of the file,
    /// oldest first. A truncated leading line is discarded. Returns an empty array on any error.
    /// </summary>
    public static string[] ReadLines(string path, int maxBytes = DefaultTailBytes)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var length = stream.Length;
            if (length == 0)
            {
                return [];
            }

            var take = (int)Math.Min(length, maxBytes);
            var truncated = take < length;
            stream.Seek(length - take, SeekOrigin.Begin);

            var buffer = new byte[take];
            var read = stream.ReadAtLeast(buffer, take, throwOnEndOfStream: false);

            var text = Encoding.UTF8.GetString(buffer, 0, read);
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // The first line is very likely cut in half when we did not start at byte zero.
            if (truncated && lines.Length > 0)
            {
                lines = lines[1..];
            }

            return lines;
        }
        catch (IOException)
        {
            return [];
        }
        catch (UnauthorizedAccessException)
        {
            return [];
        }
    }
}
