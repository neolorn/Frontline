using System.Diagnostics;
using Frontline.Services;
using Frontline.Utilities;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Spectre.Console;

namespace Frontline.UI;

internal sealed class InteractiveMode(IServiceProvider services)
{
    private readonly IStubBuilder _builder = services.GetRequiredService<IStubBuilder>();
    private readonly ICertificateService _certSvc = services.GetRequiredService<ICertificateService>();

    public async Task RunAsync()
    {
        Log.Debug(">> Entering InteractiveMode.RunAsync");
        ConsoleHelpers.AllocateConsoleIfNeeded();

        while (true)
            try
            {
                AnsiConsole.Clear();
                AnsiConsole.Write(new FigletText("Frontline").Centered().Color(Color.Green));
                AnsiConsole.MarkupLine("[grey]Silent launcher generator[/]\n");

                var action = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Select an action:[/]")
                        .AddChoices("Emit launcher", "Exit"));
                Log.Information("User selected action: {Action}", action);

                if (action == "Exit")
                {
                    Log.Debug("User chose to exit InteractiveMode");
                    break;
                }

                //  Collect Launcher Configuration 
                var output = AskForOutputFilename();
                Log.Information("Output filename set to: {Output}", output);

                var type = AskForLauncherType();
                Log.Information("Launcher type set to: {Type}", type);

                var args = Array.Empty<string>();
                var useShell = true;
                var hideWindow = true;

                if (type == LauncherType.Run) (args, useShell, hideWindow) = await ConfigureRunAsync();

                //  Optional .NET SDK validation (legacy mode)
                string? dotnetPath = null;
                if (StubBuilder.IsSdkBootstrapEnabled())
                {
                    try
                    {
                        dotnetPath = await _builder.ValidateDotnetSdkAsync();
                        Log.Information("ValidateDotnetSdkAsync returned path: {Path}", dotnetPath ?? "<null>");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "SDK validation failed");
                        AnsiConsole.MarkupLine($"[red]Error validating .NET SDK:[/] {ex.Message}");
                        PauseForKey();
                        continue;
                    }

                    if (dotnetPath is null)
                    {
                        Log.Warning("User aborted .NET SDK setup");
                        PauseForKey();
                        continue;
                    }
                }
                else
                {
                    Log.Information(
                        "Legacy SDK mode disabled (env {EnvVar} != 1); skipping .NET SDK validation.",
                        StubBuilder.SdkBootstrapEnvVar);
                }

                //  Acquire Certificate 
                var thumb = string.Empty;
                try
                {
                    await AnsiConsole.Status()
                        .StartAsync("[yellow]Requesting certificate...[/]", async ctx =>
                        {
                            thumb = await _certSvc.EnsureCertificateAsync().ConfigureAwait(false);
                            ctx.Status("[green]Certificate acquired[/]");
                            await Task.Delay(300);
                        });
                    if (string.IsNullOrWhiteSpace(thumb))
                        throw new InvalidOperationException("No certificate thumbprint available for signing.");
                    Log.Information("Certificate acquired: {Thumbprint}", thumb);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Certificate acquisition failed");
                    AnsiConsole.MarkupLine($"[red]Error acquiring certificate:[/] {Markup.Escape(ex.Message)}");
                    PauseForKey();
                    continue;
                }

                //  Build the Stub 
                var spec = new LauncherSpec(output, type, args, useShell, thumb, hideWindow);
                try
                {
                    await AnsiConsole.Status()
                        .StartAsync("[yellow]Building launcher...[/]", async ctx =>
                        {
                            DisplaySummary(spec);
                            await _builder.BuildAsync(spec, dotnetPath ?? string.Empty).ConfigureAwait(false);
                            ctx.Status("[green]Launcher built successfully[/]");
                            await Task.Delay(300);
                        });

                    Log.Information("BuildAsync succeeded, launcher at {Output}", output);
                    AnsiConsole.MarkupLineInterpolated($"\n[green]✔ Launcher written to:[/] [white]{output}[/]");
                }
                catch (OperationCanceledException)
                {
                    Log.Warning("BuildAsync was cancelled");
                    AnsiConsole.MarkupLine("[yellow]Build was cancelled.[/]");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "BuildAsync failed for spec {@Spec}", spec);
                    AnsiConsole.MarkupLine($"[red]Error building launcher:[/] {Markup.Escape(ex.Message)}");
                }

                PauseForKey();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Unhandled exception in InteractiveMode loop");
                AnsiConsole.MarkupLine($"[red]An unexpected error occurred:[/] {Markup.Escape(ex.Message)}");
                PauseForKey();
            }

        Log.Debug("<< Exiting InteractiveMode.RunAsync");
    }

    private static string AskForOutputFilename()
    {
        var file = AnsiConsole.Prompt(
                new TextPrompt<string>("Enter [green]output file name[/]:")
                    .Validate(n => !string.IsNullOrWhiteSpace(n)
                        ? ValidationResult.Success()
                        : ValidationResult.Error("[red]Filename cannot be empty[/]")))
            .Trim();

        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            file += ".exe";

        return file;
    }

    private static LauncherType AskForLauncherType()
    {
        var choice = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select [blue]launcher type[/]:")
                .AddChoices("run", "shutdown", "restart", "sleep", "lock"));

        return choice switch
        {
            "run" => LauncherType.Run,
            "shutdown" => LauncherType.Shutdown,
            "restart" => LauncherType.Restart,
            "sleep" => LauncherType.Sleep,
            "lock" => LauncherType.Lock,
            _ => throw new ArgumentOutOfRangeException(nameof(choice))
        };
    }

    private static void PauseForKey()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]Press any key to return to the main menu...[/]");
        Console.ReadKey(true);
    }

    private static void DisplaySummary(LauncherSpec spec)
    {
        if (spec is { Type: LauncherType.Run, Args.Length: > 0 })
        {
            AnsiConsole.MarkupLine($"→ Target: {spec.Args[0]}");
            AnsiConsole.MarkupLine($"→ Args: {string.Join(" ", spec.Args.Skip(1))}");
            AnsiConsole.MarkupLine($"[grey]→ UseShellExecute:[/] {spec.UseShell}");
            AnsiConsole.MarkupLine($"[grey]→ HideWindow:[/] {spec.HideWindow}");
        }
        else
        {
            AnsiConsole.MarkupLine($"→ Preset: {spec.Type}");
        }
    }

    private static async Task<(string[] args, bool useShell, bool hideWindow)> ConfigureRunAsync()
    {
        Log.Debug("Configuring RUN launcher specifics");
        var useAppPicker = await AnsiConsole.ConfirmAsync("Pick from installed/start menu apps?");
        Log.Debug("UseAppPicker: {Picker}", useAppPicker);

        string target;
        bool useShell, hideWindow;

        while (true)
            try
            {
                if (useAppPicker)
                {
                    var apps = AppDiscovery.GetUnifiedAppList()
                        .OrderBy(a => a.Name)
                        .ToList();
                    Log.Debug("Discovered {Count} apps", apps.Count);

                    if (apps.Count == 0)
                    {
                        Log.Warning("No apps found in AppDiscovery");
                        AnsiConsole.MarkupLine("[red]No apps found.[/]");
                        useAppPicker = false;
                        continue;
                    }

                    var selected = AnsiConsole.Prompt(
                        new SelectionPrompt<DiscoveredApp>()
                            .Title("Choose an app to launch:")
                            .AddChoices(apps)
                            .UseConverter(a => a.IsUwp
                                ? $"[blue]{a.Name}[/] [grey]({DescribeAppKind(a)})[/]"
                                : $"{a.Name} [grey]({DescribeAppKind(a)})[/]")
                            .HighlightStyle("green"));

                    Log.Information("User picked app: {App}", selected.Name);
                    target = selected.IsUwp
                        ? $"shell:AppsFolder\\{selected.Target}"
                        : ResolveLaunchTarget(selected.Target);
                    useShell = selected.IsUwp;
                }
                else
                {
                    var input = AnsiConsole.Ask<string>("Enter path or command:");
                    Log.Debug("User entered custom target: {Input}", input);
                    target = ResolveLaunchTarget(input);

                    if (!target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) &&
                        !File.Exists(target))
                    {
                        Log.Warning("Custom target does not exist: {Target}", target);
                        AnsiConsole.MarkupLine($"[red]The file [white]{target}[/] does not exist. Try again.[/]");
                        continue;
                    }

                    useShell = await AnsiConsole.ConfirmAsync("Launch via shell?");
                }

                if (useShell)
                {
                    hideWindow = await AnsiConsole.ConfirmAsync("Hide shell launcher when executing target?");
                }
                else
                {
                    hideWindow = false;
                    AnsiConsole.MarkupLine(
                        "[grey]Note:[/] Shell execution is off, no visible window will be shown.");
                }

                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Error during RUN configuration loop");
                AnsiConsole.MarkupLine($"[red]Configuration error:[/] {ex.Message}");
            }

        var argLine = AnsiConsole.Ask("Arguments (optional):", string.Empty);
        var args = string.IsNullOrWhiteSpace(argLine)
            ? [target]
            : new[] { target }.Concat(argLine
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .ToArray();

        // If hiding a window without a shell, force shell
        if (hideWindow && !useShell)
        {
            Log.Warning("Forcing shell execution because HideWindow=true but UseShell=false");
            useShell = true;
            AnsiConsole.MarkupLine("[yellow]Shell execute enabled automatically for hidden window.[/]");
        }

        // Optional pre-launch test
        if (await AnsiConsole.ConfirmAsync("Test target launch before building the stub?"))
            try
            {
                Log.Debug("Testing target launch: {Target}", target);
                var psi = new ProcessStartInfo
                {
                    FileName = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                        : target,
                    Arguments = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
                        ? target
                        : string.Join(" ", args.Skip(1)),
                    UseShellExecute = useShell,
                    CreateNoWindow = hideWindow
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Target launch test failed for {Target}", target);
                AnsiConsole.MarkupLine($"[red]Failed to launch target:[/] {ex.Message}");
                AnsiConsole.MarkupLine("[grey]It's most likely a problem with the app itself![/]");
            }

        return (args, useShell, hideWindow);
    }

    private static string ResolveLaunchTarget(string target)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target) ||
                target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase))
                return target;

            if (Path.IsPathRooted(target) && File.Exists(target))
                return Path.GetFullPath(target);

            // Search in PATH
            var envPaths = (Environment.GetEnvironmentVariable("PATH") ?? "")
                .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            foreach (var dir in envPaths)
            foreach (var candidate in new[] { target, target + ".exe" })
            {
                var full = Path.Combine(dir, candidate);
                if (File.Exists(full))
                    return full;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error resolving launch target: {Target}", target);
        }

        // Fallback
        return target;
    }

    private static string DescribeAppKind(DiscoveredApp app)
    {
        return app.Kind switch
        {
            AppKind.Store => "Store",
            AppKind.Win32 => "Win32",
            AppKind.System => "System",
            _ => "Unknown"
        };
    }
}