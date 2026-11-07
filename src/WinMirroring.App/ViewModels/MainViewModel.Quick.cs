namespace WinMirroring.App.ViewModels;

/// <summary>Quick actions shared by the tray menu, the screen viewer panel and the device menu.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Connected devices, for quick menus.</summary>
    public IReadOnlyList<PeerItemViewModel> ReadyPeers => Peers.Where(p => p.IsReady).ToList();

    /// <summary>This device's sound never leaves it.</summary>
    public bool SoundKeptHere => !AudioSendEnabled;

    /// <summary>This device's sound is sent to whoever controls or watches it (no fixed target).</summary>
    public bool SoundAutomatic => AudioSendEnabled && (AudioPinnedTarget is null || AudioPinnedTarget.IsEmpty);

    public bool IsSoundSentTo(string peerId) => AudioSendEnabled && AudioPinnedTarget is { IsEmpty: false } pinned && pinned.Id == peerId;

    public bool IsListeningTo(string peerId) => _audio.IsListeningTo(peerId);

    /// <summary>Sends this device's sound to <paramref name="peerId"/> always, or back to automatic when null.</summary>
    public void SendSoundTo(string? peerId, string? peerName = null)
    {
        AudioSendEnabled = true;
        AudioPinnedTarget = peerId is null ? _noDevice : EnsureChoice(peerId, peerName);
        StatusText = peerId is null
            ? "Звук этого устройства следует за вводом и просмотром экрана."
            : $"Звук этого устройства играет на {peerName ?? peerId}.";
    }

    public void KeepSoundHere()
    {
        AudioPinnedTarget = _noDevice;
        AudioSendEnabled = false;
        StatusText = "Звук этого устройства играет только здесь.";
    }

    public async Task<bool> SetListenAsync(string peerId, string peerName, bool listen)
    {
        var ok = await _audio.RequestListenAsync(peerId, listen);
        StatusText = !ok
            ? $"Не удалось связаться с {peerName}."
            : listen
                ? $"Звук {peerName} теперь играет здесь (если на том устройстве включена передача звука)."
                : $"Звук {peerName} больше не запрашивается сюда.";
        return ok;
    }

    /// <summary>Elevated processes can inject input into elevated windows (Task Manager and the like).</summary>
    public bool IsElevated => WinMirroring.Input.Native.InjectionGuard.IsElevated;

    public bool CanRestartElevated => !IsElevated;

    public string ElevationText => IsElevated
        ? "WinMirroring запущен с правами администратора: другие устройства могут управлять и окнами администратора."
        : "WinMirroring запущен с обычными правами: окна, запущенные от имени администратора (например, Диспетчер задач), не принимают ввод с других устройств.";

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private async Task RestartElevatedAsync()
    {
        if (IsElevated)
        {
            return;
        }

        if (!await ((App)System.Windows.Application.Current).RestartElevatedAsync())
        {
            StatusText = "Перезапуск с правами администратора отменён.";
        }
    }

    /// <summary>Takes the shared cursor back (here, or from the device controlling this one).</summary>
    public string ReturnCursor()
    {
        var result = _input.ReturnCursor() ?? "Общий ввод сейчас никуда не передан.";
        StatusText = result;
        return result;
    }
}
