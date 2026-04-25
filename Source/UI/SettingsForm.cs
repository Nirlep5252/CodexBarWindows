using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CodexBarWindows;

public sealed class SettingsForm : Form
{
    private readonly Label headingLabel;
    private readonly Label versionLabel;
    private readonly CheckBox startWithWindowsCheckBox;
    private readonly Label startupDescriptionLabel;
    private readonly Button closeButton;
    private readonly bool isDark = IsDarkTheme();

    public SettingsForm()
    {
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = isDark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
        ClientSize = new Size(440, 220);
        Font = CreateFont("Segoe UI Variable Text", 9f, FontStyle.Regular);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        Icon = TrayIconFactory.Create();
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        StartPosition = FormStartPosition.CenterScreen;
        Text = "CodexBarWindows Settings";

        headingLabel = new Label
        {
            AutoSize = false,
            Font = CreateFont("Segoe UI Variable Display", 15f, FontStyle.Bold),
            Location = new Point(22, 20),
            Size = new Size(360, 32),
            Text = "Settings"
        };

        versionLabel = new Label
        {
            AutoSize = false,
            Location = new Point(24, 54),
            Size = new Size(360, 22),
            Text = $"Version {AppInfo.VersionText}"
        };

        startWithWindowsCheckBox = new CheckBox
        {
            AutoSize = false,
            Checked = StartupSettings.IsEnabled,
            Location = new Point(24, 102),
            Size = new Size(250, 28),
            Text = "Start with Windows",
            UseVisualStyleBackColor = true
        };
        startWithWindowsCheckBox.CheckedChanged += (_, _) => StartupSettings.SetEnabled(startWithWindowsCheckBox.Checked);

        startupDescriptionLabel = new Label
        {
            AutoSize = false,
            Location = new Point(46, 132),
            Size = new Size(360, 36),
            Text = "Launch CodexBarWindows automatically when you sign in."
        };

        closeButton = new Button
        {
            Anchor = AnchorStyles.Bottom | AnchorStyles.Right,
            DialogResult = DialogResult.OK,
            Location = new Point(332, 176),
            Size = new Size(84, 30),
            Text = "Close",
            UseVisualStyleBackColor = true
        };

        Controls.Add(headingLabel);
        Controls.Add(versionLabel);
        Controls.Add(startWithWindowsCheckBox);
        Controls.Add(startupDescriptionLabel);
        Controls.Add(closeButton);

        AcceptButton = closeButton;
        ApplyTheme();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowAttributes(Handle, isDark);
    }

    private void ApplyTheme()
    {
        var textPrimary = isDark ? Color.FromArgb(245, 245, 245) : SystemColors.ControlText;
        var textSecondary = isDark ? Color.FromArgb(199, 199, 199) : SystemColors.GrayText;

        foreach (Control control in Controls)
        {
            control.BackColor = BackColor;
            control.ForeColor = textPrimary;
        }

        versionLabel.ForeColor = textSecondary;
        startupDescriptionLabel.ForeColor = textSecondary;
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

    private static bool IsDarkTheme()
    {
        var value = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme",
            1);

        return value is int intValue && intValue == 0;
    }

    private static void ApplyWindowAttributes(IntPtr handle, bool isDarkMode)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        var preference = 2;
        _ = DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int));

        var darkMode = isDarkMode ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, 20, ref darkMode, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);
}
