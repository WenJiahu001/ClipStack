using System;
using System.Threading.Tasks;
using ClipStack.App.Platform;
using ClipStack.Core;

namespace ClipStack.App.Services;

internal sealed class SettingsService
{
    private readonly HistoryStore store;
    private readonly AppPaths paths;
    private readonly Func<string> activeHotkey;
    private readonly Func<string, bool> registerHotkey;
    private readonly Action<bool> setStartup;
    private readonly Action unregisterHotkey;
    internal string? Warning { get; private set; }

    internal SettingsService(HistoryStore store, AppPaths paths, ClipboardMonitor monitor)
        : this(store, paths, () => monitor.Hotkeys.ActiveHotkey, monitor.Hotkeys.Register, StartupRegistration.SetEnabled, monitor.Hotkeys.Unregister) { }
    internal SettingsService(HistoryStore store, AppPaths paths, Func<string> activeHotkey, Func<string, bool> registerHotkey, Action<bool> setStartup, Action unregisterHotkey)
    { this.store = store; this.paths = paths; this.activeHotkey = activeHotkey; this.registerHotkey = registerHotkey; this.setStartup = setStartup; this.unregisterHotkey = unregisterHotkey; }

    internal async Task<string?> SaveAsync(AppSettings settings)
    {
        Warning = null;
        AppSettings previous = store.Settings;
        string previousHotkey = activeHotkey();
        if (!string.Equals(settings.Hotkey, previousHotkey, StringComparison.OrdinalIgnoreCase) && !registerHotkey(settings.Hotkey))
            return "快捷键已被占用或系统不允许，请换一个组合。";
        try
        {
            setStartup(settings.StartWithWindows);
            await Task.Run(() => store.UpdateSettings(settings));
        }
        catch (Exception error)
        {
            paths.LogError("settings-save", error);
            bool restored = true;
            try
            {
                if (HotkeyManager.TryParse(previousHotkey, out _, out _)) restored = registerHotkey(previousHotkey);
                else unregisterHotkey();
            }
            catch (Exception rollback) { restored = false; paths.LogError("hotkey-rollback", rollback); }
            try { setStartup(previous.StartWithWindows); }
            catch (Exception rollback) { restored = false; paths.LogError("settings-rollback", rollback); }
            if (!restored) return "设置保存失败，部分系统设置未能恢复，请重新选择快捷键并保存。";
            return "设置未完整保存，请检查目录权限和磁盘空间后重试。";
        }
        // 配置已经提交，清理失败属于维护失败，不回滚已生效的快捷键和设置。
        try { await Task.Run(store.PruneAndCollect); }
        catch (Exception error)
        {
            paths.LogError("settings-cleanup", error);
            Warning = "设置已保存，但历史清理未完成；请检查磁盘权限后重试。";
        }
        return null;
    }
}
