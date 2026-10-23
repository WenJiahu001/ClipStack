using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using ClipStack.Core;

namespace ClipStack.App.Platform;

internal static class PasteService
{
    public static async Task WriteAsync(ClipEntry entry, byte[] content, ClipboardMonitor monitor)
    {
        var data = new System.Windows.DataObject();
        if (entry.Kind == ClipKind.Text) data.SetText(Encoding.UTF8.GetString(content), System.Windows.TextDataFormat.UnicodeText);
        else
        {
            using var stream = new MemoryStream(content, false);
            var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
            data.SetImage(bitmap);
            // PNG 额外保留透明通道；DIB 兼容只接受位图的目标程序。
            data.SetData("PNG", new MemoryStream(content, false));
        }
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                monitor.BeginWrite();
                try { System.Windows.Clipboard.SetDataObject(data, true); }
                finally { monitor.EndWrite(); }
                return;
            }
            catch (ExternalException) when (attempt < 4) { await Task.Delay(30 * (attempt + 1)); }
        }
    }

    public static async Task<bool> TryPasteAsync(IntPtr target, Action<string>? diagnostic = null)
    {
        bool Failed(string reason) { diagnostic?.Invoke(reason); return false; }
        if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return Failed("目标窗口不存在");
        NativeMethods.GetWindowThreadProcessId(target, out uint process);
        if (process == (uint)Environment.ProcessId) return Failed("目标为当前进程");
        if (NativeMethods.GetForegroundWindow() != target && !NativeMethods.SetForegroundWindow(target)) return Failed("系统拒绝激活目标窗口");
        for (int attempt = 0; attempt < 20; attempt++)
        {
            await Task.Delay(25);
            if (NativeMethods.GetForegroundWindow() != target) return Failed("目标窗口失去前台焦点");
            bool modifierDown = false;
            foreach (int key in new[] { 0x10, 0x11, 0x12, 0x5B, 0x5C })
                modifierDown |= (NativeMethods.GetAsyncKeyState(key) & 0x8000) != 0;
            if (modifierDown) continue;
            var inputs = new[] { Key(0x11, false), Key(0x56, false), Key(0x56, true), Key(0x11, true) };
            uint sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.INPUT>());
            if (sent > 0 && sent < inputs.Length)
            {
                // 输入被中途拦截时释放本次按下的键，避免目标应用残留 Ctrl 状态。
                var releases = new[] { Key(0x56, true), Key(0x11, true) };
                NativeMethods.SendInput((uint)releases.Length, releases, Marshal.SizeOf<NativeMethods.INPUT>());
            }
            return sent == inputs.Length || Failed("系统未完整接收模拟按键");
        }
        return Failed("修饰键持续处于按下状态");
    }

    private static NativeMethods.INPUT Key(ushort key, bool up) => new()
    {
        Type = 1,
        Data = new NativeMethods.INPUTUNION { Keyboard = new NativeMethods.KEYBDINPUT { Key = key, Flags = up ? 2u : 0u } }
    };
}
