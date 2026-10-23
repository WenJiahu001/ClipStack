using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ClipStack.App.Platform;
using ClipStack.App.Services;
using ClipStack.Core;

namespace ClipStack.App.Diagnostics;

internal static class PanelBehaviorChecks
{
    internal static async Task RunAsync(MainWindow panel, HistoryStore store, AppPaths paths, Action<string, bool> check)
    {
        var defaults = new AppSettings();
        var model = panel.Model;
        IntPtr hwnd = new WindowInteropHelper(panel).Handle;
        PanelPlacement remembered = paths.LoadPanelPlacement()!;
        try
        {
            using var tray = new TrayService(() => { }, () => { }, () => { }, () => { }, visible: false);
            foreach (PanelPositionMode mode in Enum.GetValues<PanelPositionMode>())
            {
                bool saved = false;
                bool reset = mode != PanelPositionMode.FollowMouse;
                var settings = new SettingsWindow(defaults, paths.Root, next =>
                {
                    paths.SaveSettings(next); panel.ApplySettings(next);
                    tray.SetVisible(next.ShowTrayIcon);
                    AppSettings loaded = new AppPaths(paths.Root).LoadSettings();
                    saved = loaded.WindowPosition == mode && loaded.ResetListOnActivate == reset
                        && loaded.ShowTrayIcon == reset && tray.IsVisible == reset && !panel.ShowInTaskbar && panel.IsVisible;
                    return Task.FromResult<string?>(null);
                }) { Owner = panel };
                settings.Loaded += (_, _) =>
                {
                    string option = mode switch
                    {
                        PanelPositionMode.FollowMouse => "MousePosition", PanelPositionMode.ScreenCenter => "CenterPosition",
                        PanelPositionMode.ScreenRight => "RightPosition", _ => "RememberPosition"
                    };
                    ((RadioButton)settings.FindName(option)).IsChecked = true;
                    ((CheckBox)settings.FindName("ResetListBox")).IsChecked = reset;
                    ((CheckBox)settings.FindName("TrayIconBox")).IsChecked = reset;
                    ((Button)settings.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                settings.ShowDialog();
                string label = mode switch
                {
                    PanelPositionMode.FollowMouse => "跟随鼠标", PanelPositionMode.ScreenCenter => "屏幕中间",
                    PanelPositionMode.ScreenRight => "屏幕右侧", _ => "记住位置"
                };
                check($"位置、激活及托盘开关保存生效且不显示任务栏：{label}", saved);
                NativeMethods.GetCursorPos(out var cursor);
                panel.ShowPanel(); await IdleAsync(panel);
                NativeMethods.GetWindowRect(hwnd, out var rect);
                IntPtr screen = NativeMethods.MonitorFromWindow(hwnd, 2);
                var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                NativeMethods.GetMonitorInfo(screen, ref info);
                NativeMethods.GetDpiForMonitor(screen, 0, out uint dpi, out _);
                bool positioned = mode switch
                {
                    PanelPositionMode.ScreenCenter => Math.Abs((rect.Left + rect.Right) - (info.Work.Left + info.Work.Right)) <= 2
                        && Math.Abs((rect.Top + rect.Bottom) - (info.Work.Top + info.Work.Bottom)) <= 2,
                    PanelPositionMode.ScreenRight => Math.Abs(info.Work.Right - rect.Right - Math.Round(18 * dpi / 96.0)) <= 2,
                    PanelPositionMode.FollowMouse => screen == NativeMethods.MonitorFromPoint(cursor, 2)
                        && rect.Left >= info.Work.Left && rect.Top >= info.Work.Top && rect.Right <= info.Work.Right && rect.Bottom <= info.Work.Bottom,
                    _ => rect.Left == remembered.Left && rect.Top == remembered.Top
                };
                check($"呼出按所选方式定位：{label}", positioned);
            }
            panel.ApplySettings(defaults with { WindowPosition = PanelPositionMode.ScreenCenter });
            panel.ShowPanel();
            NativeMethods.SendMessage(hwnd, 0x0232, IntPtr.Zero, IntPtr.Zero);
            PanelPlacement? after = paths.LoadPanelPlacement();
            check("其他位置模式不覆盖已记住的坐标", after?.Left == remembered.Left && after.Top == remembered.Top);
            panel.ApplySettings(defaults); panel.ShowPanel();
            NativeMethods.GetWindowRect(hwnd, out var restored);
            check("切回记住位置恢复原坐标", restored.Left == remembered.Left && restored.Top == remembered.Top);

            // 使用真实虚拟化列表和异步查询，确保查询完成后仍选中最新首条。
            using var history = new HistoryController(store, paths, model, panel);
            for (int i = 0; i < 80; i++) store.Save(CapturedClip.FromText($"行为配置测试 {i:D2}"));
            model.QueryText = "行为配置测试"; model.Filter = HistoryFilter.Text;
            await history.RefreshAsync();
            panel.PreviewMode = true; panel.KeepOpen = false;
            var list = (ListBox)panel.FindName("HistoryList");
            await IdleAsync(panel);
            ScrollViewer scroll = FindScrollViewer(list)!;
            model.SelectedItem = model.Items.Last(); list.ScrollIntoView(model.SelectedItem);
            await IdleAsync(panel);
            check("激活检查使用已滚离顶部的真实列表", scroll.VerticalOffset > 0);
            panel.ShowPanel();
            string newest = store.Save(CapturedClip.FromText("行为配置测试 最新条目")).Id!;
            await history.RefreshAsync(); await IdleAsync(panel);
            check("默认激活回到顶部且选中异步加载的首条", model.SelectedItem?.Id == newest && scroll.VerticalOffset == 0);
            check("激活重置保留当前搜索与分类", model.QueryText == "行为配置测试" && model.Filter == HistoryFilter.Text);

            panel.ApplySettings(defaults with { ResetListOnActivate = false });
            model.SelectedItem = model.Items.Last(); string selected = model.SelectedItem.Id;
            list.ScrollIntoView(model.SelectedItem); await IdleAsync(panel);
            double offset = scroll.VerticalOffset;
            panel.ShowPanel(); await history.RefreshAsync(); await IdleAsync(panel);
            check("关闭激活重置后保留选择与滚动位置", model.SelectedItem?.Id == selected && offset > 0 && Math.Abs(scroll.VerticalOffset - offset) < 1);

            panel.ApplySettings(defaults); model.IsEditingNote = true;
            panel.ShowPanel(); await history.RefreshAsync(); await IdleAsync(panel);
            check("备注编辑期间激活不打断当前选择", model.SelectedItem?.Id == selected && model.IsEditingNote && scroll.VerticalOffset > 0);
            model.IsEditingNote = false;
            model.QueryText = "没有匹配的行为配置测试条目";
            panel.ShowPanel(); await history.RefreshAsync(); await IdleAsync(panel);
            check("空列表激活安全且没有选中项", model.Items.Count == 0 && model.SelectedItem == null);
            model.QueryText = ""; model.Filter = HistoryFilter.All; await history.RefreshAsync();
            await history.StopAsync();
        }
        finally
        {
            model.IsEditingNote = false; panel.PreviewMode = false; panel.KeepOpen = true;
            panel.ApplySettings(defaults);
        }
    }

    private static async Task IdleAsync(MainWindow panel)
    {
        await panel.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        panel.UpdateLayout();
        await panel.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
}
