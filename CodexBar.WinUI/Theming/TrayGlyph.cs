using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexBar.WinUI;

/// <summary>
/// Builds the notification-area icon: the Codex glyph, rendered white or black to match the
/// current taskbar theme.
/// </summary>
/// <remarks>
/// The shell previously used the boxed application icon (CodexBarWindows.ico), which carries a
/// light plate and therefore reads as a bright tile next to the flat monochrome marks every
/// other tray icon uses — and never adapted when the taskbar theme changed. This mirrors the
/// WinForms TrayIconFactory's behaviour minus the vibes gradient, which this shell does not
/// implement.
/// </remarks>
internal static class TrayGlyph
{
    private static string AssetPath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Assets", fileName);

    /// <summary>
    /// Renders the tray icon for the current system theme. The caller owns the returned icon and
    /// must dispose the previous one — a tray icon swapped without disposing leaks a GDI handle
    /// per theme change.
    /// </summary>
    public static Icon Create()
    {
        using var source = LoadGlyphBitmap();
        using var rendered = RenderIconBitmap(source, 64);

        var handle = rendered.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            // Icon.FromHandle does not take ownership; the clone above is independent.
            DestroyIcon(handle);
        }
    }

    /// <summary>
    /// A light taskbar needs the black glyph and vice versa. Falls back to the other colour, then
    /// to the boxed app icon, so a missing asset degrades instead of throwing.
    /// </summary>
    private static Bitmap LoadGlyphBitmap()
    {
        var preferred = AssetPath(IsLightSystemTheme() ? "OpenAICodexLogoBlack.png" : "OpenAICodexLogoWhite.png");
        var fallback = AssetPath(IsLightSystemTheme() ? "OpenAICodexLogoWhite.png" : "OpenAICodexLogoBlack.png");

        if (File.Exists(preferred))
        {
            return new Bitmap(preferred);
        }

        if (File.Exists(fallback))
        {
            return new Bitmap(fallback);
        }

        using var appIcon = new Icon(AssetPath("CodexBarWindows.ico"));
        return appIcon.ToBitmap();
    }

    /// <summary>
    /// Crops to the glyph's actual ink and re-renders it square, so the mark fills the tray cell
    /// consistently regardless of the transparent padding baked into the source PNG.
    /// </summary>
    private static Bitmap RenderIconBitmap(Bitmap source, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var padding = Math.Max(2, size / 24);
        var destination = new Rectangle(padding, padding, size - (padding * 2), size - (padding * 2));
        graphics.DrawImage(source, destination, ContentBounds(source), GraphicsUnit.Pixel);

        return bitmap;
    }

    private static Rectangle ContentBounds(Bitmap bitmap)
    {
        var minX = bitmap.Width;
        var minY = bitmap.Height;
        var maxX = -1;
        var maxY = -1;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y).A <= 8)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            var side = Math.Min(bitmap.Width, bitmap.Height);
            return new Rectangle((bitmap.Width - side) / 2, (bitmap.Height - side) / 2, side, side);
        }

        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        var squareSide = Math.Max(width, height);
        var left = Math.Clamp(minX + (width / 2) - (squareSide / 2), 0, bitmap.Width - squareSide);
        var top = Math.Clamp(minY + (height / 2) - (squareSide / 2), 0, bitmap.Height - squareSide);

        return new Rectangle(left, top, squareSide, squareSide);
    }

    /// <summary>
    /// SystemUsesLightTheme is the TASKBAR's theme, which is what the tray icon sits on — this is
    /// deliberately not AppsUseLightTheme, which controls app content and can differ.
    /// </summary>
    public static bool IsLightSystemTheme()
    {
        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme",
            1);

        return value is not int intValue || intValue != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
