using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Controls;
using System.ComponentModel;
using ClipStack.App.Platform;
using ClipStack.Core;

namespace ClipStack.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings original;
    private readonly Func<AppSettings, Task<string?>> save;
    private bool saving;
    internal SettingsWindow(AppSettings settings, string dataPath, Func<AppSettings, Task<string?>> save)
    {
        InitializeComponent(); this.original = settings; this.save = save;
        Closing += OnClosing;
        CompactDensity.IsChecked = settings.MainWindowDensity == DisplayDensity.Compact;
        ComfortableDensity.IsChecked = settings.MainWindowDensity == DisplayDensity.Comfortable;
        StandardDensity.IsChecked = !CompactDensity.IsChecked.Value && !ComfortableDensity.IsChecked.Value;
        MousePosition.IsChecked = settings.WindowPosition == PanelPositionMode.FollowMouse;
        CenterPosition.IsChecked = settings.WindowPosition == PanelPositionMode.ScreenCenter;
        RightPosition.IsChecked = settings.WindowPosition == PanelPositionMode.ScreenRight;
        RememberPosition.IsChecked = settings.WindowPosition == PanelPositionMode.Remember || !Enum.IsDefined(settings.WindowPosition);
        ResetListBox.IsChecked = settings.ResetListOnActivate;
        ResetFilterBox.IsChecked = settings.ResetFilterOnShow;
        HideAfterCopyBox.IsChecked = settings.HideAfterCopy;
        TrayIconBox.IsChecked = settings.ShowTrayIcon;
        HotkeyBox.Text = settings.Hotkey; MaxEntriesBox.Text = settings.MaxEntries.ToString();
        RetentionBox.Text = settings.RetentionDays.ToString(); ImageBudgetBox.Text = settings.ImageBudgetMb.ToString();
        ExcludedBox.Text = string.Join(Environment.NewLine, settings.ExcludedProcesses); StartupBox.IsChecked = settings.StartWithWindows;
        DataPathText.Text = dataPath;
        SourceInitialized += (_, _) =>
        {
            int enabled = 0;
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (NativeMethods.DwmSetWindowAttribute(hwnd, 20, ref enabled, sizeof(int)) != 0)
                NativeMethods.DwmSetWindowAttribute(hwnd, 19, ref enabled, sizeof(int));
        };
    }
    private void OnCancel(object sender, RoutedEventArgs e) => Close();
    private void OnClosing(object? sender, CancelEventArgs e) { if (saving) e.Cancel = true; }
    private void OnLicenses(object sender, RoutedEventArgs e)
    {
        var text = new StringBuilder();
        foreach (string resource in typeof(App).Assembly.GetManifestResourceNames().Where(name => name.StartsWith("ClipStack.Licenses.", StringComparison.Ordinal)).OrderBy(name => name))
        {
            using Stream stream = typeof(App).Assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            text.AppendLine(resource["ClipStack.Licenses.".Length..]).AppendLine().AppendLine(reader.ReadToEnd()).AppendLine();
        }
        var box = new TextBox { IsReadOnly = true, Text = text.ToString(), TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(18), VerticalContentAlignment = VerticalAlignment.Top };
        var dialog = new Window { Title = "ClipStack · 开源许可", Width = 720, Height = 600, Owner = this, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = box, Style = (Style)FindResource(typeof(Window)) };
        dialog.ShowDialog();
    }
    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (saving) return;
        if (!int.TryParse(MaxEntriesBox.Text, out int count) || count < 10 || count > 10000
            || !int.TryParse(RetentionBox.Text, out int days) || days < 1 || days > 365
            || !int.TryParse(ImageBudgetBox.Text, out int budget) || budget < 20 || budget > 5000)
        { ErrorText.Text = "请填写标注范围内的整数。"; return; }
        if (!HotkeyManager.TryParse(HotkeyBox.Text.Trim(), out _, out _))
        { ErrorText.Text = "快捷键格式无效，例如 Ctrl+Alt+V。"; return; }
        var next = original with
        {
            Hotkey = HotkeyBox.Text.Trim(), MaxEntries = count, RetentionDays = days, ImageBudgetMb = budget,
            StartWithWindows = StartupBox.IsChecked == true,
            ResetListOnActivate = ResetListBox.IsChecked == true,
            ResetFilterOnShow = ResetFilterBox.IsChecked == true,
            HideAfterCopy = HideAfterCopyBox.IsChecked == true,
            ShowTrayIcon = TrayIconBox.IsChecked == true,
            WindowPosition = MousePosition.IsChecked == true ? PanelPositionMode.FollowMouse
                : CenterPosition.IsChecked == true ? PanelPositionMode.ScreenCenter
                : RightPosition.IsChecked == true ? PanelPositionMode.ScreenRight : PanelPositionMode.Remember,
            MainWindowDensity = CompactDensity.IsChecked == true ? DisplayDensity.Compact
                : ComfortableDensity.IsChecked == true ? DisplayDensity.Comfortable : DisplayDensity.Standard,
            ExcludedProcesses = ExcludedBox.Text.Split(new[] { '\r', '\n', ';', ',', '；', '，' }, StringSplitOptions.RemoveEmptyEntries)
        };
        saving = true; IsEnabled = false;
        try
        {
            string? error = await save(next.Normalize());
            if (error != null) ErrorText.Text = error;
            else { saving = false; DialogResult = true; }
        }
        catch (Exception) { ErrorText.Text = "保存未完成，请稍后重试。"; }
        finally { saving = false; IsEnabled = true; }
    }
}
