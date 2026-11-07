using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using WinMirroring.App.Services;

namespace WinMirroring.App.ViewModels;

/// <summary>Feature health, remote logs and per-device sound routing.</summary>
public sealed partial class MainViewModel
{
    private readonly FeatureHealth _health;
    private readonly DiagnosticsService _diagnostics;

    public ObservableCollection<FeatureStatusItem> FeatureStatuses { get; } = [];

    private void InitializeDiagnostics()
    {
        _health.Changed += () => _dispatcher.InvokeAsync(ReloadFeatureStatuses);
        ReloadFeatureStatuses();
    }

    private void ReloadFeatureStatuses()
    {
        FeatureStatuses.Clear();
        foreach (var feature in _health.Snapshot)
        {
            FeatureStatuses.Add(new FeatureStatusItem(feature.Ok ? "✔" : "✖", feature.Name, feature.Detail, feature.Ok));
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedIsReady))]
    private async Task ToggleListenAsync()
    {
        if (SelectedPeer is not { } peer)
        {
            return;
        }

        await SetListenAsync(peer.Id, peer.Name, !_audio.IsListeningTo(peer.Id));
    }

    [RelayCommand(CanExecute = nameof(SelectedIsReady))]
    private void TogglePinnedAudio()
    {
        if (SelectedPeer is not { } peer)
        {
            return;
        }

        SendSoundTo(IsSoundSentTo(peer.Id) ? null : peer.Id, peer.Name);
    }

    [RelayCommand(CanExecute = nameof(SelectedIsReady))]
    private async Task FetchPeerLogAsync()
    {
        if (SelectedPeer is not { } peer)
        {
            return;
        }

        StatusText = $"Запрашиваю журнал {peer.Name}…";
        try
        {
            var path = await _diagnostics.FetchLogAsync(peer.Id);
            StatusText = $"Журнал {peer.Name} сохранён: {path}";
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception)
        {
            _logger.LogInformation("Log of {Peer} not fetched: {Message}", peer.Name, ex.Message);
            StatusText = $"Журнал {peer.Name} не получен: {ex.Message}";
        }
    }
}

public sealed record FeatureStatusItem(string Mark, string Name, string Detail, bool Ok);
