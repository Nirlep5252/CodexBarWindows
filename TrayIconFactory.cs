using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using Microsoft.Win32;
using System.Runtime.InteropServices;

namespace CodexBarWindows;

public static class TrayIconFactory
{
    private static readonly string WhiteLogoPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "OpenAICodexLogoWhite.png");

    private static readonly string BlackLogoPath = Path.Combine(
        AppContext.BaseDirectory,
        "Assets",
        "OpenAICodexLogoBlack.png");

    public static Icon Create()
    {
        using var source = LoadOfficialLogoBitmap();
        using var iconBitmap = RenderIconBitmap(source, 64);

        var handle = iconBitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static Bitmap LoadOfficialLogoBitmap()
    {
        var preferredPath = IsLightSystemTheme() ? BlackLogoPath : WhiteLogoPath;
        var fallbackPath = preferredPath == BlackLogoPath ? WhiteLogoPath : BlackLogoPath;

        if (File.Exists(preferredPath))
        {
            return new Bitmap(preferredPath);
        }

        if (File.Exists(fallbackPath))
        {
            return new Bitmap(fallbackPath);
        }

        return CreateFallbackBitmap();
    }

    private static Bitmap RenderIconBitmap(Bitmap source, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        bitmap.SetResolution(96, 96);

        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);

        var sourceRect = ContentBounds(source);
        var padding = Math.Max(2, size / 24);
        var destination = new Rectangle(padding, padding, size - (padding * 2), size - (padding * 2));
        graphics.DrawImage(source, destination, sourceRect, GraphicsUnit.Pixel);

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
            return new Rectangle(
                (bitmap.Width - side) / 2,
                (bitmap.Height - side) / 2,
                side,
                side);
        }

        var width = maxX - minX + 1;
        var height = maxY - minY + 1;
        var squareSide = Math.Max(width, height);
        var centerX = minX + (width / 2);
        var centerY = minY + (height / 2);
        var left = Math.Clamp(centerX - (squareSide / 2), 0, bitmap.Width - squareSide);
        var top = Math.Clamp(centerY - (squareSide / 2), 0, bitmap.Height - squareSide);

        return new Rectangle(left, top, squareSide, squareSide);
    }

    private static bool IsLightSystemTheme()
    {
        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme",
            1);

        return value is int intValue ? intValue != 0 : true;
    }

    private static Bitmap CreateFallbackBitmap()
    {
        var bitmap = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var brush = new LinearGradientBrush(
            new Rectangle(8, 8, 48, 48),
            Color.FromArgb(21, 48, 83),
            Color.FromArgb(15, 140, 255),
            LinearGradientMode.ForwardDiagonal);
        graphics.FillEllipse(brush, 8, 8, 48, 48);

        using var pen = new Pen(Color.White, 6)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };
        graphics.DrawLine(pen, 24, 39, 24, 47);
        graphics.DrawLine(pen, 34, 31, 34, 47);
        graphics.DrawLine(pen, 44, 23, 44, 47);

        return bitmap;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
