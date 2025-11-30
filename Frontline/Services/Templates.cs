namespace Frontline.Services;

internal static class Templates
{
    internal static string RenderRun(string[] parts, bool useShell, bool hideWindow)
    {
        var target = Escape(parts[0]);
        var arguments = string.Join(" ", parts.Skip(1).Select(Escape));
        var shell = useShell.ToString().ToLowerInvariant();
        var noWindow = (!hideWindow).ToString().ToLowerInvariant();
        var windowStyle = hideWindow ? "Hidden" : "Normal";

        return $$"""
                 using System;
                 using System.Diagnostics;

                 internal class P
                 {
                     public static void Main()
                     {
                         var target = "{{target}}";

                         var psi = new ProcessStartInfo();

                         if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                         {
                             psi.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
                             psi.Arguments = target;
                             psi.UseShellExecute = true;
                             psi.WindowStyle = ProcessWindowStyle.Normal;
                         }
                         else
                         {
                             psi.FileName = target;
                             psi.Arguments = "{{arguments}}";
                             psi.UseShellExecute = {{shell}};
                             psi.CreateNoWindow = {{noWindow}};
                             psi.WindowStyle = ProcessWindowStyle.{{windowStyle}};
                         }

                         Process.Start(psi);
                     }
                 }
                 """;

        string Escape(string s)
        {
            return s.Replace("\\", @"\\").Replace("\"", "\\\"");
        }
    }

    internal static string RenderShutdown(string flag)
    {
        return $$"""
                 using System.Diagnostics;

                 internal class P
                 {
                     static void Main() =>
                         Process.Start("shutdown", "{{flag}} /t 0");
                 }
                 """;
    }

    internal static string RenderSleep()
    {
        // ReSharper disable once StringLiteralTypo
        return """
               using System.Runtime.InteropServices;
               internal class P
               {
                   [DllImport("powrprof.dll", SetLastError = true)]
                   static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);

                   static void Main() => SetSuspendState(false, true, true);
               }
               """;
    }

    internal static string RenderLock()
    {
        return """
               using System.Runtime.InteropServices;
               internal class P
               {
                   [DllImport("user32.dll")]
                   static extern bool LockWorkStation();
                   static void Main() => LockWorkStation();
               }
               """;
    }
}