using System.Windows;
using System.Windows.Input;
using ClipStack.App.ViewModels;

namespace ClipStack.App;

public partial class ContentWindow : Window
{
    internal ContentWindow(MainViewModel model)
    {
        InitializeComponent();
        DataContext = model;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; }
    }
}
