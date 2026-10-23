using System;
using System.Windows;
using System.Windows.Media;
using ClipStack.Core;

namespace ClipStack.App.ViewModels;

internal sealed class ClipItem : ObservableObject
{
    private ImageSource? thumbnail;
    public ClipEntry Entry { get; private set; }
    public ClipItem(ClipEntry entry) => Entry = entry;
    public string Id => Entry.Id;
    public string Summary => Entry.Summary.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    public string KindLabel => Entry.Kind == ClipKind.Text ? "文本" : "图片";
    public string KindIcon => Entry.Kind == ClipKind.Text ? "T" : "▧";
    public string FavoriteLabel => Entry.Favorite ? "★ 已收藏" : "☆ 收藏";
    public string NoteLabel => Entry.NoteUnreadable ? "备注无法读取，点击重新编辑" : string.IsNullOrEmpty(Entry.Note) ? "添加备注" : Entry.Note;
    public string NoteSummary => NoteLabel.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
    public string NoteActionLabel => string.IsNullOrEmpty(Entry.Note) ? "添加备注" : "编辑备注";
    public Visibility NoteVisibility => Entry.Favorite && (Entry.NoteUnreadable || !string.IsNullOrEmpty(Entry.Note)) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FavoriteVisibility => Entry.Favorite ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ImageVisibility => Entry.Kind == ClipKind.Image ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TextVisibility => Entry.Kind == ClipKind.Text ? Visibility.Visible : Visibility.Collapsed;
    public string TimeLabel
    {
        get
        {
            var elapsed = DateTimeOffset.Now - Entry.CopiedAt;
            return elapsed.TotalMinutes < 1 ? "刚刚" : elapsed.TotalHours < 1 ? $"{(int)elapsed.TotalMinutes} 分钟前"
                : Entry.CopiedAt.LocalDateTime.Date == DateTime.Today ? Entry.CopiedAt.ToLocalTime().ToString("HH:mm")
                : Entry.CopiedAt.ToLocalTime().ToString("MM/dd HH:mm");
        }
    }
    public string Detail => Entry.Kind == ClipKind.Image ? $"{Entry.Width:N0} × {Entry.Height:N0} · {FormatSize(Entry.SizeBytes)}"
        : $"{FormatSize(Entry.SizeBytes)} · {Entry.CopiedAt.ToLocalTime():MM/dd HH:mm}";
    public ImageSource? Thumbnail { get => thumbnail; set => Set(ref thumbnail, value); }
    public void UpdateNote(string? note)
    {
        UpdateEntry(Entry with { Note = string.IsNullOrEmpty(note) ? null : note, NoteUnreadable = false });
    }
    public void UpdateEntry(ClipEntry entry)
    {
        if (entry.Id != Id) throw new ArgumentException("不能修改历史记录标识。", nameof(entry));
        if (Entry != entry)
        {
            Entry = entry;
            Raise(nameof(Entry)); Raise(nameof(Summary)); Raise(nameof(Detail)); Raise(nameof(FavoriteLabel));
            Raise(nameof(NoteLabel)); Raise(nameof(NoteSummary)); Raise(nameof(NoteActionLabel));
            Raise(nameof(NoteVisibility)); Raise(nameof(FavoriteVisibility));
        }
        Raise(nameof(TimeLabel));
    }
    public static string FormatSize(long bytes) => bytes >= 1024 * 1024 ? $"{bytes / 1048576.0:0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} B";
}
