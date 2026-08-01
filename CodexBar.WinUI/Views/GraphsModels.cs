using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace CodexBar.WinUI;

/// <summary>
/// One row of the graphs window's per-model breakdown: label, inline meter, cost and tokens.
/// </summary>
/// <remarks>
/// MUTABLE AND RETAINED, for the same reason <see cref="UsageRowModel"/> is. Rebuilding the
/// collection destroys every container and hands the template a fresh <c>ProgressBar</c> whose value
/// goes 0 -&gt; N, which the stock template answers with its <c>Updating -&gt; Determinate</c>
/// reposition animation - so the meters would replay their slide-in on every render, and a single
/// refresh produces several. The collection is rebuilt only when the ordered model SET changes;
/// otherwise the rows are re-assigned through <see cref="Set{T}"/>, which raises nothing when the
/// value did not actually move.
/// </remarks>
public sealed class ModelRowModel : INotifyPropertyChanged
{
    private string name = string.Empty;
    private double meterValue;
    private Brush? colorBrush;
    private Brush? trackBrush;
    private string costText = string.Empty;
    private string tokensText = string.Empty;
    private string detailText = string.Empty;

    internal ModelRowModel(string key, bool isOverflow)
    {
        Key = key;
        IsOverflow = isOverflow;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The RAW model label - the key <see cref="ChartPalette.ForCategory"/> is keyed on.</summary>
    internal string Key { get; }

    /// <summary>True for the pooled "+N more" row, which is neutral-coloured and stands for a list.</summary>
    internal bool IsOverflow { get; }

    public string Name
    {
        get => name;
        internal set => Set(ref name, value);
    }

    /// <summary>0..100, scaled to the TOP spender rather than to the period total - see the window.</summary>
    public double MeterValue
    {
        get => meterValue;
        internal set => Set(ref meterValue, value);
    }

    public Brush? ColorBrush
    {
        get => colorBrush;
        internal set => Set(ref colorBrush, value);
    }

    /// <summary>
    /// The UNFILLED part of the meter.
    /// </summary>
    /// <remarks>
    /// Carried on the row rather than left to the ProgressBar's own track because the stock track is
    /// a 1px rule (<c>ProgressBarTrackHeight</c>) - see the meter's comment in the XAML. Painting it
    /// from <c>ChartPalette.Track</c> is also what keeps it correct under a forced theme, which a
    /// brush read out of the app resources would not be.
    /// </remarks>
    public Brush? TrackBrush
    {
        get => trackBrush;
        internal set => Set(ref trackBrush, value);
    }

    public string CostText
    {
        get => costText;
        internal set => Set(ref costText, value);
    }

    /// <summary>Compact tokens with no unit word ("12.4M"), because the column is 76 DIPs wide.</summary>
    public string TokensText
    {
        get => tokensText;
        internal set => Set(ref tokensText, value);
    }

    /// <summary>Everything the row had to drop, on its tooltip - the flyout's rule for trimmed rows.</summary>
    public string DetailText
    {
        get => detailText;
        internal set => Set(ref detailText, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
