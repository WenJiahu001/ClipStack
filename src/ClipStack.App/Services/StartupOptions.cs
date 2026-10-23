using System;
using System.Linq;

namespace ClipStack.App.Services;

internal sealed record StartupOptions(string? DataDirectory, bool Background, string? PasteTarget, string? Smoke,
    string? Preview, string? SettingsPreview, string? Integration, string? PanelTest, string? PerformanceTest)
{
    internal bool IsDiagnostic => PasteTarget != null || Smoke != null || Preview != null || SettingsPreview != null || Integration != null || PanelTest != null || PerformanceTest != null;
    internal static StartupOptions Parse(string[] args) => new(Read(args, "--data-dir"), args.Contains("--background", StringComparer.OrdinalIgnoreCase),
        Read(args, "--paste-target"), Read(args, "--smoke-test"), Read(args, "--preview"), Read(args, "--preview-settings"), Read(args, "--integration-test"), Read(args, "--panel-test"), Read(args, "--performance-test"));

    private static string? Read(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        if (index < 0) return null;
        if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("启动参数缺少路径：" + name);
        return args[index + 1];
    }
}
