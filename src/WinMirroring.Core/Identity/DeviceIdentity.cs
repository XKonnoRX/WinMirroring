using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace WinMirroring.Core.Identity;

/// <summary>
/// This device as seen by peers: a stable id, a display name and a self-signed ECDSA certificate.
/// The certificate is the TLS server certificate and also signs the client-side handshake challenge.
/// </summary>
public sealed class DeviceIdentity
{
    public DeviceIdentity(string id, string name, X509Certificate2 certificate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(certificate);
        if (!certificate.HasPrivateKey)
        {
            throw new ArgumentException("The identity certificate must carry its private key.", nameof(certificate));
        }

        Id = id;
        Name = name;
        Certificate = certificate;
        Fingerprint = CertificateFingerprint.Compute(certificate);
    }

    public string Id { get; }

    /// <summary>Display name; can be changed at runtime from settings.</summary>
    public string Name { get; set; }

    public X509Certificate2 Certificate { get; }

    public string Fingerprint { get; }

    public byte[] Sign(ReadOnlySpan<byte> data)
    {
        using var key = Certificate.GetECDsaPrivateKey()
            ?? throw new InvalidOperationException("The identity certificate has no ECDSA private key.");
        return key.SignData(data, HashAlgorithmName.SHA256);
    }

    public static bool Verify(X509Certificate2 certificate, ReadOnlySpan<byte> data, ReadOnlySpan<byte> signature)
    {
        using var key = certificate.GetECDsaPublicKey();
        return key is not null && key.VerifyData(data, signature, HashAlgorithmName.SHA256);
    }
}
