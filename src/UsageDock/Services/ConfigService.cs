using UsageDock.Models;

namespace UsageDock.Services;

/// <summary>
/// Loads and saves <see cref="AppConfig"/>. A missing or corrupt file falls back to defaults
/// rather than failing, so a bad edit can never stop the app from starting.
/// </summary>
public sealed class ConfigService
{
    private readonly string _path;

    public ConfigService(string? path = null) => _path = path ?? AppPaths.ConfigFile;

    public AppConfig Load()
    {
        var config = JsonIo.TryReadFile<AppConfig>(_path) ?? AppConfig.CreateDefault();
        return config.Normalized();
    }

    public void Save(AppConfig config) => JsonIo.TryWriteFile(_path, config.Normalized());
}
