using System.Windows;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Pairing;

namespace WinMirroring.App.Views;

/// <summary>Another device asks to pair: accept or reject. Nothing to type.</summary>
public partial class PairingWindow : Window
{
    public PairingWindow(PairingPrompt prompt)
    {
        InitializeComponent();
        TitleText.Text = $"{prompt.PeerName} хочет сопрячься с этим устройством";
        DescriptionText.Text = $"Если сопряжение начали вы на {prompt.PeerName}, нажмите «Принять». После этого устройства будут подключаться друг к другу сами.";
        CodeText.Text = prompt.ConfirmationCode;
        PeerFingerprintRun.Text = CertificateFingerprint.ToDisplay(prompt.PeerFingerprint);
        LocalFingerprintRun.Text = CertificateFingerprint.ToDisplay(prompt.LocalFingerprint);
        Loaded += (_, _) => Activate();
    }

    private void OnAccept(object sender, RoutedEventArgs e) => DialogResult = true;
}
