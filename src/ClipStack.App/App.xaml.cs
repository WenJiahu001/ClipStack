using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClipStack.App.Diagnostics;
using ClipStack.App.Platform;
using ClipStack.App.Services;
using ClipStack.App.ViewModels;
using ClipStack.Core;

namespace ClipStack.App;

// 组合根只负责生命周期和服务连接，业务操作分别由服务维护。
public partial class App : System.Windows.Application
{
    private AppPaths? paths;
    private HistoryStore? store;
    private ClipboardMonitor? monitor;
    private MainViewModel? model;
    private MainWindow? panel;
    private SingleInstance? instance;
    private TrayService? tray;
    private HistoryController? history;
    private ClipboardCaptureWorker? captures;
    private SettingsService? settingsService;
    private Task<string?>? settingsSave;
    private DispatcherTimer? cleanupTimer;
    private bool exiting, shuttingDown;
    private bool handlingFatalError;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandledException;
        bool diagnostic = false;
        try
        {
            StartupOptions options = StartupOptions.Parse(e.Args);
            diagnostic = options.IsDiagnostic;
            if (options.RestoreBackup != null)
            {
                if (options.DataDirectory == null) throw new ArgumentException("恢复备份须指定 --data-dir，且目标目录必须尚不存在。");
                HistoryBackup.Restore(options.RestoreBackup, options.DataDirectory); Shutdown(); return;
            }
            if (options.PasteTarget != null) { await PasteTargetProbe.RunAsync(options.PasteTarget); Shutdown(); return; }
            if (!diagnostic)
            {
                instance = new SingleInstance();
                if (!instance.IsPrimary) { await instance.SignalAsync(); Shutdown(); return; }
            }
            string? root = options.DataDirectory;
            if (diagnostic && root == null) root = Path.Combine(Path.GetTempPath(), "ClipStack.Diagnostics", Guid.NewGuid().ToString("N"));
            paths = new AppPaths(root);
            AppSettings settings = paths.LoadSettings();
            store = await Task.Run(() => new HistoryStore(paths, settings));
            if (diagnostic) { Shutdown(await DiagnosticRunner.RunAsync(options, store, paths)); return; }

            model = new MainViewModel();
            panel = new MainWindow(model, paths);
            panel.ApplySettings(settings);
            MainWindow = panel;
            monitor = new ClipboardMonitor(() => store.Settings, error => paths.LogError("clipboard-read", error));
            history = new HistoryController(store, paths, model, panel, monitor);
            settingsService = new SettingsService(store, paths, monitor);
            panel.SaveNoteAsync = history.SaveNoteAsync;
            panel.UseRequested += paste => _ = history.UseSelectedAsync(paste);
            panel.FavoriteRequested += () => _ = history.ChangeFavoriteAsync();
            panel.DeleteRequested += () => _ = history.DeleteSelectedAsync();
            panel.PauseRequested += TogglePause;
            panel.SettingsRequested += OpenSettings;
            panel.ClearRequested += includeFavorites => _ = ClearAsync(includeFavorites);
            panel.ExitRequested += () => _ = ExitAsync();
            history.NotificationRequested += text => tray?.Notify(text);
            captures = new ClipboardCaptureWorker(store, paths, result =>
            {
                if (!result.Saved) SetStatus(result.Message);
                else if (panel.IsVisible && !exiting) history.RequestRefresh();
                return Task.CompletedTask;
            }, SetStatus);
            monitor.Captured += captures.Enqueue;
            monitor.Notice += SetStatus;
            monitor.HotkeyPressed += () =>
            {
                if (panel.IsActive && !panel.KeepOpen) _ = panel.TryHideAsync(); else ShowPanel();
            };
            bool registered = monitor.Hotkeys.Register(settings.Hotkey);
            if (!registered) registered = monitor.Hotkeys.Register("Ctrl+Alt+Shift+V");
            model.HotkeyLabel = monitor.Hotkeys.ActiveHotkey.Replace("+", " + ");
            tray = new TrayService(() => Dispatch(ShowPanel), () => Dispatch(TogglePause),
                () => Dispatch(() => { ShowPanel(); OpenSettings(); }), () => Dispatch(() => _ = ExitAsync()), settings.ShowTrayIcon);
            _ = instance!.ListenAsync(() => Dispatch(ShowPanel));
            cleanupTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(1) };
            cleanupTimer.Tick += async (_, _) => await history.PruneAsync();
            cleanupTimer.Start();
            string notice = registered ? "正在记录 · 内容仅保存在本机，可在更多菜单暂停" : "快捷键注册失败，请打开设置修改。";
            if (settings.StartWithWindows)
                try { StartupRegistration.SetEnabled(true); }
                catch (Exception error) { paths.LogError("startup-registration", error); notice = "正在记录 · 开机启动路径更新失败，请重新保存设置。"; }
            SetStatus(notice);
            if (!options.Background || !registered) ShowPanel();
        }
        catch (Exception error)
        {
            paths?.LogError("startup", error);
            if (!diagnostic)
            {
                string explanation = error is InvalidDataException ? error.Message : "程序无法启动。请检查用户数据目录是否可写，或查看诊断日志。原始历史将被保留。";
                MessageBox.Show(explanation, "ClipStack 启动失败", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            Shutdown(1);
        }
    }

    private void Dispatch(Action action) => Dispatcher.BeginInvoke(action);
    private void TogglePause()
    {
        if (monitor == null || model == null || exiting) return;
        monitor.Paused = !monitor.Paused; model.Paused = monitor.Paused;
        SetStatus(monitor.Paused ? "已暂停记录 · 点击「恢复记录」继续" : "正在记录 · 内容仅保存在本机");
        tray?.SetPaused(monitor.Paused);
    }
    private async Task ClearAsync(bool includeFavorites)
    {
        if (panel == null || history == null || panel.KeepOpen || exiting) return;
        if (!await panel.CommitPendingNotesAsync() || exiting) return;
        panel.KeepOpen = true;
        try
        {
            if (includeFavorites && MessageBox.Show(panel, "将删除全部历史和收藏，此操作无法撤销。继续吗？", "删除全部记录", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK) return;
            bool wasPaused = monitor?.Paused ?? true;
            if (monitor != null) { monitor.Paused = true; monitor.InvalidatePending(); }
            try
            {
                if (captures != null) await captures.FlushAsync();
                await history.ClearAsync(includeFavorites);
            }
            finally { if (monitor != null) monitor.Paused = wasPaused; }
        }
        finally { panel.KeepOpen = false; }
    }
    private async void OpenSettings()
    {
        if (panel == null || store == null || settingsService == null || panel.KeepOpen || exiting) return;
        if (!await panel.CommitPendingNotesAsync() || exiting) return;
        panel.KeepOpen = true;
        try
        {
            new SettingsWindow(store.Settings, paths!.Root, settings => settingsSave = SaveSettingsAsync(settings))
                { Owner = panel }.ShowDialog();
        }
        finally { panel.KeepOpen = false; }
    }
    private async Task<string?> SaveSettingsAsync(AppSettings settings)
    {
        string? error = await settingsService!.SaveAsync(settings);
        model!.HotkeyLabel = monitor!.Hotkeys.ActiveHotkey.Replace("+", " + ");
        if (error == null)
        {
            panel!.ApplySettings(store!.Settings);
            tray!.SetVisible(store.Settings.ShowTrayIcon);
            monitor!.InvalidatePending();
            await history!.GuardAsync("settings-refresh", () => history.RefreshAsync());
            SetStatus(settingsService.Warning ?? "设置已保存。");
        }
        return error;
    }
    private void ShowPanel()
    {
        if (panel == null || history == null || shuttingDown || exiting) return;
        panel.ShowPanel(); _ = history.GuardAsync("history-query", () => history.RefreshAsync());
    }
    private void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatch(() => SetStatus(text)); return; }
        if (model != null && !shuttingDown) model.StatusMessage = text;
    }
    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        paths?.LogError("ui-unhandled", e.Exception); e.Handled = true;
        if (handlingFatalError) { Shutdown(1); return; }
        handlingFatalError = true;
        _ = RecoverFromFatalErrorAsync();
    }
    private async Task RecoverFromFatalErrorAsync()
    {
        if (monitor != null) monitor.Paused = true;
        if (model != null) model.Paused = true;
        tray?.SetPaused(true);
        MessageBoxResult choice = MessageBox.Show("界面出现未预期异常，记录已暂停。是否保存已采集内容并退出？选择“否”将保留窗口，方便处理尚未保存的备注。",
            "ClipStack", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (choice == MessageBoxResult.Yes) await ExitAsync();
        else SetStatus("已暂停记录，请处理未保存的备注后退出重开。");
        handlingFatalError = false;
    }
    private async Task ExitAsync()
    {
        if (shuttingDown || exiting) return;
        // 先保留或保存编辑草稿，再停止输入源，最后排空保存队列。
        if (panel != null && !await panel.CommitPendingNotesAsync()) { panel.ShowPanel(); return; }
        if (exiting) return;
        exiting = true; cleanupTimer?.Stop();
        try
        {
            if (settingsSave != null) await settingsSave;
            if (monitor != null) await monitor.StopAsync();
            if (captures != null) await captures.CompleteAsync();
            if (history != null) await history.StopAsync();
            monitor?.Dispose(); monitor = null;
            shuttingDown = true;
            if (panel != null) panel.AllowClose = true;
            Shutdown();
        }
        catch (Exception error)
        {
            paths?.LogError("exit", error); exiting = false;
            SetStatus("退出未完成，请检查磁盘后重试。程序已暂停记录。");
            if (monitor != null) monitor.Paused = true;
            if (model != null) model.Paused = true;
            tray?.SetPaused(true); panel?.ShowPanel();
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        shuttingDown = true; cleanupTimer?.Stop(); history?.Dispose(); monitor?.Dispose();
        tray?.Dispose(); store?.Dispose(); instance?.Dispose();
        base.OnExit(e);
    }
}
