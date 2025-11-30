namespace Frontline.Services;

internal sealed record LauncherSpec(
    string OutputPath,
    LauncherType Type,
    string[] Args,
    bool UseShell,
    string SigningThumbprint,
    bool HideWindow
);

internal enum LauncherType
{
    Run,
    Shutdown,
    Restart,
    Sleep,
    Lock
}