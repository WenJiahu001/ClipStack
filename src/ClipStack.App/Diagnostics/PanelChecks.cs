using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipStack.App.ViewModels;
using ClipStack.Core;

using ClipStack.App.Platform;
using ClipStack.App.Services;
using ClipStack.App.Controls;

namespace ClipStack.App.Diagnostics;

internal static class PanelChecks
{
    // 独立诊断入口：仅使用测试目录，不监听或改写系统剪贴板。
    internal static async Task<bool> RunAsync(string outputPath, HistoryStore store, AppPaths paths)
    {
        var checks = new List<object>();
        bool success = true;
        MainWindow? panel = null;
        try
        {
            var model = new MainViewModel { StatusMessage = "正在记录 · 内容仅保存在本机" };
            panel = new MainWindow(model, paths) { KeepOpen = true };
            var shownBounds = new List<NativeMethods.RECT>();
            panel.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(panel).Handle)?.AddHook(
                (IntPtr handle, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    // 在原生显示消息到达时取位置，防止只检查最终位置漏掉先显示后移动。
                    if (message == 0x0018 && wParam != IntPtr.Zero && NativeMethods.GetWindowRect(handle, out var rect))
                        shownBounds.Add(rect);
                    return IntPtr.Zero;
                });
            panel.ShowPanel();
            await Task.Delay(150);
            IntPtr hwnd = new WindowInteropHelper(panel).Handle;
            var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, 2), ref info);
            NativeMethods.GetWindowRect(hwnd, out var first);
            NativeMethods.GetDpiForMonitor(NativeMethods.MonitorFromWindow(hwnd, 2), 0, out uint dpi, out _);
            Check("首次呼出位于屏幕右侧", Math.Abs(info.Work.Right - first.Right - Math.Round(18 * dpi / 96.0)) <= 2);
            Check("首次原生显示前已完成定位", shownBounds.Count > 0 && shownBounds.All(rect =>
                rect.Left == first.Left && rect.Top == first.Top && rect.Right == first.Right && rect.Bottom == first.Bottom));

            int movedLeft = Math.Max(info.Work.Left + 30, first.Left - 160), movedTop = Math.Max(info.Work.Top + 30, first.Top - 35);
            NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, movedLeft, movedTop, first.Right - first.Left, first.Bottom - first.Top, 0x0014);
            // 模拟系统在用户完成拖动时发送的消息，走实际保存回调。
            NativeMethods.SendMessage(hwnd, 0x0232, IntPtr.Zero, IntPtr.Zero);
            PanelPlacement? saved = paths.LoadPanelPlacement();
            Check("拖动结束后保存窗口位置", saved?.Left == movedLeft && saved.Top == movedTop);
            panel.Hide(); shownBounds.Clear(); panel.ShowPanel();
            await Task.Delay(80);
            NativeMethods.GetWindowRect(hwnd, out var reopened);
            Check("再次呼出保持用户位置", reopened.Left == movedLeft && reopened.Top == movedTop);
            Check("再次原生显示前已恢复位置", shownBounds.Count > 0 && shownBounds.All(rect => rect.Left == movedLeft && rect.Top == movedTop));

            panel.AllowClose = true; panel.Close();
            panel = new MainWindow(model, new AppPaths(paths.Root)) { KeepOpen = true };
            panel.ShowPanel();
            await Task.Delay(100);
            NativeMethods.GetWindowRect(new WindowInteropHelper(panel).Handle, out reopened);
            Check("重建窗口后仍恢复用户位置", reopened.Left == movedLeft && reopened.Top == movedTop);

            string favoriteId = store.Save(CapturedClip.FromText("你好，文件已收到，我会在今天下班前回复。谢谢！")).Id!;
            string ordinaryId = store.Save(CapturedClip.FromText("https://learn.microsoft.com/dotnet/desktop/wpf/")).Id!;
            store.SetFavorite(favoriteId, true); store.SetNote(favoriteId, "工作常用回复");
            ClipItem favorite = new(store.Query().Single(x => x.Id == favoriteId));
            ClipItem ordinary = new(store.Query().Single(x => x.Id == ordinaryId));
            model.Items = new[] { favorite, ordinary }; model.SelectedItem = ordinary;
            panel.UpdateLayout();
            var list = (ListBox)panel.FindName("HistoryList");
            var card = (ListBoxItem)list.ItemContainerGenerator.ContainerFromItem(favorite);
            var searchBox = (TextBox)panel.FindName("SearchBox");
            var filters = ((StackPanel)panel.FindName("FilterOptions")).Children.OfType<RadioButton>().ToArray();
            bool rightSwitches = true;
            foreach (HistoryFilter expected in new[] { HistoryFilter.Text, HistoryFilter.Image, HistoryFilter.Favorites, HistoryFilter.All })
            {
                KeyEventArgs key = SendKey(searchBox, Key.Right);
                rightSwitches &= key.Handled && model.Filter == expected
                    && filters.Single(option => option.IsChecked == true).Tag as string == expected.ToString();
            }
            Check("呼出后空搜索框右方向键循环切换四个分类并更新高亮", rightSwitches);
            SendKey(searchBox, Key.Left);
            Check("左方向键从全部循环到收藏", model.Filter == HistoryFilter.Favorites && filters[3].IsChecked == true);
            SendKey(list, Key.Right);
            Check("列表焦点下左右键也可切换分类", model.Filter == HistoryFilter.All && filters[0].IsChecked == true);
            SendKey(filters[0], Key.Right);
            Check("分类焦点下方向键不发生重复切换", model.Filter == HistoryFilter.Text && filters[1].IsChecked == true);
            filters[0].IsChecked = true;
            searchBox.Text = "搜索文字";
            KeyEventArgs textArrow = SendKey(searchBox, Key.Right);
            Check("搜索有文本时方向键保留编辑行为", !textArrow.Handled && model.Filter == HistoryFilter.All && searchBox.Text == "搜索文字");
            searchBox.Clear();
            double standardHeight = card.ActualHeight;
            var bounds = new Rect(panel.Left, panel.Top, panel.ActualWidth, panel.ActualHeight);
            panel.ApplyDensity(DisplayDensity.Compact); panel.UpdateLayout();
            double compactHeight = card.ActualHeight;
            Check("紧凑模式立即减小实际卡片高度", compactHeight < standardHeight);
            panel.ApplyDensity(DisplayDensity.Comfortable); panel.UpdateLayout();
            Check("宽松模式立即增大实际卡片高度", card.ActualHeight > standardHeight);
            Check("切换密度保持列表、选择和窗口位置尺寸", ReferenceEquals(card, list.ItemContainerGenerator.ContainerFromItem(favorite))
                && model.SelectedItem == ordinary && bounds == new Rect(panel.Left, panel.Top, panel.ActualWidth, panel.ActualHeight));
            foreach (DisplayDensity density in Enum.GetValues<DisplayDensity>())
            {
                bool savedDensity = false;
                var settings = new SettingsWindow(new AppSettings(), paths.Root, next =>
                {
                    paths.SaveSettings(next);
                    panel.ApplyDensity(next.MainWindowDensity);
                    savedDensity = next.MainWindowDensity == density && paths.LoadSettings().MainWindowDensity == density;
                    return Task.FromResult<string?>(null);
                }) { Owner = panel };
                settings.Loaded += (_, _) =>
                {
                    string name = density switch { DisplayDensity.Compact => "CompactDensity", DisplayDensity.Comfortable => "ComfortableDensity", _ => "StandardDensity" };
                    ((RadioButton)settings.FindName(name)).IsChecked = true;
                    ((Button)settings.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                };
                settings.ShowDialog(); panel.UpdateLayout();
                Check($"设置页保存密度并持久化：{density}", savedDensity);
            }
            panel.ApplyDensity(DisplayDensity.Standard); panel.UpdateLayout();
            bool canceledSave = false;
            var canceledSettings = new SettingsWindow(new AppSettings(), paths.Root, _ =>
            {
                canceledSave = true; return Task.FromResult<string?>(null);
            }) { Owner = panel };
            canceledSettings.Loaded += (_, _) =>
            {
                ((RadioButton)canceledSettings.FindName("CompactDensity")).IsChecked = true;
                canceledSettings.Close();
            };
            canceledSettings.ShowDialog(); panel.UpdateLayout();
            Check("取消密度选择不保存且不改变布局", !canceledSave && card.ActualHeight == standardHeight);
            double width = panel.Width;
            panel.Width = panel.MinWidth;
            foreach (DisplayDensity density in Enum.GetValues<DisplayDensity>())
            {
                panel.ApplyDensity(density); panel.UpdateLayout();
                Check($"最小窗口宽度下卡片按钮可见：{density}", Descendants<Button>(card).Where(button => button.IsVisible).All(button =>
                {
                    Rect buttonBounds = button.TransformToAncestor(card).TransformBounds(new Rect(button.RenderSize));
                    bool isNote = AutomationProperties.GetName(button) == "编辑备注";
                    return (isNote || (button.ActualWidth >= 24 && button.ActualHeight >= 24))
                        && buttonBounds.Left >= 0 && buttonBounds.Right <= card.ActualWidth;
                }));
            }
            panel.Width = width; panel.ApplyDensity(DisplayDensity.Standard); panel.UpdateLayout();
            var noteButton = Descendants<Button>(panel).Single(button => button.DataContext == favorite && AutomationProperties.GetName(button) == "编辑备注");
            Check("备注与原内容同时显示", Descendants<TextBlock>(panel).Any(block => block.IsVisible && block.Text == "工作常用回复")
                && Descendants<TextBlock>(panel).Any(block => block.IsVisible && block.Text == favorite.Entry.Summary));
            var uses = new List<bool>(); panel.UseRequested += paste => uses.Add(paste);
            int deletes = 0; panel.DeleteRequested += () => deletes++;
            var copyButton = Descendants<Button>(panel).Single(button => button.DataContext == ordinary && AutomationProperties.GetName(button) == "复制这条记录");
            copyButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check("复制按钮使用所属记录", model.SelectedItem?.Id == ordinaryId && uses.SequenceEqual(new[] { false }));
            copyButton.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
            Check("双击项内按钮不触发粘贴", uses.SequenceEqual(new[] { false }));

            var inline = Descendants<InlineNoteEditor>(panel).Single(control => control.DataContext == favorite);
            Check("备注位于卡片左下角且显示按钮左对齐", inline.HorizontalAlignment == HorizontalAlignment.Left
                && ((Button)inline.FindName("DisplayButton")).HorizontalAlignment == HorizontalAlignment.Left
                && inline.TransformToAncestor(card).Transform(new Point()).X < card.ActualWidth / 2);
            var textBox = (TextBox)inline.FindName("Editor");
            int saves = 0;
            panel.SaveNoteAsync = async (id, note) =>
            {
                await Task.Delay(15);
                store.SetNote(id, note); favorite.UpdateNote(note); saves++;
                return null;
            };
            int windows = Application.Current.Windows.Count;
            noteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(80);
            Check("原位编辑且没有新弹框", inline.IsEditing && textBox.IsVisible && Application.Current.Windows.Count == windows && model.IsEditingNote);
            textBox.Text = "工作常用回复 · 邮件确认";
            KeyEventArgs noteArrow = SendKey(textBox, Key.Left);
            Check("编辑备注时方向键不切换分类或提交草稿", !noteArrow.Handled && model.Filter == HistoryFilter.All && inline.IsEditing && saves == 0);
            SendKey(textBox, Key.Enter);
            await Task.Delay(80);
            Check("Enter 保存备注且不会粘贴", !inline.IsEditing && saves == 1 && store.Query("邮件确认").Single().Id == favoriteId && uses.Count == 1);
            noteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            textBox.Text = "不保存的草稿";
            SendKey(textBox, Key.Escape);
            await Task.Delay(30);
            Check("Esc 取消草稿且不隐藏主界面", !inline.IsEditing && panel.IsVisible && saves == 1 && !store.Query("草稿").Any());
            noteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            textBox.Text = new string('字', 51);
            Check("超限拒绝保存并保留草稿", !await inline.CommitAsync() && inline.IsEditing && textBox.Text.Length == 51 && saves == 1);
            SendKey(textBox, Key.Delete);
            Check("输入框 Delete 不会删除历史", deletes == 0);
            textBox.Text = "移开焦点自动保存";
            ((TextBox)panel.FindName("SearchBox")).Focus();
            await WaitUntilAsync(() => !inline.IsEditing);
            Check("移开焦点保存备注", !inline.IsEditing && saves == 2 && store.Query("自动保存").Single().Id == favoriteId);
            noteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            textBox.Text = "合并重复提交";
            Task<bool> firstSave = inline.CommitAsync(), secondSave = inline.CommitAsync();
            Check("同时提交只保存一次", ReferenceEquals(firstSave, secondSave) && await firstSave && await secondSave && saves == 3);
            noteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(50);
            panel.SaveNoteAsync = (_, _) => Task.FromResult<string?>("模拟磁盘失败");
            textBox.Text = "保存失败的草稿";
            Check("保存失败保留输入与编辑状态", !await inline.CommitAsync() && inline.IsEditing && textBox.Text == "保存失败的草稿");
            SendKey(textBox, Key.Escape);

            using (var history = new HistoryController(store, paths, model, panel))
            {
                panel.SaveNoteAsync = history.SaveNoteAsync;
                noteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Task.Delay(50);
                textBox.Text = "编辑期间有新复制";
                store.Save(CapturedClip.FromText("刷新保护测试"));
                await history.RefreshAsync();
                Check("编辑时刷新保留原容器与草稿", model.Items.Count == 2 && inline.IsEditing && textBox.Text == "编辑期间有新复制");
                await inline.CommitAsync();
                await WaitUntilAsync(() => model.Items.Count == 3 && !model.IsEditingNote);
                Check("保存后恢复待处理刷新", model.Items.Count == 3 && !model.IsEditingNote && store.Query("新复制").Single().Id == favoriteId);
            }
            favorite = model.Items.Single(item => item.Id == favoriteId);
            list.ScrollIntoView(favorite);
            await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            panel.UpdateLayout();
            var finalEditor = Descendants<InlineNoteEditor>(panel).Single(control => control.DataContext == favorite);
            ((Button)finalEditor.FindName("DisplayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Task.Delay(60);
            ((TextBox)finalEditor.FindName("Editor")).Text = "简单备注，最多 50 字";
            string directory = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
            await CaptureFloatingSceneAsync(panel, Path.Combine(directory, "inline-note.png"));
            SendKey((TextBox)finalEditor.FindName("Editor"), Key.Escape);
            panel.UpdateLayout();
            await CaptureFloatingSceneAsync(panel, Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outputPath))!, "floating-panel.png"));

            await PanelBehaviorChecks.RunAsync(panel, store, paths, Check);

            using (var history = new HistoryController(store, paths, model, panel))
            {
                panel.SaveNoteAsync = history.SaveNoteAsync;
                await history.RefreshAsync();
                list.ScrollIntoView(model.Items.Single(item => item.Id == ordinaryId));
                panel.UpdateLayout();
                Task? deleteTask = null;
                panel.DeleteRequested += () => deleteTask = history.DeleteSelectedAsync();
                var deleteButton = Descendants<Button>(panel).Single(button => button.DataContext is ClipItem item && item.Id == ordinaryId && AutomationProperties.GetName(button) == "删除这条记录");
                model.SelectedItem = favorite;
                deleteButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (deleteTask != null) await deleteTask;
                Check("单条删除使用所属记录并刷新列表", deleteTask != null && !store.Query().Any(entry => entry.Id == ordinaryId)
                    && model.Items.All(item => item.Id != ordinaryId) && store.Query().Any(entry => entry.Id == favoriteId));

                TaskCompletionSource? clearCompleted = null;
                bool clearBlocked = false;
                int clearRequests = 0;
                panel.ClearRequested += async includeFavorites =>
                {
                    clearRequests++;
                    // 对应应用入口的防重入条件：菜单占用尚未释放时，清理会被拒绝。
                    if (panel.KeepOpen) clearBlocked = true;
                    else await history.ClearAsync(includeFavorites);
                    clearCompleted?.TrySetResult();
                };
                panel.KeepOpen = false;
                var moreButton = Descendants<Button>(panel).Single(button => AutomationProperties.GetName(button) == "更多操作");
                int beforeCancel = store.GetStatistics().Count;
                moreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                OpenMenu(moreButton).IsOpen = false;
                await WaitUntilAsync(() => !panel.KeepOpen);
                Check("取消清理菜单不删除记录且释放占用", clearRequests == 0 && !panel.KeepOpen && store.GetStatistics().Count == beforeCancel);
                foreach (bool includeFavorites in new[] { false, true })
                {
                    store.Save(CapturedClip.FromText(includeFavorites ? "全部清理测试" : "普通清理测试"));
                    await history.RefreshAsync(); panel.UpdateLayout();
                    panel.KeepOpen = false;
                    clearBlocked = false;
                    clearCompleted = new TaskCompletionSource();
                    var more = Descendants<Button>(panel).Single(button => AutomationProperties.GetName(button) == "更多操作");
                    more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    ContextMenu menu = OpenMenu(more);
                    var menuItem = (MenuItem)menu.Items[includeFavorites ? 3 : 2];
                    ((IInvokeProvider)new MenuItemAutomationPeer(menuItem).GetPattern(PatternInterface.Invoke)).Invoke();
                    bool completed = await Task.WhenAny(clearCompleted.Task, Task.Delay(2000)) == clearCompleted.Task;
                    Check(includeFavorites ? "菜单全部清理先释放占用再删除收藏与普通记录" : "菜单普通清理先释放占用再删除并保留收藏",
                        completed && !clearBlocked && (includeFavorites ? store.GetStatistics().Count == 0
                            : store.Query().Count > 0 && store.Query().All(entry => entry.Favorite))
                        && model.Items.Count == store.GetStatistics().Count);
                    menu.IsOpen = false;
                }
                var exitRequested = new TaskCompletionSource<bool>();
                panel.ExitRequested += () => exitRequested.TrySetResult(!panel.KeepOpen);
                moreButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                ContextMenu exitMenu = OpenMenu(moreButton);
                var exitItem = exitMenu.Items.OfType<MenuItem>().Single(item => (string)item.Header == "退出 ClipStack");
                ((IInvokeProvider)new MenuItemAutomationPeer(exitItem).GetPattern(PatternInterface.Invoke)).Invoke();
                Check("主面板退出入口等待菜单关闭后发送请求", await Task.WhenAny(exitRequested.Task, Task.Delay(2000)) == exitRequested.Task
                    && await exitRequested.Task);
            }
        }
        catch (Exception error)
        {
            success = false; paths.LogError("panel-check", error);
            checks.Add(new { name = "窗口检查执行", passed = false, error = error.ToString() });
        }
        finally
        {
            if (panel != null) { panel.AllowClose = true; panel.Close(); }
            string report = Path.GetFullPath(outputPath); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { success, checks, clipboardTouched = false, hotkeysRegistered = false }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return success;

        void Check(string name, bool passed) { success &= passed; checks.Add(new { name, passed }); }
    }

    private static ContextMenu OpenMenu(Button target) => PresentationSource.CurrentSources.OfType<HwndSource>()
        .Where(source => source.RootVisual != null).SelectMany(source => Descendants<ContextMenu>(source.RootVisual))
        .Single(menu => menu.IsOpen && menu.PlacementTarget == target);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && timeout.ElapsedMilliseconds < 2000) await Task.Delay(20);
    }

    private static KeyEventArgs SendKey(UIElement target, Key key)
    {
        target.Focus();
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key)
        { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        target.RaiseEvent(args);
        return args;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task CaptureFloatingSceneAsync(MainWindow panel, string path)
    {
        await panel.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        panel.UpdateLayout();
        // 仅渲染本应用的视觉树，避免截图混入其他应用或桌面内容。
        int width = (int)Math.Ceiling(panel.ActualWidth), height = (int)Math.Ceiling(panel.ActualHeight);
        var content = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        content.Render(panel);
        var scene = new Grid
        {
            Width = width + 80, Height = height + 80,
            Background = new SolidColorBrush(Color.FromRgb(232, 237, 244))
        };
        scene.Children.Add(new Border
        {
            Margin = new Thickness(40), Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromRgb(174, 185, 202)), BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.23 },
            Child = new Image { Source = content, Stretch = Stretch.None }
        });
        scene.Measure(new Size(scene.Width, scene.Height));
        scene.Arrange(new Rect(0, 0, scene.Width, scene.Height));
        scene.UpdateLayout();
        var result = new System.Windows.Media.Imaging.RenderTargetBitmap((int)scene.Width, (int)scene.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(scene);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(result));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); encoder.Save(file);
    }
}
