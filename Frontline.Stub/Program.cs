using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Frontline.Stub;

internal sealed class StubConfig
{
    public string Target { get; init; } = "";
    public string Args { get; init; } = "";
    public bool UseShell { get; init; }
    public bool HideWindow { get; init; }
}

internal static class Program
{
    public static int Main()
    {
        var exePath = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(exePath))
            return 1;

        if (!TryReadConfig(exePath, out var cfg))
            return 1;

        try
        {
            var psi = new ProcessStartInfo
            {
                UseShellExecute = cfg.UseShell,
                CreateNoWindow = cfg.HideWindow
            };

            if (cfg.Target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
            {
                psi.FileName = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "explorer.exe");
                psi.Arguments = cfg.Target;
                psi.UseShellExecute = true;
                psi.CreateNoWindow = false;
            }
            else
            {
                psi.FileName = cfg.Target;
                psi.Arguments = cfg.Args;
            }

            Process.Start(psi);
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    private static bool TryReadConfig(string exePath, out StubConfig cfg)
    {
        cfg = null!;

        try
        {
            using var fs = File.Open(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (fs.Length < 12) // marker (8) + length (4) at minimum
                return false;

            // Read payload length (last 4 bytes)
            fs.Seek(-4, SeekOrigin.End);
            Span<byte> lenBytes = stackalloc byte[4];
            if (fs.Read(lenBytes) != 4)
                return false;
            var payloadLength = BitConverter.ToInt32(lenBytes);
            if (payloadLength <= 0 || payloadLength > fs.Length - 12)
                return false;

            // Seek to marker
            var markerOffset = fs.Length - 4 - 8 - payloadLength;
            if (markerOffset < 0)
                return false;

            fs.Seek(markerOffset, SeekOrigin.Begin);
            using var br = new BinaryReader(fs, Encoding.UTF8, true);

            var markerChars = br.ReadChars(8);
            var marker = new string(markerChars);
            if (!string.Equals(marker, "FLNCFG01", StringComparison.Ordinal))
                return false;

            var payload = br.ReadBytes(payloadLength);

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            cfg = new StubConfig
            {
                Target = root.GetProperty("Target").GetString() ?? "",
                Args = root.GetProperty("Args").GetString() ?? "",
                UseShell = root.GetProperty("UseShell").GetBoolean(),
                HideWindow = root.GetProperty("HideWindow").GetBoolean()
            };

            return true;
        }
        catch
        {
            return false;
        }
    }
}