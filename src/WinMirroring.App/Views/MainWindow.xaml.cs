using System.ComponentModel;
using System.Windows;
using WinMirroring.App.ViewModels;

namespace WinMirroring.App.Views;

public partial class MainWindow : Window
{
    private bool _allowClose;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>The window normally hides to the tray; the app calls this before a real exit.</summary>
    public void AllowClose() => _allowClose = true;

    private void OnOpenLayoutField(object sender, RoutedEventArgs e) => ((App)Application.Current).ShowHub();

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
