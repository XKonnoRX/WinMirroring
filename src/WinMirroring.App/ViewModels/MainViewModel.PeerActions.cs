using System.Diagnostics;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace WinMirroring.App.ViewModels;

/// <summary>Context-menu actions for a device in the list.</summary>
public sealed partial class MainViewModel
{
    private const int LatencyProbeCount = 20;

    private bool SelectedHasSession() => SelectedPeer is { HasSession: true };

    private bool SelectedIsReady() => SelectedPeer is { IsReady: true };

    private bool SelectedHasHost() => SelectedPeer is { Host.Length: > 0 };

    private bool CanControlSelected() => SelectedIsReady() && _input.IsEnabled;

    private void RefreshPeerCommands()
    {
        PairCommand.NotifyCanExecuteChanged();
        UnpairCommand.NotifyCanExecuteChanged();
        ControlPeerCommand.NotifyCanExecuteChanged();
        ShowScreenCommand.NotifyCanExecuteChanged();
        SendWindowCommand.NotifyCanExecuteChanged();
        MeasureLatencyCommand.NotifyCanExecuteChanged();
        IcmpPingCommand.NotifyCanExecuteChanged();
        RemoteDesktopCommand.NotifyCanExecuteChanged();
        OpenShareCommand.NotifyCanExecuteChanged();
        CopyAddressCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();
        ToggleListenCommand.NotifyCanExecuteChanged();
        TogglePinnedAudioCommand.NotifyCanExecuteChanged();
        FetchPeerLogCommand.NotifyCanExecuteChanged();
        ToggleSecondMonitorForSelectedCommand.NotifyCanExecuteChanged();
        BecomeSecondMonitorOfSelectedCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedPeerChanged(PeerItemViewModel? value) => RefreshPeerCommands();

    [RelayCommand(CanExecute = nameof(CanControlSelected))]
    private void ControlPeer()
    {
        if (SelectedPeer is { } peer)
        {
            _input.ControlPeer(peer.Id);
            StatusText = $"Ввод передан на {peer.Name}. Вернуть: Ctrl+Alt+Home.";
        }
    }

    // The tools themselves live in MainViewModel.Tools.cs, taking a device id: the old menu here and the device
    // panel in the new interface both reach for the same one.
    [RelayCommand(CanExecute = nameof(SelectedHasSession))]
    private async Task MeasureLatencyAsync()
    {
        if (SelectedPeer is { } peer)
        {
            await MeasureLatencyToAsync(peer.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void IcmpPing()
    {
        if (SelectedPeer is { } peer)
        {
            PingTo(peer.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void RemoteDesktop()
    {
        if (SelectedPeer is { } peer)
        {
            RemoteDesktopTo(peer.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void OpenShare()
    {
        if (SelectedPeer is { } peer)
        {
            OpenShareOf(peer.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void CopyAddress()
    {
        if (SelectedPeer is { } peer)
        {
            CopyAddressOf(peer.Id);
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasSession))]
    private async Task ReconnectAsync()
    {
        if (SelectedPeer is { } peer)
        {
            await ReconnectToAsync(peer.Id);
        }
    }

    /// <summary>Starts a Windows tool for a device; returns null when it started, a line to show when it did not.</summary>
    private string? Launch(string fileName, string arguments, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
            return null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Launching {File} failed", fileName);
            return Say($"Не удалось открыть {what}: {ex.Message}");
        }
    }

    private void RefreshStats()
    {
        foreach (var peer in Peers)
        {
            if (_node.Sessions.TryGetSession(peer.Id, out var session) && session.LastRoundTrip is { } rtt)
            {
                peer.RttText = $"{rtt.TotalMilliseconds:F1} мс";
            }
        }

        SmoothingText = _input.IsBeingControlled && SmoothingEnabled
            ? $"Сглаживание: {_input.PlayoutDelayMilliseconds:F0} мс"
            : string.Empty;
        ControlPeerCommand.NotifyCanExecuteChanged();
        RefreshMediaStatus();
    }
}
