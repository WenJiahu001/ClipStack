using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.Core;

namespace ClipStack.App.Platform;

internal sealed class ClipboardMonitor : IDisposable
{
    private readonly HwndSource source;
    private readonly Func<AppSettings> settings;
    private readonly Action<Exception> reportError;
    private uint suppressedSequence;
    private int generation;
    private readonly SemaphoreSlim encoders = new(2, 2);
    private bool disposed;
    private bool writing;
    private bool paused;
    private bool accepting = true;
    private readonly HashSet<Task> captures = new();
    private readonly HashSet<uint> pendingSequences = new();
    private int policyGeneration;
    private readonly uint excludeFormat = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint historyFormat = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private readonly uint pngFormat = NativeMethods.RegisterClipboardFormat("PNG");
    public event Action<CapturedClip>? Captured;
    public event Action<string>? Notice;
    public event Action? HotkeyPressed;
    public bool Paused
    {
        get => paused;
        set { if (paused != value) { paused = value; policyGeneration++; generation++; } }
    }
    public HotkeyManager Hotkeys { get; }
    internal int PendingCaptureCount => captures.Count;

    public ClipboardMonitor(Func<AppSettings> settings, Action<Exception> reportError)
    {
        this.settings = settings; this.reportError = reportError;
        source = new HwndSource(new HwndSourceParameters("ClipStack.Events") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        source.AddHook(WindowProc);
        if (!NativeMethods.AddClipboardFormatListener(source.Handle))
        { source.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error(), "无法注册剪贴板监听。"); }
        Hotkeys = new HotkeyManager(source.Handle);
    }

    public void SuppressCurrentClipboard() => suppressedSequence = NativeMethods.GetClipboardSequenceNumber();
    public void BeginWrite() { writing = true; generation++; }
    public void EndWrite() { SuppressCurrentClipboard(); writing = false; }
    internal void InvalidatePending() { policyGeneration++; generation++; }
    internal async Task StopAsync()
    {
        accepting = false;
        NativeMethods.RemoveClipboardFormatListener(source.Handle);
        await Task.WhenAll(captures.ToArray());
    }
    private Task ObserveCaptureAsync(Task task, uint sequence)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        captures.Add(completion.Task);
        _ = FinishAsync();
        return completion.Task;
        async Task FinishAsync()
        {
            try { await task; }
            catch (Exception error) { reportError(error); Notice?.Invoke("本次内容读取失败，后续复制会继续记录。"); }
            finally { pendingSequences.Remove(sequence); captures.Remove(completion.Task); completion.TrySetResult(); }
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == NativeMethods.ClipboardUpdate)
        {
            uint sequence = NativeMethods.GetClipboardSequenceNumber();
            if (accepting && !Paused && !writing && pendingSequences.Add(sequence))
                _ = ObserveCaptureAsync(CaptureAsync(++generation, sequence), sequence);
            handled = true;
        }
        else if (message == NativeMethods.HotkeyMessage) { HotkeyPressed?.Invoke(); handled = true; }
        return IntPtr.Zero;
    }

    private async Task CaptureAsync(int ticket, uint sequence)
    {
        int policy = policyGeneration;
        int[] delays = { 0, 20, 40, 80, 160 };
        foreach (int delay in delays)
        {
            if (delay != 0) await Task.Delay(delay);
            if (disposed || Paused || writing || policy != policyGeneration || ticket != generation || sequence == suppressedSequence
                || sequence != NativeMethods.GetClipboardSequenceNumber()) return;
            try
            {
                DateTimeOffset copiedAt = DateTimeOffset.UtcNow;
                if (!NativeMethods.OpenClipboard(source.Handle)) continue;
                CapturedClip? clip = null;
                BitmapSource? image = null;
                byte[]? png = null;
                try
                {
                    if (ShouldExclude()) return;
                    if (NativeMethods.IsClipboardFormatAvailable(pngFormat))
                    {
                        png = ReadGlobalBytes(pngFormat, AppSettings.MaxImageBytes);
                        if (png == null || !ValidPngDimensions(png)) { Notice?.Invoke("图片过大或格式无效，已跳过。"); return; }
                    }
                    else if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.DibV5)
                        || NativeMethods.IsClipboardFormatAvailable(NativeMethods.Dib)
                        || NativeMethods.IsClipboardFormatAvailable(NativeMethods.Bitmap))
                    {
                        if (!CheckBitmapDimensions()) { Notice?.Invoke("图片尺寸超过限制，已跳过。"); return; }
                        // WPF 的 GetImage 会自行打开剪贴板，在下面关闭原生锁后读取。
                    }
                    else if (NativeMethods.IsClipboardFormatAvailable(NativeMethods.UnicodeText))
                    {
                        string? text = ReadText();
                        if (text == null) { Notice?.Invoke("文本超过 1 MiB，已跳过。"); return; }
                        if (text.Length != 0) clip = CapturedClip.FromText(text);
                    }
                    else return;
                }
                finally { NativeMethods.CloseClipboard(); }

                if (clip == null)
                {
                    if (sequence != NativeMethods.GetClipboardSequenceNumber()) return;
                    if (!encoders.Wait(0)) { Notice?.Invoke("图片正在处理中，本次图片已跳过。"); return; }
                    try
                    {
                        if (png != null)
                        {
                            // 已复制 PNG 字节，不再持有剪贴板锁；完整解码与编码均移出 UI 线程。
                            byte[] snapshot = png;
                            clip = await BitmapCodec.ProcessAsync(() => EncodePng(snapshot));
                        }
                        else
                        {
                            image = System.Windows.Clipboard.GetImage();
                            if (image == null || sequence != NativeMethods.GetClipboardSequenceNumber()) return;
                            if ((long)image.PixelWidth * image.PixelHeight > AppSettings.MaxImagePixels) { Notice?.Invoke("图片尺寸超过限制，已跳过。"); return; }
                            if (image.CanFreeze) image.Freeze();
                            BitmapSource snapshot = image;
                            clip = await BitmapCodec.ProcessAsync(() => EncodeImage(snapshot));
                        }
                    }
                    finally { encoders.Release(); }
                    if (clip == null) { Notice?.Invoke("图片超过 20 MiB，已跳过。"); return; }
                }
                if (disposed || Paused || policy != policyGeneration) return;
                Captured?.Invoke(clip with { CopiedAt = copiedAt });
                return;
            }
            catch (Exception e) when (e is ExternalException or IOException or InvalidOperationException or NotSupportedException or ArgumentException or OverflowException)
            {
                reportError(e);
                if (ticket == generation && delay == delays[^1]) Notice?.Invoke("暂时无法读取剪贴板，后续复制会继续记录。");
            }
        }
    }

    private bool ShouldExclude()
    {
        if (NativeMethods.IsClipboardFormatAvailable(excludeFormat)) return true;
        if (NativeMethods.IsClipboardFormatAvailable(historyFormat))
        {
            IntPtr handle = NativeMethods.GetClipboardData(historyFormat);
            if (handle != IntPtr.Zero && NativeMethods.GlobalSize(handle).ToUInt64() >= 4)
            {
                IntPtr pointer = NativeMethods.GlobalLock(handle);
                if (pointer != IntPtr.Zero) try { if (Marshal.ReadInt32(pointer) == 0) return true; } finally { NativeMethods.GlobalUnlock(handle); }
            }
        }
        string[] excluded = settings().ExcludedProcesses;
        if (excluded.Length == 0) return false;
        NativeMethods.GetWindowThreadProcessId(NativeMethods.GetClipboardOwner(), out uint processId);
        if (processId == 0) return false;
        try { using var process = Process.GetProcessById((int)processId); return excluded.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or Win32Exception or InvalidOperationException) { return false; }
    }

    private static string? ReadText()
    {
        IntPtr handle = NativeMethods.GetClipboardData(NativeMethods.UnicodeText);
        if (handle == IntPtr.Zero) return "";
        ulong length = NativeMethods.GlobalSize(handle).ToUInt64();
        if (length > (ulong)(AppSettings.MaxTextBytes + 1) * 2) return null;
        IntPtr pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw new ExternalException("剪贴板内容尚未就绪。");
        try
        {
            string text = Marshal.PtrToStringUni(pointer, (int)(length / 2)) ?? "";
            int terminator = text.IndexOf('\0'); if (terminator >= 0) text = text[..terminator];
            return Encoding.UTF8.GetByteCount(text) > AppSettings.MaxTextBytes ? null : text;
        }
        finally { NativeMethods.GlobalUnlock(handle); }
    }

    private static byte[]? ReadGlobalBytes(uint format, int max)
    {
        IntPtr handle = NativeMethods.GetClipboardData(format);
        if (handle == IntPtr.Zero) return null;
        ulong length = NativeMethods.GlobalSize(handle).ToUInt64();
        if (length == 0 || length > (ulong)max) return null;
        IntPtr pointer = NativeMethods.GlobalLock(handle);
        if (pointer == IntPtr.Zero) throw new ExternalException("剪贴板内容尚未就绪。");
        try { byte[] bytes = new byte[(int)length]; Marshal.Copy(pointer, bytes, 0, bytes.Length); return bytes; }
        finally { NativeMethods.GlobalUnlock(handle); }
    }

    private static bool ValidPngDimensions(byte[] bytes)
    {
        if (bytes.Length < 24 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71) return false;
        uint width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        uint height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        return width > 0 && height > 0 && (ulong)width * height <= (ulong)AppSettings.MaxImagePixels;
    }

    private static bool CheckBitmapDimensions()
    {
        foreach (uint format in new[] { NativeMethods.DibV5, NativeMethods.Dib })
        {
            if (!NativeMethods.IsClipboardFormatAvailable(format)) continue;
            IntPtr handle = NativeMethods.GetClipboardData(format);
            ulong bytes = NativeMethods.GlobalSize(handle).ToUInt64();
            if (bytes < 12 || bytes > 128 * 1024 * 1024) return false;
            IntPtr pointer = NativeMethods.GlobalLock(handle); if (pointer == IntPtr.Zero) return false;
            try
            {
                int header = Marshal.ReadInt32(pointer);
                long width = header == 12 ? (ushort)Marshal.ReadInt16(pointer, 4) : Math.Abs((long)Marshal.ReadInt32(pointer, 4));
                long height = header == 12 ? (ushort)Marshal.ReadInt16(pointer, 6) : Math.Abs((long)Marshal.ReadInt32(pointer, 8));
                return width > 0 && height > 0 && width * height <= AppSettings.MaxImagePixels;
            }
            finally { NativeMethods.GlobalUnlock(handle); }
        }
        IntPtr bitmap = NativeMethods.GetClipboardData(NativeMethods.Bitmap);
        return NativeMethods.GetObjectW(bitmap, Marshal.SizeOf<NativeMethods.BITMAP>(), out var info) != 0
            && info.Width > 0 && info.Height > 0 && (long)info.Width * info.Height <= AppSettings.MaxImagePixels;
    }

    internal static CapturedClip? EncodePng(byte[] png)
    {
        if (!ValidPngDimensions(png)) return null;
        BitmapImage image = BitmapCodec.Decode(png);
        if ((long)image.PixelWidth * image.PixelHeight > AppSettings.MaxImagePixels) return null;
        // 保留原先统一 PNG 编码的行为，使不同剪贴板格式的去重结果保持一致。
        return EncodeImage(image);
    }

    internal static CapturedClip? EncodeImage(BitmapSource image)
    {
        using var output = new MemoryStream();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); encoder.Save(output);
        if (output.Length > AppSettings.MaxImageBytes) return null;
        double scale = Math.Min(1, 320.0 / Math.Max(image.PixelWidth, image.PixelHeight));
        BitmapSource thumb = scale < 1 ? new TransformedBitmap(image, new ScaleTransform(scale, scale)) : image;
        using var thumbOutput = new MemoryStream();
        var thumbEncoder = new PngBitmapEncoder(); thumbEncoder.Frames.Add(BitmapFrame.Create(thumb)); thumbEncoder.Save(thumbOutput);
        return new CapturedClip(ClipKind.Image, output.ToArray(), thumbOutput.ToArray(), image.PixelWidth, image.PixelHeight);
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true; generation++;
        Hotkeys.Dispose(); NativeMethods.RemoveClipboardFormatListener(source.Handle); source.RemoveHook(WindowProc); source.Dispose();
    }
}
