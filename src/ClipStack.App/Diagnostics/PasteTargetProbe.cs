using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ClipStack.Core;

using ClipStack.App.Platform;
using ClipStack.App.Services;

namespace ClipStack.App.Diagnostics;

internal static class PasteTargetProbe
{
    internal const string ExpectedText = "ClipStack 自动粘贴验证 ✓\n  保留缩进";
    public static async Task RunAsync(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        var completed = new TaskCompletionSource<bool>();
        var input = new TextBox { AcceptsReturn = true, Margin = new Thickness(18), FontSize = 17 };
        var window = new Window
        {
            Title = "ClipStack 自动粘贴测试 · 完成后自动关闭", Width = 480, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, Content = input
        };
        input.TextChanged += (_, _) =>
        {
            if (input.Text.Length > 0)
                completed.TrySetResult(input.Text.Replace("\r\n", "\n").Replace('\r', '\n') == ExpectedText);
        };
        window.Closed += (_, _) => completed.TrySetResult(false);
        window.Show(); window.Activate(); input.Focus();
        await window.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        AppPaths.AtomicWrite(reportPath + ".ready", System.Text.Encoding.UTF8.GetBytes(
            new System.Windows.Interop.WindowInteropHelper(window).Handle.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Task finished = await Task.WhenAny(completed.Task, Task.Delay(6000));
        bool success = finished == completed.Task && await completed.Task;
        string output = Path.GetFullPath(reportPath); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(new { success, contentsRecorded = false, receivedLength = input.Text.Length,
            expectedLength = ExpectedText.Length, windowWasActive = window.IsActive, inputWasFocused = input.IsKeyboardFocused }));
        window.Close();
    }
}
