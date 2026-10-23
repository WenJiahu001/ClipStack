using System;
using System.Threading.Tasks;
using ClipStack.App.Platform;
using ClipStack.Core;

namespace ClipStack.App.Services;

internal sealed class SettingsService
{
    private readonly HistoryStore store;
    private readonly AppPaths paths;
    private readonly ClipboardMonitor monitor;

    internal SettingsService(HistoryStore store, AppPaths paths, ClipboardMonitor monitor)
    { this.store = store; this.paths = paths; this.monitor = monitor; }

    internal async Task<string?> SaveAsync(AppSettings settings)
    {
        AppSettings previous = store.Settings;
        string previousHotkey = monitor.Hotkeys.ActiveHotkey;
        if (!string.Equals(settings.Hotkey, previousHotkey, StringComparison.OrdinalIgnoreCase) && !monitor.Hotkeys.Register(settings.Hotkey))
            return "快捷键已被占用或系统不允许，请换一个组合。";
        try
        {
            StartupRegistration.SetEnabled(settings.StartWithWindows);
            await Task.Run(() => store.UpdateSettings(settings));
            return null;
        }
        catch (Exception error)
        {
            paths.LogError("settings-save", error);
            if (!string.IsNullOrEmpty(previousHotkey)) monitor.Hotkeys.Register(previousHotkey);
            try { StartupRegistration.SetEnabled(previous.StartWithWindows); }
            catch (Exception rollback) { paths.LogError("settings-rollback", rollback); }
            return "设置未完整保存，请检查目录权限和磁盘空间后重试。";
        }
    }
}
