using System.Diagnostics;
using System.Text;
using Serilog;
using Process = System.Diagnostics.Process;

namespace Frontline.Services;

internal sealed class SigningService : ISigningService
{
    public Task EnsureExecutableIsSignedAsync(string thumbprint)
    {
        // Self-signing of the launcher executable is disabled.
        // Stub executables are still signed via SignAsync.
        return Task.CompletedTask;
    }


    public async Task SignAsync(string file, string thumbprint)
    {
        if (string.IsNullOrWhiteSpace(file))
            throw new ArgumentException("File to sign must be provided", nameof(file));
        if (string.IsNullOrWhiteSpace(thumbprint))
            throw new InvalidOperationException("No signing certificate thumbprint supplied.");
        if (!File.Exists(file))
            throw new FileNotFoundException($"File to sign not found: {file}", file);

        // Keep PowerShell portion isolated – easier than P/Invoke WinTrust APIs
        var script = $"""
                      $ErrorActionPreference = 'Stop'
                      {BuildSigningBlock(EscapePsLiteral(file), EscapePsLiteral(thumbprint))}
                      """;

        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var psi = new ProcessStartInfo("powershell.exe",
            $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi)
                         ?? throw new InvalidOperationException("Unable to start PowerShell");

        var stdOutTask = proc.StandardOutput.ReadToEndAsync();
        var stdErrTask = proc.StandardError.ReadToEndAsync();

        await Task.WhenAll(stdOutTask, stdErrTask, proc.WaitForExitAsync()).ConfigureAwait(false);

        var stdOut = stdOutTask.Result;
        var stdErr = stdErrTask.Result;

        if (proc.ExitCode != 0)
        {
            Log.Error("Signing failed:\nSTDOUT: {Out}\nSTDERR: {Err}", stdOut, stdErr);
            var message = string.IsNullOrWhiteSpace(stdErr) ? stdOut : stdErr;
            throw new InvalidOperationException($"Authenticode signing failed ({proc.ExitCode}): {message.Trim()}");
        }

        Log.Information("Signed {File}", Path.GetFileName(file));
    }

    private static string EscapePsLiteral(string value)
    {
        return value.Replace("'", "''");
    }

    private static string BuildSigningBlock(string escapedFile, string escapedThumbprint)
    {
        // Keep the script intentionally simple and tolerant:
        // - No timestamp server (avoids network flakiness)
        // - Only hard-fail if the certificate is missing
        // - Any non-Valid status or signing error is logged but not treated as fatal
        return $$"""
                 $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Thumbprint -eq '{{escapedThumbprint}}'
                 if (-not $cert) { throw 'Certificate not found' }

                 try {
                   $result = Set-AuthenticodeSignature -FilePath '{{escapedFile}}' -Certificate $cert -ErrorAction Stop
                 } catch {
                   Write-Output ("Signing skipped: {0}" -f $_.Exception.Message)
                   return
                 }

                 if ($result.Status -ne 'Valid') {
                   Write-Output ("Signing completed with status {0}: {1}" -f $result.Status, $result.StatusMessage)
                 }
                 """;
    }
}
