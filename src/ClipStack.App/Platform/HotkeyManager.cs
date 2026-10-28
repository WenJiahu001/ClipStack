using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace ClipStack.App.Platform;

internal sealed class HotkeyManager : IDisposable
{
    private readonly IntPtr hwnd;
    private int activeId = 1;
    public string ActiveHotkey { get; private set; } = "未注册";
    public HotkeyManager(IntPtr hwnd) => this.hwnd = hwnd;

    public bool Register(string text)
    {
        if (string.Equals(text, ActiveHotkey, StringComparison.OrdinalIgnoreCase)) return true;
        if (!TryParse(text, out uint modifiers, out uint key)) return false;
        // 两个注册槽交替使用，新绑定成功后才释放旧绑定。
        int nextId = activeId == 1 ? 2 : 1;
        if (!NativeMethods.RegisterHotKey(hwnd, nextId, modifiers | 0x4000, key)) return false;
        NativeMethods.UnregisterHotKey(hwnd, activeId);
        activeId = nextId;
        ActiveHotkey = text;
        return true;
    }

    public static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0; key = 0;
        string[] parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string part in parts)
        {
            if (!seen.Add(part)) return false;
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= 2; break;
                case "alt": modifiers |= 1; break;
                case "shift": modifiers |= 4; break;
                case "win": modifiers |= 8; break;
                default:
                    if (key != 0 || !Enum.TryParse(part, true, out Key parsed) || parsed == Key.None) return false;
                    key = (uint)KeyInterop.VirtualKeyFromKey(parsed);
                    break;
            }
        }
        return modifiers != 0 && key != 0;
    }

    internal void Unregister()
    {
        NativeMethods.UnregisterHotKey(hwnd, 1); NativeMethods.UnregisterHotKey(hwnd, 2);
        ActiveHotkey = "未注册";
    }
    public void Dispose() => Unregister();
}
