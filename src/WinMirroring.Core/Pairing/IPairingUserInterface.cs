namespace WinMirroring.Core.Pairing;

/// <summary>What the UI needs to show while pairing. Both devices get the same <see cref="ConfirmationCode"/>.</summary>
public sealed record PairingPrompt(
    string PeerId,
    string PeerName,
    string PeerFingerprint,
    string LocalFingerprint,
    string ConfirmationCode);

/// <summary>Implemented by the app (dialogs) and the CLI (console). Called off the UI thread.</summary>
public interface IPairingUserInterface
{
    /// <summary>The other device asks to pair: ask the user here to accept or reject.</summary>
    Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct);

    /// <summary>This device asked to pair and now waits for the user on the other device. Nothing to answer.</summary>
    void PairingStarted(PairingPrompt prompt)
    {
    }

    /// <summary>Both sides: pairing ended, successfully or not.</summary>
    void PairingFinished(PairingPrompt prompt, bool success, string? reason)
    {
    }
}
