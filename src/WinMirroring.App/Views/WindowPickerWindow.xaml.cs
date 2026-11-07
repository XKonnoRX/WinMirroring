using System.Windows;
using WinMirroring.Screen;
using WinMirroring.Screen.Capture;

namespace WinMirroring.App.Views;

public partial class WindowPickerWindow : Window
{
    public WindowPickerWindow(string deviceName)
    {
        InitializeComponent();
        HeaderText.Text = $"Какое окно показать на {deviceName}?";
        WindowList.ItemsSource = ScreenStreamingService.ShareableWindows();
        if (WindowList.Items.Count > 0)
        {
            WindowList.SelectedIndex = 0;
        }
    }

    public WindowDescription? Selected { get; private set; }

    private void OnSend(object sender, RoutedEventArgs e)
    {
        if (WindowList.SelectedItem is WindowDescription window)
        {
            Selected = window;
            DialogResult = true;
        }
    }
}
