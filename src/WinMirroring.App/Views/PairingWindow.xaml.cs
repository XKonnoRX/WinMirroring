using System.Windows;
using WinMirroring.Core.Identity;
using WinMirroring.Core.Pairing;

namespace WinMirroring.App.Views;

public enum PairingWindowMode
{
    /// <summary>Responder: show the PIN, ask whether to accept.</summary>
    ShowPin,

    /// <summary>Initiator: ask for the PIN displayed on the other device.</summary>
    EnterPin,
}

public partial class PairingWindow : Window
{
    private readonly PairingWindowMode _mode;

    public PairingWindow(PairingPrompt prompt, PairingWindowMode mode)
    {
        InitializeComponent();
        _mode = mode;

        PeerFingerprintText.Text = CertificateFingerprint.ToDisplay(prompt.PeerFingerprint);
        LocalFingerprintText.Text = CertificateFingerprint.ToDisplay(prompt.LocalFingerprint);

        if (mode == PairingWindowMode.ShowPin)
        {
            TitleText.Text = $"{prompt.PeerName} хочет сопрячься с этим устройством";
            DescriptionText.Text = "Введите этот PIN на другом устройстве. Сравните отпечатки, если сеть не доверенная.";
            PinText.Text = prompt.Pin ?? string.Empty;
            PinText.Visibility = Visibility.Visible;
            HintText.Text = "Запрос действует две минуты.";
            AcceptButton.Content = "Принять";
            RejectButton.Content = "Отклонить";
        }
        else
        {
            TitleText.Text = $"Сопряжение с {prompt.PeerName}";
            DescriptionText.Text = "На другом устройстве сейчас показан PIN из шести цифр. Введите его здесь.";
            PinBox.Visibility = Visibility.Visible;
            HintText.Text = "PIN показывается на другом устройстве две минуты.";
            AcceptButton.Content = "Подтвердить";
            RejectButton.Content = "Отмена";
            Loaded += (_, _) => PinBox.Focus();
        }

        Loaded += (_, _) => Activate();
    }

    public string? EnteredPin { get; private set; }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        if (_mode == PairingWindowMode.EnterPin)
        {
            var pin = PairingCrypto.NormalizePin(PinBox.Text);
            if (pin.Length != 6)
            {
                HintText.Text = "PIN состоит из шести цифр.";
                PinBox.Focus();
                return;
            }

            EnteredPin = pin;
        }

        DialogResult = true;
    }
}
