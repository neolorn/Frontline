namespace Frontline.Services;

internal interface IPrivilegeService
{
    void EnsureElevation(string[] currentArgs);
}