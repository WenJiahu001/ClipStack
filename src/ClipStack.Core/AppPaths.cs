using System;
using System.IO;
using System.Text;
using System.Text.Json;

namespace ClipStack.Core;

public sealed class AppPaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "history.db");
    public string Images => Path.Combine(Root, "images");
    public string Settings => Path.Combine(Root, "settings.json");
    public string Key => Path.Combine(Root, "fingerprint.key");
    public string Backups => Path.Combine(Root, "backups");
    public string Log => Path.Combine(Root, "diagnostics.log");
    public string PanelPosition => Path.Combine(Root, "panel-position.json");

    public AppPaths(string? root = null)
    {
        Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStack"));
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Images);
    }

    public string ImagePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || !name.EndsWith(".bin", StringComparison.Ordinal))
            throw new InvalidDataException("图片引用格式不正确。");
        return Path.Combine(Images, name);
    }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(Settings)) return new AppSettings();
        try { return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Settings)) ?? new AppSettings()).Normalize(); }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            LogError("settings-read", e);
            // 配置损坏时保留原文件，使用可用的默认值启动。
            try { File.Copy(Settings, Settings + ".invalid-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), false); }
            catch (IOException) { }
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings) => AtomicWrite(Settings,
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings.Normalize(), new JsonSerializerOptions { WriteIndented = true })));

    public PanelPlacement? LoadPanelPlacement()
    {
        try
        {
            if (!File.Exists(PanelPosition)) return null;
            PanelPlacement? placement = JsonSerializer.Deserialize<PanelPlacement>(File.ReadAllText(PanelPosition));
            return placement is { IsValid: true } ? placement : null;
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException)
        { LogError("panel-position-read", error); return null; }
    }

    public void SavePanelPlacement(PanelPlacement placement)
    {
        if (!placement.IsValid) throw new ArgumentException("窗口位置无效。", nameof(placement));
        AtomicWrite(PanelPosition, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(placement)));
    }

    public static void AtomicWrite(string path, byte[] data)
    {
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(data); file.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { try { File.Delete(temp); } catch (IOException) { } }
    }

    public void LogError(string operation, Exception error)
    {
        // 不记录异常消息、堆栈参数、文件路径或剪贴板正文。
        try
        {
            if (File.Exists(Log) && new FileInfo(Log).Length > 512 * 1024) File.Move(Log, Log + ".previous", true);
            File.AppendAllText(Log, $"{DateTimeOffset.UtcNow:O} {operation} {error.GetType().Name} 0x{error.HResult:X8}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
