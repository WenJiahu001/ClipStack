using System;
using System.IO;
using Forms = System.Windows.Forms;

namespace ClipStack.App.Platform;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly System.Drawing.Icon image;

    internal TrayService(Action open, Action pause, Action settings, Action exit, bool visible = true)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 ClipStack", null, (_, _) => open());
        menu.Items.Add("暂停 / 恢复记录", null, (_, _) => pause());
        menu.Items.Add("设置", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => exit());
        using Stream? stream = typeof(TrayService).Assembly.GetManifestResourceStream("ClipStack.App.Assets.clipstack.ico");
        image = stream == null ? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone() : new System.Drawing.Icon(stream, 32, 32);
        icon = new Forms.NotifyIcon { Icon = image, Text = "ClipStack · 正在记录", Visible = visible, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => open();
    }

    internal bool IsVisible => icon.Visible;
    internal void SetVisible(bool visible) => icon.Visible = visible;
    internal void SetPaused(bool paused) => icon.Text = paused ? "ClipStack · 已暂停记录" : "ClipStack · 正在记录";
    internal void Notify(string text) { if (icon.Visible) icon.ShowBalloonTip(5000, "ClipStack", text, Forms.ToolTipIcon.Info); }
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); image.Dispose(); }
}
