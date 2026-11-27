using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using WinMirroring.Audio;
using WinMirroring.Clipboard;

namespace WinMirroring.App.ViewModels;

/// <summary>Clipboard and sound settings and status.</summary>
public sealed partial class MainViewModel
{
    private readonly ClipboardSyncService _clipboard;
    private readonly AudioService _audio;
    private bool _mediaInitialized;

    [ObservableProperty]
    public partial bool ClipboardEnabled { get; set; }

    [ObservableProperty]
    public partial bool ClipboardShareFiles { get; set; }

    [ObservableProperty]
    public partial int ClipboardMaxInline { get; set; }

    [ObservableProperty]
    public partial int ClipboardDirectPaste { get; set; }

    [ObservableProperty]
    public partial string ClipboardActivity { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ClipboardDragDrop { get; set; }

    partial void OnClipboardDragDropChanged(bool value) => SaveClipboard(s => s.DragDropEnabled = value);

    [ObservableProperty]
    public partial bool AudioSendEnabled { get; set; }

    [ObservableProperty]
    public partial bool AudioFollowInput { get; set; }

    [ObservableProperty]
    public partial DeviceChoice? AudioPinnedTarget { get; set; }

    [ObservableProperty]
    public partial bool AudioMuteLocal { get; set; }

    [ObservableProperty]
    public partial bool AudioReceiveEnabled { get; set; }

    [ObservableProperty]
    public partial int AudioBitrate { get; set; }

    [ObservableProperty]
    public partial int AudioBuffer { get; set; }

    [ObservableProperty]
    public partial string AudioStatusText { get; set; } = string.Empty;

    /// <summary>Other devices may send their microphone here (it is played into a virtual cable).</summary>
    [ObservableProperty]
    public partial bool AudioAcceptMicrophone { get; set; }

    /// <summary>The output such a microphone is played into: its id, or empty for "find a virtual cable".</summary>
    [ObservableProperty]
    public partial string AudioMicrophoneOutput { get; set; } = string.Empty;

    private IReadOnlyList<WinMirroring.Audio.Playback.AudioOutputDevice> _outputs = [];
    private long _outputsReadTicks = long.MinValue / 2;

    /// <summary>This device's sound outputs. Asking the sound system takes a moment, so an answer is kept for a few seconds.</summary>
    private IReadOnlyList<WinMirroring.Audio.Playback.AudioOutputDevice> SoundOutputs
    {
        get
        {
            if (Environment.TickCount64 - _outputsReadTicks > 5000)
            {
                _outputs = WinMirroring.Audio.Playback.MicrophoneOutput.List();
                _outputsReadTicks = Environment.TickCount64;
            }

            return _outputs;
        }
    }

    /// <summary>What can be chosen as the place for another device's microphone: "find a cable", then every output.</summary>
    public IReadOnlyList<(string Id, string Name)> MicrophoneOutputChoices =>
        [(string.Empty, "Автоматически: виртуальный аудиокабель"), .. SoundOutputs.Select(o => (o.Id, o.Name))];

    /// <summary>Where such a microphone would go right now, and what to do about it, in a line or two.</summary>
    public string MicrophoneOutputNote
    {
        get
        {
            var picked = WinMirroring.Audio.Playback.MicrophoneOutput.Pick(SoundOutputs, AudioMicrophoneOutput);
            if (picked is null)
            {
                var none = "Подходящего виртуального аудиокабеля на этом устройстве нет, поэтому принять чужой микрофон некуда: Windows не даёт программе самой быть микрофоном. Поставьте VB-CABLE (vb-audio.com/Cable) — после установки он найдётся сам.";
                return SoundOutputs.FirstOrDefault(o => o.Distorts) is { } poor
                    ? none + $" «{poor.Name}» здесь есть, но сам не выбирается: два его конца настроены на разный формат, и звук через него искажается. Он станет годным, если в настройках звука Windows выставить обоим концам одинаковые частоту и число каналов."
                    : none;
            }

            var where = $"Микрофон другого устройства будет воспроизводиться в «{picked.Name}». В программе, которая должна его слышать, выберите микрофоном другой конец этого кабеля (для VB-CABLE это «CABLE Output»).";
            return picked.Distorts
                ? where + " Внимание: два конца этого кабеля настроены на разный формат, и звук через него искажается, пока в настройках звука Windows им не выставлены одинаковые частота и число каналов."
                : where;
        }
    }

    private void InitializeMedia()
    {
        var clipboard = _node.Settings.Clipboard;
        ClipboardEnabled = clipboard.Enabled;
        ClipboardShareFiles = clipboard.ShareFiles;
        ClipboardMaxInline = clipboard.MaxInlineMegabytes;
        ClipboardDirectPaste = clipboard.MaxDirectPasteMegabytes;
        ClipboardDragDrop = clipboard.DragDropEnabled;
        ClipboardActivity = _clipboard.LastActivity;

        var audio = _node.Settings.Audio;
        AudioSendEnabled = audio.SendEnabled;
        AudioFollowInput = audio.FollowInput;
        AudioPinnedTarget = string.IsNullOrEmpty(audio.PinnedTargetId) ? _noDevice : EnsureChoice(audio.PinnedTargetId, null);
        AudioMuteLocal = audio.MuteLocalWhileSending;
        AudioReceiveEnabled = audio.ReceiveEnabled;
        AudioAcceptMicrophone = audio.AcceptMicrophone;
        AudioMicrophoneOutput = audio.MicrophoneOutput;
        AudioBitrate = audio.BitrateKbps;
        AudioBuffer = audio.BufferMs;

        _clipboard.Activity += text => _dispatcher.InvokeAsync(() => ClipboardActivity = text);
        _audio.StatusChanged += () => _dispatcher.InvokeAsync(RefreshMediaStatus);
        RefreshMediaStatus();
        _mediaInitialized = true;
    }

    private void RefreshMediaStatus()
    {
        var status = _audio.GetStatus();
        var lines = new List<string>
        {
            status.SendingTo is null
                ? "Звук этого устройства играет здесь."
                : $"Звук этого устройства. {OwnSoundLine} ({status.CaptureMode}).",
        };

        if (status.Receiving.Count == 0)
        {
            lines.Add("Звук с других устройств сейчас не принимается.");
        }

        foreach (var incoming in status.Receiving)
        {
            lines.Add(string.Create(CultureInfo.CurrentCulture,
                $"Играет звук с {incoming.PeerName}: в буфере {incoming.BufferedMs:F0} из {incoming.AimedMs} мс, " +
                $"разброс прихода {incoming.JitterMs:F1} мс, потеряно пакетов {incoming.Lost} (восстановлено {incoming.Recovered}), " +
                $"провалов {incoming.Underruns}."));
        }

        if (status.MicrophoneTo is { } microphoneTo)
        {
            lines.Add($"Микрофон этого устройства передаётся на {microphoneTo}.");
        }

        if (status.MicrophonesHere is { Count: > 0 } microphones)
        {
            lines.Add(status.MicrophoneDevice is { } device
                ? $"Микрофон {string.Join(", ", microphones)} воспроизводится в «{device}»."
                : $"Микрофон {string.Join(", ", microphones)} приходит, но воспроизвести его некуда: нет виртуального аудиокабеля.");
        }

        AudioStatusText = string.Join(Environment.NewLine, lines);
    }

    private void SaveClipboard(Action<Core.Settings.ClipboardSettings> change)
    {
        if (!_mediaInitialized)
        {
            return;
        }

        change(_node.Settings.Clipboard);
        _node.SaveSettings();
    }

    private void SaveAudio(Action<Core.Settings.AudioSettings> change)
    {
        if (!_mediaInitialized)
        {
            return;
        }

        change(_node.Settings.Audio);
        _node.SaveSettings();
        _audio.ApplySettings();
    }

    partial void OnClipboardEnabledChanged(bool value) => SaveClipboard(s => s.Enabled = value);

    partial void OnClipboardShareFilesChanged(bool value) => SaveClipboard(s => s.ShareFiles = value);

    partial void OnClipboardMaxInlineChanged(int value)
    {
        if (Math.Clamp(value, 1, 1024) is var kept && kept != value)
        {
            ClipboardMaxInline = kept; // out of range: the nearest value that is in range, which the page then shows
            return;
        }

        if (value is >= 1 and <= 1024)
        {
            SaveClipboard(s => s.MaxInlineMegabytes = value);
        }
    }

    partial void OnClipboardDirectPasteChanged(int value)
    {
        if (Math.Clamp(value, 0, 4096) is var kept && kept != value)
        {
            ClipboardDirectPaste = kept; // out of range: the nearest value that is in range, which the page then shows
            return;
        }

        if (value is >= 0 and <= 4096)
        {
            SaveClipboard(s => s.MaxDirectPasteMegabytes = value);
        }
    }

    partial void OnAudioSendEnabledChanged(bool value) => SaveAudio(s => s.SendEnabled = value);

    partial void OnAudioFollowInputChanged(bool value) => SaveAudio(s => s.FollowInput = value);

    partial void OnAudioPinnedTargetChanged(DeviceChoice? value) =>
        SaveAudio(s => s.PinnedTargetId = value is null || value.IsEmpty || value.Id == _node.Identity.Id ? string.Empty : value.Id);

    partial void OnAudioMuteLocalChanged(bool value) => SaveAudio(s => s.MuteLocalWhileSending = value);

    partial void OnAudioReceiveEnabledChanged(bool value) => SaveAudio(s => s.ReceiveEnabled = value);

    partial void OnAudioAcceptMicrophoneChanged(bool value) => SaveAudio(s => s.AcceptMicrophone = value);

    partial void OnAudioMicrophoneOutputChanged(string value) => SaveAudio(s => s.MicrophoneOutput = value ?? string.Empty);

    partial void OnAudioBitrateChanged(int value)
    {
        if (Math.Clamp(value, 32, 510) is var kept && kept != value)
        {
            AudioBitrate = kept; // out of range: the nearest value that is in range, which the page then shows
            return;
        }

        if (value is >= 32 and <= 510)
        {
            SaveAudio(s => s.BitrateKbps = value);
        }
    }

    partial void OnAudioBufferChanged(int value)
    {
        if (Math.Clamp(value, 10, 500) is var kept && kept != value)
        {
            AudioBuffer = kept; // out of range: the nearest value that is in range, which the page then shows
            return;
        }

        if (value is >= 10 and <= 500)
        {
            SaveAudio(s => s.BufferMs = value);
        }
    }
}
