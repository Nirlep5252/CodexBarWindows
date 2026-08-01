using System;
using CodexBarWindows;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace CodexBar.WinUI;

/// <summary>
/// The provider marks, as vector path data so a tab icon stays crisp at any DPI and can be
/// tinted (the WinForms tabs blitted a pair of PNGs for Codex and hand-rolled an SVG path
/// parser for the other two).
/// </summary>
/// <remarks>
/// The data cannot live in the XAML as a <c>PathGeometry.Figures</c> attribute - the XAML
/// COMPILER refuses to convert the abbreviated syntax at compile time (WMC0055), even though
/// the runtime converter handles it happily. So it is converted at runtime instead, freshly
/// per icon: a geometry costs microseconds and sharing one across elements is exactly the
/// kind of thing that turns into a crash here.
/// <para>
/// Every path is prefixed <c>F0</c> (EvenOdd) to match the SVG sources, whose inner subpaths
/// are holes - the Codex mark's centre and the Cursor cube's face both fill solid under the
/// default nonzero rule.
/// </para>
/// </remarks>
internal static class ProviderGeometry
{
    private const string CodexData =
        "F0 M 60.8734 57.2556 v -14.9432 c 0 -1.2586 0.4722 -2.2029 1.5728 -2.8314 l 30.0443 -17.3023 c 4.0899 -2.3593 8.9662 -3.4599 13.9988 -3.4599 18.8759 0 30.8307 14.6289 30.8307 30.2006 0 1.1007 0 2.3593 -0.158 3.6178 l -31.1446 -18.2467 c -1.8872 -1.1006 -3.7754 -1.1006 -5.6629 0 l -39.4812 22.9651 Z M 131.0276 115.4561 v -35.7074 c 0 -2.2028 -0.9446 -3.7756 -2.8318 -4.8763 l -39.481 -22.9651 12.8982 -7.3934 c 1.1007 -0.6285 2.0453 -0.6285 3.1458 0 l 30.0441 17.3024 c 8.6523 5.0341 14.4708 15.7296 14.4708 26.1107 0 11.9539 -7.0769 22.965 -18.2461 27.527 v 0.0021 Z M 51.593 83.9964 l -12.8982 -7.5497 c -1.1007 -0.6285 -1.5728 -1.5728 -1.5728 -2.8314 v -34.6048 c 0 -16.8303 12.8982 -29.5722 30.3585 -29.5722 6.607 0 12.7403 2.2029 17.9324 6.1349 l -30.987 17.9324 c -1.8871 1.1007 -2.8314 2.6735 -2.8314 4.8764 v 45.6159 l -0.0014 -0.0015 Z M 79.3562 100.0403 l -18.4829 -10.3811 v -22.0209 l 18.4829 -10.3811 18.4812 10.3811 v 22.0209 l -18.4812 10.3811 Z M 91.2319 147.8591 c -6.607 0 -12.7403 -2.2031 -17.9324 -6.1344 l 30.9866 -17.9333 c 1.8872 -1.1005 2.8318 -2.6728 2.8318 -4.8759 v -45.616 l 13.0564 7.5498 c 1.1005 0.6285 1.5723 1.5728 1.5723 2.8314 v 34.6051 c 0 16.8297 -13.0564 29.5723 -30.5147 29.5723 v 0.001 Z M 53.9522 112.7822 l -30.0443 -17.3024 c -8.652 -5.0343 -14.471 -15.7296 -14.471 -26.1107 0 -12.1119 7.2356 -22.9652 18.403 -27.5272 v 35.8634 c 0 2.2028 0.9443 3.7756 2.8314 4.8763 l 39.3248 22.8068 -12.8982 7.3938 c -1.1007 0.6287 -2.045 0.6287 -3.1456 0 Z M 52.2229 138.5791 c -17.7745 0 -30.8306 -13.3713 -30.8306 -29.8871 0 -1.2585 0.1578 -2.5169 0.3143 -3.7754 l 30.987 17.9323 c 1.8871 1.1005 3.7757 1.1005 5.6628 0 l 39.4811 -22.807 v 14.9435 c 0 1.2585 -0.4721 2.2021 -1.5728 2.8308 l -30.0443 17.3025 c -4.0898 2.359 -8.9662 3.4605 -13.9989 3.4605 h 0.0014 Z M 91.2319 157.296 c 19.0327 0 34.9188 -13.5272 38.5383 -31.4594 17.6164 -4.562 28.9425 -21.0779 28.9425 -37.908 0 -11.0112 -4.719 -21.7066 -13.2133 -29.4143 0.7867 -3.3035 1.2595 -6.607 1.2595 -9.909 0 -22.4929 -18.2471 -39.3247 -39.3251 -39.3247 -4.2461 0 -8.3363 0.6285 -12.4262 2.045 -7.0792 -6.9213 -16.8318 -11.3254 -27.5271 -11.3254 -19.0331 0 -34.9191 13.5268 -38.5384 31.4591 C 11.3255 36.0212 0 52.5373 0 69.3675 c 0 11.0112 4.7184 21.7065 13.2125 29.4142 -0.7865 3.3035 -1.2586 6.6067 -1.2586 9.9092 0 22.4923 18.2466 39.3241 39.3248 39.3241 4.2462 0 8.3362 -0.6277 12.426 -2.0441 7.0776 6.921 16.8302 11.3251 27.5271 11.3251 Z";

    private const string ClaudeData =
        "F0 m 19.6 66.5 19.7 -11 0.3 -1 -0.3 -0.5 h -1 l -3.3 -0.2 -11.2 -0.3 L 14 53 l -9.5 -0.5 -2.4 -0.5 L 0 49 l 0.2 -1.5 2 -1.3 2.9 0.2 6.3 0.5 9.5 0.6 6.9 0.4 L 38 49.1 h 1.6 l 0.2 -0.7 -0.5 -0.4 -0.4 -0.4 L 29 41 l -10.6 -7 -5.6 -4.1 -3 -2 -1.5 -2 -0.6 -4.2 2.7 -3 3.7 0.3 0.9 0.2 3.7 2.9 8 6.1 L 38 36 l 1.5 1.2 0.6 -0.4 0.1 -0.3 -0.7 -1.1 L 33 25 l -6 -10.4 -2.7 -4.3 -0.7 -2.6 c -0.3 -1 -0.4 -2 -0.4 -3 l 3 -4.2 L 28 0 l 4.2 0.6 L 33.8 2 l 2.6 6 4.1 9.3 L 47 29.9 l 2 3.8 1 3.4 0.3 1 h 0.7 v -0.5 l 0.5 -7.2 1 -8.7 1 -11.2 0.3 -3.2 1.6 -3.8 3 -2 L 61 2.6 l 2 2.9 -0.3 1.8 -1.1 7.7 L 59 27.1 l -1.5 8.2 h 0.9 l 1 -1.1 4.1 -5.4 6.9 -8.6 3 -3.5 L 77 13 l 2.3 -1.8 h 4.3 l 3.1 4.7 -1.4 4.9 -4.4 5.6 -3.7 4.7 -5.3 7.1 -3.2 5.7 0.3 0.4 h 0.7 l 12 -2.6 6.4 -1.1 7.6 -1.3 3.5 1.6 0.4 1.6 -1.4 3.4 -8.2 2 -9.6 2 -14.3 3.3 -0.2 0.1 0.2 0.3 6.4 0.6 2.8 0.2 h 6.8 l 12.6 1 3.3 2 1.9 2.7 -0.3 2 -5.1 2.6 -6.8 -1.6 -16 -3.8 -5.4 -1.3 h -0.8 v 0.4 l 4.6 4.5 8.3 7.5 L 89 80.1 l 0.5 2.4 -1.3 2 -1.4 -0.2 -9.2 -7 -3.6 -3 -8 -6.8 h -0.5 v 0.7 l 1.8 2.7 9.8 14.7 0.5 4.5 -0.7 1.4 -2.6 1 -2.7 -0.6 -5.8 -8 -6 -9 -4.7 -8.2 -0.5 0.4 -2.9 30.2 -1.3 1.5 -3 1.2 -2.5 -2 -1.4 -3 1.4 -6.2 1.6 -8 1.3 -6.4 1.2 -7.9 0.7 -2.6 v -0.2 H 49 L 43 72 l -9 12.3 -7.2 7.6 -1.7 0.7 -3 -1.5 0.3 -2.8 L 24 86 l 10 -12.8 6 -7.9 4 -4.6 -0.1 -0.5 h -0.3 L 17.2 77.4 l -4.7 0.6 -2 -2 0.2 -3 1 -1 8 -5.5 Z";

    private const string CursorData =
        "F0 M 84.0704 28.9353 L 51.9066 10.4454 C 50.8738 9.8515 49.5994 9.8515 48.5666 10.4454 L 16.4043 28.9353 C 15.536 29.4345 15 30.3576 15 31.3575 V 68.6425 C 15 69.6424 15.536 70.5655 16.4043 71.0647 L 48.5681 89.5546 C 49.6009 90.1485 50.8753 90.1485 51.9081 89.5546 L 84.0719 71.0647 C 84.9402 70.5655 85.4762 69.6424 85.4762 68.6425 V 31.3575 C 85.4762 30.3576 84.9402 29.4345 84.0719 28.9353 H 84.0704 Z M 82.0501 32.8519 L 51.0006 86.4003 C 50.7907 86.7611 50.2366 86.6138 50.2366 86.1958 V 51.1329 C 50.2366 50.4322 49.8606 49.7842 49.2506 49.4324 L 18.7553 31.9017 C 18.3929 31.6927 18.5409 31.141 18.9606 31.141 H 81.0595 C 81.9414 31.141 82.4925 32.0927 82.0516 32.8534 H 82.0501 V 32.8519 Z";

    // The official OpenCode Go mark: one block O, authored on its 100x100 view box.
    private const string OpenCodeGoData =
        "F0 M 20 12 H 80 V 88 H 20 Z M 35 27 H 65 V 72 H 35 Z";

    /// <summary>
    /// The Grok mark: the two interlocking xAI swooshes, authored on a 0–24 view box (the
    /// official artwork's). <see cref="Normalize"/> scales it to <see cref="IconSize"/>, so the
    /// smaller view box costs nothing.
    /// </summary>
    /// <remarks>
    /// The source SVG draws the outer sweep with two elliptical arcs. They are transcribed here
    /// as a straight line (the first arc's sagitta is under 0.1 units - invisible at 14 DIPs)
    /// and one cubic bezier fitted to the second, which keeps the data parseable by the
    /// WinForms tab painter's mini path parser too - it has no arc support.
    /// </remarks>
    private const string GrokData =
        "F0 M9.27 15.29 l7.978-5.897 c.391-.29.95-.177 1.137.272 .98 2.369.542 5.215-1.41 7.169 " +
        "-1.951 1.954-4.667 2.382-7.149 1.406 l-2.711 1.257 c3.889 2.661 8.611 2.003 11.562-.953 " +
        "2.341-2.344 3.066-5.539 2.388-8.42 l.006.007 c-.983-4.232.242-5.924 2.75-9.383 " +
        ".06-.082.12-.164.179-.248 l-3.301 3.305 v-.01 L9.267 15.292 Z " +
        "M7.623 16.723 c-2.792-2.67-2.31-6.801.071-9.184 1.761-1.763 4.647-2.483 7.166-1.425 " +
        "l2.705-1.25 L15.736 3.864 C12.388 2.494 8.54 3.27 5.984 5.83 " +
        "c-2.533 2.536-3.33 6.436-1.962 9.764 1.022 2.487-.653 4.246-2.34 6.022 " +
        "-.599.63-1.199 1.259-1.682 1.925 l7.62-6.815 Z";

    /// <summary>Edge length of the square the mark is normalised into, in DIPs.</summary>
    private const double IconSize = 14;

    /// <summary>
    /// A ready-to-host <see cref="IconSize"/>-square icon element for one provider, or null
    /// when the mark could not be parsed (the tab then shows its name alone).
    /// </summary>
    /// <remarks>
    /// This returns a whole ELEMENT rather than a <c>Geometry</c> to bind to a
    /// <c>Path.Data</c>, because <c>{x:Bind}</c> to a Geometry-typed property silently binds
    /// nothing: it compiles, the Path lays out, and the mark never paints. Assigning
    /// <c>Data</c> in code sidesteps the binding entirely.
    /// </remarks>
    public static UIElement? CreateIcon(UsageProvider provider, Brush fill)
    {
        var geometry = Parse(provider switch
        {
            UsageProvider.Claude => ClaudeData,
            UsageProvider.Cursor => CursorData,
            UsageProvider.OpenCodeGo => OpenCodeGoData,
            UsageProvider.Grok => GrokData,
            _ => CodexData
        });

        if (geometry is null)
        {
            return null;
        }

        // Stretch stays None and the size is explicit: the geometry is already normalised, and
        // a stretching shape resolves a degenerate scale inside the horizontally scrolling tab
        // strip (which measures with infinite width) and paints nothing.
        return new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = geometry,
            Fill = fill,
            Width = IconSize,
            Height = IconSize,
            Stretch = Stretch.None,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    /// <summary>
    /// Converts abbreviated path data into a standalone, unparented geometry.
    /// </summary>
    /// <remarks>
    /// It has to be <see cref="XamlBindingHelper.ConvertValue"/> and NOT
    /// <c>XamlReader.Load("&lt;Path Data=... /&gt;")</c>: a geometry pulled back off a parsed
    /// Path is still owned by that Path, and assigning it to a second Path takes the whole
    /// process down with a 0xc000027b originate-error out of CoreMessagingXP.
    /// </remarks>
    private static Geometry? Parse(string pathData)
    {
        try
        {
            var geometry = XamlBindingHelper.ConvertValue(typeof(Geometry), pathData) as Geometry;
            if (geometry is not null)
            {
                Normalize(geometry);
            }
            else
            {
                DiagnosticLog.Write("provider geometry converted to null");
            }

            return geometry;
        }
        catch (Exception exception)
        {
            // A missing glyph must never take the flyout down with it: the tab still shows its
            // name, which is what actually identifies the provider.
            DiagnosticLog.Write("provider geometry failed to parse: {0}", exception.Message);
            return null;
        }
    }

    /// <summary>
    /// Scales the mark into an <see cref="IconSize"/> box with the geometry's own transform,
    /// so the hosting <c>Path</c> can be left at <c>Stretch="None"</c>.
    /// </summary>
    /// <remarks>
    /// THIS IS NOT COSMETIC. The tab strip lives in a horizontally scrolling ScrollViewer,
    /// which measures its content with INFINITE available width. A shape that relies on
    /// stretching (a <c>Path</c> with <c>Stretch="Uniform"</c>, or a <c>Viewbox</c> wrapper)
    /// resolves a degenerate scale against that infinity: it reserves its layout slot and then
    /// paints nothing at all. Both were tried and both produced invisible icons with a
    /// correctly sized gap where the mark should be.
    /// </remarks>
    private static void Normalize(Geometry geometry)
    {
        var bounds = geometry.Bounds;
        var extent = Math.Max(bounds.Width, bounds.Height);
        if (extent <= 0)
        {
            return;
        }

        var scale = IconSize / extent;
        var transform = new TransformGroup();
        // Children apply in order: move the mark's own bounding box onto the origin first,
        // then scale, so paths authored on different view boxes all land in the same square.
        transform.Children.Add(new TranslateTransform { X = -bounds.X, Y = -bounds.Y });
        transform.Children.Add(new ScaleTransform { ScaleX = scale, ScaleY = scale });
        geometry.Transform = transform;
    }
}
