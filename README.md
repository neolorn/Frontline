# Frontline

Silent launcher generator for Windows. Frontline lets you create tiny, signed launcher executables that silently start other applications or trigger common system actions (shutdown / restart / sleep / lock) without popping up a console window or requiring .NET to be installed on the target machine.


## Highlights

- **Single‑file, native launchers** – each generated stub is a small, self‑contained `*.exe` built with Native AOT for `win-x64`.
- **No runtime required on target** – launchers do not need any .NET SDK or runtime installed on the machine where you run them.
- **Silent by default** – launch apps without a visible console, with optional control over shell usage and window visibility.
- **Interactive UI or CLI** – use a Spectre.Console driven TUI, or automate via a simple `frontline emit …` command.
- **Built‑in app picker** – browse installed desktop and Store apps discovered from the registry, Start menu and `Get-StartApps`.
- **Per‑user code signing** – launchers are signed with a self‑signed certificate stored in the current user certificate store.
- **Optional legacy SDK mode** – advanced users can opt into using a local .NET SDK to build launchers instead of the embedded stub.


## How it works

Frontline is a two‑part tool:

1. **Generator (`frontline`)**
   - A .NET console application (with both CLI and interactive UI) that configures and emits launcher executables.
   - Ships with an embedded, pre‑compiled stub template (`frontline-stub.exe`) built with Native AOT for `win-x64`.
   - When you emit a launcher, Frontline copies the stub template to your chosen output path, appends a small JSON configuration trailer, and signs the result with a per‑user self‑signed certificate.

2. **Stub (`frontline-stub.exe`)**
   - A tiny native executable that:
     - Reads the embedded configuration trailer.
     - Reconstructs the target command, arguments, and shell/window settings.
     - Starts the configured process (or shell target) and exits immediately.
   - This stub has no dependency on .NET once built.

The result is a per‑target launcher `.exe` that you can place on the desktop, pin to Start, or distribute inside your own tools.


## Requirements

### Running generated launchers

- **OS:** Windows 10 or later.
- **Architecture:** `win-x64` (current stub template).
- **No .NET required:** Launchers are fully self‑contained native executables.
- **Shell integration:** Some launchers may use `explorer.exe` or `shell:` URIs (for Store/UWP apps); these rely on standard Windows components.

### Running the generator (`frontline`)

If you download a prebuilt `frontline.exe` (for example from a release), it is intended to be a self‑contained Native AOT executable:

- **OS:** Windows 10 or later.
- **Architecture:** `win-x64`.
- **Permissions:**  
  - Needs write access to the output folder where launchers are emitted (e.g., Desktop, custom folder, etc.).  
  - Needs permission to create and store certificates in the current user certificate stores (`My`, `TrustedPublisher`, `Root`).  
  - May prompt for elevation if you emit launchers into protected locations (e.g., `Program Files`).

To build `frontline` from source:

- **.NET SDK:** Version `10.0.100` or later (see `global.json` at the repo root).
- **Platform:** Windows (the code relies heavily on Windows‑specific APIs like the registry, COM shell links, PowerShell, and X.509 stores).


## Quick start

### Interactive mode (recommended)

1. Build or download `frontline.exe`.
2. Run it without arguments:

   ```powershell
   frontline
   ```

3. Choose **“Emit launcher”** from the menu.
4. Enter an output filename (e.g., `MyAppLauncher.exe`).
5. Choose a launcher type:
   - `run` – run a specific application or command.
   - `shutdown` – shut down the machine.
   - `restart` – restart the machine.
   - `sleep` – put the machine to sleep.
   - `lock` – lock the current session.
6. For `run`:
   - Decide whether to pick from installed apps or enter a custom path/command.
   - Optionally provide arguments.
   - Choose whether to use the shell and whether to hide the launcher window.
   - Optionally test the launch before emitting the stub.
7. Frontline will:
   - Ensure a per‑user signing certificate exists (and create one if needed).
   - Generate a launcher `.exe` using the embedded stub template.
   - Sign the launcher with the certificate.

You can then move, pin, or distribute the resulting launcher like any other executable.

### CLI mode

Frontline also exposes a simple `emit` command for scripting and automation.

Usage:

```text
frontline emit <Out.exe> run <target> [args...] [--no-shell] [--no-window]
frontline emit <Out.exe> <shutdown|restart|sleep|lock>
```

Examples:

```powershell
# Launcher that starts Notepad without showing a console
frontline emit NotepadLauncher.exe run notepad.exe

# Launcher for a specific app with arguments
frontline emit MyTool.exe run "C:\Tools\MyTool.exe" --some-flag value

# Launcher that reboots the machine
frontline emit RebootNow.exe restart
```

Flags for `run` launchers:

- `--no-shell`  
  Force `UseShellExecute = false` for the launched process.

- `--no-window`  
  Show a window instead of hiding it (i.e., do **not** call `CreateNoWindow`).

By default, `run` launchers are emitted with:

- `UseShellExecute = true`
- A hidden window (no visible console)


## What the stub actually does

Each emitted launcher carries a small JSON configuration payload appended to the end of the stub binary. The payload includes:

- `Target` – the executable path or `shell:` URI to start.
- `Args` – the command‑line arguments.
- `UseShell` – whether to set `UseShellExecute = true`.
- `HideWindow` – whether to hide the launcher window (`CreateNoWindow = true` for non‑shell launches).

At runtime the stub:

1. Reads the configuration trailer from the end of its own file.
2. Validates a fixed marker (`FLNCFG01`) and payload length.
3. Starts a `Process` based on the configuration:
   - For `shell:` targets (like `shell:AppsFolder\…`), runs `explorer.exe` with the URI.
   - For normal targets, runs the specified executable with `Args`, `UseShell`, and `HideWindow` as configured.
4. Exits immediately; it does not wait for the child process.


## App discovery (interactive mode)

When you choose the **run** launcher type in interactive mode and opt to *“Pick from installed/start menu apps”*, Frontline:

- Enumerates installed applications from:
  - Add/Remove Programs registry keys (Win32 apps).
  - Start menu shortcuts (`*.lnk`) for all users and the current user.
  - `Get-StartApps` PowerShell output (Store/UWP apps).
- Deduplicates entries by display name and prefers:
  - Store apps over Win32 apps.
  - Win32 apps over system tools where appropriate.
- Lets you pick an app from a nicely formatted Spectre.Console selection prompt.

Depending on the chosen app:

- Store/UWP apps are launched via `shell:AppsFolder\{AppID}`.
- Win32 apps are launched via their resolved executable path.


## Code signing

Frontline signs each emitted launcher with a self‑signed certificate created under the current user:

- Subject: `CN=Frontline Code Signing`
- Key: 4096‑bit RSA, SHA‑256
- EKU: Code Signing (`1.3.6.1.5.5.7.3.3`)
- Validity: 5 years (with automatic renewal when close to expiry)

The certificate is installed into:

- `CurrentUser\My` (personal store) – for locating the certificate when signing.
- `CurrentUser\TrustedPublisher` and `CurrentUser\Root` – so Windows treats launchers signed with this certificate as trusted on that user’s machine.

Signing is performed via a small PowerShell script using `Set-AuthenticodeSignature`. There is no timestamp server; signatures are primarily meant for local trust and SmartScreen friendliness, not for broad public distribution.


## Legacy SDK bootstrap mode (optional)

By default, Frontline uses the embedded Native AOT stub template and **does not** require a .NET SDK at runtime.

For advanced use cases, you can opt into a legacy mode that relies on a local .NET SDK:

- Set the environment variable:

  ```powershell
  $env:FRONTLINE_ENABLE_SDK_BOOTSTRAP = "1"
  ```

- In this mode:
  - Frontline checks for a usable `dotnet` SDK on the system.
  - If missing, it can offer to download and install the latest supported .NET SDK, either:
    - Globally (using the official installer), or
    - Locally under a `.dotnet` folder next to the executable.
  - The SDK path is then used by the legacy build pipeline (kept for future compatibility).

- When the environment variable is **not** set to `"1"`:
  - SDK checks and bootstrap prompts are skipped.
  - All launchers are emitted purely from the embedded stub template.

For most users, you never need to enable this mode.


## Building from source

Clone the repository and build on Windows with the appropriate .NET SDK:

```powershell
git clone https://github.com/<your-account>/Frontline.git
cd Frontline

# Restore & build
dotnet build Frontline.sln -c Release

# Publish a self-contained Native AOT binary for the generator
dotnet publish Frontline/Frontline.csproj -c Release -r win-x64
```

The published `frontline.exe` will be in:

```text
Frontline/bin/Release/net10.0-windows10.0.22000.0/win-x64/publish/
```

You can copy that single `frontline.exe` anywhere on your system and run it directly.


## Project structure

- `Frontline/` – main generator application:
  - `Program.cs` – entry point (CLI + interactive selection).
  - `UI/` – interactive console UI and CLI handling.
  - `Services/` – core services (stub building, signing, certificate management, elevation, app templates).
  - `Utilities/` – app discovery helpers (registry, Start menu, `Get-StartApps` wrapper).
  - `StubTemplate/` – published Native AOT stub template that gets embedded as a resource.

- `Frontline.Stub/` – source project for the stub template:
  - `Program.cs` – reads the embedded configuration payload and launches the target.
  - `Frontline.Stub.csproj` – configured for Native AOT `WinExe` targeting `win-x64`.


## Contributing

Contributions, bug reports and suggestions are very welcome. If you’d like to help:

1. Fork the repository.
2. Create a feature branch.
3. Make your changes with clear commit messages.
4. Open a pull request describing what you changed and why.

Please also include any relevant details about your environment (Windows version, .NET SDK version, etc.) when filing issues.


## License

This project is licensed under the [MIT License](https://github.com/neolorn/Frontline/tree/main?tab=MIT-1-ov-file).

