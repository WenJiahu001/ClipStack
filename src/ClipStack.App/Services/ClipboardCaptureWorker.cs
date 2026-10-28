using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ClipStack.Core;

namespace ClipStack.App.Services;

// 采集回调只入队；队列负责串行保存、内存预算和退出前排空。
internal sealed class ClipboardCaptureWorker
{
    private const long ByteBudget = 32 * 1024 * 1024;
    private sealed record Work(CapturedClip? Clip, TaskCompletionSource? Barrier);
    private readonly Channel<Work> queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(16)
    { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly HistoryStore store;
    private readonly AppPaths paths;
    private readonly Func<SaveResult, Task> completed;
    private readonly Action<string> notice;
    private readonly Task draining;
    private long queuedBytes;

    internal ClipboardCaptureWorker(HistoryStore store, AppPaths paths, Func<SaveResult, Task> completed, Action<string> notice)
    {
        this.store = store; this.paths = paths; this.completed = completed; this.notice = notice;
        draining = DrainAsync();
    }

    internal void Enqueue(CapturedClip clip)
    {
        long bytes = clip.Content.LongLength + (clip.Thumbnail?.LongLength ?? 0);
        if (Interlocked.Add(ref queuedBytes, bytes) <= ByteBudget && queue.Writer.TryWrite(new Work(clip, null))) return;
        Interlocked.Add(ref queuedBytes, -bytes);
        notice("保存队列暂时已满，本次内容已跳过。");
    }

    private async Task DrainAsync()
    {
        await foreach (Work work in queue.Reader.ReadAllAsync())
        {
            if (work.Barrier != null) { work.Barrier.TrySetResult(); continue; }
            CapturedClip clip = work.Clip!;
            try { await completed(await Task.Run(() => store.Save(clip))); }
            catch (Exception error) { paths.LogError("history-save", error); notice("内容保存失败，原始历史已保留。"); }
            finally { Interlocked.Add(ref queuedBytes, -clip.Content.LongLength - (clip.Thumbnail?.LongLength ?? 0)); }
        }
    }

    internal async Task FlushAsync()
    {
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await queue.Writer.WriteAsync(new Work(null, barrier));
        await barrier.Task;
    }

    internal Task CompleteAsync() { queue.Writer.TryComplete(); return draining; }
}
