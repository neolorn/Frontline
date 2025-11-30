using Microsoft.Extensions.DependencyInjection;

namespace Frontline.Services;

internal static class ServiceRegistry
{
    internal static ServiceProvider Build()
    {
        return new ServiceCollection()
            .AddSingleton<IPrivilegeService, PrivilegeService>()
            .AddSingleton<ICertificateService, CertificateService>()
            .AddSingleton<ISigningService, SigningService>()
            .AddSingleton<IStubBuilder, StubBuilder>()
            .BuildServiceProvider();
    }
}