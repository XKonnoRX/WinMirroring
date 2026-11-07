using WinMirroring.Core.Identity;
using WinMirroring.Core.Pairing;

namespace WinMirroring.Cli;

internal sealed class ConsolePairingUi(bool autoAccept) : IPairingUserInterface
{
    public async Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct)
    {
        Console.WriteLine($"PAIR REQUEST from {prompt.PeerName} [{CertificateFingerprint.ToDisplay(prompt.PeerFingerprint)}]  code: {prompt.ConfirmationCode}");
        if (autoAccept)
        {
            Console.WriteLine("Auto-accepting (--accept).");
            return true;
        }

        Console.Write("Accept? [y/N] ");
        var read = Task.Run(Console.In.ReadLine, ct);
        try
        {
            var answer = await read.WaitAsync(ct).ConfigureAwait(false);
            return answer is not null && answer.Trim().StartsWith('y');
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public void PairingStarted(PairingPrompt prompt) =>
        Console.WriteLine($"PAIRING waiting for {prompt.PeerName} to accept, code: {prompt.ConfirmationCode}");

    public void PairingFinished(PairingPrompt prompt, bool success, string? reason) =>
        Console.WriteLine(success
            ? $"PAIRING OK with {prompt.PeerName}"
            : $"PAIRING FAILED with {prompt.PeerName}: {reason}");
}
