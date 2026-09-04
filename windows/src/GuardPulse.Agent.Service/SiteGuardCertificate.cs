namespace GuardPulse.Agent.Service;

using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;

/// <summary>
/// Creates (once) and loads the self-signed TLS certificate the BlocklistServer serves
/// https://127.0.0.1 with. Chromium validates extension-update and extension-fetch
/// traffic against the SYSTEM trust store, so the public half is installed into
/// LocalMachine\Root by this service (running as SYSTEM); the PFX stays in the state
/// directory locked to SYSTEM/Admins. Regenerated automatically if the file is lost.
/// </summary>
public static class SiteGuardCertificate
{
    public const string PfxFileName = "https-cert.pfx";
    private const string PfxPassword = "guardpulse-loopback";
    private const string Subject = "CN=127.0.0.1";

    public static X509Certificate2? Ensure(string stateDir, ILogger logger)
    {
        var pfxPath = Path.Combine(stateDir, PfxFileName);
        try
        {
            if (!File.Exists(pfxPath))
            {
                var cert = CreateSelfSigned();
                File.WriteAllBytes(pfxPath, cert.Export(X509ContentType.Pfx, PfxPassword));
                InstallTrustedRoot(cert, logger);
                logger.LogInformation("Site Guard loopback TLS certificate created and trusted");
            }

            // Default flags (user key store, persisted): EphemeralKeySet breaks
            // TLS handshakes in a service session (no user profile to write into).
            var loaded = new X509Certificate2(pfxPath, PfxPassword);
            if (loaded.NotAfter < DateTimeOffset.UtcNow.AddDays(30))
            {
                logger.LogWarning("Site Guard TLS certificate expires {Date}; regenerate https-cert.pfx", loaded.NotAfter);
            }

            return loaded;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Site Guard TLS certificate unavailable — extension updates over https will fail");
            return null;
        }
    }

    private static X509Certificate2 CreateSelfSigned()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(Subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        // SAN must cover exactly how the browser addresses us: IP 127.0.0.1 + localhost.
        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Parse("127.0.0.1"));
        request.CertificateExtensions.Add(sanBuilder.Build());

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true)); // server auth

        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10));
    }

    private static void InstallTrustedRoot(X509Certificate2 cert, ILogger logger)
    {
        try
        {
            using var store = new X509Store(StoreName.Root, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadWrite);
            if (store.Certificates.Find(X509FindType.FindByThumbprint, cert.Thumbprint, validOnly: false).Count == 0)
            {
                store.Add(cert);
            }

            store.Close();
        }
        catch (Exception ex)
        {
            // If the root install fails, browsers will reject the TLS handshake; the
            // loopback https endpoint stays up but extension updates can't complete.
            logger.LogWarning(ex, "Could not install Site Guard root certificate into LocalMachine\\Root");
        }
    }
}
