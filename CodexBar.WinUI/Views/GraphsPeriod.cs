using System;
using System.Collections.Generic;
using System.Globalization;
using CodexBarWindows;

namespace CodexBar.WinUI;

/// <summary>
/// The timeline strip's arithmetic: a granularity plus an ANCHOR DATE derives everything else.
/// </summary>
/// <remarks>
/// <para>
/// The period is never stored as a pair of dates. Storing the anchor is what makes a granularity
/// change keep the user where they were (July, switched to Week, lands on the week containing the
/// anchor) instead of resetting to today.
/// </para>
/// <para>
/// Split out of GraphsModels.cs and kept FREE OF EVERY WinUI TYPE on purpose: the test project
/// cannot reference CodexBar.WinUI (WindowsAppSDK, win-x64, self-contained), so this file is
/// compiled into the test exe the same way <c>ChartPalette.cs</c> is. That is what lets the bucket
/// bounds, the partial-today arithmetic and the arrow-bound rule be tested against the exact source
/// the shell ships rather than a copy of it.
/// </para>
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

    /// <summary>
    /// The granularity a double-click on one column opens: the period BECOMES that column.
    /// </summary>
    /// <remarks>
    /// Deliberately not the same table as <see cref="BucketOf"/> even though it agrees with it
    /// everywhere but the bottom: a Day period's columns are hours, and there is no hour PERIOD, so
    /// Day is the floor and <see cref="CanStepFiner"/> is what the caller asks first.
    /// </remarks>
    public static UsageLedgerGranularity Finer(UsageLedgerGranularity granularity) => granularity switch
    {
        UsageLedgerGranularity.Year => UsageLedgerGranularity.Month,
        UsageLedgerGranularity.Month => UsageLedgerGranularity.Day,
        UsageLedgerGranularity.Week => UsageLedgerGranularity.Day,
        _ => UsageLedgerGranularity.Day
    };

    public static bool CanStepFiner(UsageLedgerGranularity granularity) =>
        granularity != UsageLedgerGranularity.Day;

    /// <summary>Moves the anchor by whole periods, staying inside the new period.</summary>
    public static DateOnly Shift(UsageLedgerGranularity granularity, DateOnly anchor, int delta) => granularity switch
    {
        UsageLedgerGranularity.Year => new DateOnly(anchor.Year + delta, 1, 1),
        UsageLedgerGranularity.Month => FirstOfMonth(anchor).AddMonths(delta),
        UsageLedgerGranularity.Week => WeekBounds(anchor).Start.AddDays(7 * delta),
        _ => anchor.AddDays(delta)
    };

    private static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>True when the period containing <paramref name="anchor"/> contains today.</summary>
    public static bool IsCurrent(UsageLedgerGranularity granularity, DateOnly anchor, DateOnly today)
    {
        var (start, end) = Bounds(granularity, anchor);
        return start <= today && today <= end;
    }

    /// <summary>
    /// Whether the back arrow has anywhere to go: the PREVIOUS period must still end at or after the
    /// earliest day any source can answer for.
    /// </summary>
    /// <remarks>
    /// One rule for all four granularities, which is what makes Year's arrow live as soon as the
    /// coverage floor falls in an earlier year. A null floor means "nothing is known about how far
    /// back the data goes", which must not disable navigation - the period simply comes back empty
    /// and says so.
    /// </remarks>
    public static bool CanGoBack(UsageLedgerGranularity granularity, DateOnly anchor, DateOnly? coverageFloor) =>
        coverageFloor is not { } floor ||
        Bounds(granularity, Shift(granularity, anchor, -1)).EndInclusive >= floor;

    /// <summary>
    /// How much of the period has actually HAPPENED, in buckets.
    /// </summary>
    /// <param name="Fraction">
    /// Elapsed buckets counting the in-progress one as the fraction of it that has passed. This is
    /// the divisor for "average per bucket" and the base of the projection: counting today as a
    /// whole day at 09:00 diluted the average by a fifth of a day and made the projection
    /// systematically low, worst early in the day.
    /// </param>
    /// <param name="Buckets">
    /// The same span counted in WHOLE buckets (the in-progress one counts as one), because "over 6
    /// days" is what a person says and "over 5.4 days" is not.
    /// </param>
    /// <param name="CurrentInProgress">True when the last counted bucket has not finished.</param>
    internal readonly record struct ElapsedUnits(double Fraction, int Buckets, bool CurrentInProgress);

    /// <summary>
    /// A bucket barely begun is not evidence. Below a quarter of a bucket the ratio
    /// cost/elapsed stops describing a rate and starts amplifying whatever landed in the first
    /// minutes, so the fraction is floored - the projection is then conservative rather than absurd.
    /// </summary>
    private const double MinimumElapsed = 0.25;

    /// <summary>
    /// Counts the elapsed part of a period, clamped to now AND to the coverage floor.
    /// </summary>
    /// <remarks>
    /// The floor clamp is why this is not simply "buckets before now": averaging over the calendar
    /// period reads as a 60% drop for the first month ever recorded, because a month whose recording
    /// began on the 20th has 12 days of data and 31 days of calendar.
    /// </remarks>
    public static ElapsedUnits Elapsed(
        IReadOnlyList<(DateTimeOffset Start, DateTimeOffset EndExclusive)> buckets,
        DateTimeOffset now,
        DateTimeOffset? coverageFloor)
    {
        var whole = 0;
        var partial = 0d;
        var inProgress = false;

        foreach (var (start, end) in buckets)
        {
            if (start > now)
            {
                // Dense buckets are ordered, so the first future bucket ends the count.
                break;
            }

            if (coverageFloor is { } floor && end <= floor)
            {
                continue;
            }

            if (end <= now)
            {
                whole++;
                continue;
            }

            var span = (end - start).TotalSeconds;
            if (span > 0)
            {
                partial = Math.Clamp((now - start).TotalSeconds / span, 0d, 1d);
                inProgress = true;
            }
        }

        var fraction = Math.Max(whole + (inProgress ? partial : 0d), MinimumElapsed);
        var counted = Math.Max(1, whole + (inProgress ? 1 : 0));
        return new ElapsedUnits(fraction, counted, inProgress);
    }

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

    /// <summary>
    /// What one column of the chart is called, for the drill-down chip and the peak metric.
    /// </summary>
    /// <param name="bucketSpan">
    /// How long the column actually covers, when that is not the granularity's usual bucket. In Day
    /// view a column is an hour and is named "Thu 30 Jul, 3 PM" - but when the ledger is still cold
    /// the scan can only answer for the WHOLE DAY, and naming that single column "12 AM" would claim
    /// an hour the data never had.
    /// </param>
    public static string BucketLabel(UsageLedgerGranularity granularity, DateTime bucketStart, TimeSpan? bucketSpan = null)
    {
        if (granularity == UsageLedgerGranularity.Day &&
            bucketSpan is { } span &&
            span > TimeSpan.FromHours(1))
        {
            return bucketStart.ToString("ddd d MMM", CultureInfo.CurrentCulture);
        }

        return granularity switch
        {
            UsageLedgerGranularity.Year => bucketStart.ToString("MMMM", CultureInfo.CurrentCulture),
            UsageLedgerGranularity.Day => bucketStart.ToString($"ddd d MMM, {HourPattern(bucketStart)}", CultureInfo.CurrentCulture),
            _ => bucketStart.ToString("ddd d MMM", CultureInfo.CurrentCulture)
        };
    }

    /// <summary>
    /// How an hour column's start is written: with its minutes when it HAS any.
    /// </summary>
    /// <remarks>
    /// The ledger keys records by whole UTC hours, so in a zone offset by a fraction of an hour the
    /// columns sit on the UTC grid and start at :30 (IST, ACST) or :45 (NPT). Printing those with
    /// "h tt" rounded 16:30 down to "4 PM" and made a 5 PM session look like it happened at 4.
    /// </remarks>
    public static string HourPattern(DateTime bucketStart) =>
        bucketStart.Minute == 0 ? "h tt" : "h:mm tt";

    /// <summary>How many columns apart the Day axis' labels sit - the step the axis is forced to.</summary>
    public const int DayAxisLabelEvery = 3;

    /// <summary>
    /// Explicit positions for the Day axis' labels, or <c>null</c> to leave the chart to place them.
    /// </summary>
    /// <remarks>
    /// LiveCharts picks its own separators at absolute multiples of the step
    /// (<c>Truncate(min / step) * step</c>), so they land on whole clock hours. The hour columns sit
    /// on the LEDGER'S UTC GRID - a record is keyed by a whole UTC hour - so in a zone offset by a
    /// fraction of an hour (IST +05:30, ACST +09:30, NPT +05:45) a column starts at :30 or :45 and
    /// an automatic label lands on the SEAM between two of them, naming an hour that neither column
    /// begins. Half a column is exactly the error <see cref="HourPattern"/> exists to stop the
    /// labels telling, so the columns are named explicitly instead: every label then sits under the
    /// bar it describes, at the spacing the forced step already chose.
    ///
    /// Whole-hour zones are already on the grid and keep the automatic separators untouched. The
    /// test is every column and not just the first because Lord Howe Island shifts by THIRTY
    /// MINUTES across DST, which moves the grid mid-day.
    /// </remarks>
    public static double[]? DayAxisLabelTicks(UsageLedgerGranularity granularity, IReadOnlyList<DateTime> bucketStarts)
    {
        if (granularity != UsageLedgerGranularity.Day || bucketStarts.Count < 2)
        {
            return null;
        }

        var offGrid = false;
        for (var index = 0; index < bucketStarts.Count && !offGrid; index++)
        {
            offGrid = bucketStarts[index].Minute != 0;
        }

        if (!offGrid)
        {
            return null;
        }

        var ticks = new List<double>();
        for (var index = 0; index < bucketStarts.Count; index += DayAxisLabelEvery)
        {
            ticks.Add(bucketStarts[index].Ticks);
        }

        return [.. ticks];
    }

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
