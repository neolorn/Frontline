using Frontline.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Spectre.Console;

namespace Frontline.UI;

internal sealed class CliMode(IServiceProvider services)
{
    private readonly IStubBuilder _builder = services.GetRequiredService<IStubBuilder>();
    private readonly ICertificateService _certSvc = services.GetRequiredService<ICertificateService>();

    /// <summary>
    ///     Handles `frontline emit …`. Returns true if it processed the command (even on failure),
    ///     false if args[0] != "emit" so the caller can try another mode.
    /// </summary>
    public async Task<bool> TryHandleAsync(string[] args)
    {
        Log.Debug("CLI invoked with args: {Args}", args);

        if (args.Length == 0 || !args[0].Equals("emit", StringComparison.OrdinalIgnoreCase))
        {
            Log.Debug("Not an 'emit' command, skipping CLI mode.");
            return false;
        }

        // Extract flags (--no-shell / --no-window) and positional args
        var flags = new HashSet<string>(args.Where(a => a.StartsWith("--")), StringComparer.OrdinalIgnoreCase);
        var positional = args.Where(a => !a.StartsWith("--")).ToArray();

        if (positional.Length < 3)
        {
            Log.Warning("Too few positional arguments for emit: {Positional}", positional);
            ShowUsage();
            return true;
        }

        // 1) Output filename
        var output = EnsureExe(positional[1]);
        Log.Information("Output executable: {Output}", output);

        // 2) Launcher type
        LauncherType type;
        try
        {
            type = ParseType(positional[2]);
        }
        catch (ArgumentException ex)
        {
            Log.Error(ex, "Invalid launcher type '{TypeArg}'", positional[2]);
            AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
            ShowUsage();
            return true;
        }

        Log.Information("Launcher type: {Type}", type);

        // 3) Extra arguments for "run"
        var extra = positional.Skip(3).ToArray();
        Log.Debug("Extra args: {Extra}", extra);

        // 4) Determine shell & window flags (only meaningful for 'run')
        var useShell = type == LauncherType.Run && !flags.Contains("--no-shell");
        var hideWindow = type == LauncherType.Run && !flags.Contains("--no-window");
        Log.Debug("Flags resolved → UseShell={UseShell}, HideWindow={HideWindow}", useShell, hideWindow);

        try
        {
            var sdkBootstrapEnabled = StubBuilder.IsSdkBootstrapEnabled();

            // Certificate
            Log.Information("Acquiring signing certificate…");
            var thumb = await _certSvc.EnsureCertificateAsync().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(thumb))
                throw new InvalidOperationException("No certificate thumbprint available for signing.");
            Log.Information("Certificate thumbprint: {Thumb}", thumb);

            // Build spec
            var spec = new LauncherSpec(output, type, extra, useShell, thumb, hideWindow);

            string? dotnetPath = null;

            if (sdkBootstrapEnabled)
            {
                // Legacy mode: require SDK / allow bootstrap
                Log.Information("Legacy SDK mode enabled; validating .NET SDK installation…");
                dotnetPath = await _builder.ValidateDotnetSdkAsync().ConfigureAwait(false);
                if (dotnetPath is null)
                {
                    Log.Warning("User aborted or SDK not installed");
                    AnsiConsole.MarkupLine(
                        "[red].NET SDK is not installed. Visit [underline link=https://dotnet.microsoft.com/download/dotnet/8.0]dotnet.microsoft.com/download/8.0[/] or run via the GUI.[/]");
                    return true;
                }

                Log.Information(".NET SDK path: {Path}", dotnetPath);
            }
            else
            {
                Log.Information(
                    "Legacy SDK mode disabled (env {EnvVar} != 1); skipping .NET SDK validation.",
                    StubBuilder.SdkBootstrapEnvVar);
            }

            // Build the stub
            Log.Information("Building stub…");
            await _builder.BuildAsync(spec, dotnetPath ?? string.Empty).ConfigureAwait(false);

            AnsiConsole.MarkupLineInterpolated($"[green]✔ Stub created:[/] {output}");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("CLI emit operation was canceled by user");
            AnsiConsole.MarkupLine("[yellow]Operation canceled.[/]");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during CLI emit");
            AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
        }

        return true;
    }

    private static void ShowUsage()
    {
        AnsiConsole.MarkupLine("""
                               [grey]Usage:[/]
                                 frontline emit <Out.exe> run <target> [args...] [--no-shell] [--no-window]
                                 frontline emit <Out.exe> <shutdown|restart|sleep|lock>
                               """);
    }

    private static LauncherType ParseType(string s)
    {
        return s.ToLowerInvariant() switch
        {
            "run" => LauncherType.Run,
            "shutdown" => LauncherType.Shutdown,
            "restart" => LauncherType.Restart,
            "sleep" => LauncherType.Sleep,
            "lock" => LauncherType.Lock,
            _ => throw new ArgumentException($"Unknown launcher type '{s}'", nameof(s))
        };
    }

    private static string EnsureExe(string file)
    {
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? file
            : $"{file}.exe";
    }
}