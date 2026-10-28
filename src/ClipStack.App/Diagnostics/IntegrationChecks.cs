using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core;

using ClipStack.App.Platform;
using ClipStack.App.Services;

namespace ClipStack.App.Diagnostics;

internal static class IntegrationChecks
{
    // 仅显式诊断入口使用；复制原数据到内存，所有检查结束后恢复，不输出正文。
    public static async Task<bool> RunAsync(string reportPath, HistoryStore store, AppPaths paths, bool interactive = false)
    {
        var checks = new List<object>();
        bool success = true, restored = false, clipboardModified = false;
        System.Windows.DataObject? original = null;
        ClipboardMonitor? monitor = null;
        int captured = 0;
        var errors = new List<string>();
        try
        {
            original = SnapshotClipboard();
            AppSettings currentSettings = store.Settings;
            monitor = new ClipboardMonitor(() => currentSettings, error => errors.Add(error.GetType().Name));
            monitor.Captured += clip => { captured++; store.Save(clip); };

            string text = "ClipStack integration " + Guid.NewGuid().ToString("N") + " 中文😀\n  缩进";
            System.Windows.Clipboard.SetText(text);
            clipboardModified = true;
            await WaitAsync(() => captured >= 1);
            ClipEntry entry = store.Query(text).Single();
            Check("Windows 监听通知与文本完整回读", Encoding.UTF8.GetString(store.ReadContent(entry)) == text);

            int before = captured;
            monitor.Paused = true;
            System.Windows.Clipboard.SetText("pause-" + Guid.NewGuid().ToString("N"));
            await Task.Delay(300);
            Check("暂停期间不采集", captured == before);
            monitor.Paused = false;

            var privateData = new System.Windows.DataObject();
            privateData.SetText("excluded-" + Guid.NewGuid().ToString("N"));
            privateData.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 1 }));
            System.Windows.Clipboard.SetDataObject(privateData, true);
            await Task.Delay(300);
            Check("尊重禁止记录标记", captured == before);

            var historyData = new System.Windows.DataObject();
            historyData.SetText("excluded-history-" + Guid.NewGuid().ToString("N"));
            historyData.SetData("CanIncludeInClipboardHistory", new MemoryStream(new byte[4]));
            System.Windows.Clipboard.SetDataObject(historyData, true);
            await Task.Delay(300);
            Check("尊重历史禁用标记", captured == before);

            using (var process = Process.GetCurrentProcess()) currentSettings = currentSettings with { ExcludedProcesses = new[] { process.ProcessName } };
            System.Windows.Clipboard.SetText("excluded-app-" + Guid.NewGuid().ToString("N"));
            await Task.Delay(300);
            Check("来源进程排除", captured == before);
            currentSettings = store.Settings;

            await PasteService.WriteAsync(entry, store.ReadContent(entry), monitor);
            await Task.Delay(350);
            Check("自身回写不产生记录循环", captured == before && System.Windows.Clipboard.GetText() == text);

            byte[] pixels = { 0, 0, 255, 255, 0, 128, 0, 128, 0, 0, 0, 0, 255, 0, 0, 255 };
            BitmapSource bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, pixels, 8);
            bitmap.Freeze();
            CapturedClip image = ClipboardMonitor.EncodeImage(bitmap)!;
            var imageData = new System.Windows.DataObject();
            imageData.SetImage(bitmap); imageData.SetData("PNG", new MemoryStream(image.Content));
            System.Windows.Clipboard.SetDataObject(imageData, true);
            await WaitAsync(() => captured > before);
            ClipEntry imageEntry = store.Query(filter: HistoryFilter.Image).First();
            BitmapImage decoded = BitmapCodec.Decode(store.ReadContent(imageEntry));
            var converted = new FormatConvertedBitmap(decoded, PixelFormats.Bgra32, null, 0);
            byte[] actualPixels = new byte[16]; converted.CopyPixels(actualPixels, 8, 0);
            Check("PNG 图片监听与透明通道保存", decoded.PixelWidth == 2 && decoded.PixelHeight == 2 && actualPixels.SequenceEqual(pixels));

            before = captured;
            await PasteService.WriteAsync(imageEntry, store.ReadContent(imageEntry), monitor);
            await Task.Delay(350);
            Check("图片回写与循环抑制", captured == before && System.Windows.Clipboard.ContainsImage());

            before = captured;
            System.Windows.Clipboard.SetImage(bitmap);
            await WaitAsync(() => captured > before);
            Check("无 PNG 格式时使用 DIB 位图回退", store.Query(filter: HistoryFilter.Image).Any(x => x.Width == 2 && x.Height == 2));

            bool registered = false;
            foreach (string key in new[] { "Ctrl+Alt+Shift+F23", "Ctrl+Alt+Shift+F22", "Ctrl+Alt+Shift+F21" })
                if (monitor.Hotkeys.Register(key)) { registered = true; break; }
            Check("Windows 全局快捷键注册", registered);
            string active = monitor.Hotkeys.ActiveHotkey;
            Check("相同快捷键重复保存", monitor.Hotkeys.Register(active));
            Check("非法快捷键不破坏已有注册", !monitor.Hotkeys.Register("not-a-hotkey") && monitor.Hotkeys.ActiveHotkey == active);
            Check("不存在的目标窗口拒绝自动粘贴", !await PasteService.TryPasteAsync(IntPtr.Zero));
            Check("INPUT 结构匹配 Windows x64", System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.INPUT>() == 40);

            string targetReport = Path.Combine(paths.Root, "paste-target-result.json");
            File.Delete(targetReport);
            string readyFile = targetReport + ".ready";
            File.Delete(readyFile);
            var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            start.ArgumentList.Add("--paste-target"); start.ArgumentList.Add(targetReport);
            // 建立真实的前台调用方，再通过 Windows 公共 API 向子进程交接焦点权限。
            var foreground = new Window { Width = 320, Height = 100, Title = "ClipStack 系统粘贴检查", ShowInTaskbar = false };
            if (interactive)
            {
                var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var button = new System.Windows.Controls.Button { Content = "开始验证自动粘贴", Margin = new Thickness(12) };
                button.Click += (_, _) => proceed.TrySetResult(); foreground.Content = button;
                // 启动器可能指定 SW_HIDE；首次 Show 消耗该参数，再显式显示交互窗口。
                foreground.Show(); foreground.Hide(); foreground.Show();
                if (await Task.WhenAny(proceed.Task, Task.Delay(120000)) != proceed.Task)
                { foreground.Close(); throw new TimeoutException("等待粘贴检查交互超时。"); }
            }
            foreground.Show(); foreground.Activate();
            using (Process target = Process.Start(start) ?? throw new InvalidOperationException("无法启动粘贴测试窗口。"))
            {
                NativeMethods.AllowSetForegroundWindow((uint)target.Id);
                try
                {
                    IntPtr handle = IntPtr.Zero;
                    // 等待目标实际完成布局并聚焦；冷启动不能仅以进程存在判断就绪。
                    for (int attempt = 0; attempt < 300; attempt++)
                    {
                        await Task.Delay(30);
                        if (File.Exists(readyFile))
                        {
                            handle = new IntPtr(long.Parse(File.ReadAllText(readyFile), System.Globalization.CultureInfo.InvariantCulture));
                            break;
                        }
                        if (target.HasExited) break;
                    }
                    SaveResult pasteRecord = store.Save(CapturedClip.FromText(PasteTargetProbe.ExpectedText));
                    ClipEntry pasteEntry = store.Query().First(x => x.Id == pasteRecord.Id);
                    await PasteService.WriteAsync(pasteEntry, store.ReadContent(pasteEntry), monitor);
                    string? failure = null;
                    bool sent = await PasteService.TryPasteAsync(handle, reason => failure = reason);
                    using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(8));
                    await target.WaitForExitAsync(timeout.Token);
                    bool received = File.Exists(targetReport) && JsonDocument.Parse(File.ReadAllText(targetReport)).RootElement.GetProperty("success").GetBoolean();
                    checks.Add(new { name = "自动粘贴诊断", targetReady = handle != IntPtr.Zero, inputSent = sent, targetReceived = received, failure });
                    Check("独立进程窗口焦点恢复及真实自动粘贴", sent && received);
                }
                finally
                {
                    if (!target.HasExited) { target.Kill(); await target.WaitForExitAsync(); }
                    File.Delete(readyFile);
                    foreground.Close();
                }
            }

            // 固定在后台图像槽等待，验证退出等待的是完整采集任务而非仅保存队列。
            var imageSlotEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseImageSlot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Task heldImageSlot = BitmapCodec.ProcessAsync(() =>
            {
                imageSlotEntered.TrySetResult(); releaseImageSlot.Task.GetAwaiter().GetResult(); return true;
            });
            await imageSlotEntered.Task;
            int beforeStop = captured;
            try
            {
                var pendingImage = new System.Windows.DataObject();
                pendingImage.SetData("PNG", new MemoryStream(image.Content, false));
                System.Windows.Clipboard.SetDataObject(pendingImage, true);
                await WaitAsync(() => monitor.PendingCaptureCount > 0);
                Task stopping = monitor.StopAsync();
                Check("退出等待尚未完成的图片处理", monitor.PendingCaptureCount > 0 && !stopping.IsCompleted);
                releaseImageSlot.TrySetResult();
                await heldImageSlot; await stopping;
                Check("退出等待完成后最后的图片已交付保存", captured >= beforeStop + 1 && monitor.PendingCaptureCount == 0);
            }
            finally { releaseImageSlot.TrySetResult(); await heldImageSlot; }

            string instanceScope = "diagnostic-" + Guid.NewGuid().ToString("N");
            using (var firstInstance = new SingleInstance(instanceScope))
            using (var secondInstance = new SingleInstance(instanceScope))
            {
                Check("单实例互斥", firstInstance.IsPrimary && !secondInstance.IsPrimary);
                var activated = new TaskCompletionSource<bool>();
                Task listening = firstInstance.ListenAsync(() => activated.TrySetResult(true));
                await secondInstance.SignalAsync();
                Check("重复启动通过用户会话管道唤醒主进程", await Task.WhenAny(activated.Task, Task.Delay(3000)) == activated.Task);
                firstInstance.Dispose();
                await listening;
            }
        }
        catch (Exception error)
        {
            success = false; paths.LogError("native-integration", error);
            checks.Add(new { name = "系统集成执行", passed = false, errorType = error.GetType().Name });
        }
        finally
        {
            monitor?.Dispose();
            if (clipboardModified)
            {
                try
                {
                    for (int attempt = 0; attempt < 5; attempt++)
                    {
                        try
                        {
                            if (original == null) System.Windows.Clipboard.Clear();
                            else System.Windows.Clipboard.SetDataObject(original, true);
                            restored = true; break;
                        }
                        catch (System.Runtime.InteropServices.ExternalException) when (attempt < 4) { await Task.Delay(100); }
                    }
                }
                catch (Exception error) { success = false; paths.LogError("clipboard-restore", error); }
            }
            else restored = true;
            Check("恢复原系统剪贴板", restored);
            string output = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { success, checks, errors, clipboardRestored = restored,
                os = System.Runtime.InteropServices.RuntimeInformation.OSDescription }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return success;

        void Check(string name, bool passed) { success &= passed; checks.Add(new { name, passed }); }
    }

    private static System.Windows.DataObject? SnapshotClipboard()
    {
        System.Windows.IDataObject? source = System.Windows.Clipboard.GetDataObject();
        if (source == null) return null;
        var copy = new System.Windows.DataObject();
        foreach (string format in source.GetFormats(false))
        {
            object? data = source.GetData(format, false);
            object cloned = data switch
            {
                string value => value,
                byte[] bytes => bytes.ToArray(),
                string[] strings => strings.ToArray(),
                MemoryStream stream => new MemoryStream(stream.ToArray()),
                BitmapSource bitmap => bitmap.Clone(),
                System.Drawing.Bitmap bitmap => bitmap.Clone(),
                null => throw new InvalidOperationException("无法完整备份系统剪贴板，取消系统集成测试。"),
                _ => throw new NotSupportedException("存在无法安全恢复的剪贴板格式，取消系统集成测试。")
            };
            copy.SetData(format, cloned, false);
        }
        return copy;
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (int attempt = 0; attempt < 100; attempt++) { if (condition()) return; await Task.Delay(30); }
        throw new TimeoutException("剪贴板事件未在预期时间内到达。");
    }
}
