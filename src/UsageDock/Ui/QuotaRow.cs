using UsageDock.Models;

namespace UsageDock.Ui;

/// <summary>
/// One quota window rendered as a row in the hover card. The strings are pre-computed so the
/// card can bind directly without value converters.
/// </summary>
public sealed record QuotaRow(
    string Name,
    double Percent,
    string UsedText,
    string LeftText,
    string ResetText)
{
    public static QuotaRow From(QuotaWindow window, DateTimeOffset nowUtc) => new(
        window.Name,
        window.UsedPercent,
        $"{window.UsedPercent:0.#}% used",
        $"{window.LeftPercent:0.#}% left",
        window.FormatResetIn(nowUtc));
}
