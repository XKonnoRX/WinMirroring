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
        MeasureLatencyCommand.NotifyCanExecuteChanged();
        IcmpPingCommand.NotifyCanExecuteChanged();
        RemoteDesktopCommand.NotifyCanExecuteChanged();
        OpenShareCommand.NotifyCanExecuteChanged();
        CopyAddressCommand.NotifyCanExecuteChanged();
        ReconnectCommand.NotifyCanExecuteChanged();
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

    [RelayCommand(CanExecute = nameof(SelectedHasSession))]
    private async Task MeasureLatencyAsync()
    {
        var peer = SelectedPeer;
        if (peer is null || !_node.Sessions.TryGetSession(peer.Id, out var session))
        {
            return;
        }

        StatusText = $"Измеряю задержку до {peer.Name}…";
        var samples = new List<double>(LatencyProbeCount);
        try
        {
            for (var i = 0; i < LatencyProbeCount; i++)
            {
                if (await session.PingAsync(TimeSpan.FromSeconds(2)) is { } rtt)
                {
                    samples.Add(rtt.TotalMilliseconds);
                }

                await Task.Delay(50);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or ObjectDisposedException)
        {
            StatusText = $"Измерение прервано: соединение с {peer.Name} закрыто.";
            return;
        }

        if (samples.Count == 0)
        {
            StatusText = $"{peer.Name} не ответил ни на один из {LatencyProbeCount} запросов.";
            return;
        }

        var jitter = samples.Count > 1
            ? samples.Zip(samples.Skip(1), (a, b) => Math.Abs(b - a)).Average()
            : 0;
        StatusText = $"{peer.Name}: в среднем {samples.Average():F1} мс (мин {samples.Min():F1}, макс {samples.Max():F1}), "
            + $"разброс {jitter:F1} мс, потеряно {LatencyProbeCount - samples.Count} из {LatencyProbeCount}.";
        peer.RttText = $"{samples.Average():F1} мс";
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void IcmpPing()
    {
        if (SelectedPeer is { Host.Length: > 0 } peer)
        {
            Launch("cmd.exe", $"/k ping -t {peer.Host}", $"ping {peer.Host}");
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void RemoteDesktop()
    {
        if (SelectedPeer is { Host.Length: > 0 } peer)
        {
            Launch("mstsc.exe", $"/v:{peer.Host}", "удалённый рабочий стол");
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void OpenShare()
    {
        if (SelectedPeer is { Host.Length: > 0 } peer)
        {
            Launch("explorer.exe", $@"\\{peer.Host}", "сетевую папку");
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasHost))]
    private void CopyAddress()
    {
        if (SelectedPeer is { Host.Length: > 0 } peer)
        {
            try
            {
                Clipboard.SetText(peer.Host);
                StatusText = $"Адрес {peer.Host} скопирован.";
            }
            catch (System.Runtime.InteropServices.ExternalException ex)
            {
                StatusText = $"Буфер обмена занят: {ex.Message}";
            }
        }
    }

    [RelayCommand(CanExecute = nameof(SelectedHasSession))]
    private async Task ReconnectAsync()
    {
        var peer = SelectedPeer;
        if (peer is null || !_node.Sessions.TryGetSession(peer.Id, out var session))
        {
            return;
        }

        await session.CloseAsync("reconnect requested");
        StatusText = peer.IsPaired
            ? $"Соединение с {peer.Name} закрыто, переподключение произойдёт автоматически."
            : $"Соединение с {peer.Name} закрыто.";
    }

    private void Launch(string fileName, string arguments, string what)
    {
        try
        {
            Process.Start(new ProcessStartInfo(fileName, arguments) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Launching {File} failed", fileName);
            StatusText = $"Не удалось открыть {what}: {ex.Message}";
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
    }
}
