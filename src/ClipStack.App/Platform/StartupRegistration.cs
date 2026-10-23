using System;
using Microsoft.Win32;

namespace ClipStack.App.Platform;

internal static class StartupRegistration
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal static void SetEnabled(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath, true);
        if (enabled)
        {
            string executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位程序路径。");
            key.SetValue("ClipStack", "\"" + executable + "\" --background", RegistryValueKind.String);
        }
        else key.DeleteValue("ClipStack", false);
    }
}
