using System.Windows;
using ClipStack.Core;

namespace ClipStack.App.Controls;

// 只替换窗口局部资源，使已显示和后续虚拟化生成的卡片共用同一套布局。
internal sealed record PanelLayout(double Side, double Gap, double PaddingX, double PaddingTop,
    double PaddingBottom, double HeaderGap, double LineHeight, int TextLines, double ImageHeight,
    double SearchHeight, double FilterHeight, double FooterPadding)
{
    internal static void Apply(ResourceDictionary resources, DisplayDensity density)
    {
        PanelLayout layout = density switch
        {
            DisplayDensity.Compact => new(10, 4, 10, 5, 8, 2, 21, 2, 80, 34, 40, 6),
            DisplayDensity.Comfortable => new(18, 12, 16, 13, 16, 10, 24, 4, 144, 42, 56, 12),
            _ => new(14, 8, 12, 9, 12, 6, 22, 3, 112, 38, 48, 9)
        };
        resources["PanelSearchMargin"] = new Thickness(layout.Side, 2, layout.Side, 0);
        resources["PanelSearchHeight"] = layout.SearchHeight;
        resources["PanelFilterHeight"] = new GridLength(layout.FilterHeight);
        resources["PanelFilterMargin"] = new Thickness(layout.Side, 0, layout.Side, 0);
        resources["PanelListMargin"] = new Thickness(layout.Side, 0, layout.Side - 4, 0);
        resources["PanelCardMargin"] = new Thickness(0, 0, 4, layout.Gap);
        resources["PanelCardPadding"] = new Thickness(layout.PaddingX, layout.PaddingTop, layout.PaddingX, layout.PaddingBottom);
        resources["PanelHeaderMargin"] = new Thickness(0, 0, -5, layout.HeaderGap);
        resources["PanelNoteMargin"] = new Thickness(0, layout.HeaderGap, 0, 0);
        resources["PanelTextHeight"] = layout.LineHeight * layout.TextLines;
        resources["PanelLineHeight"] = layout.LineHeight;
        resources["PanelImageHeight"] = layout.ImageHeight;
        resources["PanelImageDetailMargin"] = new Thickness(0, layout.HeaderGap + 1, 0, 0);
        resources["PanelFooterPadding"] = new Thickness(layout.Side, layout.FooterPadding, layout.Side, layout.FooterPadding);
        resources["PanelStatusMargin"] = new Thickness(0, layout.HeaderGap + 1, 0, 0);
    }
}
