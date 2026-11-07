using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WinMirroring.Core.Protocol;
using WinMirroring.Core.Settings;

namespace WinMirroring.Core.Identity;

/// <summary>
/// Creates and loads the device certificate. The key lives in the CurrentUser\My store because
/// SChannel refuses ephemeral keys for TLS server authentication.
/// </summary>
public static class DeviceIdentityStore
{
    // Only used to carry the freshly generated key into the certificate store; never written to disk.
    private const string TransientPfxPassword = "winmirroring-transient";

    private static readonly Oid ServerAuthentication = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuthentication = new("1.3.6.1.5.5.7.3.2");

    /// <summary>Loads the identity described by <paramref name="settings"/>, creating missing pieces. Returns true when settings changed.</summary>
    public static bool LoadOrCreate(AppSettings settings, out DeviceIdentity identity)
    {
        var changed = false;

        if (string.IsNullOrWhiteSpace(settings.DeviceId))
        {
            settings.DeviceId = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(settings.DeviceName))
        {
            settings.DeviceName = Environment.MachineName;
            changed = true;
        }

        X509Certificate2? certificate = null;
        if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
        {
            certificate = FindInStore(settings.CertificateThumbprint);
        }

        if (certificate is null || !certificate.HasPrivateKey)
        {
            certificate?.Dispose();
            certificate = CreateAndStore(settings.DeviceId);
            settings.CertificateThumbprint = certificate.Thumbprint;
            changed = true;
        }

        identity = new DeviceIdentity(settings.DeviceId, settings.DeviceName, certificate);
        return changed;
    }

    public static X509Certificate2? FindInStore(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadOnly);

        X509Certificate2? result = null;
        foreach (var candidate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            if (result is null)
            {
                result = candidate;
            }
            else
            {
                candidate.Dispose();
            }
        }

        return result;
    }

    public static X509Certificate2 CreateAndStore(string deviceId)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN=WinMirroring {deviceId}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyAgreement, critical: false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { ServerAuthentication, ClientAuthentication }, critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, critical: false));

        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(ProtocolConstants.TlsTargetHost);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        using var ephemeral = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(20));
        var pfx = ephemeral.Export(X509ContentType.Pfx, TransientPfxPassword);

        var persisted = X509CertificateLoader.LoadPkcs12(
            pfx,
            TransientPfxPassword,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);

        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        store.Add(persisted);
        return persisted;
    }

    public static void RemoveFromStore(string thumbprint)
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);
        foreach (var certificate in store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false))
        {
            store.Remove(certificate);
            certificate.Dispose();
        }
    }
}
