using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace CodexBar.WinUI;

/// <summary>One rate-limit window row inside the usage card.</summary>
public sealed class UsageRowModel
{
    internal UsageRowModel(
        string title,
        string percentText,
        double meterValue,
        Brush heatBrush,
        string remainingText,
        string resetText,
        bool showSeparator,
        bool isIndeterminate)
    {
        Title = title;
        PercentText = percentText;
        MeterValue = meterValue;
        HeatBrush = heatBrush;
        RemainingText = remainingText;
        ResetText = resetText;
        SeparatorVisibility = showSeparator ? Visibility.Visible : Visibility.Collapsed;
        IsIndeterminate = isIndeterminate;
    }

    public string Title { get; }

    public string PercentText { get; }

    public double MeterValue { get; }

    /// <summary>Shared by the meter fill and the percent figure so they always agree.</summary>
    public Brush HeatBrush { get; }

    public string RemainingText { get; }

    public string ResetText { get; }

    public Visibility SeparatorVisibility { get; }

    /// <summary>True while the first snapshot for this provider is still being fetched.</summary>
    public bool IsIndeterminate { get; }
}
