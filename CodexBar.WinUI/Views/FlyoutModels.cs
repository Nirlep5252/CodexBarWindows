using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;

namespace CodexBar.WinUI;

/// <summary>One rate-limit window row: short label, inline meter, percent and reset time.</summary>
/// <remarks>
/// This is deliberately a MUTABLE, observable view-model rather than the immutable record it
/// used to be. The rows were previously rebuilt (<c>Items.Clear()</c> + re-add) on every render,
/// which destroys every container and hands the template a brand-new <c>ProgressBar</c> whose
/// value goes 0 -&gt; N; the stock progress-bar template answers that with its
/// <c>Updating -&gt; Determinate</c> reposition animation, so the meter replayed its slide-in on
/// every one of the four renders a single refresh produces. Retaining the row and raising
/// <see cref="PropertyChanged"/> only when a value ACTUALLY changes is what makes the meter
/// animate exactly once - and, when it does animate, animate the delta from where it already was
/// rather than from zero.
/// </remarks>
public sealed class UsageRowModel : INotifyPropertyChanged
{
    private string title = string.Empty;
    private string percentText = string.Empty;
    private double meterValue;
    private Brush? heatBrush;
    private string resetText = string.Empty;
    private string detailText = string.Empty;
    private bool isIndeterminate;

    internal UsageRowModel(string key) => Key = key;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Stable identity (provider group position + window title) so a re-render finds the row it
    /// already drew instead of creating a replacement.
    /// </summary>
    internal string Key { get; }

    /// <summary>Short window label ("5h", "Week", "Fable", "Auto"…).</summary>
    public string Title
    {
        get => title;
        internal set => Set(ref title, value);
    }

    public string PercentText
    {
        get => percentText;
        internal set => Set(ref percentText, value);
    }

    public double MeterValue
    {
        get => meterValue;
        internal set => Set(ref meterValue, value);
    }

    /// <summary>Shared by the meter fill and the percent figure so they always agree.</summary>
    public Brush? HeatBrush
    {
        get => heatBrush;
        internal set => Set(ref heatBrush, value);
    }

    /// <summary>Reset time, already shortened for the single-line layout.</summary>
    public string ResetText
    {
        get => resetText;
        internal set => Set(ref resetText, value);
    }

    /// <summary>
    /// The long form ("63% used · 37% remaining · resets in 2d 3h"), on the tooltip. The compact
    /// row drops the second line the old layout had, so this is where that detail survives.
    /// </summary>
    public string DetailText
    {
        get => detailText;
        internal set => Set(ref detailText, value);
    }

    /// <summary>True while the first snapshot for this provider is still being fetched.</summary>
    public bool IsIndeterminate
    {
        get => isIndeterminate;
        internal set => Set(ref isIndeterminate, value);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        // The equality check is the whole point: re-applying an identical value must not raise,
        // or the meter re-animates on every render that happens to carry the same numbers.
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
