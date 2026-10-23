using System;

namespace ClipStack.Core;

// 保存物理像素坐标，跨显示器恢复时再按目标屏幕 DPI 换算。
public sealed record PanelPlacement(int Left, int Top, int Width, int Height)
{
    public bool IsValid => Left is >= -1_000_000 and <= 1_000_000 && Top is >= -1_000_000 and <= 1_000_000
        && Width is > 0 and <= 65_536 && Height is > 0 and <= 65_536;

    public static PanelPlacement Resolve(PanelPlacement workArea, PanelPlacement? saved, double scale = 1,
        PanelPositionMode mode = PanelPositionMode.Remember, int? cursorX = null, int? cursorY = null)
    {
        if (!workArea.IsValid || !double.IsFinite(scale) || scale <= 0) throw new ArgumentException("屏幕工作区无效。");
        if (saved is not { IsValid: true }) saved = null;
        int margin = Math.Min((int)Math.Round(18 * scale), Math.Max(0, Math.Min(workArea.Width, workArea.Height) / 4));
        int availableWidth = Math.Max(1, workArea.Width - margin * 2), availableHeight = Math.Max(1, workArea.Height - margin * 2);
        int width = Math.Clamp(saved?.Width ?? (int)Math.Round(500 * scale), Math.Min((int)Math.Round(380 * scale), availableWidth), availableWidth);
        int height = Math.Clamp(saved?.Height ?? (int)Math.Round(660 * scale), Math.Min((int)Math.Round(400 * scale), availableHeight), availableHeight);
        int left = mode == PanelPositionMode.Remember && saved != null ? saved.Left : workArea.Left + workArea.Width - margin - width;
        int top = mode == PanelPositionMode.Remember && saved != null ? saved.Top : workArea.Top + (workArea.Height - height) / 2;
        if (mode == PanelPositionMode.ScreenCenter)
            left = workArea.Left + (workArea.Width - width) / 2;
        else if (mode == PanelPositionMode.FollowMouse && cursorX.HasValue && cursorY.HasValue)
        {
            int offset = (int)Math.Round(12 * scale);
            left = cursorX.Value + offset;
            top = cursorY.Value + offset;
            // 临近右侧或底部时翻转到鼠标另一侧，最终再限制到工作区。
            if (left + width > workArea.Left + workArea.Width - margin) left = cursorX.Value - offset - width;
            if (top + height > workArea.Top + workArea.Height - margin) top = cursorY.Value - offset - height;
        }
        return new PanelPlacement(Math.Clamp(left, workArea.Left + margin, workArea.Left + workArea.Width - margin - width),
            Math.Clamp(top, workArea.Top + margin, workArea.Top + workArea.Height - margin - height), width, height);
    }
}
