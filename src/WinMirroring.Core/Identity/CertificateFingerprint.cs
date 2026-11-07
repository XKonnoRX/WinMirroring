using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinMirroring.Core.Identity;

/// <summary>SHA-256 over the DER encoding of a certificate, upper-case hex. This is the device identity on the wire.</summary>
public static class CertificateFingerprint
{
    public static string Compute(X509Certificate2 certificate) => Compute(certificate.RawData);

    public static string Compute(ReadOnlySpan<byte> der) => Convert.ToHexString(SHA256.HashData(der));

    public static bool AreEqual(string? left, string? right) =>
        !string.IsNullOrEmpty(left) && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    /// <summary>Short human-comparable form: the first 16 hex digits in groups of four.</summary>
    public static string ToDisplay(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
        {
            return string.Empty;
        }

        var head = fingerprint.Length > 16 ? fingerprint[..16] : fingerprint;
        var groups = new List<string>();
        for (var i = 0; i < head.Length; i += 4)
        {
            groups.Add(head.Substring(i, Math.Min(4, head.Length - i)));
        }

        return string.Join(' ', groups);
    }
}
