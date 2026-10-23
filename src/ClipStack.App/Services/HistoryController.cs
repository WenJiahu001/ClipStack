using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;
using ClipStack.App.Platform;
using ClipStack.App.ViewModels;
using ClipStack.Core;

namespace ClipStack.App.Services;

// 统一协调历史列表、预览及操作；视图不直接访问数据库或剪贴板。
internal sealed class HistoryController : IDisposable
{
    private readonly HistoryStore store;
    private readonly AppPaths paths;
    private readonly MainViewModel model;
    private readonly MainWindow panel;
    private readonly ClipboardMonitor? monitor;
    private readonly ThumbnailLoader thumbnails;
    private CancellationTokenSource? refreshCancellation;
    private int refreshGeneration, selectionGeneration;
    private bool disposed, usingClip, exiting, refreshPending;
    private bool captureRefreshScheduled;
    private readonly HashSet<Task> operations = new();
    internal event Action<string>? NotificationRequested;

    internal HistoryController(HistoryStore store, AppPaths paths, MainViewModel model, MainWindow panel, ClipboardMonitor? monitor = null)
    {
        this.store = store; this.paths = paths; this.model = model; this.panel = panel; this.monitor = monitor;
        thumbnails = new ThumbnailLoader(store, paths);
        panel.ThumbnailRequested += thumbnails.Load;
        panel.ThumbnailReleased += thumbnails.Release;
        panel.LoadPreviewAsync = LoadSelectionAsync;
        model.SearchRequested += DebounceRefresh;
        model.SelectionChanged += OnSelectionChanged;
        model.EditingChanged += OnEditingChanged;
    }

    private void OnSelectionChanged()
    {
        selectionGeneration++;
        ClipItem? item = model.SelectedItem;
        model.PreviewImage = null;
        model.PreviewKind = item?.KindLabel ?? "";
        model.PreviewDetail = item?.Detail ?? "";
        model.PreviewText = item?.Entry.Summary ?? "选择一条记录查看内容。";
    }
    private void OnEditingChanged() { if (!model.IsEditingNote && refreshPending) { refreshPending = false; DebounceRefresh(); } }
    private void DebounceRefresh()
    {
        refreshCancellation?.Cancel();
        int ticket = ++refreshGeneration;
        _ = RunAsync();
        async Task RunAsync()
        {
            await Task.Delay(160);
            if (ticket == refreshGeneration && !disposed) await GuardAsync("history-query", () => RefreshAsync());
        }
    }
    internal void RequestRefresh()
    {
        if (disposed || exiting || captureRefreshScheduled) return;
        captureRefreshScheduled = true;
        _ = RunAsync();
        async Task RunAsync()
        {
            await Task.Delay(120);
            // 连续采集也会定期刷新；不反复取消正在进行的首次解密查询。
            while (!disposed && !exiting && refreshCancellation != null) await Task.Delay(40);
            captureRefreshScheduled = false;
            if (!disposed && !exiting && panel.IsVisible) await GuardAsync("capture-refresh", () => RefreshAsync());
        }
    }

    internal Task RefreshAsync(string? preferredId = null) => TrackAsync(() => RefreshCoreAsync(preferredId));
    private async Task RefreshCoreAsync(string? preferredId)
    {
        if (disposed || exiting) return;
        if (model.IsEditingNote) { refreshPending = true; return; }
        int ticket = ++refreshGeneration;
        refreshCancellation?.Cancel();
        using var cancellation = new CancellationTokenSource();
        refreshCancellation = cancellation;
        string query = model.QueryText;
        HistoryFilter filter = model.Filter;
        string? selectedId = preferredId ?? model.SelectedItem?.Id;
        model.IsLoading = true;
        IReadOnlyList<ClipEntry> entries;
        try { entries = await Task.Run(() => store.Query(query, filter, cancellation.Token), cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { return; }
        finally
        {
            if (ReferenceEquals(refreshCancellation, cancellation)) { refreshCancellation = null; model.IsLoading = false; }
        }
        if (ticket != refreshGeneration || disposed || exiting) return;
        if (model.IsEditingNote) { refreshPending = true; return; }
        var existing = model.Items.ToDictionary(item => item.Id);
        ClipItem[] items = entries.Select(entry =>
        {
            if (!existing.TryGetValue(entry.Id, out var item)) return new ClipItem(entry);
            item.UpdateEntry(entry); return item;
        }).ToArray();
        // 无增删或排序变化时保留 ItemsSource，避免重建卡片、滚动位置和选择。
        if (!model.Items.SequenceEqual(items)) model.Items = items;
        model.SelectedItem = items.FirstOrDefault(x => x.Id == selectedId) ?? items.FirstOrDefault();
        panel.OnHistoryRefreshed();
    }

    internal Task LoadSelectionAsync() => TrackAsync(LoadSelectionCoreAsync);
    private async Task LoadSelectionCoreAsync()
    {
        if (disposed || exiting) return;
        int ticket = ++selectionGeneration;
        ClipItem? item = model.SelectedItem;
        model.PreviewImage = null;
        model.PreviewKind = item?.KindLabel ?? "";
        model.PreviewDetail = item?.Detail ?? "";
        model.PreviewText = item == null ? "选择一条记录查看内容。" : item.Entry.Kind == ClipKind.Text ? item.Entry.Summary : "";
        if (item == null) return;
        try
        {
            byte[] bytes = await Task.Run(() => store.ReadContent(item.Entry));
            if (ticket != selectionGeneration || disposed || exiting) return;
            if (item.Entry.Kind == ClipKind.Text)
            {
                string text = Encoding.UTF8.GetString(bytes);
                model.PreviewText = text.Length <= 100_000 ? text : text[..100_000] + "\n\n[预览已截断，复制时会使用完整内容]";
            }
            else
            {
                ImageSource image = await Task.Run(() => BitmapCodec.Decode(bytes));
                if (ticket == selectionGeneration && !disposed && !exiting) model.PreviewImage = image;
            }
        }
        catch (Exception error)
        {
            paths.LogError("preview-read", error);
            if (ticket == selectionGeneration && !disposed && !exiting) { model.PreviewText = "内容无法读取，原始记录已保留。"; SetStatus("内容读取失败，可删除损坏记录或恢复备份。"); }
        }
    }

    internal Task UseSelectedAsync(bool paste) => TrackAsync(() => UseSelectedCoreAsync(paste));
    private async Task UseSelectedCoreAsync(bool paste)
    {
        if (usingClip || model.SelectedItem == null || monitor == null || disposed || exiting) return;
        usingClip = true;
        ClipItem item = model.SelectedItem;
        try
        {
            byte[] content = await Task.Run(() => store.ReadContent(item.Entry));
            if (disposed || exiting) return;
            await PasteService.WriteAsync(item.Entry, content, monitor);
            if (disposed || exiting) return;
            await Task.Run(() => store.MarkUsed(item.Id));
            if (disposed || exiting) return;
            if (paste)
            {
                IntPtr target = panel.PasteTarget;
                panel.Hide();
                bool sent = await PasteService.TryPasteAsync(target);
                if (!sent) Notify("内容已复制。请返回目标应用，按 Ctrl+V 手动粘贴。");
                else SetStatus("已发送粘贴操作；目标应用若未接收，可按 Ctrl+V 再试。");
            }
            else
            {
                SetStatus("已复制到系统剪贴板，按 Ctrl+V 使用。");
                // 只在复制成功后隐藏；沿用备注提交保护，避免丢失操作期间输入的草稿。
                await panel.OnCopyCompletedAsync();
            }
            // 自身写入剪贴板会被监听器忽略，成功使用后须主动刷新排序。
            await GuardAsync("history-use-refresh", async () =>
            {
                await RefreshAsync(item.Id);
                panel.RevealSelectedHistoryItem();
            });
        }
        catch (Exception error) { paths.LogError("clipboard-write", error); SetStatus("复制失败，请稍后重试。原始记录已保留。"); }
        finally { usingClip = false; }
    }

    internal Task ChangeFavoriteAsync() => GuardAsync("favorite", async () =>
    {
        ClipItem? item = model.SelectedItem; if (item == null) return;
        await Task.Run(() => store.SetFavorite(item.Id, !item.Entry.Favorite)); await RefreshAsync(item.Id);
    });

    internal Task DeleteSelectedAsync() => GuardAsync("delete", async () =>
    {
        ClipItem? item = model.SelectedItem; if (item == null) return;
        await Task.Run(() => store.Delete(item.Id)); await RefreshAsync(); SetStatus("记录已删除。");
    });
    internal async Task<string?> SaveNoteAsync(string id, string note)
    {
        try
        {
            if (disposed || exiting) return "程序正在退出，请稍后重试。";
            string prepared = FavoriteNote.Prepare(note);
            await Task.Run(() => store.SetNote(id, prepared));
            model.Items.FirstOrDefault(item => item.Id == id)?.UpdateNote(prepared);
            refreshPending = true;
            SetStatus("备注已保存。");
            return null;
        }
        catch (ArgumentException error) { return error.Message; }
        catch (InvalidOperationException error) { return error.Message; }
        catch (Exception error) { paths.LogError("note-save", error); return "备注保存失败，请检查磁盘空间后重试。"; }
    }

    internal Task ClearAsync(bool includeFavorites) => GuardAsync("clear", async () =>
    {
        await Task.Run(() => store.Clear(includeFavorites)); await RefreshAsync();
        SetStatus(includeFavorites ? "全部记录已删除。" : "普通历史已清理，收藏已保留。");
    });

    internal Task PruneAsync() => GuardAsync("cleanup", async () =>
    {
        await Task.Run(store.PruneAndCollect); if (panel.IsVisible) await RefreshAsync();
    });

    internal Task GuardAsync(string operation, Func<Task> action) => TrackAsync(async () =>
    {
        try { if (!disposed && !exiting) await action(); }
        catch (Exception error) { paths.LogError(operation, error); SetStatus("操作未完成，请检查磁盘空间或稍后重试。原始记录已保留。"); }
    });

    private async Task TrackAsync(Func<Task> action)
    {
        Task task = action();
        operations.Add(task);
        try { await task; }
        finally { operations.Remove(task); }
    }

    private void SetStatus(string message) => model.StatusMessage = message;
    private void Notify(string message) { SetStatus(message); NotificationRequested?.Invoke(message); }
    internal void Stop() { exiting = true; refreshCancellation?.Cancel(); refreshGeneration++; selectionGeneration++; }
    internal async Task StopAsync()
    {
        Stop();
        await thumbnails.StopAsync();
        // 在 UI 线程停止接收新操作，并等待已发出的读写，再释放底层资源。
        try { await Task.WhenAll(operations.ToArray()); }
        catch (Exception error) { paths.LogError("history-stop", error); }
    }
    public void Dispose()
    {
        disposed = true; refreshGeneration++; selectionGeneration++;
        refreshCancellation?.Cancel(); thumbnails.Dispose();
        panel.ThumbnailRequested -= thumbnails.Load; panel.ThumbnailReleased -= thumbnails.Release;
        panel.LoadPreviewAsync = null;
        model.SearchRequested -= DebounceRefresh; model.SelectionChanged -= OnSelectionChanged; model.EditingChanged -= OnEditingChanged;
    }
}

