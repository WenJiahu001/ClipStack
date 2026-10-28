using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Data.Sqlite;

namespace ClipStack.Core;

public sealed class HistoryStore : IDisposable
{
    private const int ListTextLimit = 512;
    private readonly object gate = new();
    private readonly object queryGate = new(), cacheGate = new();
    private long contentRevision;
    private readonly AppPaths paths;
    private readonly UserProtection protection;
    private readonly SqliteConnection connection;
    private volatile bool disposed;
    private sealed record DecodedSummary(string? Text, bool Truncated, bool Unreadable, string? Note, bool NoteUnreadable);
    private readonly BoundedCache<DecodedSummary> summaries = new(16 * 1024 * 1024, 10000);
    private readonly BoundedCache<string> fullTexts = new(16 * 1024 * 1024, 4096);
    public AppSettings Settings { get; private set; }

    public HistoryStore(AppPaths paths, AppSettings settings)
    {
        this.paths = paths;
        Settings = settings.Normalize();
        protection = new UserProtection(paths);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.Database, Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false, DefaultTimeout = 3
        }.ToString());
        try
        {
            connection.Open();
            DatabaseMigrator.Initialize(connection, paths);
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON;");
            PruneAndCollect();
        }
        catch { connection.Dispose(); protection.Dispose(); throw; }
    }

    public SaveResult Save(CapturedClip clip, DateTimeOffset? timestamp = null)
    {
        lock (gate)
        {
            EnsureOpen();
            string? validation = Validate(clip);
            if (validation != null) return new SaveResult(false, validation);
            long now = (timestamp ?? clip.CopiedAt ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds();
            string fingerprint = protection.Fingerprint(clip);
            using (var existing = Command("SELECT id FROM clips WHERE fingerprint=$hash;", null, ("$hash", fingerprint)))
            {
                string? id = existing.ExecuteScalar() as string;
                if (id != null)
                {
                    RepairDuplicate(id, clip);
                    Promote(id, now, used: false);
                    return new SaveResult(true, "已更新重复内容的复制时间", id);
                }
            }

            string newId = Guid.NewGuid().ToString("N");
            byte[] payload = UserProtection.Protect(clip.Content);
            byte[]? thumb = clip.Thumbnail == null ? null : UserProtection.Protect(clip.Thumbnail);
            long bytes = payload.LongLength + (thumb?.LongLength ?? 0);
            string? imageName = clip.Kind == ClipKind.Image ? newId + ".bin" : null;
            string? thumbName = clip.Kind == ClipKind.Image && thumb != null ? newId + ".thumb.bin" : null;
            var deleted = new List<string>();
            bool committed = false;
            try
            {
                using var transaction = connection.BeginTransaction();
                RemoveExpired(now, transaction, deleted);
                if (!MakeRoom(clip.Kind == ClipKind.Image ? bytes : 0, 1, transaction, deleted))
                    return new SaveResult(false, "收藏已占满保存额度，请删除部分收藏或增大保存上限。");

                if (imageName != null) AppPaths.AtomicWrite(paths.ImagePath(imageName), payload);
                if (thumbName != null) AppPaths.AtomicWrite(paths.ImagePath(thumbName), thumb!);
                Execute("""
                    INSERT INTO clips(id,kind,fingerprint,text_payload,image_file,thumbnail_file,size_bytes,width,height,created_utc,copied_utc,sort_order)
                    VALUES($id,$kind,$hash,$text,$image,$thumb,$bytes,$w,$h,$now,$now,(SELECT COALESCE(MAX(sort_order),0)+1 FROM clips));
                    """, transaction, ("$id", newId), ("$kind", (int)clip.Kind), ("$hash", fingerprint),
                    ("$text", clip.Kind == ClipKind.Text ? payload : null), ("$image", imageName), ("$thumb", thumbName),
                    ("$bytes", bytes), ("$w", clip.Width), ("$h", clip.Height), ("$now", now));
                transaction.Commit();
                committed = true;
                DeleteFiles(deleted);
                return new SaveResult(true, clip.Kind == ClipKind.Text ? "已保存文本" : "已保存图片", newId);
            }
            finally
            {
                if (!committed)
                {
                    if (imageName != null) DeleteFiles(new[] { imageName });
                    if (thumbName != null) DeleteFiles(new[] { thumbName });
                }
            }
        }
    }

    private static string? Validate(CapturedClip clip)
    {
        if (clip.Content.Length == 0) return "空内容已跳过。";
        if (clip.Kind == ClipKind.Text)
            return clip.Content.Length > AppSettings.MaxTextBytes ? "文本超过 1 MiB，已跳过。" : null;
        if (clip.Kind != ClipKind.Image) return "暂不支持此内容类型。";
        if (clip.Content.Length > AppSettings.MaxImageBytes) return "图片超过 20 MiB，已跳过。";
        if (clip.Width <= 0 || clip.Height <= 0 || (long)clip.Width * clip.Height > AppSettings.MaxImagePixels)
            return "图片尺寸超过限制，已跳过。";
        if (clip.Thumbnail is { Length: > 1024 * 1024 }) return "图片缩略图超过限制，已跳过。";
        return null;
    }

    public IReadOnlyList<ClipEntry> Query(string? query = null, HistoryFilter filter = HistoryFilter.All, CancellationToken cancellationToken = default, int limit = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        cancellationToken.ThrowIfCancellationRequested();
        // 查询独占自己的只读连接；长文本解密不再占用写入连接和存储锁。
        lock (queryGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureOpen();
            long revision = Interlocked.Read(ref contentRevision);
            using var readConnection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = paths.Database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 3 }.ToString());
            readConnection.Open();
            string term = (query ?? "").Trim();
            var result = new List<ClipEntry>();
            string predicate = filter switch
            {
                HistoryFilter.Text => " WHERE kind=1", HistoryFilter.Image => " WHERE kind=2",
                HistoryFilter.Favorites => " WHERE favorite=1", _ => ""
            };
            using var command = readConnection.CreateCommand();
            command.CommandText = "SELECT id,kind,text_payload,image_file,thumbnail_file,size_bytes,width,height,created_utc,copied_utc,used_utc,favorite,note_payload FROM clips" + predicate + " ORDER BY sort_order DESC,id DESC;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                string id = reader.GetString(0);
                var kind = (ClipKind)reader.GetInt32(1);
                bool favorite = reader.GetBoolean(11);
                DecodedSummary? summary;
                lock (cacheGate) { if (revision != contentRevision || !summaries.TryGet(id, out summary)) summary = null; }
                if (summary == null)
                {
                    string? text = null, note = null;
                    bool unreadable = false, noteUnreadable = false, truncated = false;
                    if (!reader.IsDBNull(12))
                    {
                        try { note = Encoding.UTF8.GetString(UserProtection.Unprotect((byte[])reader[12])); }
                        catch (CryptographicException) { noteUnreadable = true; }
                    }
                    if (kind == ClipKind.Text)
                    {
                        try
                        {
                            text = DecodeText(reader, id, revision);
                            truncated = text.Length > ListTextLimit;
                            if (truncated)
                            {
                                int length = char.IsHighSurrogate(text[ListTextLimit - 1]) && char.IsLowSurrogate(text[ListTextLimit]) ? ListTextLimit - 1 : ListTextLimit;
                                text = text[..length];
                            }
                        }
                        catch (CryptographicException) { unreadable = true; }
                    }
                    summary = new DecodedSummary(text, truncated, unreadable, note, noteUnreadable);
                    lock (cacheGate)
                        if (!disposed && revision == contentRevision) summaries.Set(id, summary, 192L + 2L * ((text?.Length ?? 0) + (note?.Length ?? 0)));
                }
                var entry = new ClipEntry(id, kind, summary.Text,
                    reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
                    reader.GetInt64(5), reader.GetInt32(6), reader.GetInt32(7),
                    DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(8)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(9)),
                    reader.IsDBNull(10) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(10)), favorite,
                    summary.Unreadable, summary.Note, summary.NoteUnreadable);
                if (term.Length != 0 && !entry.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
                    && !(favorite && summary.Note?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))
                {
                    if (kind != ClipKind.Text || summary.Unreadable || !summary.Truncated
                        || !DecodeText(reader, id, revision).Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
                }
                result.Add(entry);
                if (result.Count >= limit) break;
            }
            return result;
        }
    }

    private string DecodeText(SqliteDataReader reader, string id, long revision)
    {
        lock (cacheGate)
            if (revision == contentRevision && fullTexts.TryGet(id, out var cached)) return cached;
        string text = Encoding.UTF8.GetString(UserProtection.Unprotect((byte[])reader[2]));
        // 小文本已经保留在摘要里；仅缓存全文搜索需要的长文本，且有容量上限。
        lock (cacheGate)
            if (!disposed && revision == contentRevision && text.Length > ListTextLimit) fullTexts.Set(id, text, text.Length * 2L + 128);
        return text;
    }

    public byte[] ReadContent(ClipEntry entry, bool thumbnail = false) => ReadContent(entry.Id, thumbnail);
    private byte[] ReadContent(string id, bool thumbnail = false)
    {
        lock (gate)
        {
            EnsureOpen();
            using var command = Command("SELECT kind,text_payload,image_file,thumbnail_file FROM clips WHERE id=$id;", null, ("$id", id));
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new FileNotFoundException("这条记录已被删除。");
            if ((ClipKind)reader.GetInt32(0) == ClipKind.Text) return UserProtection.Unprotect((byte[])reader[1]);
            int column = thumbnail && !reader.IsDBNull(3) ? 3 : 2;
            string path = paths.ImagePath(reader.GetString(column));
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > AppSettings.MaxImageBytes + 1024 * 1024)
                throw new InvalidDataException("图片文件缺失或大小异常，记录已保留。");
            return UserProtection.Unprotect(File.ReadAllBytes(path));
        }
    }

    public void SetFavorite(string id, bool favorite)
    {
        lock (gate) { EnsureOpen(); Execute("UPDATE clips SET favorite=$value WHERE id=$id;", null, ("$value", favorite ? 1 : 0), ("$id", id)); }
    }

    public void SetNote(string id, string? note)
    {
        string normalized = FavoriteNote.Prepare(note);
        lock (gate)
        {
            EnsureOpen();
            byte[]? payload = normalized.Length == 0 ? null : UserProtection.Protect(Encoding.UTF8.GetBytes(normalized));
            // 条件更新与收藏检查在同一语句中完成，避免删除或取消收藏后误写。
            using var command = Command("UPDATE clips SET note_payload=$note WHERE id=$id AND favorite=1;", null,
                ("$note", payload), ("$id", id));
            if (command.ExecuteNonQuery() != 1) throw new InvalidOperationException("这条记录已删除或已取消收藏，请重新选择。");
            Invalidate(id);
        }
    }

    public void MarkUsed(string id)
    {
        lock (gate) { EnsureOpen(); Promote(id, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), used: true); }
    }

    private void Promote(string id, long now, bool used)
    {
        // 序号单调递增，真实时间只负责展示和保留期限。
        Execute("""
            UPDATE clips SET copied_utc=$now,sort_order=(SELECT COALESCE(MAX(sort_order),0)+1 FROM clips),
                used_utc=CASE WHEN $used=1 THEN $now ELSE used_utc END WHERE id=$id;
            """, null, ("$now", now), ("$used", used ? 1 : 0), ("$id", id));
    }

    public void Delete(string id) => DeleteWhere("id=$id", ("$id", id));
    public void Clear(bool includeFavorites) => DeleteWhere(includeFavorites ? "1=1" : "favorite=0");

    private void DeleteWhere(string condition, params (string Name, object? Value)[] parameters)
    {
        lock (gate)
        {
            EnsureOpen();
            var files = new List<string>();
            using var transaction = connection.BeginTransaction();
            CollectFiles(condition, transaction, files, parameters);
            InvalidateWhere(condition, transaction, parameters);
            Execute("DELETE FROM clips WHERE " + condition + ";", transaction, parameters);
            transaction.Commit();
            DeleteFiles(files);
        }
    }

    public StorageStatistics GetStatistics()
    {
        lock (gate)
        {
            EnsureOpen();
            using var command = Command("SELECT COUNT(*),COALESCE(SUM(favorite),0),COALESCE(SUM(CASE WHEN kind=2 THEN size_bytes ELSE 0 END),0) FROM clips;");
            using var reader = command.ExecuteReader(); reader.Read();
            return new StorageStatistics(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt64(2));
        }
    }

    public void UpdateSettings(AppSettings settings)
    {
        lock (gate)
        {
            EnsureOpen();
            AppSettings normalized = settings.Normalize();
            paths.SaveSettings(normalized);
            Settings = normalized;
        }
    }

    public void PruneAndCollect()
    {
        lock (gate)
        {
            EnsureOpen();
            var files = new List<string>();
            using (var transaction = connection.BeginTransaction())
            {
                RemoveExpired(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), transaction, files);
                MakeRoom(0, 0, transaction, files);
                transaction.Commit();
            }
            DeleteFiles(files);
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var command = Command("SELECT image_file,thumbnail_file FROM clips WHERE kind=2;"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) { referenced.Add(reader.GetString(0)); if (!reader.IsDBNull(1)) referenced.Add(reader.GetString(1)); }
            foreach (string file in Directory.EnumerateFiles(paths.Images))
                if (!referenced.Contains(Path.GetFileName(file)) && (file.EndsWith(".bin", StringComparison.Ordinal) || file.EndsWith(".tmp", StringComparison.Ordinal)))
                    DeleteFiles(new[] { Path.GetFileName(file) });
        }
    }

    private void RemoveExpired(long now, SqliteTransaction transaction, List<string> deleted)
    {
        long cutoff = now - (long)Settings.RetentionDays * 86_400_000;
        CollectFiles("favorite=0 AND copied_utc < $cutoff", transaction, deleted, ("$cutoff", cutoff));
        InvalidateWhere("favorite=0 AND copied_utc < $cutoff", transaction, ("$cutoff", cutoff));
        Execute("DELETE FROM clips WHERE favorite=0 AND copied_utc < $cutoff;", transaction, ("$cutoff", cutoff));
    }

    private bool MakeRoom(long incomingBytes, int incomingCount, SqliteTransaction transaction, List<string> deleted)
    {
        long count, imageBytes;
        using (var totals = Command("SELECT COUNT(*),COALESCE(SUM(CASE WHEN kind=2 THEN size_bytes ELSE 0 END),0) FROM clips;", transaction))
        using (var reader = totals.ExecuteReader()) { reader.Read(); count = reader.GetInt64(0); imageBytes = reader.GetInt64(1); }
        long budget = (long)Settings.ImageBudgetMb * 1024 * 1024;
        if (count + incomingCount <= Settings.MaxEntries && imageBytes + incomingBytes <= budget) return true;
        var candidates = new List<(string Id, int Kind, long Size)>();
        bool imagePressure = imageBytes + incomingBytes > budget;
        using (var command = Command("SELECT id,kind,size_bytes FROM clips WHERE favorite=0"
            + (imagePressure && count + incomingCount <= Settings.MaxEntries ? " AND kind=2" : "")
            + " ORDER BY sort_order ASC LIMIT $limit;", transaction,
            ("$limit", imagePressure ? -1L : count + incomingCount - Settings.MaxEntries)))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) candidates.Add((reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        foreach (var candidate in candidates)
        {
            if (count + incomingCount <= Settings.MaxEntries && imageBytes + incomingBytes <= budget) return true;
            // 仅图片超额时无需牺牲不占图片预算的文本。
            if (count + incomingCount <= Settings.MaxEntries && candidate.Kind != (int)ClipKind.Image) continue;
            CollectFiles("id=$id", transaction, deleted, ("$id", candidate.Id));
            Execute("DELETE FROM clips WHERE id=$id;", transaction, ("$id", candidate.Id));
            Invalidate(candidate.Id);
            count--;
            if (candidate.Kind == (int)ClipKind.Image) imageBytes -= candidate.Size;
        }
        return count + incomingCount <= Settings.MaxEntries && imageBytes + incomingBytes <= budget;
    }

    private void CollectFiles(string condition, SqliteTransaction transaction, List<string> files, params (string Name, object? Value)[] parameters)
    {
        using var command = Command("SELECT image_file,thumbnail_file FROM clips WHERE " + condition + ";", transaction, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read()) { if (!reader.IsDBNull(0)) files.Add(reader.GetString(0)); if (!reader.IsDBNull(1)) files.Add(reader.GetString(1)); }
    }

    private void InvalidateWhere(string condition, SqliteTransaction transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = Command("SELECT id FROM clips WHERE " + condition + ";", transaction, parameters);
        using var reader = command.ExecuteReader();
        while (reader.Read()) Invalidate(reader.GetString(0));
    }

    private void Invalidate(string id)
    {
        lock (cacheGate) { contentRevision++; summaries.Remove(id); fullTexts.Remove(id); }
    }

    private void RepairDuplicate(string id, CapturedClip clip)
    {
        try
        {
            bool healthy = ReadContent(id).AsSpan().SequenceEqual(clip.Content);
            if (clip.Thumbnail != null) healthy &= ReadContent(id, true).AsSpan().SequenceEqual(clip.Thumbnail);
            if (healthy) return;
        }
        catch (Exception error) when (error is CryptographicException or IOException or InvalidDataException) { paths.LogError("duplicate-repair", error); }
        byte[] payload = UserProtection.Protect(clip.Content);
        byte[]? thumb = clip.Thumbnail == null ? null : UserProtection.Protect(clip.Thumbnail);
        string? image = clip.Kind == ClipKind.Image ? id + ".bin" : null;
        string? thumbnail = image != null && thumb != null ? id + ".thumb.bin" : null;
        if (image != null) AppPaths.AtomicWrite(paths.ImagePath(image), payload);
        if (thumbnail != null) AppPaths.AtomicWrite(paths.ImagePath(thumbnail), thumb!);
        Execute("UPDATE clips SET text_payload=$text,image_file=$image,thumbnail_file=$thumb,size_bytes=$size WHERE id=$id;", null,
            ("$text", image == null ? payload : null), ("$image", image), ("$thumb", thumbnail),
            ("$size", payload.LongLength + (thumb?.LongLength ?? 0)), ("$id", id));
        Invalidate(id);
    }

    public string CreateBackup() { lock (gate) { EnsureOpen(); return HistoryBackup.Create(connection, paths); } }

    private void DeleteFiles(IEnumerable<string> files)
    {
        foreach (string file in files)
            try
            {
                // 临时文件只允许清理图片目录中的直接子项。
                string fullPath = file.EndsWith(".tmp", StringComparison.Ordinal) && Path.GetFileName(file) == file
                    ? Path.Combine(paths.Images, file) : paths.ImagePath(file);
                File.Delete(fullPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException) { paths.LogError("image-cleanup", e); }
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; command.Transaction = transaction;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        return command;
    }
    private void Execute(string sql, SqliteTransaction? transaction = null, params (string Name, object? Value)[] parameters)
    { using var command = Command(sql, transaction, parameters); command.ExecuteNonQuery(); }
    private long ScalarLong(string sql, SqliteTransaction? transaction = null)
    { using var command = Command(sql, transaction); return Convert.ToInt64(command.ExecuteScalar()); }
    private void EnsureOpen() => ObjectDisposedException.ThrowIf(disposed, this);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            lock (cacheGate) { contentRevision++; summaries.Clear(); fullTexts.Clear(); }
            connection.Dispose(); protection.Dispose();
        }
    }
}
