using Microsoft.Win32;

namespace CodexBarWindows;

public sealed class UsagePopupForm : Form
{
    private readonly Label titleLabel;
    private readonly Label planLabel;
    private readonly Label statusLabel;
    private readonly UsageSection fiveHourSection;
    private readonly UsageSection weeklySection;
    private readonly CloseGlyphButton closeButton;
    private ThemePalette theme = ThemePalette.FromWindows();

    public UsagePopupForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = theme.Surface;
        ClientSize = new Size(420, 294);
        ControlBox = false;
        DoubleBuffered = true;
        Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Codex limits";
        TopMost = true;

        closeButton = new CloseGlyphButton
        {
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
            Location = new Point(374, 14),
            Size = new Size(30, 30),
            TabIndex = 0
        };
        closeButton.Click += (_, _) => Hide();

        titleLabel = new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Display", 15.5f, FontStyle.Bold),
            Location = new Point(22, 18),
            Size = new Size(310, 30),
            Text = "Codex rate limits"
        };

        planLabel = new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular),
            Location = new Point(24, 50),
            Size = new Size(330, 22)
        };

        fiveHourSection = new UsageSection("5 hour limit")
        {
            Location = new Point(18, 78),
            Size = new Size(384, 86)
        };

        weeklySection = new UsageSection("Weekly limit")
        {
            Location = new Point(18, 174),
            Size = new Size(384, 86)
        };

        statusLabel = new Label
        {
            AutoEllipsis = true,
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Text", 8.5f, FontStyle.Regular),
            Location = new Point(24, 264),
            Size = new Size(372, 22)
        };

        Controls.Add(titleLabel);
        Controls.Add(planLabel);
        Controls.Add(fiveHourSection);
        Controls.Add(weeklySection);
        Controls.Add(statusLabel);
        Controls.Add(closeButton);

        EnableDragMove(this);
        EnableDragMove(titleLabel);
        EnableDragMove(planLabel);
        EnableDragMove(statusLabel);
        fiveHourSection.EnableDragMove();
        weeklySection.EnableDragMove();

        Deactivate += (_, _) => Hide();
        FormClosing += OnFormClosing;
        ApplyTheme();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int csDropShadow = 0x00020000;
            var cp = base.CreateParams;
            cp.ClassStyle |= csDropShadow;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.ApplyWindowAttributes(Handle, theme.IsDark);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        using var borderPen = new Pen(theme.Border);
        var borderRect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = RoundedPath(borderRect, 16);
        e.Graphics.DrawPath(borderPen, path);
    }

    public void ShowNear(Point anchor)
    {
        RefreshTheme();
        Location = CalculateLocation(anchor);
        Show();
        Activate();
    }

    protected override void WndProc(ref Message m)
    {
        const int wmSettingChange = 0x001A;
        const int wmThemeChanged = 0x031A;

        base.WndProc(ref m);

        if (m.Msg is wmSettingChange or wmThemeChanged)
        {
            RefreshTheme();
        }
    }

    public void UpdateUsage(UsageLookupResult result)
    {
        if (result.Snapshot is not { } snapshot)
        {
            planLabel.Text = "Waiting for local Codex usage data";
            fiveHourSection.SetUnavailable();
            weeklySection.SetUnavailable();
            statusLabel.Text = result.Error ?? "No usage data found.";
            return;
        }

        planLabel.Text = string.IsNullOrWhiteSpace(snapshot.PlanType)
            ? "Local Codex session data"
            : $"{ToTitleCase(snapshot.PlanType)} plan";

        fiveHourSection.SetUsage(snapshot.FiveHour);
        weeklySection.SetUsage(snapshot.Weekly);
        statusLabel.Text = string.Empty;
    }

    public void SetLoading(UsageLookupResult current)
    {
        if (current.Snapshot is { })
        {
            UpdateUsage(current);
            statusLabel.Text = "Refreshing Codex limits...";
            return;
        }

        planLabel.Text = "Fetching Codex limits...";
        fiveHourSection.SetLoading();
        weeklySection.SetLoading();
        statusLabel.Text = "Reading from Codex CLI...";
    }

    private Point CalculateLocation(Point anchor)
    {
        var screen = Screen.FromPoint(anchor);
        var workingArea = screen.WorkingArea;

        var x = Math.Clamp(anchor.X - Width + 20, workingArea.Left + 8, workingArea.Right - Width - 8);
        var y = anchor.Y >= workingArea.Top + (workingArea.Height / 2)
            ? workingArea.Bottom - Height - 8
            : workingArea.Top + 8;

        return new Point(x, y);
    }

    private static string FormatObservedAt(DateTimeOffset observedAt)
    {
        var local = observedAt.ToLocalTime();
        var age = DateTimeOffset.Now - local;

        if (age.TotalSeconds < 90)
        {
            return "just now";
        }

        if (age.TotalMinutes < 60)
        {
            return $"{Math.Floor(age.TotalMinutes)} min ago";
        }

        return local.ToString("ddd, dd MMM h:mm tt");
    }

    private static string ToTitleCase(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        return char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();
    }

    private void RefreshTheme()
    {
        var updated = ThemePalette.FromWindows();
        if (updated == theme)
        {
            return;
        }

        theme = updated;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        BackColor = theme.Surface;
        titleLabel.BackColor = theme.Surface;
        titleLabel.ForeColor = theme.TextPrimary;
        planLabel.BackColor = theme.Surface;
        planLabel.ForeColor = theme.TextSecondary;
        statusLabel.BackColor = theme.Surface;
        statusLabel.ForeColor = theme.TextSecondary;

        closeButton.ApplyTheme(theme);
        fiveHourSection.ApplyTheme(theme);
        weeklySection.ApplyTheme(theme);

        if (IsHandleCreated)
        {
            NativeMethods.ApplyWindowAttributes(Handle, theme.IsDark);
        }

        Invalidate(true);
    }

    private static Font CreateFont(string family, float size, FontStyle style)
    {
        try
        {
            return new Font(family, size, style);
        }
        catch
        {
            return new Font("Segoe UI", size, style);
        }
    }

    private static System.Drawing.Drawing2D.GraphicsPath RoundedPath(Rectangle rectangle, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        var arc = new Rectangle(rectangle.Location, new Size(diameter, diameter));

        path.AddArc(arc, 180, 90);
        arc.X = rectangle.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rectangle.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rectangle.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();

        return path;
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.ApplicationExitCall || e.CloseReason == CloseReason.TaskManagerClosing)
        {
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private void EnableDragMove(Control control)
    {
        control.MouseDown += (_, e) =>
        {
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WmNclButtonDown, NativeMethods.HtCaption, 0);
        };
    }

    private sealed class UsageSection : Panel
    {
        private readonly Label nameLabel;
        private readonly Label percentLabel;
        private readonly Label remainingLabel;
        private readonly Label resetLabel;
        private readonly UsageMeterControl meter;
        private ThemePalette theme = ThemePalette.FromWindows();

        public UsageSection(string name)
        {
            BackColor = theme.Card;
            DoubleBuffered = true;
            Padding = new Padding(14, 12, 14, 12);

            nameLabel = new Label
            {
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 9.5f, FontStyle.Bold),
                Location = new Point(16, 13),
                Size = new Size(164, 22),
                Text = name
            };

            percentLabel = new Label
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 9.5f, FontStyle.Bold),
                Location = new Point(198, 13),
                Size = new Size(168, 22),
                TextAlign = ContentAlignment.TopRight
            };

            meter = new UsageMeterControl
            {
                Anchor = AnchorStyles.Left | AnchorStyles.Top,
                Location = new Point(16, 43),
                Size = new Size(320, 8)
            };

            remainingLabel = new Label
            {
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 8.75f, FontStyle.Regular),
                Location = new Point(16, 59),
                Size = new Size(160, 20)
            };

            resetLabel = new Label
            {
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
                AutoSize = false,
                Font = CreateFont("Segoe UI Variable Text", 8.75f, FontStyle.Regular),
                Location = new Point(174, 59),
                Size = new Size(192, 20),
                TextAlign = ContentAlignment.TopRight
            };

            Controls.Add(nameLabel);
            Controls.Add(percentLabel);
            Controls.Add(meter);
            Controls.Add(remainingLabel);
            Controls.Add(resetLabel);
            ApplyTheme(theme);
            UpdateChildLayout();
        }

        public void EnableDragMove()
        {
            if (FindForm() is not UsagePopupForm popup)
            {
                HandleCreated += (_, _) =>
                {
                    if (FindForm() is UsagePopupForm createdPopup)
                    {
                        createdPopup.EnableDragMove(this);
                        createdPopup.EnableDragMove(nameLabel);
                        createdPopup.EnableDragMove(percentLabel);
                        createdPopup.EnableDragMove(remainingLabel);
                        createdPopup.EnableDragMove(resetLabel);
                        createdPopup.EnableDragMove(meter);
                    }
                };
                return;
            }

            popup.EnableDragMove(this);
            popup.EnableDragMove(nameLabel);
            popup.EnableDragMove(percentLabel);
            popup.EnableDragMove(remainingLabel);
            popup.EnableDragMove(resetLabel);
            popup.EnableDragMove(meter);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Card;

            foreach (Control control in Controls)
            {
                control.BackColor = theme.Card;
            }

            nameLabel.ForeColor = theme.TextPrimary;
            percentLabel.ForeColor = theme.Accent;
            remainingLabel.ForeColor = theme.TextSecondary;
            resetLabel.ForeColor = theme.TextSecondary;

            meter.TrackColor = theme.MeterTrack;
            meter.AccentColor = theme.Accent;
            meter.WarningColor = theme.Warning;
            meter.DangerColor = theme.Danger;
            meter.BackColor = theme.Card;

            Invalidate(true);
        }

        public void SetUnavailable()
        {
            percentLabel.Text = "-- used";
            meter.Value = 0;
            remainingLabel.Text = "-- remaining";
            resetLabel.Text = "Reset unknown";
        }

        public void SetLoading()
        {
            percentLabel.Text = "Loading...";
            meter.Value = 0;
            remainingLabel.Text = "Fetching usage";
            resetLabel.Text = "Reset loading";
        }

        public void SetUsage(UsageWindow usage)
        {
            percentLabel.Text = $"{usage.UsedPercent:0.#}% used";
            meter.Value = usage.UsedPercent;
            remainingLabel.Text = $"{usage.RemainingPercent:0.#}% remaining";
            resetLabel.Text = $"Resets {FormatReset(usage.ResetsAt)}";
        }

        private static string FormatReset(DateTimeOffset resetAt)
        {
            var now = DateTimeOffset.Now;
            var remaining = resetAt - now;

            if (remaining.TotalSeconds <= 0)
            {
                return "now";
            }

            var relative = remaining.TotalDays >= 1
                ? $"in {(int)remaining.TotalDays}d {remaining.Hours}h"
                : remaining.TotalHours >= 1
                    ? $"in {(int)remaining.TotalHours}h {remaining.Minutes}m"
                    : $"in {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))}m";

            return $"{relative}, {resetAt:h:mm tt}";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
            using var fillBrush = new SolidBrush(theme.Card);
            using var borderPen = new Pen(theme.CardBorder);
            using var path = RoundedPath(bounds, 12);

            e.Graphics.FillPath(fillBrush, path);
            e.Graphics.DrawPath(borderPen, path);

            base.OnPaint(e);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateChildLayout();
        }

        private void UpdateChildLayout()
        {
            const int horizontalPadding = 16;
            const int rightPadding = 34;

            meter.Width = Math.Max(80, Width - horizontalPadding - rightPadding);
            percentLabel.Left = Math.Max(horizontalPadding, Width - 186);
            resetLabel.Left = Math.Max(horizontalPadding, Width - 210);
            percentLabel.Width = Math.Max(120, Width - percentLabel.Left - rightPadding);
            resetLabel.Width = Math.Max(140, Width - resetLabel.Left - rightPadding);
        }
    }

    private sealed record ThemePalette(
        bool IsDark,
        Color Surface,
        Color Card,
        Color Border,
        Color CardBorder,
        Color TextPrimary,
        Color TextSecondary,
        Color Accent,
        Color MeterTrack,
        Color Hover,
        Color Pressed,
        Color Warning,
        Color Danger)
    {
        public static ThemePalette FromWindows()
        {
            var isLight = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1) as int? ?? 1;

            return isLight == 0 ? Dark() : Light();
        }

        private static ThemePalette Dark()
        {
            return new ThemePalette(
                true,
                Color.FromArgb(32, 32, 32),
                Color.FromArgb(43, 43, 43),
                Color.FromArgb(62, 62, 62),
                Color.FromArgb(70, 70, 70),
                Color.FromArgb(245, 245, 245),
                Color.FromArgb(199, 199, 199),
                Color.FromArgb(96, 205, 255),
                Color.FromArgb(62, 69, 74),
                Color.FromArgb(54, 54, 54),
                Color.FromArgb(68, 68, 68),
                Color.FromArgb(255, 185, 0),
                Color.FromArgb(255, 99, 71));
        }

        private static ThemePalette Light()
        {
            return new ThemePalette(
                false,
                Color.FromArgb(243, 243, 243),
                Color.FromArgb(255, 255, 255),
                Color.FromArgb(218, 220, 224),
                Color.FromArgb(226, 226, 226),
                Color.FromArgb(32, 31, 30),
                Color.FromArgb(96, 94, 92),
                Color.FromArgb(0, 95, 184),
                Color.FromArgb(233, 238, 243),
                Color.FromArgb(235, 235, 235),
                Color.FromArgb(225, 225, 225),
                Color.FromArgb(202, 80, 16),
                Color.FromArgb(196, 43, 28));
        }
    }

    private sealed class CloseGlyphButton : Control
    {
        private ThemePalette theme = ThemePalette.FromWindows();
        private bool hovering;
        private bool pressing;

        public CloseGlyphButton()
        {
            Cursor = Cursors.Hand;
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
        }

        public void ApplyTheme(ThemePalette palette)
        {
            theme = palette;
            BackColor = theme.Surface;
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            hovering = true;
            Invalidate();
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hovering = false;
            pressing = false;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            pressing = true;
            Invalidate();
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            pressing = false;
            Invalidate();
            base.OnMouseUp(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            var fill = pressing ? theme.Pressed : hovering ? theme.Hover : theme.Surface;
            using var fillBrush = new SolidBrush(fill);
            using var path = RoundedPath(new Rectangle(0, 0, Width - 1, Height - 1), 7);
            e.Graphics.FillPath(fillBrush, path);

            using var pen = new Pen(theme.TextSecondary, 1.4f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round
            };

            var inset = 10;
            e.Graphics.DrawLine(pen, inset, inset, Width - inset, Height - inset);
            e.Graphics.DrawLine(pen, Width - inset, inset, inset, Height - inset);
        }
    }

    private static class NativeMethods
    {
        public const int WmNclButtonDown = 0x00A1;
        public const int HtCaption = 0x0002;

        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwcpRound = 2;

        public static void ApplyWindowAttributes(IntPtr handle, bool isDark)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                return;
            }

            var preference = DwmwcpRound;
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaWindowCornerPreference,
                ref preference,
                sizeof(int));

            var darkMode = isDark ? 1 : 0;
            _ = DwmSetWindowAttribute(
                handle,
                DwmwaUseImmersiveDarkMode,
                ref darkMode,
                sizeof(int));
        }

        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd,
            int dwAttribute,
            ref int pvAttribute,
            int cbAttribute);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);
    }
}
