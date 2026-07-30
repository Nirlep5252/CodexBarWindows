using System;
using CodexBarWindows;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace CodexBar.WinUI;

/// <summary>
/// A standard (title-barred, resizable) secondary window. Phase 2 uses it as the shell for
/// "Settings" and "Usage graphs"; the real content replaces the placeholder in later phases.
/// Its job right now is to be a genuine sibling window, so the flyout's dismiss logic is
/// exercised against the case it exists for: another window of the SAME process taking focus.
/// </summary>
public sealed partial class ToolWindow : Window
{
    private readonly IntPtr hwnd;

    public ToolWindow(string title, string heading, string body, int widthDip, int heightDip)
    {
        InitializeComponent();

        hwnd = WindowNative.GetWindowHandle(this);
        Title = $"{title} - {AppInfo.AppName}";
        HeadingText.Text = heading;
        BodyText.Text = body;

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "CodexBarWindows.ico"));

        var scale = NativeWindow.ScaleFor(hwnd);
        AppWindow.Resize(new SizeInt32(
            (int)Math.Round(widthDip * scale),
            (int)Math.Round(heightDip * scale)));

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
        }

        RootGrid.ActualThemeChanged += (_, _) => AppTheme.ApplyTint(RootGrid, TintLayer);
        AppTheme.Changed += OnThemeChanged;
        Closed += (_, _) => AppTheme.Changed -= OnThemeChanged;
        Activated += (_, _) => ActivationChanged?.Invoke(this, EventArgs.Empty);

        AppTheme.Apply(this, RootGrid, TintLayer);
    }

    /// <summary>
    /// Raised whenever this window's activation changes, so the flyout can re-test whether the
    /// foreground is still inside this process. See <see cref="FlyoutWindow.ReArmDismissCheck"/>.
    /// </summary>
    public event EventHandler? ActivationChanged;

    public void ShowAndFocus()
    {
        AppWindow.Show(activateWindow: true);
        NativeWindow.ForceForeground(hwnd);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => AppTheme.Apply(this, RootGrid, TintLayer);
}
