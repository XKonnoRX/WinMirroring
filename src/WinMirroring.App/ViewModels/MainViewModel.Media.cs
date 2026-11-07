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

    private void InitializeMedia()
    {
        var clipboard = _node.Settings.Clipboard;
        ClipboardEnabled = clipboard.Enabled;
        ClipboardShareFiles = clipboard.ShareFiles;
        ClipboardMaxInline = clipboard.MaxInlineMegabytes;
        ClipboardDirectPaste = clipboard.MaxDirectPasteMegabytes;
        ClipboardActivity = _clipboard.LastActivity;

        var audio = _node.Settings.Audio;
        AudioSendEnabled = audio.SendEnabled;
        AudioFollowInput = audio.FollowInput;
        AudioPinnedTarget = string.IsNullOrEmpty(audio.PinnedTargetId) ? _noDevice : EnsureChoice(audio.PinnedTargetId, null);
        AudioMuteLocal = audio.MuteLocalWhileSending;
        AudioReceiveEnabled = audio.ReceiveEnabled;
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
                : $"Звук этого устройства передаётся на {status.SendingTo} ({status.CaptureMode}).",
        };

        if (status.Receiving.Count == 0)
        {
            lines.Add("Звук с других устройств сейчас не принимается.");
        }

        foreach (var incoming in status.Receiving)
        {
            lines.Add(string.Create(CultureInfo.CurrentCulture,
                $"Играет звук с {incoming.PeerName}: в буфере {incoming.BufferedMs:F0} мс, потеряно пакетов {incoming.Lost}, провалов {incoming.Underruns}."));
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
        if (value is >= 1 and <= 1024)
        {
            SaveClipboard(s => s.MaxInlineMegabytes = value);
        }
    }

    partial void OnClipboardDirectPasteChanged(int value)
    {
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

    partial void OnAudioBitrateChanged(int value)
    {
        if (value is >= 32 and <= 510)
        {
            SaveAudio(s => s.BitrateKbps = value);
        }
    }

    partial void OnAudioBufferChanged(int value)
    {
        if (value is >= 10 and <= 500)
        {
            SaveAudio(s => s.BufferMs = value);
        }
    }
}
