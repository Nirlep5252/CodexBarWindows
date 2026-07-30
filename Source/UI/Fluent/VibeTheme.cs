using System.Drawing.Drawing2D;

namespace CodexBarWindows;

/// <summary>
/// Palette, gradient and motion helpers for the opt-in "vibes" appearance. Everything here is
/// inert unless <see cref="FluentTheme.VibesActive"/> is true; callers gate on that flag so the
/// default appearance stays byte-for-byte identical when the toggle is off.
/// </summary>
public static class VibeTheme
{
    // V3 Code palette: near-black indigo canvas, violet-tinted surfaces, and a signature
    // magenta -> violet -> blue sweep for fills and highlights.
    public static readonly Color Background = Color.FromArgb(0xFF, 0x0E, 0x0B, 0x18);
    public static readonly Color Accent = Color.FromArgb(0xFF, 0x8F, 0x6C, 0xFF);

    /// <summary>
    /// Chrome accent for the vibes appearance (buttons, toggles, pickers). The vibes surface
    /// theme is baked in as Graphite: hue-free charcoal with a steel-blue accent.
    /// </summary>
    public static readonly Color StyleAccent = Color.FromArgb(0xFF, 0x6F, 0xA8, 0xFF);

    /// <summary>
    /// Heat color the fill tip morphs toward between 70% and 90%. Violet by design: it
    /// contrasts both the teal Codex identity and the orange Claude identity, where the
    /// classic amber would disappear.
    /// </summary>
    public static readonly Color HeatWarn = Color.FromArgb(0xFF, 0x8F, 0x6C, 0xFF);

    /// <summary>Heat color the fill tip reaches at 100%: electric purple.</summary>
    public static readonly Color HeatDanger = Color.FromArgb(0xFF, 0xD0, 0x2E, 0xFF);
    public static readonly Color GradientStart = Color.FromArgb(0xFF, 0xFF, 0x2E, 0x97);
    public static readonly Color GradientMid = Color.FromArgb(0xFF, 0x9B, 0x5C, 0xFF);
    public static readonly Color GradientEnd = Color.FromArgb(0xFF, 0x4E, 0x8C, 0xFF);
    public static readonly Color WarnStart = Color.FromArgb(0xFF, 0xFF, 0xB4, 0x54);
    public static readonly Color WarnEnd = Color.FromArgb(0xFF, 0xFF, 0x2E, 0x97);
    public static readonly Color DangerStart = Color.FromArgb(0xFF, 0xFF, 0x5C, 0x8A);
    public static readonly Color DangerEnd = Color.FromArgb(0xFF, 0xFF, 0x2E, 0x3C);
    public static readonly Color Spark = Color.FromArgb(0xFF, 0xFF, 0x9A, 0x3C);

    /// <summary>Sparkle tints used for celebration bursts.</summary>
    public static readonly Color[] SparklePalette =
    [
        GradientStart,
        GradientMid,
        GradientEnd,
        Spark,
        Color.White,
    ];

    /// <summary>Grow-from-zero reveal duration for meters and chart bars.</summary>
    public const int RevealDurationMs = 900;

    /// <summary>Duration of the palette cross-fade when switching providers.</summary>
    public const int PaletteTransitionMs = 350;

    /// <summary>Codex: mint -> teal -> blue.</summary>
    public static readonly ProviderVibe CodexVibe = new(
        Color.FromArgb(0xFF, 0x2E, 0xD9, 0xB8),
        Color.FromArgb(0xFF, 0x2E, 0xE6, 0xA8),
        Color.FromArgb(0xFF, 0x1F, 0xC8, 0xD0),
        Color.FromArgb(0xFF, 0x3D, 0x8C, 0xFF));

    /// <summary>Claude: amber -> orange -> coral.</summary>
    public static readonly ProviderVibe ClaudeVibe = new(
        Color.FromArgb(0xFF, 0xFF, 0x8A, 0x3C),
        Color.FromArgb(0xFF, 0xFF, 0xB4, 0x54),
        Color.FromArgb(0xFF, 0xFF, 0x8A, 0x3C),
        Color.FromArgb(0xFF, 0xFF, 0x4E, 0x6A));

    /// <summary>The signature magenta -> violet -> blue sweep (Cursor and fallback).</summary>
    public static readonly ProviderVibe SignatureVibe = new(Accent, GradientStart, GradientMid, GradientEnd);

    /// <summary>Resolves the provider-identity palette from a provider key.</summary>
    public static ProviderVibe ForProvider(string? providerKey)
    {
        if (providerKey is null)
        {
            return SignatureVibe;
        }

        if (providerKey.Contains("claude", StringComparison.OrdinalIgnoreCase))
        {
            return ClaudeVibe;
        }

        return providerKey.Contains("cursor", StringComparison.OrdinalIgnoreCase) ? SignatureVibe : CodexVibe;
    }

    /// <summary>Linear RGB interpolation preserving full opacity.</summary>
    public static Color LerpColor(Color from, Color to, double amount)
    {
        amount = Math.Clamp(amount, 0d, 1d);
        return Color.FromArgb(
            (int)Math.Round(from.A + ((to.A - from.A) * amount)),
            (int)Math.Round(from.R + ((to.R - from.R) * amount)),
            (int)Math.Round(from.G + ((to.G - from.G) * amount)),
            (int)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    /// <summary>Blends two provider palettes; drives the smooth provider-switch transition.</summary>
    public static ProviderVibe Lerp(ProviderVibe from, ProviderVibe to, double amount)
    {
        return new ProviderVibe(
            LerpColor(from.Accent, to.Accent, amount),
            LerpColor(from.GradientStart, to.GradientStart, amount),
            LerpColor(from.GradientMid, to.GradientMid, amount),
            LerpColor(from.GradientEnd, to.GradientEnd, amount));
    }

    /// <summary>
    /// Hue-family series color for chart legends and model breakdowns: sample
    /// <paramref name="index"/> of <paramref name="count"/> along the palette sweep.
    /// </summary>
    public static Color SeriesColor(ProviderVibe vibe, int index, int count)
    {
        if (count <= 1)
        {
            return vibe.GradientMid;
        }

        var t = Math.Clamp(index / (double)(count - 1), 0d, 1d);
        var color = t < 0.5d
            ? LerpColor(vibe.GradientStart, vibe.GradientMid, t * 2d)
            : LerpColor(vibe.GradientMid, vibe.GradientEnd, (t - 0.5d) * 2d);

        // Alternate lightness so adjacent series (e.g. a model and its "fast" variant)
        // separate clearly even within one hue family.
        return index % 2 == 1 ? FluentTheme.Lighten(color, 0.28f) : color;
    }

    /// <summary>Duration of the glow pulse played when an animated value lands.</summary>
    public const int PulseDurationMs = 650;

    public static double EaseOutQuint(double progress)
    {
        progress = Math.Clamp(progress, 0d, 1d);
        return 1d - Math.Pow(1d - progress, 5d);
    }

    /// <summary>
    /// 0 while comfortably under the limit, ramping to 1 as usage reaches 100%. Drives the
    /// continuous warning treatment so provider identity is never discarded wholesale.
    /// </summary>
    public static double HeatLevel(double percent) => Math.Clamp((percent - 70d) / 30d, 0d, 1d);

    /// <summary>
    /// Gradient stops for a usage fill. The provider sweep is kept at the trailing end while
    /// the leading tip continuously warms: identity color up to 70%, morphing to amber by
    /// 90% and to pure red at 100% — so a Codex bar stays teal at its base with an
    /// increasingly hot tip, and Claude's coral deepens to unmistakable red.
    /// </summary>
    public static (Color Start, Color End) FillGradient(double percent, ProviderVibe? vibe = null)
    {
        var palette = vibe ?? SignatureVibe;
        var warn = HeatWarn;
        Color end;
        if (percent <= 70d)
        {
            end = palette.GradientEnd;
        }
        else if (percent <= 90d)
        {
            end = LerpColor(palette.GradientEnd, warn, (percent - 70d) / 20d);
        }
        else
        {
            end = LerpColor(warn, HeatDanger, Math.Min(1d, (percent - 90d) / 10d));
        }

        var start = LerpColor(palette.GradientStart, warn, HeatLevel(percent) * 0.3d);
        return (start, end);
    }

    /// <summary>
    /// Horizontal gradient brush spanning <paramref name="track"/> so partial fills clip a
    /// consistent sweep instead of compressing it. Caller owns disposal.
    /// </summary>
    public static LinearGradientBrush FillBrush(RectangleF track, double percent, ProviderVibe? vibe = null)
    {
        var palette = vibe ?? SignatureVibe;
        var (start, end) = FillGradient(percent, palette);
        // Inflate slightly: LinearGradientBrush edge texels wrap when the path touches the
        // brush rectangle boundary, which reads as a dark seam on the rounded caps.
        var brushRect = RectangleF.Inflate(track, 1f, 1f);
        var brush = new LinearGradientBrush(brushRect, start, end, LinearGradientMode.Horizontal);
        if (percent < 70d)
        {
            var blend = new ColorBlend(3)
            {
                Colors = [start, palette.GradientMid, end],
                Positions = [0f, 0.55f, 1f]
            };
            brush.InterpolationColors = blend;
        }

        return brush;
    }

    /// <summary>Alpha-scaled copy of <paramref name="color"/> (0..1).</summary>
    public static Color WithOpacity(Color color, double opacity)
    {
        var alpha = (int)Math.Round(Math.Clamp(opacity, 0d, 1d) * 255d);
        return Color.FromArgb(alpha, color);
    }
}

/// <summary>
/// One provider's vibe identity: an accent for chrome (tabs, pickers, hairlines) and a
/// three-stop gradient sweep for fills. Codex is blue-green, Claude is orange, Cursor keeps
/// the signature violet.
/// </summary>
public sealed record ProviderVibe(Color Accent, Color GradientStart, Color GradientMid, Color GradientEnd);

/// <summary>
/// A short-lived particle burst rendered by its host control's own paint pass, so it composites
/// correctly over custom-drawn and backdrop surfaces. Create one per host, call
/// <see cref="Burst"/> for each celebration, and call <see cref="Render"/> at the end of the
/// host's OnPaint. The field drives repaints itself while particles are alive and goes fully
/// idle (timer stopped) when none are.
/// </summary>
public sealed class SparkleField : IDisposable
{
    private const int TickMs = 30;

    private readonly Control host;
    private readonly System.Windows.Forms.Timer timer;
    private readonly List<Particle> particles = [];
    private readonly Random random = new();
    private long lastTick;

    public SparkleField(Control host)
    {
        this.host = host;
        timer = new System.Windows.Forms.Timer { Interval = TickMs };
        timer.Tick += (_, _) => Step();
    }

    public bool IsActive => particles.Count > 0;

    /// <summary>Spawns a radial burst of sparkles at <paramref name="origin"/> (host client coordinates).</summary>
    public void Burst(PointF origin, int count = 14)
    {
        if (!FluentAnimator.AnimationsEnabled)
        {
            return;
        }

        for (var i = 0; i < count; i++)
        {
            var angle = random.NextDouble() * Math.PI * 2d;
            var speed = 28d + (random.NextDouble() * 68d);
            particles.Add(new Particle
            {
                X = origin.X,
                Y = origin.Y,
                VelocityX = (float)(Math.Cos(angle) * speed),
                VelocityY = (float)((Math.Sin(angle) * speed) - 22d),
                LifeMs = 420 + random.Next(420),
                AgeMs = 0,
                Size = 2f + ((float)random.NextDouble() * 3f),
                Rotation = (float)(random.NextDouble() * 360d),
                Spin = (float)((random.NextDouble() - 0.5d) * 540d),
                Tint = VibeTheme.SparklePalette[random.Next(VibeTheme.SparklePalette.Length)]
            });
        }

        if (!timer.Enabled)
        {
            lastTick = Environment.TickCount64;
            timer.Start();
        }
    }

    /// <summary>Draws live particles as four-point stars. Call last in the host's OnPaint.</summary>
    public void Render(Graphics graphics)
    {
        if (particles.Count == 0)
        {
            return;
        }

        var previousSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        foreach (var particle in particles)
        {
            var life = 1f - ((float)particle.AgeMs / particle.LifeMs);
            var alpha = (int)Math.Clamp(life * 255f, 0f, 255f);
            using var brush = new SolidBrush(Color.FromArgb(alpha, particle.Tint));

            var state = graphics.Save();
            graphics.TranslateTransform(particle.X, particle.Y);
            graphics.RotateTransform(particle.Rotation);
            var reach = particle.Size * (0.6f + (life * 0.8f));
            var waist = Math.Max(0.7f, particle.Size * 0.28f);
            using var star = new GraphicsPath();
            star.AddPolygon(
            [
                new PointF(0f, -reach),
                new PointF(waist, 0f),
                new PointF(0f, reach),
                new PointF(-waist, 0f),
            ]);
            star.AddPolygon(
            [
                new PointF(-reach, 0f),
                new PointF(0f, waist),
                new PointF(reach, 0f),
                new PointF(0f, -waist),
            ]);
            graphics.FillPath(brush, star);
            graphics.Restore(state);
        }

        graphics.SmoothingMode = previousSmoothing;
    }

    private void Step()
    {
        var now = Environment.TickCount64;
        var deltaMs = (int)Math.Clamp(now - lastTick, 1, 100);
        lastTick = now;
        var deltaSeconds = deltaMs / 1000f;

        for (var i = particles.Count - 1; i >= 0; i--)
        {
            var particle = particles[i];
            particle.AgeMs += deltaMs;
            if (particle.AgeMs >= particle.LifeMs)
            {
                particles.RemoveAt(i);
                continue;
            }

            particle.X += particle.VelocityX * deltaSeconds;
            particle.Y += particle.VelocityY * deltaSeconds;
            particle.VelocityY += 88f * deltaSeconds;
            particle.VelocityX *= 1f - (1.6f * deltaSeconds);
            particle.Rotation += particle.Spin * deltaSeconds;
        }

        if (particles.Count == 0)
        {
            timer.Stop();
        }

        if (!host.IsDisposed)
        {
            host.Invalidate();
        }
    }

    public void Dispose()
    {
        timer.Stop();
        timer.Dispose();
        particles.Clear();
    }

    private sealed class Particle
    {
        public float X;
        public float Y;
        public float VelocityX;
        public float VelocityY;
        public int LifeMs;
        public int AgeMs;
        public float Size;
        public float Rotation;
        public float Spin;
        public Color Tint;
    }
}
