using CommunityToolkit.Mvvm.ComponentModel;
using WinMirroring.Audio;

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

    /// <summary>That device's sound plays here right now.</summary>
    public bool IsSoundFrom(string peerId) => _audio.IsReceivingFrom(peerId);

    /// <summary>What this device asked for that device's sound: automatic, always here, never here.</summary>
    public IncomingSoundChoice SoundHereChoice(string peerId) => _audio.IncomingChoice(peerId);

    public AudioStatus SoundStatus => _audio.GetStatus();

    /// <summary>Where this device's sound plays now and why, in a line for the panel.</summary>
    public string OwnSoundLine
    {
        get
        {
            var status = _audio.GetStatus();
            if (status.SendingTo is not { } target)
            {
                return "Сейчас играет здесь";
            }

            var why = status.Reason switch
            {
                AudioRouteReason.Pinned => "выбрано здесь",
                AudioRouteReason.Asked => $"{target} попросил",
                AudioRouteReason.Controls => $"{target} управляет этим устройством",
                AudioRouteReason.Watches => $"{target} показывает экран этого устройства",
                _ => null,
            };
            var line = why is null ? $"Сейчас играет на {target}" : $"Сейчас играет на {target}: {why}";
            return status.SpeakersMuted ? line + ". Динамики здесь выключены" : line;
        }
    }

    public async Task<bool> SetSoundHereAsync(string peerId, string peerName, IncomingSoundChoice choice)
    {
        var ok = await _audio.SetIncomingChoiceAsync(peerId, choice);
        StatusText = !ok
            ? $"Не удалось связаться с {peerName}."
            : choice switch
            {
                IncomingSoundChoice.Always => $"Звук {peerName} теперь играет здесь (если на {peerName} передача звука не выключена).",
                IncomingSoundChoice.Never => $"Звук {peerName} здесь не играет, пока вы не включите его снова.",
                _ => $"Звук {peerName} играет здесь, когда открыт его экран или вы управляете им.",
            };
        return ok;
    }

    /// <summary>The tray's on/off for another device's sound here: on asks for it, off turns it off here.</summary>
    public Task<bool> ToggleSoundHereAsync(string peerId, string peerName) =>
        SetSoundHereAsync(peerId, peerName, IsSoundFrom(peerId) || SoundHereChoice(peerId) == IncomingSoundChoice.Always
            ? IncomingSoundChoice.Never
            : IncomingSoundChoice.Always);

    /// <summary>Sends this device's sound to <paramref name="peerId"/> always, or back to automatic when null.</summary>
    public void SendSoundTo(string? peerId, string? peerName = null)
    {
        AudioSendEnabled = true;
        AudioPinnedTarget = peerId is null ? _noDevice : EnsureChoice(peerId, peerName);
        StatusText = peerId is null
            ? "Звук этого устройства следует за вводом и просмотром экрана."
            : $"Звук этого устройства играет на {peerName ?? peerId}.";
    }

    /// <summary>This device's microphone is being sent to that device.</summary>
    public bool IsMicrophoneSentTo(string peerId) => _audio.IsMicrophoneSentTo(peerId);

    /// <summary>
    /// Starts sending this device's microphone to <paramref name="peerId"/>, or stops if it already goes there.
    /// What it says back is for showing: where it is played there, or why it did not start.
    /// </summary>
    public async Task<string> ToggleMicrophoneAsync(string peerId, string peerName)
    {
        if (_audio.IsMicrophoneSentTo(peerId))
        {
            await _audio.StopMicrophoneAsync();
            return StatusText = "Микрофон этого устройства больше никуда не передаётся.";
        }

        var result = await _audio.SendMicrophoneToAsync(peerId);
        return StatusText = result.Outcome switch
        {
            MicrophoneOutcome.Started => $"Микрофон этого устройства передаётся на {peerName}: там он воспроизводится в «{result.Device}». В программе на {peerName} выберите микрофоном другой конец этого кабеля.",
            MicrophoneOutcome.Unreachable => $"Нет связи с {peerName}.",
            MicrophoneOutcome.NotSupported => $"{peerName} не ответил на предложение микрофона: вероятно, там версия WinMirroring старше 1.0.76.",
            MicrophoneOutcome.Refused => $"На {peerName} приём микрофона выключен (Параметры → Звук).",
            MicrophoneOutcome.NoOutputThere => $"На {peerName} нет виртуального аудиокабеля, в который можно воспроизвести микрофон. Поставьте там VB-CABLE (vb-audio.com/Cable) или выберите устройство в «Параметры → Звук».",
            MicrophoneOutcome.NoMicrophoneHere => "На этом устройстве нет микрофона, который удалось открыть.",
            _ => "Не удалось начать передачу микрофона: подробности в журнале.",
        };
    }

    public void KeepSoundHere()
    {
        AudioPinnedTarget = _noDevice;
        AudioSendEnabled = false;
        StatusText = "Звук этого устройства играет только здесь.";
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

    private bool _applyingAdminControl;

    /// <summary>Control of administrator windows via the elevated helper (a separate process, not this app elevated).</summary>
    [ObservableProperty]
    public partial bool AdminWindowControl { get; set; }

    [ObservableProperty]
    public partial string AdminWindowControlText { get; set; } =
        "Отдельный помощник с правами администратора вводит мышь, клавиатуру и касания с других устройств в окна администратора. Само приложение остаётся с обычными правами, поэтому перетаскивание и вставка файлов продолжают работать. При включении Windows один раз спросит права администратора.";

    partial void OnAdminWindowControlChanged(bool value)
    {
        if (!_initialized || _applyingAdminControl)
        {
            return;
        }

        _ = ((App)System.Windows.Application.Current).SetAdminWindowControlAsync(value);
    }

    /// <summary>Called by the app when the helper connects, fails or goes away, to reflect the real state.</summary>
    public void SetAdminControlState(bool active, string detail)
    {
        _applyingAdminControl = true;
        AdminWindowControl = active;
        _applyingAdminControl = false;
        AdminWindowControlText = detail;
    }

    /// <summary>Takes the shared cursor back (here, or from the device controlling this one).</summary>
    public string ReturnCursor()
    {
        var result = _input.ReturnCursor() ?? "Общий ввод сейчас никуда не передан.";
        StatusText = result;
        return result;
    }

    /// <summary>Hands this device's mouse and keyboard to a device, used from the control panel.</summary>
    public void ControlPeerById(string peerId, string peerName)
    {
        _input.ControlPeer(peerId);
        StatusText = $"Ввод передан на {peerName}. Вернуть: Ctrl+Alt+Home.";
    }

    // ---- files and versions of the other devices ---------------------------------------------------------

    private readonly Services.DeviceInfoService _devices;
    private readonly Services.PeerFilesService _peerFiles;

    /// <summary>"Версия 1.0.12 · здесь 1.0.13" — what each device runs, for the panel.</summary>
    public string VersionLineOf(string peerId)
    {
        var mine = Services.DeviceInfoService.LocalVersion.ToString(3);
        if (_devices.PeerVersion(peerId) is not { } theirs)
        {
            return $"Версия неизвестна · здесь {mine}";
        }

        return theirs.ToString(3) == mine ? $"Версия {mine}, как здесь" : $"Версия {theirs.ToString(3)} · здесь {mine}";
    }

    /// <summary>This device's build is newer, so it can update that device.</summary>
    public bool CanUpdatePeer(string peerId) => _devices.CanUpdate(peerId);

    public string LocalVersionText => Services.DeviceInfoService.LocalVersion.ToString(3);

    /// <summary>Asks for files here and sends them to a device to be opened there.</summary>
    public async Task OpenOnDeviceAsync(string peerId, string peerName)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Что открыть на {peerName}",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        StatusText = $"Отправляю на {peerName}…";
        StatusText = await _peerFiles.OpenOnDeviceAsync(peerId, peerName, dialog.FileNames);
    }

    /// <summary>Sends this device's build to a device running an older one (it asks its user before installing).</summary>
    public async Task UpdatePeerAsync(string peerId, string peerName)
    {
        StatusText = $"Упаковываю эту сборку для {peerName}…";
        StatusText = await _peerFiles.SendOwnBuildAsync(peerId, peerName);
    }
}
