using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ClipStack.App.Controls;
using ClipStack.App.Platform;
using ClipStack.App.ViewModels;
using ClipStack.Core;

namespace ClipStack.App;

public partial class MainWindow : Window
{
    private readonly WindowPlacementController placement;
    private readonly HashSet<InlineNoteEditor> noteEditors = new();
    private bool resetListOnActivate = true, hideAfterCopy = true, resetAfterRefresh;
    internal MainViewModel Model { get; }
    internal bool KeepOpen { get; set; }
    internal bool PreviewMode { get; set; }
    internal bool AllowClose { get; set; }
    internal IntPtr PasteTarget { get; private set; }
    internal Func<string, string, Task<string?>>? SaveNoteAsync { get; set; }
    internal Func<Task>? LoadPreviewAsync { get; set; }
    internal event Action<ClipItem>? ThumbnailRequested;
    internal event Action<ClipItem>? ThumbnailReleased;
    internal event Action<bool>? UseRequested;
    internal event Action? FavoriteRequested;
    internal event Action? DeleteRequested;
    internal event Action? PauseRequested;
    internal event Action? SettingsRequested;
    internal event Action<bool>? ClearRequested;
    internal event Action? ExitRequested;

    internal MainWindow(MainViewModel model, AppPaths paths)
    {
        Model = model;
        DataContext = model;
        InitializeComponent();
        ApplyDensity(DisplayDensity.Standard);
        placement = new WindowPlacementController(this, paths, () => !PreviewMode, message => Model.StatusMessage = message);
        Activated += (_, _) =>
        {
            if (!PreviewMode && !KeepOpen && !Model.IsEditingNote && resetListOnActivate) ResetHistoryList();
        };
    }

    internal void ApplyDensity(DisplayDensity density) => PanelLayout.Apply(Resources, density);

    internal void ApplySettings(AppSettings settings)
    {
        ApplyDensity(settings.MainWindowDensity);
        resetListOnActivate = settings.ResetListOnActivate;
        hideAfterCopy = settings.HideAfterCopy;
        placement.Mode = settings.WindowPosition;
        if (!resetListOnActivate) resetAfterRefresh = false;
    }

    internal void OnHistoryRefreshed()
    {
        if (!resetAfterRefresh || Model.IsEditingNote) return;
        resetAfterRefresh = false;
        ResetHistoryList();
    }

    private void ResetHistoryList()
    {
        Model.SelectedItem = Model.Items.FirstOrDefault();
        HistoryList.ApplyTemplate();
        FindScrollViewer(HistoryList)?.ScrollToTop();
    }

    internal void RevealSelectedHistoryItem()
    {
        if (!Model.IsEditingNote && Model.SelectedItem != null) HistoryList.ScrollIntoView(Model.SelectedItem);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }

    internal void ShowPanel()
    {
        IntPtr foreground = NativeMethods.GetForegroundWindow();
        NativeMethods.GetWindowThreadProcessId(foreground, out uint process);
        if (foreground != IntPtr.Zero && process != (uint)Environment.ProcessId) PasteTarget = foreground;
        // 先创建隐藏句柄并定位，再显示，避免原生边框和阴影在默认位置闪现。
        new WindowInteropHelper(this).EnsureHandle();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        placement.Place(PasteTarget);
        Show();
        Activate();
        if (!KeepOpen && !Model.IsEditingNote && resetListOnActivate)
        {
            // 呼出后的查询可能晚于激活事件完成，最新列表到达后还须选中真正的首条。
            resetAfterRefresh = true;
            ResetHistoryList();
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Model.IsEditingNote) return;
            SearchBox.Focus(); SearchBox.SelectAll();
        }));
    }

    private void OnNoteEditorLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not InlineNoteEditor editor) return;
        editor.SaveAsync = note => editor.DataContext is ClipItem item && SaveNoteAsync != null
            ? SaveNoteAsync(item.Id, note) : Task.FromResult<string?>("暂时无法保存备注。");
    }
    private void OnThumbnailLoaded(object sender, RoutedEventArgs e) => UpdateThumbnailRequest((FrameworkElement)sender);
    private void OnThumbnailUnloaded(object sender, RoutedEventArgs e)
    { if (sender is FrameworkElement { DataContext: ClipItem item }) ThumbnailReleased?.Invoke(item); }
    private void OnThumbnailVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateThumbnailRequest((FrameworkElement)sender);
    private void OnThumbnailDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ClipItem old) ThumbnailReleased?.Invoke(old);
        UpdateThumbnailRequest((FrameworkElement)sender);
    }
    private void UpdateThumbnailRequest(FrameworkElement element)
    {
        if (element.DataContext is not ClipItem item) return;
        if (element.IsLoaded && element.IsVisible) ThumbnailRequested?.Invoke(item); else ThumbnailReleased?.Invoke(item);
    }

    private void OnNoteEditingChanged(object? sender, EventArgs e)
    {
        if (sender is not InlineNoteEditor editor) return;
        if (editor.IsEditing) noteEditors.Add(editor); else noteEditors.Remove(editor);
        // 编辑期间保留容器，避免滚动虚拟化回收正在输入的草稿。
        if (ItemsControl.ContainerFromElement(HistoryList, editor) is ListBoxItem container)
            VirtualizingPanel.SetIsContainerVirtualizable(container, !editor.IsEditing);
        Model.IsEditingNote = noteEditors.Count > 0;
    }

    internal async Task<bool> CommitPendingNotesAsync()
    {
        foreach (InlineNoteEditor editor in noteEditors.ToArray())
            if (!await editor.CommitAsync()) { editor.FocusEditor(); return false; }
        return true;
    }
    internal async Task TryHideAsync() { if (await CommitPendingNotesAsync()) Hide(); }
    internal Task OnCopyCompletedAsync() => hideAfterCopy ? TryHideAsync() : Task.CompletedTask;
    private void OnTitleDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !IsButtonSource(e.OriginalSource as DependencyObject))
            try { DragMove(); } catch (InvalidOperationException) { }
    }
    private async void OnHide(object sender, RoutedEventArgs e) => await TryHideAsync();
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!AllowClose) { e.Cancel = true; await TryHideAsync(); }
    }
    private async void OnDeactivated(object? sender, EventArgs e)
    {
        await Task.Delay(120);
        if (!KeepOpen && !PreviewMode && !IsActive && IsVisible) await TryHideAsync();
    }
    private void OnFilterChanged(object sender, RoutedEventArgs e)
    {
        if (Model != null && sender is RadioButton button && Enum.TryParse(button.Tag as string, out HistoryFilter value)) Model.Filter = value;
    }
    private async void OnSettings(object sender, RoutedEventArgs e)
    { if (await CommitPendingNotesAsync()) SettingsRequested?.Invoke(); }
    private void OnPause(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();

    private async Task DispatchItemActionAsync(object sender, Action action)
    {
        string? id = (sender as FrameworkElement)?.DataContext is ClipItem item ? item.Id : Model.SelectedItem?.Id;
        if (!await CommitPendingNotesAsync()) return;
        if (id != null) Model.SelectedItem = Model.Items.FirstOrDefault(item => item.Id == id);
        action();
    }
    private async void OnCopy(object sender, RoutedEventArgs e)
    { e.Handled = true; await DispatchItemActionAsync(sender, () => UseRequested?.Invoke(false)); }
    private async void OnPaste(object sender, RoutedEventArgs e)
    { e.Handled = true; await DispatchItemActionAsync(sender, () => UseRequested?.Invoke(true)); }
    private async void OnFavorite(object sender, RoutedEventArgs e)
    { e.Handled = true; await DispatchItemActionAsync(sender, () => FavoriteRequested?.Invoke()); }
    private async void OnDelete(object sender, RoutedEventArgs e)
    { e.Handled = true; await DispatchItemActionAsync(sender, () => DeleteRequested?.Invoke()); }
    private static bool IsButtonSource(DependencyObject? source)
    {
        for (DependencyObject? current = source; current != null; current = current is Visual
                 ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is ButtonBase or TextBoxBase) return true;
        return false;
    }
    private async void OnPreview(object sender, RoutedEventArgs e)
    {
        if (!await CommitPendingNotesAsync() || !Model.HasSelection || KeepOpen) return;
        string? previewId = Model.SelectedItem?.Id;
        KeepOpen = true;
        try
        {
            if (LoadPreviewAsync != null) await LoadPreviewAsync();
            if (!IsVisible || Model.SelectedItem?.Id != previewId) return;
            new ContentWindow(Model) { Owner = this }.ShowDialog();
        }
        finally { Model.PreviewImage = null; Model.PreviewText = Model.SelectedItem?.Entry.Summary ?? ""; KeepOpen = false; }
    }
    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsButtonSource(e.OriginalSource as DependencyObject)
            && ItemsControl.ContainerFromElement(HistoryList, e.OriginalSource as DependencyObject) is ListBoxItem container)
        { e.Handled = true; await DispatchItemActionAsync(container, () => UseRequested?.Invoke(true)); }
    }
    private async void OnCleanup(object sender, RoutedEventArgs e)
    {
        if (KeepOpen || !await CommitPendingNotesAsync()) return;
        KeepOpen = true;
        var menu = new ContextMenu();
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool actionRequested = false;
        var pause = new MenuItem { Header = Model.PauseLabel };
        pause.Click += (_, _) => PauseRequested?.Invoke();
        menu.Items.Add(pause); menu.Items.Add(new Separator());
        var ordinary = new MenuItem { Header = "清理普通历史（保留收藏）" };
        var all = new MenuItem { Header = "删除全部记录（包括收藏）…" };
        ordinary.Click += async (_, _) => await RequestActionAsync(() => ClearRequested?.Invoke(false));
        all.Click += async (_, _) => await RequestActionAsync(() => ClearRequested?.Invoke(true));
        menu.Items.Add(ordinary); menu.Items.Add(all);
        menu.Items.Add(new Separator());
        var exit = new MenuItem { Header = "退出 ClipStack" };
        exit.Click += async (_, _) => await RequestActionAsync(() => ExitRequested?.Invoke());
        menu.Items.Add(exit);
        menu.Closed += (_, _) => { KeepOpen = false; closed.TrySetResult(); };
        menu.PlacementTarget = (Button)sender; menu.IsOpen = true;

        async Task RequestActionAsync(Action request)
        {
            if (actionRequested) return;
            actionRequested = true;
            // WPF 菜单点击可能早于 Closed；先释放占用，再执行清理或退出。
            menu.IsOpen = false;
            await closed.Task;
            request();
        }
    }
    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 备注框保留文字编辑的全部按键，由控件处理 Enter / Esc。
        if (Model.IsEditingNote && Keyboard.FocusedElement is TextBox edit && edit != SearchBox) return;
        if (e.Key == Key.Escape) { e.Handled = true; await TryHideAsync(); return; }
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None && HistoryList.IsKeyboardFocusWithin)
        { OnPreview(sender, e); e.Handled = true; return; }
        if (e.Key == Key.Enter && Keyboard.FocusedElement is ButtonBase) return;
        if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None && Model.HasSelection)
        { e.Handled = true; await DispatchItemActionAsync(sender, () => UseRequested?.Invoke(true)); return; }
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && Model.HasSelection)
        {
            if (Keyboard.FocusedElement is TextBox box && box.SelectionLength > 0) return;
            e.Handled = true; await DispatchItemActionAsync(sender, () => UseRequested?.Invoke(false)); return;
        }
        if ((e.Key == Key.Down || e.Key == Key.Up) && Keyboard.FocusedElement == SearchBox && Model.Items.Count > 0)
        {
            int index = Math.Clamp(HistoryList.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, Model.Items.Count - 1);
            HistoryList.SelectedIndex = index; HistoryList.ScrollIntoView(HistoryList.SelectedItem); e.Handled = true;
        }
        if (e.Key == Key.Delete && HistoryList.IsKeyboardFocusWithin && Model.HasSelection)
        { e.Handled = true; await DispatchItemActionAsync(sender, () => DeleteRequested?.Invoke()); }
    }
}
