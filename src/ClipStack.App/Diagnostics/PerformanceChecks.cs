using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipStack.App.Platform;
using ClipStack.App.Services;
using ClipStack.App.ViewModels;
using ClipStack.Core;

namespace ClipStack.App.Diagnostics;

internal static class PerformanceChecks
{
    internal static async Task<bool> RunAsync(string reportPath, HistoryStore store, AppPaths paths)
    {
        var checks = new List<object>();
        bool success = true;
        var model = new MainViewModel();
        var panel = new MainWindow(model, paths) { PreviewMode = true, KeepOpen = true, AllowClose = true };
        using var history = new HistoryController(store, paths, model, panel);
        try
        {
            BitmapSource large = CreateImage(2400, 1600, 80);
            CapturedClip captured = (await Task.Run(() => ClipboardMonitor.EncodeImage(large)))!;
            string largeId = store.Save(captured with { Thumbnail = null }).Id!;
            for (int i = 0; i < 40; i++)
            {
                BitmapSource source = CreateImage(320, 320, (byte)(100 + i));
                CapturedClip clip = (await Task.Run(() => ClipboardMonitor.EncodeImage(source)))!;
                store.Save(clip);
            }
            string newestSearchId = store.Save(CapturedClip.FromText("唯一最新搜索结果")).Id!;
            await history.RefreshAsync();
            var initialItems = model.Items;
            await history.RefreshAsync();
            Check("重复刷新复用列表与记录对象", ReferenceEquals(initialItems, model.Items));
            panel.Show(); await IdleAsync(panel); await Task.Delay(150);
            int loaded = model.Items.Count(item => item.Thumbnail != null);
            Check("仅加载可视区附近图片", loaded > 0 && loaded < 24);
            ClipItem largeItem = model.Items.Single(item => item.Id == largeId);
            model.SelectedItem = largeItem; await Task.Delay(30);
            Check("选择记录不自动解码原图", model.PreviewImage == null);
            await history.LoadSelectionAsync();
            Check("主动查看仍能读取原图", model.PreviewImage is BitmapSource bitmap && bitmap.PixelWidth == 2400 && bitmap.PixelHeight == 1600);
            model.PreviewImage = null;

            var list = (ListBox)panel.FindName("HistoryList");
            list.ScrollIntoView(largeItem); await IdleAsync(panel);
            for (int i = 0; i < 200 && largeItem.Thumbnail == null; i++) await Task.Delay(10);
            Check("第24张之后的图片按需加载且原图回退受尺寸约束", largeItem.Thumbnail is BitmapSource thumb && thumb.PixelWidth <= 320 && thumb.PixelHeight <= 320);
            int realized = Enumerable.Range(0, model.Items.Count).Count(i => list.ItemContainerGenerator.ContainerFromIndex(i) != null);
            Check("滚动后仍保持列表虚拟化", realized < model.Items.Count);

            using (var loader = new ThumbnailLoader(store, paths))
            {
                var isolated = new ClipItem(largeItem.Entry);
                loader.Load(isolated); loader.Load(isolated); await loader.WaitForIdleAsync();
                ImageSource? first = isolated.Thumbnail;
                loader.Release(isolated); Check("离屏卡片释放图片引用", isolated.Thumbnail == null);
                loader.Load(isolated); await loader.WaitForIdleAsync();
                Check("重复缩略图请求合并并复用缓存", first != null && ReferenceEquals(first, isolated.Thumbnail) && loader.DecodeCount == 1);
                loader.Release(isolated);
                foreach (ClipItem item in model.Items.Where(item => item.Entry.Kind == ClipKind.Image))
                {
                    var copy = new ClipItem(item.Entry); loader.Load(copy); await loader.WaitForIdleAsync(); loader.Release(copy);
                }
                Check("连续浏览图片后缓存不超过8MiB", loader.CacheBytes > 0 && loader.CacheBytes <= 8 * 1024 * 1024);
                var cancelled = new ClipItem(largeItem.Entry);
                loader.Load(cancelled); loader.Release(cancelled); await loader.WaitForIdleAsync();
                Check("撤销的图片请求不回写离屏卡片", cancelled.Thumbnail == null);
                await loader.StopAsync(); Check("停止后释放缩略图缓存", loader.CacheBytes == 0);
            }

            var searches = new List<Task>();
            for (int i = 0; i < 12; i++)
            {
                model.QueryText = "过时搜索" + i;
                searches.Add(history.RefreshAsync());
            }
            model.QueryText = "唯一最新搜索"; searches.Add(history.RefreshAsync());
            await Task.WhenAll(searches);
            Check("快速搜索只显示最新请求结果", model.Items.Count == 1 && model.Items[0].Id == newestSearchId);
            model.QueryText = ""; await history.RefreshAsync();
            ClipItem usedItem = model.Items.Single(item => item.Id == largeId);
            panel.ApplySettings(new AppSettings { ResetListOnActivate = false });
            list.ScrollIntoView(model.Items.Last()); await IdleAsync(panel);
            await Task.Run(() => store.MarkUsed(largeId));
            await history.RefreshAsync(largeId); panel.RevealSelectedHistoryItem(); await IdleAsync(panel);
            Check("关闭激活重置时使用记录仍置顶并显示，复用原对象", model.Items[0] == usedItem
                && model.SelectedItem == usedItem && FindScrollViewer(list)?.VerticalOffset == 0);
            await Task.Run(() => store.Save(CapturedClip.FromText("唯一最新搜索结果")));
            await history.RefreshAsync();
            Check("重复复制置顶且不增加列表条数", model.Items[0].Id == newestSearchId && model.Items.Count == 42);
            int saved = 0;
            var worker = new ClipboardCaptureWorker(store, paths, result =>
            {
                if (result.Saved) saved++;
                history.RequestRefresh();
                return Task.CompletedTask;
            }, _ => { });
            for (int i = 0; i < 8; i++) worker.Enqueue(CapturedClip.FromText("连续保存验证-" + i));
            await worker.CompleteAsync();
            Check("连续保存队列排空且不等待逐条刷新", saved == 8 && store.Query("连续保存验证").Count == 8);
            await Task.Delay(350);
            Check("连续保存合并刷新后列表完整", model.Items.Count == 50);

            int uiThread = Environment.CurrentManagedThreadId;
            int encoderThread = uiThread;
            long start = Stopwatch.GetTimestamp();
            Task<CapturedClip?> encoding = Task.Run(() => { encoderThread = Environment.CurrentManagedThreadId; return ClipboardMonitor.EncodePng(captured.Content); });
            await IdleAsync(panel);
            CapturedClip? encoded = await encoding;
            Check("PNG完整解码和重编码在后台且保留去重结果", encoderThread != uiThread && encoded != null && encoded.Content.SequenceEqual(captured.Content));
            checks.Add(new { name = "大图后台处理测量", milliseconds = Stopwatch.GetElapsedTime(start).TotalMilliseconds, width = 2400, height = 1600 });
        }
        catch (Exception error)
        {
            success = false; paths.LogError("performance-check", error);
            checks.Add(new { name = "性能回归执行", passed = false, error = error.ToString() });
        }
        finally
        {
            panel.Close(); await history.StopAsync();
            string output = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { success, checks, clipboardTouched = false, syntheticDataOnly = true }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return success;
        void Check(string name, bool passed) { success &= passed; checks.Add(new { name, passed }); }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } found) return found;
        return null;
    }
    private static async Task IdleAsync(Window window) => await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static BitmapSource CreateImage(int width, int height, byte color)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(color, 160, 210)), null, new Rect(0, 0, width, height));
            context.DrawEllipse(Brushes.White, null, new Point(width / 2.0, height / 2.0), width / 4.0, height / 4.0);
        }
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        image.Render(visual); image.Freeze(); return image;
    }
}
