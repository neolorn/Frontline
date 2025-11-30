using System;
using System.ComponentModel;
using System.Linq;
using Frontline.Services;
using Frontline.UI;
using Serilog;
using Spectre.Console;

Log.Logger = LogUtilities.ConfigureRootLogger();
Log.Information("=== Frontline Launcher Generator starting ===");

var rawArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
Log.Debug("Command‐line args: {Args}", rawArgs);

try
{
    // Build DI container
    var services = ServiceRegistry.Build();
    Log.Debug("Dependency injection container built");

    // Try CLI mode
    var cli = new CliMode(services);
    if (await cli.TryHandleAsync(rawArgs).ConfigureAwait(false))
    {
        Log.Debug("Handled via CLI mode; exiting");
        return;
    }

    // Fall back to interactive
    Log.Debug("Switching to interactive mode");
    var ui = new InteractiveMode(services);
    await ui
        .RunAsync()
        .ConfigureAwait(false);
}
catch (OperationCanceledException)
{
    // User explicitly cancelled (e.g. via CLI usage or interactive Ctrl+C)
    Log.Warning("Operation was cancelled by user");
}
catch (Exception ex) when (IsElevationError(ex))
{
    Log.Warning(ex, "Operation failed due to insufficient privileges; attempting elevation");

    try
    {
        var privilege = new PrivilegeService();
        privilege.EnsureElevation(rawArgs);
    }
    catch (OperationCanceledException)
    {
        Log.Warning("User cancelled the elevation prompt");
        AnsiConsole.MarkupLine(
            "[yellow]This operation requires administrator privileges, but elevation was cancelled.[/]");
    }
}
catch (Exception ex)
{
    // Anything unexpected bubbles up here
    Log.Fatal(ex, "Unrecoverable error in Main");
    AnsiConsole.MarkupLine($"[red]Fatal error encountered:[/] {ex.Message}");
}
finally
{
    Log.Information("=== Frontline Launcher Generator shutting down ===");
    Log.CloseAndFlush();
}

return;

static bool IsElevationError(Exception ex)
{
    // Walk the exception chain looking for common "access denied" patterns.
    for (var current = ex; current is not null; current = current.InnerException)
        switch (current)
        {
            case UnauthorizedAccessException:
            // ERROR_PRIVILEGE_NOT_HELD
            case Win32Exception win32 when
                win32.NativeErrorCode == 5 || // ERROR_ACCESS_DENIED
                win32.NativeErrorCode == 1314:
                return true;
        }

    return false;
}