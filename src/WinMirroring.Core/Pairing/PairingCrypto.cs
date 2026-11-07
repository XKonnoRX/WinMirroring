using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace WinMirroring.Core.Pairing;

/// <summary>
/// Pairing helpers. The handshake already proves both devices own their certificates; pairing only records the
/// peer's certificate after the user on the other device accepts. To let a careful user notice a device in the
/// middle impersonating the other one, both sides show the same short code derived from both certificates; it is
/// only for comparing at a glance, nothing has to be typed.
/// </summary>
public static class PairingCrypto
{
    private static readonly byte[] CodeDomain = Encoding.UTF8.GetBytes("WinMirroring-confirmation-v1");

    public static byte[] GenerateNonce() => RandomNumberGenerator.GetBytes(32);

    /// <summary>Six digits from both fingerprints; the same on both devices regardless of who asked.</summary>
    public static string ConfirmationCode(string fingerprintA, string fingerprintB)
    {
        var pair = new[] { fingerprintA.ToUpperInvariant(), fingerprintB.ToUpperInvariant() };
        Array.Sort(pair, StringComparer.Ordinal);

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(CodeDomain);
        hash.AppendData(Encoding.ASCII.GetBytes(pair[0]));
        hash.AppendData(Encoding.ASCII.GetBytes(pair[1]));
        var digest = hash.GetHashAndReset();

        var value = BitConverter.ToUInt32(digest, 0) % 1_000_000;
        var digits = value.ToString("D6", CultureInfo.InvariantCulture);
        return $"{digits[..3]} {digits[3..]}";
    }
}
