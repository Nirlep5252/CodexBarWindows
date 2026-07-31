using System;
using System.Collections.Generic;
using System.Linq;
using LiveChartsCore;
using LiveChartsCore.Drawing;
using LiveChartsCore.Drawing.Layouts;
using LiveChartsCore.Kernel;
using LiveChartsCore.SkiaSharpView.Drawing;
using LiveChartsCore.SkiaSharpView.Drawing.Geometries;
using LiveChartsCore.SkiaSharpView.Drawing.Layouts;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.SKCharts;
using SkiaSharp;

namespace CodexBar.WinUI;

/// <summary>
/// The stock LiveCharts tooltip with the stacked TOTAL appended: a hairline rule, then a bold
/// "Total" row carrying the whole day's spend.
/// </summary>
/// <remarks>
/// <para>
/// This subclasses <see cref="SKDefaultTooltip"/> and overrides only <c>GetLayout</c> (which is
/// <c>protected virtual</c> in LiveCharts 2.0.5) rather than implementing <c>IChartTooltip</c>
/// from scratch, so the popup geometry, the wedge, the placement maths, the show/hide animation
/// AND the chart's own TooltipTextPaint / TooltipBackgroundPaint / TooltipTextSize all keep
/// working exactly as they do today - the base class reads them itself.
/// </para>
/// <para>
/// THE TOTAL LIVES IN THE BASE TABLE, NOT BESIDE IT. The stock layout is a vertical StackLayout
/// holding an optional heading label and one <see cref="TableLayout"/> whose three columns are
/// [miniature] [series name, Align.Start] [value, Align.End]. A total appended to the STACK is a
/// sibling of that table, so its columns are its own and its amount lands wherever its own text
/// happens to end - which is what read as "not lined up". Added as two more CELLS of the same
/// table, with the same column indices, the same cell paddings and the same per-cell alignments,
/// the amount shares a column with the amounts above it by construction, and the empty column 0
/// is what indents the word "Total" past the colour dots.
/// </para>
/// <para>
/// The two alternatives were rejected against the installed package: the stacked total is not
/// exposed to a per-series <c>YToolTipLabelFormatter</c> in a way that can produce a SEPARATE
/// row (it can only decorate one segment's own label, and which segment is topmost changes per
/// day), and a zero-height carrier series would still be drawn as a legend entry and a stack
/// member, and could not be given a rule or a bold weight.
/// </para>
/// <para>
/// The number comes from <see cref="ChartPoint.StackedValue"/>, which the stacker fills in for
/// every point of a stacked series, so it is the chart's own total rather than a re-derived sum.
/// </para>
/// </remarks>
internal sealed class StackTotalTooltip : SKDefaultTooltip
{
    /// <summary>
    /// Column indices of the stock tooltip table, mirrored so the total row lands in them. Column
    /// 0 is the series miniature and is deliberately left empty for the total.
    /// </summary>
    private const int NameColumn = 1;
    private const int ValueColumn = 2;

    /// <summary>Height of the rule's own table row - the rule is 1px inside it, the rest is air.</summary>
    private const float RuleRowHeight = 13f;

    private readonly Func<double, string> format;

    private SolidColorPaint? boldPaint;
    private SolidColorPaint? rulePaint;
    private SKColor boldPaintColor;
    private SKColor rulePaintColor;

    public StackTotalTooltip(Func<double, string> format)
    {
        this.format = format;
    }

    /// <summary>
    /// The hairline colour. Set from the chart palette on every theme rebuild - the tooltip is
    /// Skia-drawn, so nothing re-resolves it for free.
    /// </summary>
    public SKColor RuleColor { get; set; } = new(0x80, 0x80, 0x80, 0x60);

    protected override Layout<SkiaSharpDrawingContext> GetLayout(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        var layout = base.GetLayout(foundPoints, chart);

        if (layout is not StackLayout stack ||
            stack.Children.OfType<TableLayout>().FirstOrDefault() is not { } table ||
            ResolveTotal(foundPoints) is not { } total)
        {
            return layout;
        }

        // Cells is the only public read-back of the table's contents; the stock rows are numbered
        // from 0, so the rule and the total go straight after the last of them.
        var ruleRow = table.Cells.Length == 0 ? 0 : table.Cells.Max(cell => cell.Row) + 1;
        var totalRow = ruleRow + 1;

        // The chart sets TooltipTextSize explicitly (ApplyTooltipPaints); the fallback only
        // covers the "inherit the LiveCharts theme" sentinel, which this app never uses.
        var textSize = chart.View.TooltipTextSize > 0 ? (float)chart.View.TooltipTextSize : 12f;

        var color = (chart.View.TooltipTextPaint as SolidColorPaint)?.Color ?? SKColors.Black;
        var bold = BoldPaint(color);

        var rule = new SpanningRuleGeometry { Fill = RulePaint() };
        table.AddChild(rule, ruleRow, 0, Align.Start, Align.Middle);

        // Paddings copied from the stock cells (name 10 left/right, value 8 left/right) so the
        // text baselines start on the same x as the rows above; only the vertical padding differs,
        // and only to keep the total off the rule.
        table.AddChild(
            new LabelGeometry
            {
                Text = "Total",
                Paint = bold,
                TextSize = textSize,
                Padding = new Padding(10, 1, 10, 2),
                VerticalAlign = Align.Start,
                HorizontalAlign = Align.Start
            },
            totalRow,
            NameColumn,
            Align.Start);

        table.AddChild(
            new LabelGeometry
            {
                Text = format(total),
                Paint = bold,
                TextSize = textSize,
                Padding = new Padding(8, 1, 8, 2),
                VerticalAlign = Align.Start,
                HorizontalAlign = Align.Start
            },
            totalRow,
            ValueColumn,
            Align.End);

        // The rule spans the finished TABLE, and nothing knows that width until every cell has
        // been measured - including the total row just added, which can be the widest of them.
        // SpanningRuleGeometry measures as zero-width precisely so it cannot widen column 0 and
        // feed back into the number it is being handed here.
        rule.Span = table.Measure().Width;

        return layout;
    }

    private static double? ResolveTotal(IEnumerable<ChartPoint> foundPoints)
    {
        var points = foundPoints as IReadOnlyList<ChartPoint> ?? foundPoints.ToArray();

        // A total under a SINGLE row just prints the same number twice under a rule, which reads
        // as a rendering fault rather than a summary. An account that only ever used one model
        // would see that on every hover, so the row is earned by having something to add up.
        var contributing = points.Count(point => point.Coordinate.PrimaryValue > 0);
        if (contributing < 2)
        {
            return null;
        }

        // Every point of one stack carries the same Total; a non-stacked chart leaves it null and
        // gets no total row, which is correct - there is nothing to total.
        return points[0].StackedValue?.Total;
    }

    /// <summary>
    /// Paints are canvas-level tasks in LiveCharts, so they are cached rather than rebuilt on
    /// every hover; only an actual colour change replaces one.
    /// </summary>
    private SolidColorPaint BoldPaint(SKColor color)
    {
        if (boldPaint is null || boldPaintColor != color)
        {
            boldPaintColor = color;
            // SKTypeface, not the obsolete SKFontStyle: passing a null family keeps whatever the
            // system resolves for the tooltip's other rows and only changes the weight.
            boldPaint = new SolidColorPaint(color)
            {
                SKTypeface = SKTypeface.FromFamilyName(null, SKFontStyle.Bold)
            };
        }

        return boldPaint;
    }

    private SolidColorPaint RulePaint()
    {
        if (rulePaint is null || rulePaintColor != RuleColor)
        {
            rulePaintColor = RuleColor;
            rulePaint = new SolidColorPaint(RuleColor);
        }

        return rulePaint;
    }

    /// <summary>
    /// A hairline that OWNS a table row but not a table column: it measures as zero wide (so the
    /// miniature column it sits in keeps the width the colour dots gave it) and as a tall, mostly
    /// empty row (so the separation is air rather than a crowded 1px line), then draws itself
    /// <see cref="Span"/> wide across the whole table.
    /// </summary>
    /// <remarks>
    /// <see cref="BoundedDrawnGeometry.Measure"/> is virtual and is what <c>CoreTableLayout</c>
    /// sizes both columns and rows from, so overriding it is the only way to be laid out as a cell
    /// while contributing nothing to the column widths that the span is derived from.
    /// </remarks>
    private sealed class SpanningRuleGeometry : RectangleGeometry
    {
        /// <summary>Drawn width. Set after the table is measured; see GetLayout.</summary>
        public float Span { get; set; }

        public override LvcSize Measure() => new(0f, RuleRowHeight);

        public override void Draw(SkiaSharpDrawingContext context) =>
            context.Canvas.DrawRect(
                SKRect.Create(X, Y + ((RuleRowHeight - 1f) * 0.5f), Span, 1f),
                context.ActiveSkiaPaint);
    }
}
