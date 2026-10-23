using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClipStack.Core;
using Microsoft.Data.Sqlite;

namespace ClipStack.Tests;

internal static class Program
{
    private static int passed, failed;
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--performance-report") return PerformanceBenchmarks.Run(args[1]);
        Run("中文、表情、空白及换行完整保存", TextRoundTrip);
        Run("重复内容更新复制时间，使用不改变复制顺序", Deduplication);
        Run("同内容的不同类型分别保存", SeparateKinds);
        Run("文本、图片、缩略图均加密落盘", EncryptionAtRest);
        Run("搜索完整正文并限制列表摘要大小", FullTextSearch);
        Run("类型筛选与收藏筛选", Filtering);
        Run("收藏备注保存、搜索、清空及重复复制保留", FavoriteNotes);
        Run("备注边界、损坏隔离与收藏状态检查", NoteLimits);
        Run("备注按可见字符限长并规范化单行", NoteUnicodeLimits);
        Run("缓存字节上限、淘汰与覆盖", CacheLimits);
        Run("缓存查询与备注、删除、清理保持一致", CachedQueries);
        Run("取消搜索后连接与历史仍可正常使用", CancelledQuery);
        Run("旧数据库升级、备份及重启幂等", MigrateNotes);
        Run("首次靠右、位置保持及小屏幕回收", PanelPosition);
        Run("窗口位置持久化及损坏配置恢复", PanelPositionRecovery);
        Run("收藏满额时回滚，释放额度后继续保存", FavoriteQuotaRollback);
        Run("清理过期历史并保护收藏", Retention);
        Run("图片空间淘汰与收藏空间回滚", ImageQuota);
        Run("删除图片时同时清理缩略图", DeleteImage);
        Run("清理普通记录保留收藏，全部清理删除收藏", ClearHistory);
        Run("重启恢复与孤立文件回收", ReopenAndCollect);
        Run("损坏正文可查询但无法复制，保留原记录", CorruptPayload);
        Run("较新数据库拒绝写入并保持原文件", FutureSchema);
        Run("外来数据库拒绝写入", ForeignDatabase);
        Run("密钥损坏时停止启动且不覆盖原密钥", CorruptKey);
        Run("单条大小、空内容及像素限制", Limits);
        Run("配置规范化、保存及损坏恢复", SettingsRecovery);
        Run("来源进程含点名称保留完整进程名", DottedProcessName);
        Run("缺失图片文件不会删除历史记录", MissingImage);
        await RunAsync("多线程并发保存、读取及去重", ConcurrentWrites);
        Console.WriteLine($"\n通过 {passed} 项，失败 {failed} 项。");
        return failed == 0 ? 0 : 1;
    }

    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("[通过] " + name); }
        catch (Exception error) { failed++; Console.WriteLine("[失败] " + name + ": " + error); }
    }
    private static async Task RunAsync(string name, Func<Task> test)
    {
        try { await test(); passed++; Console.WriteLine("[通过] " + name); }
        catch (Exception error) { failed++; Console.WriteLine("[失败] " + name + ": " + error); }
    }
    private static void Assert(bool condition, string message = "断言失败") { if (!condition) throw new InvalidOperationException(message); }
    private static T Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T error) { return error; } throw new InvalidOperationException("预期异常未发生：" + typeof(T).Name); }
    private static CapturedClip Image(byte seed = 1, int length = 128) => new(ClipKind.Image, Enumerable.Repeat(seed, length).ToArray(), new byte[] { seed, 2, 3 }, 20, 30);

    private static void TextRoundTrip()
    {
        using var context = new Context();
        string text = "  中文 😀\r\n\tfunction test() {\n  return '保留缩进';\n}\n";
        SaveResult result = context.Store.Save(CapturedClip.FromText(text));
        ClipEntry entry = context.Store.Query().Single();
        Assert(result.Saved && entry.Text == text);
        Assert(Encoding.UTF8.GetString(context.Store.ReadContent(entry)) == text);
    }
    private static void Deduplication()
    {
        using var context = new Context();
        DateTimeOffset time = DateTimeOffset.UtcNow;
        SaveResult a = context.Store.Save(CapturedClip.FromText("A"), time.AddMinutes(-5));
        context.Store.Save(CapturedClip.FromText("B"), time.AddMinutes(-2));
        context.Store.MarkUsed(a.Id!);
        Assert(context.Store.Query()[0].Text == "B");
        SaveResult repeated = context.Store.Save(CapturedClip.FromText("A"), time);
        Assert(repeated.Id == a.Id && context.Store.GetStatistics().Count == 2);
        Assert(context.Store.Query()[0].UsedAt != null && context.Store.Query()[0].Text == "A");
    }
    private static void SeparateKinds()
    {
        using var context = new Context();
        context.Store.Save(CapturedClip.FromText("hello"));
        context.Store.Save(new CapturedClip(ClipKind.Image, Encoding.UTF8.GetBytes("hello"), null, 1, 1));
        Assert(context.Store.GetStatistics().Count == 2);
    }
    private static void EncryptionAtRest()
    {
        using var context = new Context();
        string secret = "绝不应该明文落盘-TopSecret-72631";
        context.Store.Save(CapturedClip.FromText(secret));
        string noteId = context.Store.Query().Single().Id;
        context.Store.SetFavorite(noteId, true);
        context.Store.SetNote(noteId, secret + "note");
        CapturedClip image = new(ClipKind.Image, Encoding.UTF8.GetBytes(secret + "image"), Encoding.UTF8.GetBytes(secret + "thumb"), 3, 2);
        context.Store.Save(image);
        context.Store.Dispose();
        byte[] needle = Encoding.UTF8.GetBytes(secret);
        foreach (string file in Directory.EnumerateFiles(context.Paths.Root, "*", SearchOption.AllDirectories))
            Assert(File.ReadAllBytes(file).AsSpan().IndexOf(needle) < 0, "发现明文：" + file);
    }
    private static void FullTextSearch()
    {
        using var context = new Context();
        string text = new string('a', 8000) + "中文关键词 %_';--";
        context.Store.Save(CapturedClip.FromText(text));
        Assert(context.Store.Query("中文关键词").Single().Text!.Length == 512);
        Assert(context.Store.Query("%_';--").Count == 1);
        Assert(context.Store.Query("AAA").Count == 1);
        Assert(context.Store.Query("无匹配").Count == 0);
    }
    private static void Filtering()
    {
        using var context = new Context();
        SaveResult text = context.Store.Save(CapturedClip.FromText("A")); context.Store.Save(Image());
        context.Store.SetFavorite(text.Id!, true);
        Assert(context.Store.Query(filter: HistoryFilter.Text).Single().Kind == ClipKind.Text);
        Assert(context.Store.Query(filter: HistoryFilter.Image).Single().Kind == ClipKind.Image);
        Assert(context.Store.Query(filter: HistoryFilter.Favorites).Single().Id == text.Id);
    }
    private static void FavoriteNotes()
    {
        using var context = new Context();
        string id = context.Store.Save(CapturedClip.FromText("原始正文\n  保留空白")).Id!;
        context.Store.SetFavorite(id, true);
        context.Store.SetNote(id, "  常用回复 😀\n工作备注  ");
        ClipEntry entry = context.Store.Query("工作备注").Single();
        Assert(entry.Note == "常用回复 😀 工作备注" && entry.Id == id);
        Assert(Encoding.UTF8.GetString(context.Store.ReadContent(entry)) == "原始正文\n  保留空白");
        Assert(HistorySearch.Query(new[] { entry }, "工作备注", HistoryFilter.Favorites).Count == 1);
        context.Store.Save(CapturedClip.FromText("原始正文\n  保留空白"));
        Assert(context.Store.Query().Single().Note == entry.Note);
        context.Store.SetFavorite(id, false);
        Assert(context.Store.Query("工作备注").Count == 0 && context.Store.Query().Single().Note == entry.Note);
        context.Store.SetFavorite(id, true);
        context.Store.Dispose();
        using var reopened = new HistoryStore(context.Paths, new AppSettings());
        Assert(reopened.Query("工作备注", HistoryFilter.Favorites).Single().Note == entry.Note);
        reopened.SetNote(id, "\n  ");
        Assert(reopened.Query().Single().Note == null && reopened.Query("工作备注").Count == 0);
        string image = reopened.Save(Image()).Id!;
        reopened.SetFavorite(image, true); reopened.SetNote(image, "设计截图");
        Assert(reopened.Query("设计截图", HistoryFilter.Image).Single().Id == image);
    }
    private static void NoteLimits()
    {
        using var context = new Context();
        string id = context.Store.Save(CapturedClip.FromText("原文")).Id!;
        Throws<InvalidOperationException>(() => context.Store.SetNote(id, "未收藏"));
        context.Store.SetFavorite(id, true);
        context.Store.SetNote(id, new string('注', AppSettings.MaxNoteLength));
        Throws<ArgumentException>(() => context.Store.SetNote(id, new string('注', AppSettings.MaxNoteLength + 1)));
        Assert(context.Store.Query().Single().Note!.Length == AppSettings.MaxNoteLength);
        context.Store.Dispose();
        Sql(context.Paths.Database, "UPDATE clips SET note_payload=X'010203';");
        using var reopened = new HistoryStore(context.Paths, new AppSettings());
        ClipEntry entry = reopened.Query().Single();
        Assert(entry.NoteUnreadable && !entry.Unreadable);
        Assert(Encoding.UTF8.GetString(reopened.ReadContent(entry)) == "原文");
        reopened.SetNote(id, "重新备注");
        Assert(!reopened.Query().Single().NoteUnreadable);
        reopened.Delete(id);
        Throws<InvalidOperationException>(() => reopened.SetNote(id, "已删除"));
    }
    private static void NoteUnicodeLimits()
    {
        using var context = new Context();
        string id = context.Store.Save(CapturedClip.FromText("原文不变")).Id!;
        context.Store.SetFavorite(id, true);
        string fifty = string.Concat(Enumerable.Repeat("👩‍💻", 50));
        context.Store.SetNote(id, fifty);
        Assert(FavoriteNote.Count(fifty) == 50 && context.Store.Query().Single().Note == fifty);
        Throws<ArgumentException>(() => context.Store.SetNote(id, fifty + "字"));
        Assert(FavoriteNote.Count("e\u0301") == 1);
        Assert(FavoriteNote.Prepare(" 甲\r\n乙\t丙 ") == "甲 乙 丙");
        Assert(context.Store.Query().Single().Note == fifty);
        Assert(Encoding.UTF8.GetString(context.Store.ReadContent(context.Store.Query().Single())) == "原文不变");
        context.Store.Dispose();
        string legacy = new string('旧', 100);
        string encrypted = Convert.ToHexString(UserProtection.Protect(Encoding.UTF8.GetBytes(legacy)));
        Sql(context.Paths.Database, "UPDATE clips SET note_payload=X'" + encrypted + "';");
        using var reopened = new HistoryStore(context.Paths, new AppSettings());
        Assert(reopened.Query().Single().Note == legacy, "升级后不能截断已有长备注");
    }
    private static void CacheLimits()
    {
        var cache = new BoundedCache<string>(10, 2);
        cache.Set("A", "a", 4); cache.Set("B", "b", 4);
        Assert(cache.TryGet("A", out _)); cache.Set("C", "c", 4);
        Assert(!cache.TryGet("B", out _) && cache.Bytes == 8 && cache.Count == 2);
        cache.Set("A", "replacement", 9);
        Assert(cache.Count == 1 && cache.Bytes == 9 && cache.TryGet("A", out var value) && value == "replacement");
        cache.Set("oversized", "too large", 11);
        Assert(cache.Count == 1 && cache.Bytes <= 10);
        cache.Remove("A"); Assert(cache.Bytes == 0 && cache.Count == 0);
        cache.Set("zero", "", 0); cache.Set("zero2", "", 0); cache.Set("zero3", "", 0);
        Assert(cache.Count == 2); cache.Clear(); Assert(cache.Count == 0 && cache.Bytes == 0);
    }
    private static void CachedQueries()
    {
        using var context = new Context();
        string text = new string('前', 8000) + "末尾缓存命中";
        string favorite = context.Store.Save(CapturedClip.FromText(text)).Id!;
        context.Store.SetFavorite(favorite, true); context.Store.SetNote(favorite, "旧备注");
        string ordinary = context.Store.Save(CapturedClip.FromText("待清理普通记录")).Id!;
        for (int i = 0; i < 3; i++) Assert(context.Store.Query("末尾缓存命中").Single().Id == favorite);
        context.Store.Query(); context.Store.SetNote(favorite, "新备注");
        Assert(context.Store.Query("旧备注").Count == 0 && context.Store.Query("新备注").Single().Id == favorite);
        context.Store.SetFavorite(favorite, false); Assert(context.Store.Query("新备注").Count == 0);
        context.Store.SetFavorite(favorite, true); context.Store.MarkUsed(favorite);
        Assert(context.Store.Query("新备注").Single().UsedAt != null);
        context.Store.Delete(ordinary); Assert(context.Store.Query("待清理").Count == 0);
        context.Store.Save(CapturedClip.FromText("再次普通记录")); context.Store.Query();
        context.Store.Clear(false); Assert(context.Store.Query().Single().Id == favorite);
        context.Store.Clear(true); Assert(context.Store.Query("末尾缓存命中").Count == 0);
    }
    private static void CancelledQuery()
    {
        using var context = new Context();
        context.Store.Save(CapturedClip.FromText("取消后仍可读取"));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Throws<OperationCanceledException>(() => context.Store.Query(cancellationToken: cancellation.Token));
        Assert(context.Store.Query().Single().Text == "取消后仍可读取");
        Assert(context.Store.Save(CapturedClip.FromText("取消后仍可写入")).Saved);
    }
    private static void MigrateNotes()
    {
        using var context = new Context();
        string id = context.Store.Save(CapturedClip.FromText("旧版历史")).Id!;
        context.Store.SetFavorite(id, true); context.Store.MarkUsed(id);
        DateTimeOffset copied = context.Store.Query().Single().CopiedAt;
        context.Store.Dispose();
        // 恢复为真实的 v1 表结构，再走生产迁移入口。
        Sql(context.Paths.Database, "ALTER TABLE clips DROP COLUMN note_payload; PRAGMA user_version=1;");
        using (var migrated = new HistoryStore(context.Paths, new AppSettings()))
        {
            ClipEntry entry = migrated.Query().Single();
            Assert(entry.Id == id && entry.Favorite && entry.UsedAt != null && entry.CopiedAt == copied && entry.Note == null);
            Assert(Encoding.UTF8.GetString(migrated.ReadContent(entry)) == "旧版历史");
            migrated.SetNote(id, "升级后的备注");
        }
        string backup = Directory.GetFiles(context.Paths.Backups, "*.db").Single();
        using (var connection = new SqliteConnection($"Data Source={backup};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version;";
            Assert(Convert.ToInt32(command.ExecuteScalar()) == 1);
            command.CommandText = "SELECT COUNT(*) FROM pragma_table_info('clips') WHERE name='note_payload';";
            Assert(Convert.ToInt32(command.ExecuteScalar()) == 0);
        }
        using var reopened = new HistoryStore(context.Paths, new AppSettings());
        Assert(reopened.Query().Single().Note == "升级后的备注");
        Assert(Directory.GetFiles(context.Paths.Backups, "*.db").Length == 1);
    }
    private static void PanelPosition()
    {
        var work = new PanelPlacement(0, 0, 1920, 1040);
        PanelPlacement first = PanelPlacement.Resolve(work, null);
        Assert(first == new PanelPlacement(1402, 190, 500, 660));
        var moved = new PanelPlacement(140, 80, 520, 700);
        Assert(PanelPlacement.Resolve(work, moved) == moved);
        Assert(PanelPlacement.Resolve(work, moved, mode: PanelPositionMode.ScreenCenter) == new PanelPlacement(700, 170, 520, 700));
        Assert(PanelPlacement.Resolve(work, moved, mode: PanelPositionMode.ScreenRight) == new PanelPlacement(1382, 170, 520, 700));
        Assert(PanelPlacement.Resolve(work, moved, mode: PanelPositionMode.FollowMouse, cursorX: 500, cursorY: 200) == new PanelPlacement(512, 212, 520, 700));
        Assert(PanelPlacement.Resolve(work, moved, mode: PanelPositionMode.FollowMouse, cursorX: 1900, cursorY: 1000) == new PanelPlacement(1368, 288, 520, 700));
        foreach (var screen in new[] { work, new PanelPlacement(-1920, -200, 1920, 1080), new PanelPlacement(0, 0, 600, 400) })
        foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
        foreach (PanelPositionMode mode in Enum.GetValues<PanelPositionMode>())
        foreach (bool bottomRight in new[] { false, true })
        {
            PanelPlacement placed = PanelPlacement.Resolve(screen, new PanelPlacement(5000, -8000, 900, 1100), scale, mode,
                bottomRight ? screen.Left + screen.Width - 1 : screen.Left, bottomRight ? screen.Top + screen.Height - 1 : screen.Top);
            Assert(placed.Left >= screen.Left && placed.Top >= screen.Top);
            Assert(placed.Left + placed.Width <= screen.Left + screen.Width && placed.Top + placed.Height <= screen.Top + screen.Height);
        }
        Assert(PanelPlacement.Resolve(new PanelPlacement(-1920, 0, 1920, 1080), null).Left < 0);
    }
    private static void PanelPositionRecovery()
    {
        using var context = new Context();
        Assert(context.Paths.LoadPanelPlacement() == null);
        var placement = new PanelPlacement(-1500, 120, 500, 660);
        context.Paths.SavePanelPlacement(placement);
        Assert(new AppPaths(context.Paths.Root).LoadPanelPlacement() == placement);
        context.Paths.SaveSettings(new AppSettings { MaxEntries = 200 });
        Assert(context.Paths.LoadPanelPlacement() == placement);
        File.WriteAllText(context.Paths.PanelPosition, "{invalid");
        Assert(context.Paths.LoadPanelPlacement() == null);
        File.WriteAllText(context.Paths.PanelPosition, "{\"Left\":0,\"Top\":0,\"Width\":-1,\"Height\":100}");
        Assert(context.Paths.LoadPanelPlacement() == null);
    }
    private static void FavoriteQuotaRollback()
    {
        using var context = new Context(new AppSettings { MaxEntries = 10 });
        string? first = null;
        for (int index = 0; index < 10; index++)
        {
            string id = context.Store.Save(CapturedClip.FromText("收藏" + index), DateTimeOffset.UtcNow.AddMinutes(index - 20)).Id!;
            first ??= id; context.Store.SetFavorite(id, true);
        }
        Assert(!context.Store.Save(CapturedClip.FromText("新记录")).Saved);
        Assert(context.Store.GetStatistics() == new StorageStatistics(10, 10, 0));
        context.Store.SetFavorite(first!, false);
        Assert(context.Store.Save(CapturedClip.FromText("新记录")).Saved);
        Assert(context.Store.Query().All(x => x.Id != first));
        Assert(context.Store.GetStatistics().Favorites == 9);
    }
    private static void Retention()
    {
        using var context = new Context();
        DateTimeOffset old = DateTimeOffset.UtcNow.AddDays(-40);
        string favorite = context.Store.Save(CapturedClip.FromText("旧收藏"), old).Id!; context.Store.SetFavorite(favorite, true);
        context.Store.Save(CapturedClip.FromText("旧记录"), old);
        context.Store.Save(CapturedClip.FromText("新记录"));
        Assert(context.Store.Query().Count == 2);
        Assert(context.Store.Query().Any(x => x.Id == favorite));
    }
    private static void ImageQuota()
    {
        using var context = new Context(new AppSettings { ImageBudgetMb = 20 });
        context.Store.Save(CapturedClip.FromText("文本应保留"), DateTimeOffset.UtcNow.AddDays(-1));
        context.Store.Save(Image(1, 8 * 1024 * 1024));
        context.Store.Save(Image(2, 8 * 1024 * 1024));
        context.Store.Save(Image(3, 8 * 1024 * 1024));
        Assert(context.Store.Query(filter: HistoryFilter.Image).Count == 2);
        Assert(context.Store.Query(filter: HistoryFilter.Text).Count == 1);
        foreach (var entry in context.Store.Query(filter: HistoryFilter.Image)) context.Store.SetFavorite(entry.Id, true);
        int files = Directory.GetFiles(context.Paths.Images).Length;
        Assert(!context.Store.Save(Image(4, 8 * 1024 * 1024)).Saved);
        Assert(Directory.GetFiles(context.Paths.Images).Length == files && context.Store.GetStatistics().Count == 3);
    }
    private static void DeleteImage()
    {
        using var context = new Context();
        string id = context.Store.Save(Image()).Id!;
        Assert(Directory.GetFiles(context.Paths.Images).Length == 2);
        context.Store.Delete(id); Assert(Directory.GetFiles(context.Paths.Images).Length == 0);
    }
    private static void ClearHistory()
    {
        using var context = new Context();
        string id = context.Store.Save(CapturedClip.FromText("收藏")).Id!; context.Store.SetFavorite(id, true);
        context.Store.Save(Image()); context.Store.Clear(false);
        Assert(context.Store.Query().Single().Id == id && Directory.GetFiles(context.Paths.Images).Length == 0);
        context.Store.Clear(true); Assert(context.Store.GetStatistics().Count == 0);
    }
    private static void ReopenAndCollect()
    {
        using var context = new Context();
        context.Store.Save(CapturedClip.FromText("重启保留")); context.Store.Save(Image());
        context.Store.Dispose();
        File.WriteAllText(Path.Combine(context.Paths.Images, "orphan.bin"), "orphan");
        File.WriteAllText(Path.Combine(context.Paths.Images, "orphan.bin.work.tmp"), "temp");
        using var reopened = new HistoryStore(context.Paths, new AppSettings());
        Assert(reopened.GetStatistics().Count == 2 && Directory.GetFiles(context.Paths.Images).Length == 2);
    }
    private static void CorruptPayload()
    {
        using var context = new Context(); context.Store.Save(CapturedClip.FromText("A")); context.Store.Dispose();
        Sql(context.Paths.Database, "UPDATE clips SET text_payload=X'010203';");
        using var reopened = new HistoryStore(context.Paths, new AppSettings());
        ClipEntry entry = reopened.Query().Single(); Assert(entry.Unreadable);
        Throws<CryptographicException>(() => reopened.ReadContent(entry)); Assert(reopened.GetStatistics().Count == 1);
    }
    private static void FutureSchema()
    {
        using var context = new Context(); context.Store.Save(CapturedClip.FromText("保留")); context.Store.Dispose();
        Sql(context.Paths.Database, "PRAGMA user_version=999;");
        byte[] before = File.ReadAllBytes(context.Paths.Database);
        Throws<InvalidDataException>(() => { using var ignored = new HistoryStore(context.Paths, new AppSettings()); });
        Assert(before.SequenceEqual(File.ReadAllBytes(context.Paths.Database)));
    }
    private static void ForeignDatabase()
    {
        using var context = new Context(); context.Store.Dispose();
        Sql(context.Paths.Database, "PRAGMA user_version=0; PRAGMA application_id=0;");
        Throws<InvalidDataException>(() => { using var ignored = new HistoryStore(context.Paths, new AppSettings()); });
    }
    private static void CorruptKey()
    {
        using var context = new Context(); context.Store.Dispose();
        byte[] corrupt = { 1, 2, 3 }; File.WriteAllBytes(context.Paths.Key, corrupt);
        Throws<CryptographicException>(() => { using var ignored = new HistoryStore(context.Paths, new AppSettings()); });
        Assert(File.ReadAllBytes(context.Paths.Key).SequenceEqual(corrupt));
    }
    private static void Limits()
    {
        using var context = new Context();
        Assert(!context.Store.Save(CapturedClip.FromText("")).Saved);
        Assert(context.Store.Save(CapturedClip.FromText(new string('a', AppSettings.MaxTextBytes))).Saved);
        Assert(!context.Store.Save(CapturedClip.FromText(new string('a', AppSettings.MaxTextBytes + 1))).Saved);
        Assert(!context.Store.Save(new CapturedClip(ClipKind.Image, new byte[] { 1 }, null, 10000, 10000)).Saved);
        Assert(!context.Store.Save(Image(1, AppSettings.MaxImageBytes + 1)).Saved);
    }
    private static void SettingsRecovery()
    {
        using var context = new Context();
        AppSettings normalized = new AppSettings { MaxEntries = -1, RetentionDays = 9999, ImageBudgetMb = 0, ExcludedProcesses = new[] { " KeePass.exe ", "keepass", "" } }.Normalize();
        Assert(normalized.MaxEntries == 10 && normalized.RetentionDays == 365 && normalized.ImageBudgetMb == 20);
        Assert(normalized.ExcludedProcesses.SequenceEqual(new[] { "keepass" }));
        context.Paths.SaveSettings(normalized); Assert(context.Paths.LoadSettings().MaxEntries == 10);
        foreach (DisplayDensity density in Enum.GetValues<DisplayDensity>())
        {
            context.Paths.SaveSettings(normalized with { MainWindowDensity = density });
            Assert(context.Paths.LoadSettings().MainWindowDensity == density);
        }
        Assert((normalized with { MainWindowDensity = (DisplayDensity)99 }).Normalize().MainWindowDensity == DisplayDensity.Standard);
        foreach (PanelPositionMode mode in Enum.GetValues<PanelPositionMode>())
        {
            context.Paths.SaveSettings(normalized with { WindowPosition = mode, ResetListOnActivate = false, ShowTrayIcon = false });
            Assert(context.Paths.LoadSettings() is { ResetListOnActivate: false, ShowTrayIcon: false } loaded && loaded.WindowPosition == mode);
        }
        Assert((normalized with { WindowPosition = (PanelPositionMode)99 }).Normalize().WindowPosition == PanelPositionMode.Remember);
        File.WriteAllText(context.Paths.Settings, "{\"MaxEntries\":250,\"ShowTaskbarIcon\":false}");
        Assert(context.Paths.LoadSettings() is { MaxEntries: 250, MainWindowDensity: DisplayDensity.Standard,
            WindowPosition: PanelPositionMode.Remember, ResetListOnActivate: true, ShowTrayIcon: true });
        File.WriteAllText(context.Paths.Settings, "{invalid-json");
        Assert(context.Paths.LoadSettings().MaxEntries == 1000);
        Assert(Directory.GetFiles(context.Paths.Root, "settings.json.invalid-*").Length == 1);
    }
    private static void MissingImage()
    {
        using var context = new Context(); context.Store.Save(Image());
        ClipEntry entry = context.Store.Query().Single(); File.Delete(context.Paths.ImagePath(entry.ImageFile!));
        Throws<InvalidDataException>(() => context.Store.ReadContent(entry)); Assert(context.Store.GetStatistics().Count == 1);
    }
    private static void DottedProcessName()
    {
        var settings = new AppSettings { ExcludedProcesses = new[] { "My.Company.App.exe", "my.company.app" } }.Normalize();
        Assert(settings.ExcludedProcesses.SequenceEqual(new[] { "my.company.app" }));
    }
    private static async Task ConcurrentWrites()
    {
        using var context = new Context();
        await Task.WhenAll(Enumerable.Range(0, 80).Select(index => Task.Run(() =>
        {
            context.Store.Save(CapturedClip.FromText("并发" + index % 40));
            context.Store.Query("并发");
        })));
        Assert(context.Store.GetStatistics().Count == 40);
    }
    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    private sealed class Context : IDisposable
    {
        private readonly string testRoot = Path.Combine(Path.GetTempPath(), "ClipStack.Tests", Guid.NewGuid().ToString("N"));
        public AppPaths Paths { get; }
        public HistoryStore Store { get; }
        public Context(AppSettings? settings = null) { Paths = new AppPaths(testRoot); Store = new HistoryStore(Paths, settings ?? new AppSettings()); }
        public void Dispose()
        {
            Store.Dispose();
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ClipStack.Tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(testRoot).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("拒绝清理测试目录之外的路径。");
            Directory.Delete(testRoot, true);
        }
    }
}
