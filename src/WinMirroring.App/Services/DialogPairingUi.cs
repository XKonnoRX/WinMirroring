using System.Windows;
using WinMirroring.App.Views;
using WinMirroring.Core.Pairing;

namespace WinMirroring.App.Services;

/// <summary>Pairing UI: a confirmation dialog on the device being asked, status text on the device asking.</summary>
public sealed class DialogPairingUi : IPairingUserInterface
{
    /// <summary>Tray balloon (title, text); set by the app once the tray icon exists.</summary>
    public Action<string, string>? Notify { get; set; }

    /// <summary>Status line for the main window.</summary>
    public event Action<string>? StatusChanged;

    public Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct)
    {
        var dispatcher = Application.Current.Dispatcher;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = dispatcher.InvokeAsync(() =>
        {
            var window = new PairingWindow(prompt);
            using var registration = ct.Register(() => window.Dispatcher.InvokeAsync(() =>
            {
                if (window.IsVisible)
                {
                    window.DialogResult = false;
                }
            }));

            completion.TrySetResult(window.ShowDialog() == true);
        });

        return completion.Task;
    }

    public void PairingStarted(PairingPrompt prompt)
    {
        var text = $"Подтвердите сопряжение на {prompt.PeerName}. Код на обоих устройствах: {prompt.ConfirmationCode}";
        StatusChanged?.Invoke(text);
        Notify?.Invoke("WinMirroring", text);
    }

    public void PairingFinished(PairingPrompt prompt, bool success, string? reason)
    {
        var text = success
            ? $"Сопряжение с {prompt.PeerName} выполнено."
            : $"Сопряжение с {prompt.PeerName} не выполнено ({Describe(reason)}).";
        StatusChanged?.Invoke(text);
        Notify?.Invoke("WinMirroring", text);
    }

    public static string Describe(string? reason) => reason switch
    {
        "rejected" => "отклонено на другом устройстве",
        "cancelled" => "отменено",
        "timeout" => "на другом устройстве не ответили за две минуты",
        "not-pairable" => "другое устройство сейчас не может сопрягаться",
        "connection-lost" => "соединение потеряно",
        null => "без причины",
        _ => reason,
    };
}
