using System.Windows;
using WinMirroring.App.Views;
using WinMirroring.Core.Pairing;

namespace WinMirroring.App.Services;

/// <summary>Shows the pairing dialogs on the UI thread; called by the node from background threads.</summary>
public sealed class DialogPairingUi : IPairingUserInterface
{
    public Action<string, string>? Notify { get; set; }

    public async Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct)
    {
        var result = await ShowAsync(prompt, PairingWindowMode.ShowPin, ct);
        return result.Accepted;
    }

    public async Task<string?> RequestPinAsync(PairingPrompt prompt, CancellationToken ct)
    {
        var result = await ShowAsync(prompt, PairingWindowMode.EnterPin, ct);
        return result.Accepted ? result.Pin : null;
    }

    public void PairingFinished(PairingPrompt prompt, bool success, string? reason)
    {
        var text = success
            ? $"Сопряжение с {prompt.PeerName} выполнено."
            : $"Сопряжение с {prompt.PeerName} не выполнено ({Describe(reason)}).";
        Notify?.Invoke("WinMirroring", text);
    }

    private static Task<(bool Accepted, string? Pin)> ShowAsync(PairingPrompt prompt, PairingWindowMode mode, CancellationToken ct)
    {
        var dispatcher = Application.Current.Dispatcher;
        var completion = new TaskCompletionSource<(bool, string?)>(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = dispatcher.InvokeAsync(() =>
        {
            var window = new PairingWindow(prompt, mode);
            using var registration = ct.Register(() => window.Dispatcher.InvokeAsync(() =>
            {
                if (window.IsVisible)
                {
                    window.DialogResult = false;
                }
            }));

            var accepted = window.ShowDialog() == true;
            completion.TrySetResult((accepted, window.EnteredPin));
        });

        return completion.Task;
    }

    public static string Describe(string? reason) => reason switch
    {
        "rejected" => "отклонено на другом устройстве",
        "wrong-pin" => "неверный PIN",
        "cancelled" => "отменено",
        "timeout" => "время ожидания истекло",
        "not-pairable" => "другое устройство сейчас не может сопрягаться",
        "connection-lost" => "соединение потеряно",
        "responder-proof-mismatch" => "другое устройство не подтвердило PIN",
        null => "без причины",
        _ => reason,
    };
}
