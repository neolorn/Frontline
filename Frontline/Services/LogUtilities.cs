using Serilog;
using Serilog.Events;

namespace Frontline.Services;

internal static class LogUtilities
{
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontline");

    internal static ILogger ConfigureRootLogger()
    {
        Directory.CreateDirectory(LogDirectory);

        return new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File(Path.Combine(LogDirectory, "frontline.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                restrictedToMinimumLevel: LogEventLevel.Debug)
            .CreateLogger();
    }
}