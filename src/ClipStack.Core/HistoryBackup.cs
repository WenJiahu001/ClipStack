using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace ClipStack.Core;

public static class HistoryBackup
{
    // 调用者须持有存储锁；完成标记最后写入，不把半成品当作可恢复备份。
    internal static string Create(SqliteConnection connection, AppPaths paths)
    {
        Directory.CreateDirectory(paths.Backups);
        string directory = Path.Combine(paths.Backups, $"history-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var backupPaths = new AppPaths(directory);
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPaths.Database, Pooling = false }.ToString()))
        { backup.Open(); connection.BackupDatabase(backup); }
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT image_file,thumbnail_file FROM clips WHERE kind=2;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                for (int column = 0; column < 2; column++)
                    if (!reader.IsDBNull(column))
                    {
                        string name = reader.GetString(column);
                        File.Copy(paths.ImagePath(name), backupPaths.ImagePath(name), true);
                    }
        }
        foreach (string file in new[] { paths.Key, paths.Settings, paths.PanelPosition })
            if (File.Exists(file)) File.Copy(file, Path.Combine(directory, Path.GetFileName(file)), true);
        var hashes = new Dictionary<string, string>();
        foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            using var input = File.OpenRead(file);
            hashes.Add(Path.GetRelativePath(directory, file), Convert.ToHexString(SHA256.HashData(input)));
        }
        AppPaths.AtomicWrite(Path.Combine(directory, "backup.json"), JsonSerializer.SerializeToUtf8Bytes(new
        { format = 1, createdUtc = DateTimeOffset.UtcNow, files = hashes }, new JsonSerializerOptions { WriteIndented = true }));
        return directory;
    }

    // 仅恢复到新目录，避免覆盖运行中的历史；恢复后仍使用原 Windows 用户解密。
    public static void Restore(string backupDirectory, string destination)
    {
        string source = Path.GetFullPath(backupDirectory), target = Path.GetFullPath(destination);
        if (Directory.Exists(target) || File.Exists(target)) throw new IOException("恢复目标必须是尚不存在的新目录。");
        using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(source, "backup.json")));
        if (manifest.RootElement.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("备份格式不支持。");
        var files = new List<(string Name, string Source)>();
        foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateObject())
        {
            string file = Path.GetFullPath(Path.Combine(source, entry.Name));
            if (!file.StartsWith(source.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份文件路径无效。");
            using var input = File.OpenRead(file);
            if (Convert.ToHexString(SHA256.HashData(input)) != entry.Value.GetString()) throw new InvalidDataException("备份校验失败。");
            files.Add((entry.Name, file));
        }
        if (!files.Exists(file => file.Name == "history.db") || !files.Exists(file => file.Name == "fingerprint.key"))
            throw new InvalidDataException("备份缺少数据库或密钥。");
        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            string output = Path.Combine(target, file.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.Copy(file.Source, output, false);
        }
    }
}
