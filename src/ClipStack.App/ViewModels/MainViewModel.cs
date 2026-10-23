using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using ClipStack.Core;

namespace ClipStack.App.ViewModels;

internal sealed class MainViewModel : ObservableObject
{
    private string queryText = "", statusMessage = "正在启动…", hotkeyLabel = "Ctrl + Alt + V";
    private IReadOnlyList<ClipItem> items = Array.Empty<ClipItem>();
    private ClipItem? selectedItem;
    private string previewText = "选择一条记录查看内容。", previewDetail = "", previewKind = "";
    private ImageSource? previewImage;
    private HistoryFilter filter;
    private bool paused;
    private bool isEditingNote;
    private bool isLoading;
    public bool IsLoading
    {
        get => isLoading;
        set { if (Set(ref isLoading, value)) { Raise(nameof(ResultLabel)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDescription)); } }
    }
    public event Action? SearchRequested;
    public event Action? SelectionChanged;
    public event Action? EditingChanged;
    public bool IsEditingNote { get => isEditingNote; set { if (Set(ref isEditingNote, value)) EditingChanged?.Invoke(); } }

    public string QueryText { get => queryText; set { if (Set(ref queryText, value)) SearchRequested?.Invoke(); } }
    public string StatusMessage { get => statusMessage; set => Set(ref statusMessage, value); }
    public string HotkeyLabel { get => hotkeyLabel; set => Set(ref hotkeyLabel, value); }
    public bool Paused { get => paused; set { if (Set(ref paused, value)) Raise(nameof(PauseLabel)); } }
    public string PauseLabel => paused ? "▶  恢复记录" : "Ⅱ  暂停记录";
    public HistoryFilter Filter
    {
        get => filter;
        set { if (Set(ref filter, value)) { Raise(nameof(SectionTitle)); SearchRequested?.Invoke(); } }
    }
    public string SectionTitle => Filter switch { HistoryFilter.Text => "文本", HistoryFilter.Image => "图片", HistoryFilter.Favorites => "我的收藏", _ => "最近复制" };
    public IReadOnlyList<ClipItem> Items
    {
        get => items;
        set { if (Set(ref items, value)) { Raise(nameof(ResultLabel)); Raise(nameof(EmptyVisibility)); Raise(nameof(EmptyTitle)); Raise(nameof(EmptyDescription)); } }
    }
    public string ResultLabel => IsLoading && Items.Count == 0 ? "加载中…" : $"{Items.Count} 条";
    public Visibility EmptyVisibility => Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string EmptyTitle => IsLoading ? "正在加载历史…" : QueryText.Length != 0 ? "没有找到匹配内容" : Filter == HistoryFilter.Favorites ? "收藏常用的内容" : "从下一次复制开始";
    public string EmptyDescription => IsLoading ? "" : QueryText.Length != 0 ? "试试更短的关键词，或切换筛选。" : Filter == HistoryFilter.Favorites ? "选中一条记录，点击「收藏」。" : "复制文字或截图，就会出现在这里。\n历史仅保存在这台电脑。";
    public ClipItem? SelectedItem
    {
        get => selectedItem;
        set
        {
            if (!Set(ref selectedItem, value)) return;
            Raise(nameof(HasSelection)); Raise(nameof(FavoriteLabel)); Raise(nameof(TextPreviewVisibility)); Raise(nameof(ImagePreviewVisibility));
            SelectionChanged?.Invoke();
        }
    }
    public bool HasSelection => SelectedItem != null;
    public string FavoriteLabel => SelectedItem?.FavoriteLabel ?? "☆ 收藏";
    public Visibility TextPreviewVisibility => SelectedItem?.Entry.Kind == ClipKind.Image ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ImagePreviewVisibility => SelectedItem?.Entry.Kind == ClipKind.Image ? Visibility.Visible : Visibility.Collapsed;
    public string PreviewText { get => previewText; set => Set(ref previewText, value); }
    public string PreviewDetail { get => previewDetail; set => Set(ref previewDetail, value); }
    public string PreviewKind { get => previewKind; set => Set(ref previewKind, value); }
    public ImageSource? PreviewImage { get => previewImage; set => Set(ref previewImage, value); }
}
