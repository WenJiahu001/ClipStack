using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClipStack.App.Platform;
using ClipStack.Core;

namespace ClipStack.App.Diagnostics;

internal static class SoakChecks
{
    internal static async Task<bool> RunAsync(string report, int seconds, HistoryStore store, AppPaths paths)
    {
        var watch = Stopwatch.StartNew();
        var memory = new List<object>();
        int operations = 0;
        bool success = true;
        try
        {
            var image = PreviewFixtures.CreateImage();
            CapturedClip captured = (await BitmapCodec.ProcessAsync(() => ClipboardMonitor.EncodeImage(image)))!;
            while (watch.Elapsed.TotalSeconds < seconds)
            {
                int sequence = operations++;
                string text = "持续检查合成数据 " + sequence + new string('a', 6000);
                await Task.Run(() => store.Save(CapturedClip.FromText(text)));
                Task query = Task.Run(() => store.Query("合成数据", limit: 101));
                Task save = Task.Run(() => store.Save(captured));
                await Task.WhenAll(query, save);
                if (sequence % 20 == 0)
                {
                    using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
                    try { store.Query(cancellationToken: cancellation.Token); }
                    catch (OperationCanceledException) { }
                    await Task.Run(store.PruneAndCollect);
                    using var process = Process.GetCurrentProcess();
                    memory.Add(new { seconds = watch.Elapsed.TotalSeconds, privateBytes = process.PrivateMemorySize64, workingSetBytes = process.WorkingSet64 });
                }
                if (Encoding.UTF8.GetString(store.ReadContent(store.Query(text[..20], limit: 1).First())) != text)
                    throw new InvalidOperationException("持续检查回读不一致。");
                await Task.Delay(5);
            }
        }
        catch (Exception error) { success = false; paths.LogError("soak", error); }
        using var current = Process.GetCurrentProcess();
        string output = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            success, version = typeof(App).Assembly.GetName().Version?.ToString(), requestedSeconds = seconds,
            elapsedSeconds = watch.Elapsed.TotalSeconds, operations, peakWorkingSetBytes = current.PeakWorkingSet64, memory,
            clipboardTouched = false, hotkeysRegistered = false, syntheticDataOnly = true,
            os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString()
        }, new JsonSerializerOptions { WriteIndented = true }));
        return success;
    }
}
