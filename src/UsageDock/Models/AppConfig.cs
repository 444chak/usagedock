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

    /// <summary>Screen edge the dock sits on.</summary>
    [JsonPropertyName("edge")]
    [JsonConverter(typeof(JsonStringEnumConverter<DockEdge>))]
    public DockEdge Edge { get; set; } = DockEdge.Right;

    /// <summary>
    /// Position of the dock along its edge on the target monitor work area, 0..1: top to bottom
    /// on a side edge, left to right on the top edge. 0.5 is centred. The JSON name predates the
    /// top edge and is kept so existing configs keep their position.
    /// </summary>
    [JsonPropertyName("verticalAnchor")]
    public double Anchor { get; set; } = 0.5d;

    /// <summary>Folded away to a small tab against the screen edge.</summary>
    [JsonPropertyName("collapsed")]
    public bool Collapsed { get; set; }

    /// <summary>Gap in logical pixels between the dock and its screen edge.</summary>
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
        Anchor = double.IsFinite(Anchor) ? Math.Clamp(Anchor, 0d, 1d) : 0.5d;
        Edge = Enum.IsDefined(Edge) ? Edge : DockEdge.Right;
        EdgeMargin = double.IsFinite(EdgeMargin) ? Math.Clamp(EdgeMargin, 0d, 200d) : 8d;
        PollSeconds = Math.Clamp(PollSeconds, 15, 3600);
        MonitorDeviceName ??= string.Empty;
        return this;
    }
}
