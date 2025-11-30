namespace Frontline.Services;

internal interface ICertificateService
{
    Task<string> EnsureCertificateAsync();
}