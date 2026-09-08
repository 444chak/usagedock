using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using UsageDock.Models;

namespace UsageDock.Ui;

/// <summary>
/// What the dock and the hover card show for one provider.
/// </summary>
public sealed class ProviderViewModel : INotifyPropertyChanged
{
    private static readonly Brush OkBrush = Frozen(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush WarnBrush = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush HighBrush = Frozen(Color.FromRgb(0xF8, 0x71, 0x71));
    private static readonly Brush MutedBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));

    private static readonly Brush WorkingBrush = Frozen(Color.FromRgb(0x38, 0xBD, 0xF8));
    private static readonly Brush WaitingBrush = Frozen(Color.FromRgb(0xFB, 0xBF, 0x24));
    private static readonly Brush IdleBrush = Frozen(Color.FromRgb(0x4B, 0x52, 0x63));

    private UsageSnapshot _snapshot;
    private ActivityState _activity = ActivityState.Idle;

    public ProviderViewModel(ProviderId id, string glyph)
    {
        Id = id;
        Glyph = glyph;
        _snapshot = UsageSnapshot.Failure(id, SnapshotStatus.Error, "Loading");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ProviderId Id { get; }

    public string Glyph { get; }

    public string Name => Id.DisplayName();

    public UsageSnapshot Snapshot => _snapshot;

    public ActivityState Activity => _activity;

    public bool HasData => _snapshot.HasData;

    public double Percent => _snapshot.Primary?.UsedPercent ?? 0d;

    public string PercentText => _snapshot.Primary is { } window
        ? $"{(int)Math.Round(window.UsedPercent)}"
        : "--";

    public Brush RingBrush => Percent switch
    {
        >= 90d => HighBrush,
        >= 70d => WarnBrush,
        _ => OkBrush
    };

    public bool Dimmed => !HasData;

    public Brush ActivityBrush => _activity switch
    {
        ActivityState.Working => WorkingBrush,
        ActivityState.Waiting => WaitingBrush,
        _ => IdleBrush
    };

    public Brush PercentBrush => HasData ? RingBrush : MutedBrush;

    public string ActivityText => _activity.DisplayName();

    public string PlanText => string.IsNullOrWhiteSpace(_snapshot.Plan) ? "-" : _snapshot.Plan!;

    public string StatusMessage => _snapshot.Message ?? string.Empty;

    public bool HasStatusMessage => !string.IsNullOrEmpty(_snapshot.Message);

    public IReadOnlyList<QuotaWindow> Windows => _snapshot.Windows;

    /// <summary>Rows for the hover card, recomputed on every update so countdowns stay live.</summary>
    public IReadOnlyList<QuotaRow> Rows => _snapshot.Windows
        .Select(w => QuotaRow.From(w, DateTimeOffset.UtcNow))
        .ToList();

    public string UpdatedText => FormatUpdated(_snapshot, DateTimeOffset.UtcNow);

    public void Update(UsageSnapshot snapshot)
    {
        _snapshot = snapshot;
        RaiseAll();
    }

    public void UpdateActivity(ActivityState activity)
    {
        if (_activity == activity)
        {
            return;
        }

        _activity = activity;
        Raise(nameof(Activity));
        Raise(nameof(ActivityBrush));
        Raise(nameof(ActivityText));
    }

    /// <summary>Relative age of the reading, for example "12s ago".</summary>
    internal static string FormatUpdated(UsageSnapshot snapshot, DateTimeOffset nowUtc)
    {
        if (!snapshot.HasData)
        {
            return "never";
        }

        var age = nowUtc - snapshot.UpdatedAt;
        var suffix = snapshot.FromCache ? " (cached)" : string.Empty;

        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        if (age.TotalSeconds < 60)
        {
            return $"{(int)age.TotalSeconds}s ago{suffix}";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{(int)age.TotalMinutes}m ago{suffix}";
        }

        if (age.TotalHours < 24)
        {
            return $"{(int)age.TotalHours}h ago{suffix}";
        }

        return $"{(int)age.TotalDays}d ago{suffix}";
    }

    private void RaiseAll()
    {
        foreach (var name in (string[])
                 [
                     nameof(Snapshot), nameof(HasData), nameof(Percent), nameof(PercentText),
                     nameof(RingBrush), nameof(PercentBrush), nameof(Dimmed), nameof(PlanText),
                     nameof(StatusMessage), nameof(HasStatusMessage), nameof(Windows), nameof(Rows),
                     nameof(UpdatedText)
                 ])
        {
            Raise(name);
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
