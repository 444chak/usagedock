namespace UsageDock.Models;

/// <summary>
/// A single rate-limit window (for example the rolling 5 hour window, or the weekly window).
/// Percentages are stored as 0..100.
/// </summary>
public sealed record QuotaWindow
{
    public required string Name { get; init; }

    /// <summary>Used share of the window, 0..100. Always clamped.</summary>
    public required double UsedPercent { get; init; }

    /// <summary>When the window rolls over, if known.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Nominal length of the window, if known.</summary>
    public TimeSpan? WindowLength { get; init; }

    public double LeftPercent => Math.Round(100d - UsedPercent, 2);

    public static QuotaWindow Create(
        string name,
        double usedPercent,
        DateTimeOffset? resetsAt = null,
        TimeSpan? windowLength = null) => new()
        {
            Name = name,
            UsedPercent = Clamp(usedPercent),
            ResetsAt = resetsAt,
            WindowLength = windowLength
        };

    /// <summary>Clamps to 0..100 and normalises NaN/Infinity to 0.</summary>
    public static double Clamp(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0d;
        }

        return Math.Clamp(value, 0d, 100d);
    }

    /// <summary>Converts a 0..1 utilisation ratio into a 0..100 percentage.</summary>
    public static double FromRatio(double ratio) => Clamp(ratio * 100d);

    /// <summary>Human readable countdown to the reset, for example "2h 14m".</summary>
    public string FormatResetIn(DateTimeOffset now)
    {
        if (ResetsAt is not { } resets)
        {
            return "unknown";
        }

        var remaining = resets - now;
        if (remaining <= TimeSpan.Zero)
        {
            return "now";
        }

        if (remaining.TotalDays >= 1)
        {
            return $"{(int)remaining.TotalDays}d {remaining.Hours}h";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        }

        if (remaining.TotalMinutes >= 1)
        {
            return $"{(int)remaining.TotalMinutes}m";
        }

        return "<1m";
    }
}
