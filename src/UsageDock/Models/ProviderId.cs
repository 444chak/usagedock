namespace UsageDock.Models;

public enum ProviderId
{
    Claude,
    Codex
}

public static class ProviderIdExtensions
{
    public static string DisplayName(this ProviderId id) => id switch
    {
        ProviderId.Claude => "Claude",
        ProviderId.Codex => "Codex",
        _ => id.ToString()
    };

    /// <summary>Stable lowercase key used for file names and JSON keys.</summary>
    public static string Key(this ProviderId id) => id switch
    {
        ProviderId.Claude => "claude",
        ProviderId.Codex => "codex",
        _ => id.ToString().ToLowerInvariant()
    };
}
