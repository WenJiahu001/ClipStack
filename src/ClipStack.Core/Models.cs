using System;
using System.Collections.Generic;
using System.Linq;

namespace ClipStack.Core;

public enum ClipKind { Text = 1, Image = 2 }
public enum HistoryFilter { All, Text, Image, Favorites }

public sealed record CapturedClip(ClipKind Kind, byte[] Content, byte[]? Thumbnail = null, int Width = 0, int Height = 0, DateTimeOffset? CopiedAt = null)
{
    public static CapturedClip FromText(string text) => new(ClipKind.Text, System.Text.Encoding.UTF8.GetBytes(text));
}

public sealed record ClipEntry(string Id, ClipKind Kind, string? Text, string? ImageFile, string? ThumbnailFile,
    long SizeBytes, int Width, int Height, DateTimeOffset CreatedAt, DateTimeOffset CopiedAt,
    DateTimeOffset? UsedAt, bool Favorite, bool Unreadable = false, string? Note = null, bool NoteUnreadable = false)
{
    public string Summary => Unreadable ? "内容无法解密，原始记录已保留" : Kind == ClipKind.Image
        ? $"图片 · {Width:N0} × {Height:N0}" : string.IsNullOrEmpty(Text) ? "空文本" : Text;
}

public sealed record SaveResult(bool Saved, string Message, string? Id = null);
public sealed record StorageStatistics(int Count, int Favorites, long ImageBytes);

public static class HistorySearch
{
    public static IReadOnlyList<ClipEntry> Query(IEnumerable<ClipEntry> entries, string? query, HistoryFilter filter)
    {
        string term = (query ?? "").Trim();
        return entries.Where(e => filter switch
        {
            HistoryFilter.Text => e.Kind == ClipKind.Text,
            HistoryFilter.Image => e.Kind == ClipKind.Image,
            HistoryFilter.Favorites => e.Favorite,
            _ => true
        }).Where(e => term.Length == 0 || e.Summary.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (e.Favorite && e.Note?.Contains(term, StringComparison.OrdinalIgnoreCase) == true))
          .OrderByDescending(e => e.CopiedAt).ToArray();
    }
}
