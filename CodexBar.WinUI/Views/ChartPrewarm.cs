using System;
using System.Diagnostics;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SkiaSharp;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>
/// Renders one throwaway LiveCharts chart in an off-screen window at startup, so the Usage
/// graphs window opens instantly instead of sitting blank.
/// </summary>
/// <remarks>
/// <para>
/// THE PROBLEM THIS SOLVES IS MEASURED, NOT THEORETICAL. The first chart rendered in a process
/// pays for loading libSkiaSharp, building the Skia surface and warming the drawing pipeline.
/// Until that first frame lands the whole XAML window it lives in shows nothing - not a partial
/// chart, an empty window. Opening "Usage graphs" cold therefore looked like the app had hung.
/// </para>
/// <para>
/// The cost is per PROCESS, not per chart, so a chart nobody sees can absorb it. This one is
/// drawn in a tiny window parked far outside the virtual desktop, shown WITHOUT activation (it
/// must never steal focus or blink on screen), and closed as soon as its first frame lands.
/// </para>
/// <para>
/// Set <c>CODEXBAR_WINUI_NOPREWARM=1</c> to skip it - that is how the before/after timings in the
/// diagnostic log are produced.
/// </para>
/// </remarks>
internal static class ChartPrewarm
{
    /// <summary>Backstop teardown in case the chart never reports a finished update.</summary>
    private static readonly TimeSpan MaxLifetime = TimeSpan.FromSeconds(8);

    private static Window? window;
    private static bool started;

    /// <summary>True once a chart has actually painted a frame in this process.</summary>
    public static bool IsWarm { get; private set; }

    public static void Start(DispatcherQueue queue)
    {
        if (started)
        {
            return;
        }

        started = true;

        if (Environment.GetEnvironmentVariable("CODEXBAR_WINUI_NOPREWARM") == "1")
        {
            DiagnosticLog.Write("chart prewarm skipped (CODEXBAR_WINUI_NOPREWARM=1)");
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            var host = new Window();
            window = host;

            var hwnd = WindowNative.GetWindowHandle(host);
            if (host.AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
                presenter.IsAlwaysOnTop = false;
            }

            host.AppWindow.IsShownInSwitchers = false;
            host.AppWindow.Title = "CodexBar chart prewarm";

            // Parked far outside the virtual desktop: the window is genuinely visible (so the
            // compositor renders it, which is the whole point) but never on any monitor.
            host.AppWindow.MoveAndResize(new RectInt32(-32000, -32000, 320, 240));

            var chart = new CartesianChart
            {
                // The same series and axis types the real window uses, so the warm-up walks the
                // same code paths rather than a cheaper one.
                Series =
                [
                    new StackedColumnSeries<double>
                    {
                        Values = [1d, 2d, 3d],
                        Fill = new SolidColorPaint(SKColors.Gray)
                    },
                    new StackedColumnSeries<double>
                    {
                        Values = [3d, 2d, 1d],
                        Fill = new SolidColorPaint(SKColors.DarkGray)
                    }
                ],
                XAxes = [new Axis { LabelsPaint = new SolidColorPaint(SKColors.Gray) }],
                YAxes = [new Axis { LabelsPaint = new SolidColorPaint(SKColors.Gray) }]
            };

            chart.UpdateFinished += _ =>
            {
                if (!IsWarm)
                {
                    IsWarm = true;
                    DiagnosticLog.Write("chart prewarm first frame in {0} ms", stopwatch.ElapsedMilliseconds);
                    // Deferred: tearing the window down inside the chart's own update callback
                    // disposes the canvas it is still walking.
                    queue.TryEnqueue(Stop);
                }
            };

            host.Content = chart;
            host.AppWindow.Show(activateWindow: false);
            DiagnosticLog.Write("chart prewarm window shown");

            var backstop = queue.CreateTimer();
            backstop.Interval = MaxLifetime;
            backstop.IsRepeating = false;
            backstop.Tick += (_, _) =>
            {
                if (!IsWarm)
                {
                    DiagnosticLog.Write("chart prewarm gave up after {0} ms", stopwatch.ElapsedMilliseconds);
                }

                Stop();
            };
            backstop.Start();
        }
        catch (Exception exception)
        {
            // A pre-warm that fails must never take the app with it; the graphs window just pays
            // the stall it would have paid anyway.
            DiagnosticLog.Write("chart prewarm failed: {0}", exception.Message);
            Stop();
        }
    }

    public static void Stop()
    {
        var host = window;
        window = null;
        if (host is null)
        {
            return;
        }

        try
        {
            host.Content = null;
            host.Close();
            DiagnosticLog.Write("chart prewarm window closed warm={0}", IsWarm);
        }
        catch (Exception exception)
        {
            DiagnosticLog.Write("chart prewarm teardown failed: {0}", exception.Message);
        }
    }
}
