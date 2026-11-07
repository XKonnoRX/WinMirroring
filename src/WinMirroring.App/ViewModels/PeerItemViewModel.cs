using CommunityToolkit.Mvvm.ComponentModel;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Peers;

namespace WinMirroring.App.ViewModels;

public sealed partial class PeerItemViewModel : ObservableObject
{
    public PeerItemViewModel(PeerInfo info)
    {
        Id = info.Id;
        Update(info);
    }

    public string Id { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Address { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsPaired { get; set; }

    [ObservableProperty]
    public partial bool HasAddress { get; set; }

    [ObservableProperty]
    public partial string PairedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FingerprintDisplay { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string LastError { get; set; } = string.Empty;

    public void Update(PeerInfo info)
    {
        Name = string.IsNullOrWhiteSpace(info.Name) ? info.Id[..Math.Min(8, info.Id.Length)] : info.Name;
        StateText = Describe(info.State);
        Address = info.TransportEndPoint?.ToString() ?? string.Empty;
        IsPaired = info.IsPaired;
        HasAddress = info.TransportEndPoint is not null;
        PairedText = info.IsPaired ? "да" : "нет";
        FingerprintDisplay = CertificateFingerprint.ToDisplay(info.Fingerprint);
        LastError = info.LastError ?? string.Empty;
    }

    private static string Describe(PeerState state) => state switch
    {
        PeerState.Offline => "не в сети",
        PeerState.Discovered => "в сети",
        PeerState.Connecting => "подключение…",
        PeerState.Connected => "подключено, без сопряжения",
        PeerState.Pairing => "сопряжение…",
        PeerState.Ready => "готово",
        _ => state.ToString(),
    };
}
