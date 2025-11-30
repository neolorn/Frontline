using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using JetBrains.Annotations;
using Serilog;
using Spectre.Console;

namespace Frontline.Services;

internal sealed class StubBuilder(ISigningService signer, HttpClient? httpClient = null) : IStubBuilder
{
    internal const string SdkBootstrapEnvVar = "FRONTLINE_ENABLE_SDK_BOOTSTRAP";

    private readonly HttpClient _httpClient = httpClient ?? new HttpClient();
    private readonly ISigningService _signer = signer ?? throw new ArgumentNullException(nameof(signer));

    public async Task BuildAsync(LauncherSpec spec, string dotnetPath, CancellationToken token = default)
    {
        Log.Debug("Entering BuildAsync with spec={@Spec}", spec);

        try
        {
            var outputPath = Path.GetFullPath(spec.OutputPath);

            Log.Information("Writing stub launcher to {Path}", outputPath);
            var stubBytes = LoadEmbeddedStubTemplate();
            await File.WriteAllBytesAsync(outputPath, stubBytes, token).ConfigureAwait(false);

            Log.Debug("Appending configuration trailer to {Path}", outputPath);
            await AppendConfigAsync(outputPath, spec, token).ConfigureAwait(false);

            Log.Information("Signing {Path} with thumbprint {Thumbprint}", outputPath, spec.SigningThumbprint);
            await _signer.SignAsync(outputPath, spec.SigningThumbprint).ConfigureAwait(false);
            Log.Information("Signing completed for {Path}", outputPath);
        }
        catch (OperationCanceledException)
        {
            Log.Warning("BuildAsync cancelled by token");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in BuildAsync for spec {@Spec}", spec);
            throw;
        }
        finally
        {
            Log.Debug("Exiting BuildAsync");
        }
    }

    public async Task<string?> ValidateDotnetSdkAsync(CancellationToken token)
    {
        var dotnetPath = "dotnet";
        try
        {
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = dotnetPath,
                Arguments = "--version",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            });

            if (process != null)
            {
                await process.WaitForExitAsync(token);
                if (process.ExitCode == 0)
                {
                    Log.Information("Using installed .NET SDK at system PATH");
                    return dotnetPath;
                }
            }
        }
        catch
        {
            Log.Debug("No system-wide dotnet found.");
        }

        var localPath = Path.Combine(AppContext.BaseDirectory, ".dotnet", "dotnet.exe");
        if (File.Exists(localPath))
        {
            Log.Information("Using local SDK at {LocalPath}", localPath);
            return localPath;
        }

        var bootstrapEnabled = string.Equals(
            Environment.GetEnvironmentVariable("FRONTLINE_ENABLE_SDK_BOOTSTRAP"),
            "1",
            StringComparison.OrdinalIgnoreCase);

        if (!bootstrapEnabled)
        {
            Log.Error(
                ".NET SDK not found and bootstrapper is disabled. Set FRONTLINE_ENABLE_SDK_BOOTSTRAP=1 to enable legacy download/installation, or provide a local SDK at {BaseDir}\\.dotnet\\dotnet.exe.",
                AppContext.BaseDirectory);
            return null;
        }

        Log.Debug("Entering ValidateDotnetSdkAsync");
        try
        {
            var (sdkVersion, zipUrl, installerUrl) = await GetLatestDotNet8SdkInfoAsync().ConfigureAwait(false);

            Log.Warning(".NET SDK {Version} not found locally. Prompting user for action.", sdkVersion);
            var options = new[]
            {
                new SdkInstallOption { Label = $"Install .NET SDK {sdkVersion}", Action = "global" },
                new SdkInstallOption
                    { Label = $"Install .NET SDK {sdkVersion} (Only for Frontline)", Action = "local" },
                new SdkInstallOption { Label = "Abort", Action = "abort" }
            };

            var choice = AnsiConsole.Prompt(
                new SelectionPrompt<SdkInstallOption>()
                    .Title("[yellow].NET SDK is missing! What would you like to do?[/]")
                    .AddChoices(options)
            );

            return choice.Action switch
            {
                "global" => await InstallGlobally(installerUrl, token).ConfigureAwait(false),
                "local" => await InstallLocally(zipUrl, token).ConfigureAwait(false),
                "abort" => null,
                _ => throw new InvalidOperationException($"Unknown action: {choice.Action}")
            };
        }
        catch (OperationCanceledException)
        {
            Log.Warning("ValidateDotnetSdkAsync cancelled");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error in ValidateDotnetSdkAsync");
            throw;
        }
        finally
        {
            Log.Debug("Exiting ValidateDotnetSdkAsync");
        }
    }

    internal static bool IsSdkBootstrapEnabled()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable(SdkBootstrapEnvVar),
            "1",
            StringComparison.OrdinalIgnoreCase);
    }

    // Legacy SDK bootstrap and publish logic is retained for reference only.
    // New builds use an embedded stub template instead of compiling at runtime.

    private static byte[] LoadEmbeddedStubTemplate()
    {
        var assembly = typeof(StubBuilder).Assembly;
        const string resourceName = "Frontline.StubTemplate";

        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new InvalidOperationException(
                               $"Embedded stub template '{resourceName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private static async Task AppendConfigAsync(string outputPath, LauncherSpec spec, CancellationToken token)
    {
        var target = spec.Args.Length > 0 ? spec.Args[0] : string.Empty;
        var args = spec.Args.Length > 1
            ? string.Join(" ", spec.Args.Skip(1))
            : string.Empty;

        await using var ms = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(ms))
        {
            writer.WriteStartObject();
            writer.WriteString(nameof(StubConfig.Target), target);
            writer.WriteString(nameof(StubConfig.Args), args);
            writer.WriteBoolean(nameof(StubConfig.UseShell), spec.UseShell);
            writer.WriteBoolean(nameof(StubConfig.HideWindow), spec.HideWindow);
            writer.WriteEndObject();
        }

        var payload = ms.ToArray();

        await using var fs = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.None);
        await using var bw = new BinaryWriter(fs, Encoding.UTF8, true);

        // Layout expected by Frontline.Stub:
        // [stub bytes][marker][payload][payloadLength (int32 at end)]
        bw.Write("FLNCFG01".ToCharArray());
        bw.Write(payload);
        bw.Write(payload.Length);

        await fs.FlushAsync(token).ConfigureAwait(false);
    }

    [UsedImplicitly]
    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, true);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete temp directory {Path}", path);
        }
    }

    private async Task<string> InstallGlobally(string installerUrl, CancellationToken token)
    {
        Log.Information("Starting global .NET SDK installation from {Url}", installerUrl);
        var installerPath = Path.Combine(Path.GetTempPath(), "dotnet-sdk-installer.exe");

        try
        {
            Log.Debug("Resolving installer redirect");
            using var initialResponse = await _httpClient
                .GetAsync(installerUrl, HttpCompletionOption.ResponseHeadersRead, token)
                .ConfigureAwait(false);
            initialResponse.EnsureSuccessStatusCode();

            var finalUri = initialResponse.RequestMessage?.RequestUri?.ToString()
                           ?? throw new InvalidOperationException(
                               "Could not resolve final installer URI from redirect.");
            Log.Debug("Resolved installer URL: {FinalUri}", finalUri);

            await DownloadWithProgressAsync(finalUri, installerPath, token).ConfigureAwait(false);

            Log.Information("Launching installer at {Path}", installerPath);
            var proc = Process.Start(new ProcessStartInfo(installerPath, "/quiet /norestart")
            {
                UseShellExecute = true,
                Verb = "runas"
            }) ?? throw new InvalidOperationException("Failed to launch SDK installer");

            await proc.WaitForExitAsync(token).ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                Log.Error("Installer exited with code {ExitCode}", proc.ExitCode);
                throw new InvalidOperationException($"SDK installer failed (exit code {proc.ExitCode})");
            }

            if (!IsDotnetAvailable("dotnet"))
            {
                Log.Error(".NET not available after global install");
                throw new InvalidOperationException("Global .NET SDK installation failed");
            }

            Log.Information("Global .NET SDK installation succeeded");
            return "dotnet";
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Global SDK installation canceled");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during global SDK installation");
            throw;
        }
        finally
        {
            TryDeleteFile(installerPath);
        }
    }

    private async Task<string> InstallLocally(string zipUrl, CancellationToken token)
    {
        Log.Information("Starting local .NET SDK installation from {Url}", zipUrl);
        var zipPath = Path.Combine(Path.GetTempPath(), "dotnet-sdk.zip");
        var extractPath = Path.Combine(AppContext.BaseDirectory, ".dotnet");

        try
        {
            await DownloadWithProgressAsync(zipUrl, zipPath, token).ConfigureAwait(false);

            if (Directory.Exists(extractPath))
            {
                Log.Debug("Deleting existing SDK folder at {Path}", extractPath);
                Directory.Delete(extractPath, true);
            }

            Log.Information("Extracting SDK to {Path}", extractPath);
            await ExtractWithProgressAsync(zipPath, extractPath, token).ConfigureAwait(false);

            Log.Information("Local .NET SDK installation completed at {Path}", extractPath);
            return Path.Combine(extractPath, "dotnet.exe");
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Local SDK installation canceled");
            throw;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error during local SDK installation");
            throw;
        }
        finally
        {
            TryDeleteFile(zipPath);
        }
    }

    private async Task DownloadWithProgressAsync(string url, string destinationPath, CancellationToken token)
    {
        Log.Debug("Beginning download from {Url} to {Path}", url, destinationPath);

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(
                new TaskDescriptionColumn(),
                new ProgressBarColumn(),
                new PercentageColumn(),
                new RemainingTimeColumn(),
                new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                using var response = await _httpClient
                    .GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token)
                    .ConfigureAwait(false);

                response.EnsureSuccessStatusCode();

                var total = response.Content.Headers.ContentLength ?? -1L;
                var task = ctx.AddTask(
                    $"[green]Downloading[/] {Path.GetFileName(destinationPath)}",
                    maxValue: total);

                await using var contentStream = await response.Content
                    .ReadAsStreamAsync(token)
                    .ConfigureAwait(false);

                await using var fileStream = File.Create(destinationPath);

                var buffer = new byte[81920];
                int read;
                while ((read = await contentStream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    task.Increment(read);
                }
            })
            .ConfigureAwait(false);

        Log.Debug("Download completed: {Path}", destinationPath);
    }

    private static async Task ExtractWithProgressAsync(string zipPath, string extractTo, CancellationToken token)
    {
        Log.Debug("Opening zip archive {Path}", zipPath);
        await using var archive = await ZipFile.OpenReadAsync(zipPath, token);
        var entries = archive.Entries;

        await AnsiConsole.Progress()
            .AutoClear(false)
            .Columns(new TaskDescriptionColumn(), new ProgressBarColumn(), new PercentageColumn(), new SpinnerColumn())
            .StartAsync(async ctx =>
            {
                var task = ctx.AddTask("[yellow]Extracting[/] archive", maxValue: entries.Count);
                foreach (var entry in entries)
                {
                    token.ThrowIfCancellationRequested();
                    var dest = Path.Combine(extractTo, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await entry.ExtractToFileAsync(dest, true, token);
                    task.Increment(1);
                    await Task.Yield();
                }
            })
            .ConfigureAwait(false);

        Log.Debug("Extraction completed to {Path}", extractTo);
    }

    private async Task<(string version, string zipUrl, string installerUrl)> GetLatestDotNet8SdkInfoAsync()
    {
        const string metadataUrl = "https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/8.0/releases.json";
        Log.Debug("Fetching .NET SDK metadata from {Url}", metadataUrl);

        try
        {
            using var resp = await _httpClient.GetAsync(metadataUrl).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var latest = root.GetProperty("latest-sdk").GetString()
                         ?? throw new InvalidOperationException("Missing latest-sdk in metadata");
            Log.Information("Latest .NET 8 SDK version: {Version}", latest);

            foreach (var release in root.GetProperty("releases").EnumerateArray())
            {
                if (!release.TryGetProperty("sdk", out var sdk)) continue;
                if (sdk.GetProperty("version").GetString() != latest) continue;

                string? zip = null, exe = null;
                foreach (var file in sdk.GetProperty("files").EnumerateArray())
                {
                    var rid = file.GetProperty("rid").GetString();
                    var name = file.GetProperty("name").GetString();
                    var url = file.GetProperty("url").GetString();
                    if (rid == "win-x64" && !string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    {
                        if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip = url;
                        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe = url;
                    }
                }

                if (zip is null || exe is null)
                    throw new InvalidOperationException("Failed to find win-x64 zip and exe in metadata");

                Log.Debug("Resolved SDK URLs: zip={Zip}, installer={Exe}", zip, exe);
                return (latest, zip, exe);
            }

            throw new InvalidOperationException($"Could not find matching release for version {latest}");
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            Log.Error(ex, "Error fetching .NET SDK metadata from {Url}", metadataUrl);
            throw new InvalidOperationException("Failed to retrieve .NET SDK metadata", ex);
        }
        finally
        {
            Log.Debug("Exiting GetLatestDotNet8SdkInfoAsync");
        }
    }

    private static bool IsDotnetAvailable(string dotnetPath)
    {
        Log.Debug("Checking availability of '{DotnetPath}'", dotnetPath);
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(dotnetPath, "--version")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }) ?? throw new InvalidOperationException("Failed to start dotnet for --version");

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit();
            var ok = proc.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
            Log.Debug("dotnet --version returned ExitCode={ExitCode}, Output={Output}", proc.ExitCode, output.Trim());
            return ok;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "dotnet not available at path '{DotnetPath}'", dotnetPath);
            return false;
        }
    }

    private static void TryDeleteFile(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
                File.Delete(filePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to delete file {FilePath}", filePath);
        }
    }

    private sealed class SdkInstallOption
    {
        public string Label { get; init; } = null!;
        public string Action { get; init; } = null!;

        public override string ToString()
        {
            return Label;
        }
    }
}

internal abstract class StubConfig
{
    public string Target { get; init; } = "";
    public string Args { get; init; } = "";
    public bool UseShell { get; init; }
    public bool HideWindow { get; init; }
}