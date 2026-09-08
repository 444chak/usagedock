using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace UsageDock.Ui;

/// <summary>
/// Maps a 0..100 percentage onto the pixel width of the progress bar in the hover card.
/// The card has a fixed width, so a fixed track width keeps this a single multiplication.
/// </summary>
public sealed class PercentToWidthConverter : IValueConverter
{
    /// <summary>Must match the track width used in HoverCard.xaml.</summary>
    public const double TrackWidth = 222d;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = value is double d ? d : 0d;
        return Math.Clamp(percent, 0d, 100d) / 100d * TrackWidth;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        Binding.DoNothing;
}
