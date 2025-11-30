namespace Frontline.Services;

internal interface ISigningService
{
    Task EnsureExecutableIsSignedAsync(string thumbprint);
    Task SignAsync(string file, string thumbprint);
}