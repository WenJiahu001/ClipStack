using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using ClipStack.App.Platform;
using ClipStack.App.Services;
using ClipStack.Core;

namespace ClipStack.App.Diagnostics;

internal static class SmokeChecks
{
    internal static async Task RunAsync(string report, HistoryStore store, AppPaths paths)
    {
        SaveResult saved = await Task.Run(() => store!.Save(CapturedClip.FromText("ClipStack 单文件启动验证 ✓")));
        ClipEntry entry = store!.Query().First(x => x.Id == saved.Id);
        bool roundTrip = Encoding.UTF8.GetString(store.ReadContent(entry)) == "ClipStack 单文件启动验证 ✓";
        var image = PreviewFixtures.CreateImage();
        CapturedClip captured = ClipboardMonitor.EncodeImage(image) ?? throw new InvalidOperationException("图片编码失败。");
        SaveResult imageResult = store.Save(captured);
        ClipEntry imageEntry = store.Query().First(x => x.Id == imageResult.Id);
        bool imageOk = BitmapCodec.Decode(store.ReadContent(imageEntry)).PixelWidth == captured.Width;
        using var process = Process.GetCurrentProcess();
        var modules = process.Modules.Cast<ProcessModule>().ToArray();
        string? runtimeModulePath = modules.FirstOrDefault(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
        string? sqliteModulePath = modules.FirstOrDefault(module => module.ModuleName.Equals("e_sqlite3.dll", StringComparison.OrdinalIgnoreCase))?.FileName;
        string output = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new
        {
            success = saved.Saved && roundTrip && imageOk, version = typeof(App).Assembly.GetName().Version?.ToString(), os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(), runtime = Environment.Version.ToString(),
            runtimeModulePath, sqliteModulePath,
            runtimeDirectory = RuntimeEnvironment.GetRuntimeDirectory(),
            frameworkDepsFile = AppContext.GetData("FX_DEPS_FILE") as string,
            textRoundTrip = roundTrip, imageRoundTrip = imageOk, dataPath = paths!.Root,
            clipboardTouched = false, hotkeysRegistered = false
        }, new JsonSerializerOptions { WriteIndented = true }));
        if (!roundTrip || !imageOk) throw new InvalidOperationException("启动验证失败。");
    }

}
