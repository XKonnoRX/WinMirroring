using WinMirroring.Core.Identity;
using WinMirroring.Core.Pairing;

namespace WinMirroring.Cli;

internal sealed class ConsolePairingUi(bool autoAccept) : IPairingUserInterface
{
    public async Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct)
    {
        Console.WriteLine($"PAIR REQUEST from {prompt.PeerName} [{CertificateFingerprint.ToDisplay(prompt.PeerFingerprint)}]  PIN: {prompt.Pin}");
        if (autoAccept)
        {
            Console.WriteLine("Auto-accepting (--accept).");
            return true;
        }

        var answer = await ReadLineAsync("Accept? [y/N] ", ct).ConfigureAwait(false);
        return answer is not null && answer.Trim().StartsWith('y');
    }

    public Task<string?> RequestPinAsync(PairingPrompt prompt, CancellationToken ct) =>
        ReadLineAsync($"Enter the PIN shown on {prompt.PeerName}: ", ct);

    public void PairingFinished(PairingPrompt prompt, bool success, string? reason) =>
        Console.WriteLine(success
            ? $"PAIRING OK with {prompt.PeerName}"
            : $"PAIRING FAILED with {prompt.PeerName}: {reason}");

    private static async Task<string?> ReadLineAsync(string question, CancellationToken ct)
    {
        Console.Write(question);
        var read = Task.Run(Console.In.ReadLine, ct);
        try
        {
            return await read.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
