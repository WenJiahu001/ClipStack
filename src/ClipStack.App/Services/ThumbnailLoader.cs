using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using ClipStack.App.ViewModels;
using ClipStack.App.Platform;
using ClipStack.Core;

namespace ClipStack.App.Services;

// UI 线程维护请求和缓存；仅后台读取/解码。离屏即撤销请求并释放卡片持有的图片。
internal sealed class ThumbnailLoader : IDisposable
{
    private sealed class Request { internal readonly CancellationTokenSource Cancellation = new(); }
    private readonly HistoryStore store;
    private readonly AppPaths paths;
    private readonly SemaphoreSlim slots = new(2, 2);
    private readonly BoundedCache<BitmapImage> cache = new(8 * 1024 * 1024, 128);
    private readonly Dictionary<ClipItem, Request> requests = new();
    private readonly HashSet<ClipItem> visible = new();
    private readonly HashSet<Task> tasks = new();
    private bool disposed;
    internal long CacheBytes => cache.Bytes;
    internal int DecodeCount { get; private set; }

    internal ThumbnailLoader(HistoryStore store, AppPaths paths) { this.store = store; this.paths = paths; }
    internal void Load(ClipItem item)
    {
        if (disposed || item.Entry.Kind != ClipKind.Image) return;
        visible.Add(item);
        if (item.Thumbnail != null) return;
        if (cache.TryGet(item.Id, out var image)) { item.Thumbnail = image; return; }
        if (requests.TryGetValue(item, out var existing) && !existing.Cancellation.IsCancellationRequested) return;
        var request = new Request(); requests[item] = request;
        Task task = LoadAsync(item, request); tasks.Add(task); _ = ObserveAsync(task);
    }
    private async Task ObserveAsync(Task task) { try { await task; } finally { tasks.Remove(task); } }
    private async Task LoadAsync(ClipItem item, Request request)
    {
        CancellationToken token = request.Cancellation.Token;
        bool entered = false;
        try
        {
            await slots.WaitAsync(token); entered = true;
            token.ThrowIfCancellationRequested();
            if (!cache.TryGet(item.Id, out var image))
            {
                var decoded = await Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    byte[] bytes = store.ReadContent(item.Entry, true);
                    token.ThrowIfCancellationRequested();
                    double scale = Math.Min(1, 320.0 / Math.Max(item.Entry.Width, item.Entry.Height));
                    BitmapImage bitmap = BitmapCodec.Decode(bytes, Math.Max(1, (int)(item.Entry.Width * scale)));
                    // BitmapImage 可能仍持有 StreamSource；预算同时计入编码字节，防止原图回退撑大缓存。
                    return (Image: bitmap, Bytes: bytes.LongLength + (long)bitmap.PixelWidth * bitmap.PixelHeight * 4 + 256);
                }, token);
                image = decoded.Image;
                DecodeCount++;
                if (!disposed && !token.IsCancellationRequested && visible.Contains(item)) cache.Set(item.Id, image, decoded.Bytes);
            }
            if (disposed || token.IsCancellationRequested || !visible.Contains(item)) return;
            item.Thumbnail = image;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed) paths.LogError("thumbnail-read", error); }
        finally
        {
            if (entered) slots.Release();
            if (requests.TryGetValue(item, out var current) && ReferenceEquals(current, request)) requests.Remove(item);
            request.Cancellation.Dispose();
        }
    }
    internal void Release(ClipItem item)
    {
        visible.Remove(item);
        if (requests.TryGetValue(item, out var request)) request.Cancellation.Cancel();
        item.Thumbnail = null;
    }
    // 不在 Dispatcher 上循环等待已完成任务，否则会阻塞负责移除任务的 UI continuation。
    internal Task WaitForIdleAsync() => Task.WhenAll(tasks.ToArray());
    internal async Task StopAsync() { Dispose(); await WaitForIdleAsync(); }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (ClipItem item in visible.ToArray()) Release(item);
        cache.Clear();
    }
}
