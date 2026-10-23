using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ClipStack.Core;

namespace ClipStack.App.Controls;

public partial class InlineNoteEditor : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(string), typeof(InlineNoteEditor), new PropertyMetadata("", OnValueChanged));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(InlineNoteEditor), new PropertyMetadata("添加备注", OnValueChanged));
    private Task<bool>? pendingSave;
    private string original = "";
    public string Value { get => (string?)GetValue(ValueProperty) ?? ""; set => SetValue(ValueProperty, value); }
    public string Label { get => (string?)GetValue(LabelProperty) ?? "添加备注"; set => SetValue(LabelProperty, value); }
    public bool IsEditing { get; private set; }
    public Func<string, Task<string?>>? SaveAsync { get; set; }
    public event EventHandler? EditingChanged;
    internal void FocusEditor() { if (IsEditing) Editor.Focus(); }

    public InlineNoteEditor() { InitializeComponent(); UpdateDisplay(); }

    private static void OnValueChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((InlineNoteEditor)sender).UpdateDisplay();
    private void UpdateDisplay()
    {
        if (DisplayText == null) return;
        DisplayText.Text = Label.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        FullNoteText.Text = Label;
    }

    private void OnBeginEdit(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (IsEditing) return;
        original = Value;
        Editor.Text = original;
        IsEditing = true;
        DisplayButton.Visibility = Visibility.Collapsed; EditorPanel.Visibility = Visibility.Visible;
        ErrorText.Text = FavoriteNote.Validate(original) ?? "";
        EditingChanged?.Invoke(this, EventArgs.Empty);
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!IsEditing) return;
            Editor.Focus();
            int index = Editor.GetCharacterIndexFromPoint(Mouse.GetPosition(Editor), true);
            Editor.CaretIndex = index < 0 ? Editor.Text.Length : index;
        }), DispatcherPriority.Input);
    }

    public Task<bool> CommitAsync()
    {
        if (!IsEditing) return Task.FromResult(true);
        if (pendingSave != null) return pendingSave;
        // 先发布任务，再执行回调；失焦与 Enter 同时发生也只提交一次。
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pendingSave = completion.Task;
        _ = SaveCoreAsync(completion);
        return completion.Task;
    }

    private async Task SaveCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            string note = FavoriteNote.Normalize(Editor.Text);
            if (note == FavoriteNote.Normalize(original)) { FinishEditing(); completion.SetResult(true); return; }
            if (FavoriteNote.Validate(note) is { } validation) { ErrorText.Text = validation; completion.SetResult(false); return; }
            Editor.IsReadOnly = true;
            string? error = SaveAsync == null ? "暂时无法保存备注。" : await SaveAsync(note);
            if (error == null) { FinishEditing(); completion.SetResult(true); }
            else { ErrorText.Text = error; completion.SetResult(false); }
        }
        catch (Exception)
        {
            ErrorText.Text = "保存失败，请稍后重试。";
            completion.TrySetResult(false);
        }
        finally { Editor.IsReadOnly = false; pendingSave = null; }
    }

    private void FinishEditing()
    {
        IsEditing = false; EditorPanel.Visibility = Visibility.Collapsed; DisplayButton.Visibility = Visibility.Visible;
        ErrorText.Text = "";
        EditingChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (Counter == null) return;
        Counter.Text = $"{FavoriteNote.Count(FavoriteNote.Normalize(Editor.Text))}/{FavoriteNote.MaxLength}";
        ErrorText.Text = FavoriteNote.Validate(Editor.Text) ?? "";
    }

    private async void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        // IME 组合输入使用 ImeProcessed，不把中文候选确认当作提交。
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (await CommitAsync()) DisplayButton.Focus(); else Editor.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (pendingSave == null) { FinishEditing(); DisplayButton.Focus(); }
        }
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            if (IsEditing && !Editor.IsKeyboardFocusWithin) await CommitAsync();
        }), DispatcherPriority.ContextIdle);
    }

    private async void OnUnloaded(object sender, RoutedEventArgs e) { if (IsEditing) await CommitAsync(); }
}
