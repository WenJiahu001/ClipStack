using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using ClipStack.Core;

namespace ClipStack.App.Platform;

internal sealed class WindowPlacementController
{
    private readonly Window window;
    private readonly AppPaths paths;
    private readonly Func<bool> canRemember;
    private readonly Action<string> showStatus;
    private PanelPlacement? savedPlacement;
    internal PanelPositionMode Mode { get; set; }

    internal WindowPlacementController(Window window, AppPaths paths, Func<bool> canRemember, Action<string> showStatus)
    {
        this.window = window; this.paths = paths; this.canRemember = canRemember; this.showStatus = showStatus;
        savedPlacement = paths.LoadPanelPlacement();
        window.SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(window).Handle)?.AddHook(OnWindowMessage);
    }
    internal void Place(IntPtr pasteTarget)
    {
        IntPtr monitor = NativeMethods.MonitorFromWindow(pasteTarget, 2);
        bool hasCursor = NativeMethods.GetCursorPos(out var cursor);
        if (Mode == PanelPositionMode.FollowMouse && hasCursor)
            monitor = NativeMethods.MonitorFromPoint(cursor, 2);
        else if (Mode == PanelPositionMode.Remember && savedPlacement is { IsValid: true } saved)
        {
            var rect = new NativeMethods.RECT { Left = saved.Left, Top = saved.Top, Right = saved.Left + saved.Width, Bottom = saved.Top + saved.Height };
            monitor = NativeMethods.MonitorFromRect(ref rect, 2);
        }
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        double scale = NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : VisualTreeHelper.GetDpi(window).DpiScaleX;
        var work = new PanelPlacement(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
        PanelPlacement target = PanelPlacement.Resolve(work, savedPlacement, scale, Mode,
            hasCursor ? cursor.X : null, hasCursor ? cursor.Y : null);
        window.MinWidth = Math.Min(380, target.Width / scale); window.MinHeight = Math.Min(400, target.Height / scale);
        // 使用物理像素放置；不带 SWP_SHOWWINDOW，由调用方在定位后统一显示。
        NativeMethods.SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
            target.Left, target.Top, target.Width, target.Height, 0x0014);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // 原生移动/调整大小循环结束后才保存，程序呼出定位不会覆盖用户位置。
        if (message == 0x0232 && canRemember() && window.WindowState == WindowState.Normal) Remember();
        return IntPtr.Zero;
    }

    internal void Remember()
    {
        if (!NativeMethods.GetWindowRect(new WindowInteropHelper(window).Handle, out var rect)) return;
        var placement = new PanelPlacement(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        if (!placement.IsValid) return;
        // 其他定位方式允许调整大小，但不覆盖「记住位置」的坐标。
        if (Mode != PanelPositionMode.Remember)
        {
            PanelPlacement remembered = savedPlacement ?? DefaultRememberedPlacement(hwnd: new WindowInteropHelper(window).Handle, placement);
            placement = placement with { Left = remembered.Left, Top = remembered.Top };
        }
        savedPlacement = placement;
        try { paths.SavePanelPlacement(placement); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { paths.LogError("panel-position-save", error); showStatus("本次已记住位置，但无法写入磁盘。"); }
    }

    private static PanelPlacement DefaultRememberedPlacement(IntPtr hwnd, PanelPlacement size)
    {
        var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
        if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(hwnd, 2), ref info)) return size;
        double scale = NativeMethods.GetDpiForMonitor(NativeMethods.MonitorFromWindow(hwnd, 2), 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        return PanelPlacement.Resolve(new PanelPlacement(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top),
            size, scale, PanelPositionMode.ScreenRight);
    }

}
