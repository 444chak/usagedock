using System.Text.Json.Serialization;

namespace UsageDock.Models;

/// <summary>
/// User settings. Never holds secrets.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Device name of the monitor to dock to. Empty means the primary monitor.</summary>
    [JsonPropertyName("monitorDeviceName")]
    public string MonitorDeviceName { get; set; } = string.Empty;

    /// <summary>
    /// Vertical position of the dock centre on the target monitor work area, 0..1.
    /// 0.5 is vertically centred.
    /// </summary>
    [JsonPropertyName("verticalAnchor")]
    public double VerticalAnchor { get; set; } = 0.5d;

    /// <summary>Gap in logical pixels between the dock and the right edge of the work area.</summary>
    [JsonPropertyName("edgeMargin")]
    public double EdgeMargin { get; set; } = 8d;

    [JsonPropertyName("pollSeconds")]
    public int PollSeconds { get; set; } = 60;

    [JsonPropertyName("startWithWindows")]
    public bool StartWithWindows { get; set; }

    public static AppConfig CreateDefault() => new();

    /// <summary>Repairs out of range values coming from a hand edited config file.</summary>
    public AppConfig Normalized()
    {
        VerticalAnchor = double.IsFinite(VerticalAnchor) ? Math.Clamp(VerticalAnchor, 0d, 1d) : 0.5d;
        EdgeMargin = double.IsFinite(EdgeMargin) ? Math.Clamp(EdgeMargin, 0d, 200d) : 8d;
        PollSeconds = Math.Clamp(PollSeconds, 15, 3600);
        MonitorDeviceName ??= string.Empty;
        return this;
    }
}
