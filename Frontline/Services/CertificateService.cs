using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace Frontline.Services;

internal sealed class CertificateService : ICertificateService
{
    private const string Subject = "CN=Frontline Code Signing";
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromDays(30);

    public async Task<string> EnsureCertificateAsync()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);

        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, Subject, false)
            .FirstOrDefault(c => c.NotAfter > DateTime.UtcNow + RenewalMargin);

        if (existing is not null)
        {
            var thumbprint = existing.Thumbprint ??
                             throw new InvalidOperationException("Existing certificate has no thumbprint");
            Log.Information("Found existing signing certificate ({Thumbprint})", thumbprint);
            return thumbprint;
        }

        Log.Information("No valid certificate found – generating new self-signed certificate");

        using var rsa = RSA.Create(4096);

        var request = new CertificateRequest(
            new X500DistinguishedName(Subject),
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);

        request.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new Oid("1.3.6.1.5.5.7.3.3") }, false)); // Code signing

        request.CertificateExtensions.Add(
            new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, false));

        var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        cert.FriendlyName = "Frontline Code Signing Cert";

        store.Add(cert);
        store.Close();

        await InstallToTrustStoresAsync(cert).ConfigureAwait(false);

        var createdThumbprint = cert.Thumbprint ??
                                throw new InvalidOperationException("Generated certificate has no thumbprint");
        Log.Information("Created certificate {Thumbprint}", createdThumbprint);
        return createdThumbprint;
    }

    private static async Task InstallToTrustStoresAsync(X509Certificate2 cert)
    {
        // TrustedPublisher & Root so SmartScreen will recognize it locally
        foreach (var target in new[] { StoreName.TrustedPublisher, StoreName.Root })
            try
            {
                using var ts = new X509Store(target, StoreLocation.CurrentUser);
                ts.Open(OpenFlags.ReadWrite);
                ts.Add(cert);
                Log.Information("Added certificate to {Store}", target);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed adding certificate to {Store}", target);
            }

        await Task.CompletedTask;
    }
}