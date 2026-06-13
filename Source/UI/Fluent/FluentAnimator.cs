using System.Runtime.InteropServices;

namespace CodexBarWindows;

/// <summary>
/// Lightweight UI-thread animation helper built on <see cref="System.Windows.Forms.Timer"/>.
/// Respects the user's reduced-motion preference (client area animation setting).
/// </summary>
public static class FluentAnimator
{
    private const uint SpiGetClientAreaAnimation = 0x1042;

    /// <summary>
    /// True when the user allows client-area animations (SPI_GETCLIENTAREAANIMATION).
    /// Defaults to true when the setting cannot be read.
    /// </summary>
    public static bool AnimationsEnabled
    {
        get
        {
            var enabled = 1;
            if (!SystemParametersInfo(SpiGetClientAreaAnimation, 0, ref enabled, 0))
            {
                return true;
            }

            return enabled != 0;
        }
    }

    /// <summary>
    /// Animates a value from <paramref name="from"/> to <paramref name="to"/> over
    /// <paramref name="durationMs"/> with ease-out cubic, invoking <paramref name="apply"/> on
    /// the UI thread roughly every 15 ms and <paramref name="completed"/> when finished. When
    /// animations are disabled (or duration is non-positive) it applies the final value
    /// immediately. Disposing the returned handle cancels the animation without firing
    /// <paramref name="completed"/>.
    /// </summary>
    public static IDisposable Animate(double from, double to, int durationMs, Action<double> apply, Action? completed = null)
    {
        ArgumentNullException.ThrowIfNull(apply);

        if (!AnimationsEnabled || durationMs <= 0)
        {
            apply(to);
            completed?.Invoke();
            return EmptyDisposable.Instance;
        }

        var timer = new System.Windows.Forms.Timer { Interval = 15 };
        var handle = new AnimationHandle(timer);
        var start = Environment.TickCount64;
        timer.Tick += (_, _) =>
        {
            var progress = Math.Clamp((Environment.TickCount64 - start) / (double)durationMs, 0d, 1d);
            var eased = 1d - Math.Pow(1d - progress, 3d);
            apply(from + ((to - from) * eased));
            if (progress >= 1d)
            {
                handle.Dispose();
                completed?.Invoke();
            }
        };

        apply(from);
        timer.Start();
        return handle;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref int pvParam, uint fWinIni);

    private sealed class AnimationHandle(System.Windows.Forms.Timer timer) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            timer.Stop();
            timer.Dispose();
        }
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
