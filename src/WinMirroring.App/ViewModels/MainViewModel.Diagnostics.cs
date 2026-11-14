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

        await ToggleSoundHereAsync(peer.Id, peer.Name);
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
        if (SelectedPeer is { } peer)
        {
            await FetchLogOfAsync(peer.Id);
        }
    }
}

public sealed record FeatureStatusItem(string Mark, string Name, string Detail, bool Ok);
