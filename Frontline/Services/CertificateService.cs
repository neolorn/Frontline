using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Serilog;

namespace Frontline.Services;

internal sealed class CertificateService : ICertificateService
{
    private const string Subject = "CN=Frontline Code Signing";
    private static readonly TimeSpan RenewalMargin = TimeSpan.FromDays(30);
    private const string CodeSigningOid = "1.3.6.1.5.5.7.3.3";

    public async Task<string> EnsureCertificateAsync()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite | OpenFlags.OpenExistingOnly);

        var candidates = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, Subject, false)
            .Cast<X509Certificate2>()
            .Where(c => c.NotAfter > DateTime.UtcNow + RenewalMargin)
            .ToList();

        // Filter out any stale certificates that don't actually have the code-signing EKU.
        var existing = candidates.FirstOrDefault(HasCodeSigningEku);

        if (existing is null && candidates.Count > 0)
        {
            Log.Information(
                "Found {Count} certificate(s) with subject {Subject}, but none have the Code Signing EKU ({Oid}); generating a fresh one.",
                candidates.Count, Subject, CodeSigningOid);
        }

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
                new OidCollection { new Oid(CodeSigningOid) }, false)); // Code signing

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

    private static bool HasCodeSigningEku(X509Certificate2 cert)
    {
        try
        {
            foreach (var extension in cert.Extensions)
            {
                if (extension is not X509EnhancedKeyUsageExtension eku)
                    continue;

                foreach (var oid in eku.EnhancedKeyUsages)
                    if (string.Equals(oid.Value, CodeSigningOid, StringComparison.Ordinal))
                        return true;
            }
        }
        catch
        {
            // If we can't inspect EKUs for any reason, treat as not suitable.
        }

        return false;
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
