using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ClipStack.App.Platform;
using ClipStack.Core;

namespace ClipStack.App.Diagnostics;

internal static class PreviewFixtures
{
    internal static async Task SeedAsync(HistoryStore store)
    {
        if (store!.GetStatistics().Count > 0) return;
        DateTimeOffset now = DateTimeOffset.Now;
        var rows = new[]
        {
            ("把每一次复制，都变成随手可用的灵感。\n\nClipStack 会自动记住你复制的文字和图片。\n按 Ctrl + Alt + V，搜索、选择，再粘贴。\n\n✓ 本地加密保存\n✓ 支持文字与截图\n✓ 收藏常用内容\n✓ 纯键盘快速操作", 0),
            ("https://learn.microsoft.com/dotnet/desktop/wpf/", 3),
            ("周五产品评审\n1. 确认首版功能\n2. 验证快捷键与粘贴体验\n3. 整理内测反馈", 12),
            ("git log --oneline --graph -10", 28),
            ("你好，文件已收到，我会在今天下班前回复。谢谢！", 60),
            ("const ideas = clipboard.history.filter(item => item.favorite);", 110)
        };
        foreach (var row in rows)
        {
            SaveResult saved = store.Save(CapturedClip.FromText(row.Item1), now.AddMinutes(-row.Item2));
            if (row.Item2 == 60 || row.Item2 == 0)
            {
                store.SetFavorite(saved.Id!, true);
                store.SetNote(saved.Id!, row.Item2 == 0 ? "使用小贴士" : "工作常用回复");
            }
        }
        CapturedClip image = ClipboardMonitor.EncodeImage(CreateImage())!;
        SaveResult savedImage = await Task.Run(() => store.Save(image, now.AddMinutes(-7)));
        store.SetFavorite(savedImage.Id!, true);
        store.SetNote(savedImage.Id!, "项目配色参考");
    }

    internal static BitmapSource CreateImage()
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(224, 242, 232)), null, new Rect(0, 0, 600, 360));
            drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(21, 44, 34)), null, new Rect(48, 48, 504, 264), 22, 22);
            drawing.DrawEllipse(new SolidColorBrush(Color.FromRgb(169, 237, 197)), null, new Point(470, 118), 27, 27);
            var text = new FormattedText("A little less friction.\nA little more flow.", System.Globalization.CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 31, Brushes.White, 1);
            drawing.DrawText(text, new Point(80, 147));
        }
        var bitmap = new RenderTargetBitmap(600, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
}
