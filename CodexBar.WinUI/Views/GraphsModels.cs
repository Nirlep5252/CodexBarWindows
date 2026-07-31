using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CodexBarWindows;
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

/// <summary>
/// The timeline strip's arithmetic: a granularity plus an ANCHOR DATE derives everything else.
/// </summary>
/// <remarks>
/// The period is never stored as a pair of dates. Storing the anchor is what makes a granularity
/// change keep the user where they were (July, switched to Week, lands on the week containing the
/// anchor) instead of resetting to today.
/// </remarks>
internal static class GraphsPeriod
{
    /// <summary>
    /// Inclusive local bounds of the period containing <paramref name="anchor"/>.
    /// </summary>
    /// <remarks>
    /// Weeks start on MONDAY, not on the culture's first day: <see cref="UsageLedger.Query"/> floors
    /// its week buckets to ISO Monday, and a strip that disagreed with the buckets it is asking for
    /// would place a column outside its own period.
    /// </remarks>
    public static (DateOnly Start, DateOnly EndInclusive) Bounds(UsageLedgerGranularity granularity, DateOnly anchor) => granularity switch
    {
        UsageLedgerGranularity.Year => (new DateOnly(anchor.Year, 1, 1), new DateOnly(anchor.Year, 12, 31)),
        UsageLedgerGranularity.Month => (new DateOnly(anchor.Year, anchor.Month, 1),
            new DateOnly(anchor.Year, anchor.Month, DateTime.DaysInMonth(anchor.Year, anchor.Month))),
        UsageLedgerGranularity.Week => WeekBounds(anchor),
        _ => (anchor, anchor)
    };

    private static (DateOnly Start, DateOnly EndInclusive) WeekBounds(DateOnly anchor)
    {
        var start = anchor.AddDays(-(((int)anchor.DayOfWeek + 6) % 7));
        return (start, start.AddDays(6));
    }

    /// <summary>The bucket a column of the chart stands for, one step finer than the period.</summary>
    public static UsageLedgerGranularity BucketOf(UsageLedgerGranularity granularity) => granularity switch
    {
        UsageLedgerGranularity.Year => UsageLedgerGranularity.Month,
        UsageLedgerGranularity.Day => UsageLedgerGranularity.Hour,
        _ => UsageLedgerGranularity.Day
    };

    /// <summary>Moves the anchor by whole periods, staying inside the new period.</summary>
    public static DateOnly Shift(UsageLedgerGranularity granularity, DateOnly anchor, int delta) => granularity switch
    {
        UsageLedgerGranularity.Year => new DateOnly(anchor.Year + delta, 1, 1),
        UsageLedgerGranularity.Month => FirstOfMonth(anchor).AddMonths(delta),
        UsageLedgerGranularity.Week => WeekBounds(anchor).Start.AddDays(7 * delta),
        _ => anchor.AddDays(delta)
    };

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>
    /// The strip's label. Relative wording is used for Day ONLY - a week or a month named
    /// "This week" reads as a filter rather than as a position in a sequence you can page through.
    /// </summary>
    public static string Label(UsageLedgerGranularity granularity, DateOnly anchor, DateOnly today)
    {
        var (start, end) = Bounds(granularity, anchor);
        switch (granularity)
        {
            case UsageLedgerGranularity.Year:
                return start.Year.ToString(CultureInfo.CurrentCulture);

            case UsageLedgerGranularity.Month:
                // The year is always shown: months are the navigation currency here, and "July"
                // alone is ambiguous the moment a second year of history exists.
                return start.ToString("MMMM yyyy", CultureInfo.CurrentCulture);

            case UsageLedgerGranularity.Week:
                if (start.Year != end.Year)
                {
                    return $"{start:d MMM yyyy} - {end:d MMM yyyy}";
                }

                return start.Year == today.Year
                    ? $"{start:d MMM} - {end:d MMM}"
                    : $"{start:d MMM} - {end:d MMM yyyy}";

            default:
                if (start == today)
                {
                    return "Today";
                }

                if (start == today.AddDays(-1))
                {
                    return "Yesterday";
                }

                return start.Year == today.Year
                    ? start.ToString("ddd d MMM", CultureInfo.CurrentCulture)
                    : start.ToString("ddd d MMM yyyy", CultureInfo.CurrentCulture);
        }
    }

    /// <summary>The unabbreviated inclusive range, for the label's tooltip.</summary>
    public static string RangeTooltip(UsageLedgerGranularity granularity, DateOnly anchor)
    {
        var (start, end) = Bounds(granularity, anchor);
        return start == end
            ? start.ToString("dddd, d MMMM yyyy", CultureInfo.CurrentCulture)
            : $"{start:d MMMM yyyy} - {end:d MMMM yyyy}";
    }

    /// <summary>What one column of the chart is called, for the drill-down chip and the peak metric.</summary>
    public static string BucketLabel(UsageLedgerGranularity granularity, DateTime bucketStart) => granularity switch
    {
        UsageLedgerGranularity.Year => bucketStart.ToString("MMMM", CultureInfo.CurrentCulture),
        UsageLedgerGranularity.Day => bucketStart.ToString("ddd d MMM, h tt", CultureInfo.CurrentCulture),
        _ => bucketStart.ToString("ddd d MMM", CultureInfo.CurrentCulture)
    };

    /// <summary>"day" / "week" / "month" / "year" - the unit the arrows step in.</summary>
    public static string Noun(UsageLedgerGranularity granularity) => granularity switch
    {
        UsageLedgerGranularity.Year => "year",
        UsageLedgerGranularity.Month => "month",
        UsageLedgerGranularity.Week => "week",
        _ => "day"
    };

    /// <summary>What one BUCKET is called in prose ("day", "hour", "month").</summary>
    public static string BucketNoun(UsageLedgerGranularity granularity) => granularity switch
    {
        UsageLedgerGranularity.Year => "month",
        UsageLedgerGranularity.Day => "hour",
        _ => "day"
    };

    /// <summary>Short name of the PREVIOUS period, for the delta metric's detail line.</summary>
    public static string PreviousShortLabel(UsageLedgerGranularity granularity, DateOnly anchor)
    {
        var previous = Shift(granularity, anchor, -1);
        return granularity switch
        {
            UsageLedgerGranularity.Year => previous.Year.ToString(CultureInfo.CurrentCulture),
            UsageLedgerGranularity.Month => previous.ToString("MMMM", CultureInfo.CurrentCulture),
            UsageLedgerGranularity.Week => $"the week of {Bounds(granularity, previous).Start:d MMM}",
            _ => previous.ToString("ddd d MMM", CultureInfo.CurrentCulture)
        };
    }

    /// <summary>Where the projection lands: "by 31 Jul", "by Sun", "by 11 PM", "by Dec".</summary>
    public static string ProjectionTarget(UsageLedgerGranularity granularity, DateOnly anchor)
    {
        var (_, end) = Bounds(granularity, anchor);
        return granularity switch
        {
            UsageLedgerGranularity.Year => $"by {end:MMM}",
            UsageLedgerGranularity.Week => $"by {end:ddd}",
            UsageLedgerGranularity.Day => "by 11 PM",
            _ => $"by {end:d MMM}"
        };
    }
}
