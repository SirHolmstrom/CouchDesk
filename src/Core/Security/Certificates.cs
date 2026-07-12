using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Core.Config;

namespace Core.Security;

/// <summary>
/// Generates and persists a self-signed TLS certificate whose SAN list covers the
/// LAN hostname, localhost, the LAN IP and loopback — so the browser can match
/// whichever address you connect by. The PFX password is random and stored
/// DPAPI-protected next to the PFX.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Certificates
{
    // UserKeySet    -> private key in the CURRENT USER's container, so no admin is
    //                  needed (unlike MachineKeySet).
    // PersistKeySet -> key persists in a real container Schannel/Kestrel can read for
    //                  the handshake (EphemeralKeySet does NOT survive into Kestrel).
    // Cost: each load drops a small key-container file under the user profile.
    // Negligible here; delete the CouchDesk app-data certs folder to reset.
    private const X509KeyStorageFlags StorageFlags =
        X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.PersistKeySet;

    public static X509Certificate2 LoadOrCreate(string hostname, IPAddress lanIp)
    {
        if (File.Exists(AppPaths.CertPfx) && File.Exists(AppPaths.CertPwd))
        {
            string password = SecretStore.UnprotectFromFile(AppPaths.CertPwd);
            return new X509Certificate2(AppPaths.CertPfx, password, StorageFlags);
        }

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={hostname}", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        var subjectAltNames = new SubjectAlternativeNameBuilder();
        subjectAltNames.AddDnsName(hostname);
        subjectAltNames.AddDnsName("localhost");
        subjectAltNames.AddIpAddress(lanIp);
        subjectAltNames.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(subjectAltNames.Build());

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, true)); // serverAuth

        var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(2));

        string pfxPassword = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(AppPaths.CertPfx, certificate.Export(X509ContentType.Pfx, pfxPassword));
        SecretStore.ProtectToFile(AppPaths.CertPwd, pfxPassword);

        // Re-load from the PFX so the returned cert's private key lives in a key
        // container Schannel can use for the TLS handshake.
        return new X509Certificate2(AppPaths.CertPfx, pfxPassword, StorageFlags);
    }

    /// <summary>
    /// Installs the (public) cert into the CurrentUser "Trusted Root" store so the
    /// browser trusts both the HTTPS page and the wss:// stream. No admin needed.
    /// Without this, Chrome lets you click past the page warning but still refuses
    /// the WebSocket.
    /// </summary>
    public static void EnsureTrusted(X509Certificate2 certificate)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.CurrentUser);
            store.Open(OpenFlags.ReadWrite);

            var existing = store.Certificates.Find(
                X509FindType.FindByThumbprint, certificate.Thumbprint, false);

            if (existing.Count == 0)
            {
                // Public part only — a trust anchor doesn't need the private key.
                using var publicOnly = new X509Certificate2(certificate.Export(X509ContentType.Cert));
                store.Add(publicOnly);
            }
        }
        catch { /* if this fails, the user can still proceed past the warning */ }
    }
}
