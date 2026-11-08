using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WinMirroring.App.Services;

namespace WinMirroring.App.ViewModels;

/// <summary>Auto-update from GitHub Releases: the current version, the toggle and a manual check.</summary>
public sealed partial class MainViewModel
{
    private bool _autoUpdateReady;

    [ObservableProperty]
    public partial bool AutoUpdate { get; set; }

    public string VersionText => $"Версия {GithubUpdater.CurrentVersion}";

    public string RepositoryUrl => "https://github.com/XKonnoRX/WinMirroring";

    partial void OnAutoUpdateChanged(bool value)
    {
        if (!_autoUpdateReady)
        {
            return;
        }

        _node.Settings.AutoUpdate = value;
        _node.SaveSettings();
    }

    [RelayCommand]
    private async Task CheckForUpdatesAsync()
    {
        StatusText = "Проверяю обновления…";
        await ((App)Application.Current).CheckForUpdatesNowAsync();
    }

    [RelayCommand]
    private void OpenRepository()
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            StatusText = $"Не удалось открыть страницу проекта: {ex.Message}";
        }
    }
}
