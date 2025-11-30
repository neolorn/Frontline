using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace Frontline.Utilities;

internal enum AppSource
{
    Registry,
    StartMenu,
    StartApps
}

internal enum AppKind
{
    Win32,
    Store,
    System,
    Unknown
}

internal sealed record DiscoveredApp(string Name, string Target, bool IsUwp, AppSource Source, AppKind Kind);

internal static class AppDiscovery
{
    private static readonly string StartMenuPathAll =
        Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);

    private static readonly string StartMenuPathUser = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);

    public static IEnumerable<DiscoveredApp> GetUnifiedAppList(bool verbose = false)
    {
        var allApps = new List<DiscoveredApp>();

        allApps.AddRange(GetRegistryApps(verbose));
        allApps.AddRange(GetUwpApps(verbose));
        allApps.AddRange(GetStartMenuApps(verbose));

        return allApps
            .GroupBy(a => a.Name.ToLowerInvariant()) // dedupe by display name
            .Select(g =>
                g.OrderBy(app => app.Kind switch
                    {
                        AppKind.Store => 0,
                        AppKind.Win32 => 1,
                        AppKind.System => 2,
                        _ => 3
                    })
                    .ThenBy(app => app.Source switch
                    {
                        AppSource.StartApps => 0,
                        AppSource.Registry => 1,
                        AppSource.StartMenu => 2,
                        _ => 3
                    })
                    .First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<DiscoveredApp> GetRegistryApps(bool verbose)
    {
        var apps = new List<DiscoveredApp>();

        string[] keys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        ];

        foreach (var root in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var keyPath in keys)
        {
            using var baseKey = RegistryKey.OpenBaseKey(root, RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(keyPath);
            if (key == null) continue;

            foreach (var subKeyName in key.GetSubKeyNames())
            {
                using var subKey = key.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var name = subKey.GetValue("DisplayName") as string;
                var installLoc = subKey.GetValue("InstallLocation") as string;
                var icon = subKey.GetValue("DisplayIcon") as string;

                var candidate = !string.IsNullOrWhiteSpace(icon) && File.Exists(icon)
                    ? icon
                    : !string.IsNullOrWhiteSpace(installLoc) && Directory.Exists(installLoc)
                        ? Directory.GetFiles(installLoc, "*.exe").FirstOrDefault()
                        : null;

                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(candidate) ||
                    !File.Exists(candidate)) continue;

                if (verbose)
                    Console.WriteLine($"[registry] {name} -> {candidate}");

                apps.Add(new DiscoveredApp(name, candidate, false, AppSource.Registry, AppKind.Win32));
            }
        }

        return apps;
    }

    private static IEnumerable<DiscoveredApp> GetStartMenuApps(bool verbose)
    {
        var folders = new[]
        {
            Path.Combine(StartMenuPathAll, "Programs"),
            Path.Combine(StartMenuPathUser, "Programs")
        };

        foreach (var dir in folders.Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(dir, "*.lnk", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            var resolved = ResolveShortcut(file);

            if (string.IsNullOrWhiteSpace(resolved))
                continue;

            // Detect UWP launcher shell shortcut
            if (resolved.StartsWith("explorer.exe shell:AppsFolder\\", StringComparison.OrdinalIgnoreCase))
            {
                var appId = resolved
                    .Substring("explorer.exe shell:AppsFolder\\".Length)
                    .Trim();

                if (verbose)
                    Console.WriteLine($"[startmenu-uwp] {name} -> {appId}");

                yield return new DiscoveredApp(name, appId, true, AppSource.StartMenu, AppKind.Store);
                continue;
            }

            if (!File.Exists(resolved) ||
                !Path.GetExtension(resolved).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (verbose)
                Console.WriteLine($"[startmenu] {name} -> {resolved}");

            yield return new DiscoveredApp(name, resolved, false, AppSource.StartMenu, ClassifyWin32Target(resolved));
        }
    }

    private static IEnumerable<DiscoveredApp> GetUwpApps(bool verbose)
    {
        var output = RunPowerShell("Get-StartApps | Select-Object Name, AppID | ConvertTo-Json -Depth 2");
        if (string.IsNullOrWhiteSpace(output))
            yield break;

        List<DiscoveredApp> apps = [];

        try
        {
            using var doc = JsonDocument.Parse(output);

            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var app in doc.RootElement.EnumerateArray())
                {
                    var name = app.GetProperty("Name").GetString();
                    var appId = app.GetProperty("AppID").GetString();

                    if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId))
                    {
                        var kind = ClassifyStartAppsEntry(appId);
                        if (verbose)
                            Console.WriteLine($"[startapps-{kind}] {name} -> {appId}");

                        apps.Add(new DiscoveredApp(name, appId, true, AppSource.StartApps, kind));
                    }
                }
            }
            else if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                var name = doc.RootElement.GetProperty("Name").GetString();
                var appId = doc.RootElement.GetProperty("AppID").GetString();

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(appId))
                {
                    var kind = ClassifyStartAppsEntry(appId);
                    if (verbose)
                        Console.WriteLine($"[startapps-{kind}] {name} -> {appId}");

                    apps.Add(new DiscoveredApp(name, appId, true, AppSource.StartApps, kind));
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[UWP ERROR] Failed to parse UWP app list: " + ex.Message);
        }

        foreach (var app in apps)
            yield return app;
    }

    private static string RunPowerShell(string command)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -Command \"{command}\"",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8
        };

        using var proc = Process.Start(psi);
        return proc?.StandardOutput.ReadToEnd() ?? string.Empty;
    }

    private static string? ResolveShortcut(string shortcutPath)
    {
        try
        {
            var shellLinkObj = new ShellLink();

            var shellLink = ComCast<IShellLink>(shellLinkObj);
            var persistFile = ComCast<IPersistFile>(shellLinkObj);

            persistFile.Load(shortcutPath, 0);

            var sb = new StringBuilder(260);
            shellLink.GetPath(sb, sb.Capacity, out _, 0);

            return sb.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static AppKind ClassifyWin32Target(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return AppKind.Unknown;

            var fullPath = Path.GetFullPath(path);
            var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            if (!string.IsNullOrEmpty(windowsDir) &&
                fullPath.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase))
                return AppKind.System;

            if ((!string.IsNullOrEmpty(programFiles) &&
                 fullPath.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(programFilesX86) &&
                 fullPath.StartsWith(programFilesX86, StringComparison.OrdinalIgnoreCase)))
                return AppKind.Win32;
        }
        catch
        {
            // Fall through to Unknown.
        }

        return AppKind.Unknown;
    }

    private static AppKind ClassifyStartAppsEntry(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
            return AppKind.Unknown;

        // Heuristic: UWP / Store apps have a package family and
        // AppID separated by '!' (e.g. Microsoft.WindowsCalculator_8wekyb3d8bbwe!App).
        return appId.Contains('!')
            ? AppKind.Store
            :
            // Desktop / Win32 entries exposed via StartApps typically do not
            // have '!' in the AppID, treat them as Win32-backed.
            AppKind.Win32;
    }

    private static TInterface ComCast<TInterface>(object comObject)
        where TInterface : class
    {
        return (TInterface)comObject;
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLink
    {
        void GetPath(
            [Out] [MarshalAs(UnmanagedType.LPWStr)]
            StringBuilder pszFile,
            int cchMaxPath,
            out IntPtr pfd,
            int fFlags);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        [PreserveSig]
        int GetClassID(out Guid pClassId);

        [PreserveSig]
        int IsDirty();

        [PreserveSig]
        int Load(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            uint dwMode);

        [PreserveSig]
        int Save(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFileName,
            bool fRemember);

        [PreserveSig]
        int SaveCompleted(
            [MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        [PreserveSig]
        int GetCurFile(
            [MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}