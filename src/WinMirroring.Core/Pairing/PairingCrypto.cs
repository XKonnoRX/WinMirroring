using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinMirroring.Core.Pairing;

public enum PairingRole
{
    Initiator,
    Responder,
}

/// <summary>
/// PIN pairing: both sides derive a key from the PIN and exchange HMACs over both certificate fingerprints and a
/// responder nonce, so each side proves it knew the PIN and saw the same two certificates.
/// </summary>
public static class PairingCrypto
{
    private static readonly byte[] KeyDomain = Encoding.UTF8.GetBytes("WinMirroring-pairing-v1");

    public static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);

    public static byte[] GenerateNonce() => RandomNumberGenerator.GetBytes(32);

    public static string NormalizePin(string pin) =>
        new(pin.Where(char.IsAsciiDigit).ToArray());

    public static byte[] DeriveKey(string pin)
    {
        ArgumentNullException.ThrowIfNull(pin);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(KeyDomain);
        hash.AppendData(Encoding.UTF8.GetBytes(NormalizePin(pin)));
        return hash.GetHashAndReset();
    }

    public static byte[] ComputeProof(
        byte[] key,
        PairingRole role,
        string initiatorFingerprint,
        string responderFingerprint,
        ReadOnlySpan<byte> nonce)
    {
        using var hmac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, key);
        hmac.AppendData(Encoding.ASCII.GetBytes(role == PairingRole.Initiator ? "initiator" : "responder"));
        hmac.AppendData(Encoding.ASCII.GetBytes(initiatorFingerprint.ToUpperInvariant()));
        hmac.AppendData(Encoding.ASCII.GetBytes(responderFingerprint.ToUpperInvariant()));
        hmac.AppendData(nonce);
        return hmac.GetHashAndReset();
    }

    public static bool ProofMatches(byte[] expected, string? receivedBase64)
    {
        if (string.IsNullOrEmpty(receivedBase64))
        {
            return false;
        }

        byte[] received;
        try
        {
            received = Convert.FromBase64String(receivedBase64);
        }
        catch (FormatException)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, received);
    }
}
