using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClipStack.App.Services;
using ClipStack.App.ViewModels;
using ClipStack.Core;

namespace ClipStack.App.Diagnostics;

internal static class DiagnosticRunner
{
    internal static async Task<int> RunAsync(StartupOptions options, HistoryStore store, AppPaths paths)
    {
        if (options.Smoke != null) { await SmokeChecks.RunAsync(options.Smoke, store, paths); return 0; }
        if (options.Integration != null) return await IntegrationChecks.RunAsync(options.Integration, store, paths) ? 0 : 1;
        if (options.PanelTest != null) return await PanelChecks.RunAsync(options.PanelTest, store, paths) ? 0 : 1;
        if (options.PerformanceTest != null) return await PerformanceChecks.RunAsync(options.PerformanceTest, store, paths) ? 0 : 1;
        await RenderPreviewAsync(options, store, paths); return 0;
    }

    private static async Task RenderPreviewAsync(StartupOptions options, HistoryStore store, AppPaths paths)
    {
        await PreviewFixtures.SeedAsync(store);
        var model = new MainViewModel { HotkeyLabel = "Ctrl + Alt + V", StatusMessage = "正在记录 · 仅保存到本机 · 按快捷键随时呼出" };
        var panel = new MainWindow(model, paths) { PreviewMode = true, AllowClose = true };
        panel.ApplySettings(store.Settings);
        using var history = new HistoryController(store, paths, model, panel);
        Window renderWindow = panel;
        try
        {
            await history.RefreshAsync(); panel.Show(); await Task.Delay(250);
            if (options.SettingsPreview != null)
            {
                var settings = new SettingsWindow(store.Settings, paths.Root, _ => Task.FromResult<string?>(null)) { Owner = panel };
                settings.Show(); renderWindow = settings;
            }
            await panel.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            renderWindow.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)renderWindow.ActualWidth, (int)renderWindow.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(renderWindow);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            string output = Path.GetFullPath(options.SettingsPreview ?? options.Preview!); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var file = File.Create(output); encoder.Save(file);
        }
        finally { if (renderWindow != panel) renderWindow.Close(); panel.Close(); await history.StopAsync(); }
    }
}
