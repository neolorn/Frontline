using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Serilog;

namespace Frontline.Services;

internal sealed class PrivilegeService : IPrivilegeService
{
    public void EnsureElevation(string[] currentArgs)
    {
        // If we're already elevated, don't relaunch.
        if (IsAdministrator())
            return;

        Log.Information("Elevation required - relaunching as administrator");

        var startInfo = CreateElevationStartInfo(currentArgs);

        try
        {
            _ = Process.Start(startInfo) ?? throw new InvalidOperationException("Elevation failed");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) // user cancelled UAC
        {
            throw new OperationCanceledException("Administrator privileges are required.", ex);
        }

        Environment.Exit(0);
    }

    private static ProcessStartInfo CreateElevationStartInfo(string[] currentArgs)
    {
        var exePath = Environment.ProcessPath!;
        var args = ArgUtils.Concatenate(currentArgs);

        // Prefer Windows Terminal when available, so the elevated
        // instance uses the same console host experience.
        if (IsWindowsTerminalAvailable())
        {
            var wtArgs = new List<string>
            {
                "new-tab",
                "--title",
                "Frontline (Admin)",
                exePath
            };

            wtArgs.AddRange(currentArgs);

            return new ProcessStartInfo
            {
                FileName = "wt.exe",
                Arguments = ArgUtils.Concatenate(wtArgs),
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Normal
            };
        }

        // Fallback: elevate the current process directly (classic console host).
        return new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Normal
        };
    }

    private static bool IsWindowsTerminalAvailable()
    {
        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                var candidate = Path.Combine(dir, "wt.exe");
                if (File.Exists(candidate))
                    return true;
            }
        }
        catch
        {
            // Ignore detection errors and assume unavailable.
        }

        return false;
    }

    private static bool IsAdministrator()
    {
        return new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    }
}