namespace Frontline.Services;

internal interface IStubBuilder
{
    Task BuildAsync(LauncherSpec spec, string dotnetPath, CancellationToken token = default);
    Task<string?> ValidateDotnetSdkAsync(CancellationToken token = default);
}