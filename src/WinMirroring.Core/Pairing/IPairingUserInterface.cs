namespace WinMirroring.Core.Pairing;

/// <summary>What the UI needs to show while pairing. <see cref="Pin"/> is set on the side that displays it.</summary>
public sealed record PairingPrompt(
    string PeerId,
    string PeerName,
    string PeerFingerprint,
    string LocalFingerprint,
    string? Pin);

/// <summary>Implemented by the app (dialogs) and the CLI (console). Called off the UI thread.</summary>
public interface IPairingUserInterface
{
    /// <summary>Responder side: show the PIN and ask whether to accept this peer.</summary>
    Task<bool> ConfirmIncomingPairingAsync(PairingPrompt prompt, CancellationToken ct);

    /// <summary>Initiator side: ask the user for the PIN displayed on the other device. Null cancels.</summary>
    Task<string?> RequestPinAsync(PairingPrompt prompt, CancellationToken ct);

    /// <summary>Both sides: pairing ended, successfully or not.</summary>
    void PairingFinished(PairingPrompt prompt, bool success, string? reason)
    {
    }
}
