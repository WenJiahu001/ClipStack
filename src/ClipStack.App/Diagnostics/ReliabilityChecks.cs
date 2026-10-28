using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ClipStack.App.Services;
using ClipStack.App.ViewModels;
using ClipStack.Core;

namespace ClipStack.App.Diagnostics;

internal static class ReliabilityChecks
{
    internal static async Task RunAsync(AppPaths parent, Action<string, bool> check)
    {
        var paths = new AppPaths(Path.Combine(parent.Root, "reliability"));
        using var store = new HistoryStore(paths, new AppSettings());
        string key = "Ctrl+Alt+V";
        bool startup = false;
        var service = new SettingsService(store, paths, () => key, next => { key = next; return true; }, next => startup = next, () => key = "未注册");
        AppSettings changed = store.Settings with { Hotkey = "Ctrl+Alt+Shift+V", StartWithWindows = true, HideAfterCopy = false };
        Directory.CreateDirectory(paths.Settings);
        string? failed = await service.SaveAsync(changed);
        check("设置持久化失败时恢复快捷键、开机启动及内存设置", failed != null && key == "Ctrl+Alt+V" && !startup && store.Settings.HideAfterCopy);
        key = "未注册";
        failed = await service.SaveAsync(changed);
        check("没有旧快捷键时保存失败会撤销新注册", failed != null && key == "未注册" && !startup);
        key = "Ctrl+Alt+V";
        Directory.Delete(paths.Settings);
        Directory.Move(paths.Images, paths.Images + ".held");
        try
        {
            string? error = await service.SaveAsync(changed);
            check("清理失败保留已提交设置并明确返回维护提示", error == null && service.Warning != null && key == changed.Hotkey
                && startup && !store.Settings.HideAfterCopy && !paths.LoadSettings().HideAfterCopy);
        }
        finally { Directory.Move(paths.Images + ".held", paths.Images); }

        for (int i = 0; i < 230; i++) store.Save(CapturedClip.FromText("可靠性分页 " + i));
        var model = new MainViewModel();
        var panel = new MainWindow(model, paths) { KeepOpen = true, PreviewMode = true, AllowClose = true };
        using var history = new HistoryController(store, paths, model, panel);
        try
        {
            panel.Show();
            await history.RefreshAsync();
            check("首次只加载首批100条并标记仍有更多", model.Items.Count == 100 && model.HasMore);
            ClipItem selected = model.Items[10]; model.SelectedItem = selected;
            history.LoadMore(); await WaitAsync(() => !model.IsLoading);
            check("追加历史保留已有对象与选中项", model.Items.Count == 200 && ReferenceEquals(selected, model.SelectedItem));
            history.LoadMore(); await WaitAsync(() => !model.IsLoading);
            check("末批加载完整且停止继续请求", model.Items.Count == 230 && !model.HasMore);
            string originalId = model.SelectedItem!.Id;
            model.QueryText = "可靠性分页 229";
            await history.DeleteSelectedAsync();
            check("防抖期间立即禁用旧结果的删除操作", !model.ResultsCurrent && store.Query().Any(entry => entry.Id == originalId));
            var search = (TextBox)panel.FindName("SearchBox");
            var enter = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(search), Environment.TickCount, Key.Enter)
            { RoutedEvent = Keyboard.PreviewKeyDownEvent };
            int uses = 0; panel.UseRequested += _ => uses++;
            search.RaiseEvent(enter);
            check("防抖期间回车不会使用旧选中内容", uses == 0);
            await history.RefreshAsync();
            check("最新搜索完成后恢复操作且重置分页", model.ResultsCurrent && model.Items.Count == 1 && !model.HasMore);

            var saveGate = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var settings = new SettingsWindow(store.Settings, paths.Root, _ => saveGate.Task) { Owner = panel };
            bool stayedOpen = false;
            settings.Loaded += (_, _) =>
            {
                ((Button)settings.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                settings.Close();
                stayedOpen = settings.IsVisible && !settings.IsEnabled;
                saveGate.SetResult(null);
            };
            settings.ShowDialog();
            check("保存期间不能关闭设置，完成后正常结束对话框", stayedOpen && settings.DialogResult == true);

            model.SelectedItem = new ClipItem(model.SelectedItem!.Entry with { Kind = ClipKind.Image });
            model.PreviewImage = PreviewFixtures.CreateImage();
            var content = new ContentWindow(model) { Owner = panel, Width = 400 };
            content.Show(); content.UpdateLayout();
            var horizontal = FindHorizontalScrollBar(content);
            check("横向滚动条使用横向轨道、尺寸和翻页命令", horizontal != null && double.IsNaN(horizontal.Width)
                && horizontal.Height == 7 && horizontal.Template.FindName("PART_Track", horizontal) is Track track
                && track.Orientation == Orientation.Horizontal && !track.IsDirectionReversed
                && ((RepeatButton)horizontal.Template.FindName("Increase", horizontal)).Command == ScrollBar.PageRightCommand);
            content.Close();

            int saved = 0;
            var worker = new ClipboardCaptureWorker(store, paths, _ => { saved++; return Task.CompletedTask; }, _ => { });
            for (int i = 0; i < 8; i++) worker.Enqueue(CapturedClip.FromText("清理前队列 " + i));
            await worker.FlushAsync();
            store.Clear(true);
            await worker.CompleteAsync();
            check("清理边界等待既有队列落盘，清理后不会重新出现", saved == 8 && store.GetStatistics().Count == 0);
        }
        finally { panel.Close(); await history.StopAsync(); }
    }

    private static ScrollBar? FindHorizontalScrollBar(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollBar { Orientation: Orientation.Horizontal, IsVisible: true } bar) return bar;
            if (FindHorizontalScrollBar(child) is { } found) return found;
        }
        return null;
    }
    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
    }
}
