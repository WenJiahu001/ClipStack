using System;
using System.Linq;

namespace ClipStack.Core;

public enum DisplayDensity { Standard, Compact, Comfortable }
public enum PanelPositionMode { Remember, FollowMouse, ScreenCenter, ScreenRight }

public sealed record AppSettings
{
    public int MaxEntries { get; init; } = 1000;
    public int RetentionDays { get; init; } = 30;
    public int ImageBudgetMb { get; init; } = 500;
    public string Hotkey { get; init; } = "Ctrl+Alt+V";
    public bool StartWithWindows { get; init; }
    public DisplayDensity MainWindowDensity { get; init; } = DisplayDensity.Standard;
    public bool ResetListOnActivate { get; init; } = true;
    public bool ShowTrayIcon { get; init; } = true;
    public PanelPositionMode WindowPosition { get; init; } = PanelPositionMode.Remember;
    public string[] ExcludedProcesses { get; init; } = Array.Empty<string>();
    public const int MaxTextBytes = 1024 * 1024;
    public const int MaxImageBytes = 20 * 1024 * 1024;
    public const long MaxImagePixels = 25_000_000;
    public const int MaxNoteLength = FavoriteNote.MaxLength;

    public AppSettings Normalize() => this with
    {
        MaxEntries = Math.Clamp(MaxEntries, 10, 10_000),
        RetentionDays = Math.Clamp(RetentionDays, 1, 365),
        ImageBudgetMb = Math.Clamp(ImageBudgetMb, 20, 5000),
        MainWindowDensity = Enum.IsDefined(MainWindowDensity) ? MainWindowDensity : DisplayDensity.Standard,
        WindowPosition = Enum.IsDefined(WindowPosition) ? WindowPosition : PanelPositionMode.Remember,
        Hotkey = string.IsNullOrWhiteSpace(Hotkey) ? "Ctrl+Alt+V" : Hotkey.Trim(),
        ExcludedProcesses = (ExcludedProcesses ?? Array.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => System.IO.Path.GetFileName(x.Trim()))
            .Select(x => (x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? x[..^4] : x).ToLowerInvariant()).Distinct().ToArray()
    };
}

