using System;
using System.IO;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace ClipStack.Core;

internal static class DatabaseMigrator
{
    private const int SchemaVersion = 2;
    private const int ApplicationId = 1129072976;
    internal static void Initialize(SqliteConnection connection, AppPaths paths)
    {
        long version = ScalarLong(connection, "PRAGMA user_version;");
        long appId = ScalarLong(connection, "PRAGMA application_id;");
        if (version > SchemaVersion) throw new InvalidDataException("历史数据库来自较新版本，请使用较新版 ClipStack。");
        if (version != 0 && appId != ApplicationId) throw new InvalidDataException("数据文件不属于 ClipStack。");
        if (version == SchemaVersion) return;
        if (version == 0 && ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%';") != 0)
            throw new InvalidDataException("检测到未识别的数据结构，已停止写入以保留原始数据。");

        // 已有数据库在迁移前做一致性备份；新数据库没有用户数据。
        if (version > 0) BackupDatabase(connection, paths);
        using var transaction = connection.BeginTransaction();
        string[] migrations = { "001_initial.sql", "002_favorite_notes.sql" };
        for (int index = (int)version; index < SchemaVersion; index++)
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ClipStack.Core.Migrations." + migrations[index])
                ?? throw new InvalidOperationException("数据库迁移资源缺失。");
            using var reader = new StreamReader(stream);
            using var command = connection.CreateCommand();
            command.Transaction = transaction; command.CommandText = reader.ReadToEnd(); command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    private static void BackupDatabase(SqliteConnection connection, AppPaths paths)
    {
        Directory.CreateDirectory(paths.Backups);
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = Path.Combine(paths.Backups, $"history-{DateTime.UtcNow:yyyyMMddHHmmssfff}.db"), Pooling = false }.ToString());
        backup.Open();
        connection.BackupDatabase(backup);
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }
}