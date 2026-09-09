using System.Text.Json.Serialization;
using UsageDock.Models;

namespace UsageDock.Services;

/// <summary>
/// Last known good usage per provider, so a 429 or a dropped network keeps showing numbers
/// instead of blanking the dock.
/// </summary>
public sealed class CacheService
{
    private readonly string _path;
    private CacheFile _cache;

    public CacheService(string? path = null)
    {
        _path = path ?? AppPaths.CacheFile;
        var loaded = JsonIo.TryReadFile<CacheFile>(_path);
        _cache = loaded ?? new CacheFile();

        if (path is null)
        {
            // A cache that silently fails to load looks exactly like a provider that has never
            // succeeded, which is a confusing thing to debug from the outside.
            Log.Info(loaded is null
                ? $"No usable cache at {_path}"
                : $"Cache loaded from {_path}: {string.Join(", ", _cache.Entries.Keys)}");
        }
    }

    public UsageSnapshot? Get(ProviderId provider) =>
        _cache.Entries.TryGetValue(provider.Key(), out var entry) ? entry.ToSnapshot(provider) : null;

    /// <summary>Stores a snapshot. Only successful reads with data are worth remembering.</summary>
    public void Store(UsageSnapshot snapshot)
    {
        if (snapshot.Status != SnapshotStatus.Ok || !snapshot.HasData)
        {
            return;
        }

        _cache.Entries[snapshot.Provider.Key()] = CacheEntry.FromSnapshot(snapshot);
        JsonIo.TryWriteFile(_path, _cache);
    }

    public void Clear()
    {
        _cache = new CacheFile();
        JsonIo.TryWriteFile(_path, _cache);
    }

    internal sealed class CacheFile
    {
        [JsonPropertyName("entries")]
        public Dictionary<string, CacheEntry> Entries { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class CacheEntry
    {
        [JsonPropertyName("plan")]
        public string? Plan { get; set; }

        [JsonPropertyName("updatedAt")]
        public DateTimeOffset UpdatedAt { get; set; }

        [JsonPropertyName("windows")]
        public List<CachedWindow> Windows { get; set; } = [];

        public static CacheEntry FromSnapshot(UsageSnapshot snapshot) => new()
        {
            Plan = snapshot.Plan,
            UpdatedAt = snapshot.UpdatedAt,
            Windows = snapshot.Windows
                .Select(w => new CachedWindow
                {
                    Name = w.Name,
                    UsedPercent = w.UsedPercent,
                    ResetsAt = w.ResetsAt,
                    WindowMinutes = w.WindowLength?.TotalMinutes
                })
                .ToList()
        };

        public UsageSnapshot ToSnapshot(ProviderId provider) => new()
        {
            Provider = provider,
            Status = SnapshotStatus.Ok,
            Plan = Plan,
            UpdatedAt = UpdatedAt,
            FromCache = true,
            Windows = Windows
                .Select(w => QuotaWindow.Create(
                    w.Name ?? "window",
                    w.UsedPercent,
                    w.ResetsAt,
                    w.WindowMinutes is { } m ? TimeSpan.FromMinutes(m) : null))
                .ToList()
        };
    }

    internal sealed class CachedWindow
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("usedPercent")]
        public double UsedPercent { get; set; }

        [JsonPropertyName("resetsAt")]
        public DateTimeOffset? ResetsAt { get; set; }

        [JsonPropertyName("windowMinutes")]
        public double? WindowMinutes { get; set; }
    }
}
