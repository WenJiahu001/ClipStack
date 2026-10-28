using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ClipStack.App.Platform;

internal static class NativeMethods
{
    internal const int ClipboardUpdate = 0x031D, HotkeyMessage = 0x0312;
    internal const uint UnicodeText = 13, Dib = 8, DibV5 = 17, Bitmap = 2;
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseClipboard();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")] internal static extern IntPtr GetClipboardOwner();
    [DllImport("kernel32.dll")] internal static extern UIntPtr GlobalSize(IntPtr memory);
    [DllImport("kernel32.dll")] internal static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AllowSetForegroundWindow(uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] internal static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] internal static extern int GetObjectW(IntPtr handle, int size, out BITMAP bitmap);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromPoint(POINT point, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
    [DllImport("shcore.dll")] internal static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)] internal struct BITMAP
    { public int Type, Width, Height, WidthBytes; public ushort Planes, BitsPixel; public IntPtr Bits; }
    [StructLayout(LayoutKind.Sequential)] internal struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] internal struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] internal struct MONITORINFO { public int Size; public RECT Monitor, Work; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] internal struct INPUT { public uint Type; public INPUTUNION Data; }
    [StructLayout(LayoutKind.Explicit)] internal struct INPUTUNION
    {
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
        [FieldOffset(0)] public MOUSEINPUT Mouse;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct KEYBDINPUT { public ushort Key, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] internal struct MOUSEINPUT { public int X, Y; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
}
