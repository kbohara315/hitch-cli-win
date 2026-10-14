# Hitch

A cross-platform C# implementation of Hitch’s shared-session model. Hitch launches a managed shell in a PTY, mirrors its input/output to the invoking terminal, and exposes same-user agent commands over local IPC. Windows uses ConPTY and an ACL-restricted named pipe; Linux x64 uses a `forkpty` helper, Bash, and a private Unix-domain socket.

## Requirements

- Windows 10 version 1809 (build 17763) or newer
- Linux x64 with Bash and `stty` (coreutils)
- .NET 10 SDK to build
- Windows x64 and Linux x64 release targets

See [installation-linux.md](installation-linux.md) for Linux installation and setup. Windows installation remains documented in [installation.md](installation.md).

## Build and test

```sh
dotnet build Hitch.sln
dotnet test Hitch.sln
```

Linux x64 builds compile the included `src/Hitch/Native/hitch-pty.c` helper with `cc` and link libutil. The `hitch-pty` executable must remain alongside `hitch`; Linux packages and `hitch setup` install both files together.

## Publish for Windows x64

Both publishing modes produce a self-contained executable, so end users do **not** need to install the .NET runtime. The standard publish uses the .NET apphost plus bundled runtime; Native AOT compiles the app to native code and generally produces a much smaller executable. Neither mode changes the normal Release build.

Publish the standard self-contained single-file executable:

```powershell
dotnet publish src/Hitch/Hitch.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish-standard
```

The output is `artifacts/publish-standard/hitch.exe`.

Publish the Linux x64 package on Linux (the helper is compiled during publish):

```bash
dotnet publish src/Hitch/Hitch.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/linux-x64
```

The output includes `hitch` and its required `hitch-pty` sidecar. Build or publish this target on Linux; cross-publishing it from Windows is unsupported and fails with a build error rather than producing an incomplete package. Linux runtime support currently targets x64 and Bash; macOS, Linux ARM64, and other shells are outside this first release.

Publish the Native AOT executable using the dedicated profile:

```powershell
dotnet publish src/Hitch/Hitch.csproj -c Release -p:PublishProfile=NativeAot -o artifacts/publish-aot
```

The output is `artifacts/publish-aot/hitch.exe`. The `NativeAot` profile targets self-contained `win-x64`; it leaves ordinary Release and standard publishing unchanged.

Native AOT requires the .NET 10 SDK and the Visual C++ x64 native build toolchain (Visual Studio or Build Tools with the MSVC C++ build tools and Windows SDK). On this Windows environment, `vswhere.exe` and `link.exe` are installed but are not on `PATH`. For this installation, prepare the current shell before running the AOT publish command:

```powershell
$env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;${env:ProgramFiles(x86)}\Microsoft Visual Studio\2022\BuildTools\VC\Tools\MSVC\14.42.34433\bin\Hostx64\x64;$env:PATH"
```

If the MSVC version or Visual Studio edition differs, use the corresponding installed `Installer` directory and MSVC `bin\Hostx64\x64` directory. `vswhere.exe` must be discoverable on `PATH` for the Native AOT linker step.

## Install with PowerShell

On Windows 10 version 1809 or newer (Windows x64), run this in PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "`$script = (Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/kbohara315/hitch-cli-win/main/scripts/install.ps1').Content; Invoke-Expression `$script"
```

The installer downloads the latest Native AOT release and its SHA256 checksum from GitHub, verifies the executable, then installs it for the current user under `%LOCALAPPDATA%\Hitch`. It configures the existing PowerShell profiles, CMD wrapper, and user PATH, preserving backups. No .NET installation or administrator access is needed. Open a fresh PowerShell or Command Prompt window and type `hitch`.

Linux x64 users can install with the script in [installation-linux.md](installation-linux.md). Releases are created by pushing a `v*` version tag; CI publishes `hitch.exe` and its checksum plus `hitch-linux-x64.tar.gz` and its checksum.

## Try it

```powershell
hitch.exe on --shell "$PSHOME\pwsh.exe"
```

Or start Command Prompt:

```powershell
hitch.exe on --shell "$env:ComSpec"
```

The managed prompt shows its session ID in green (for example, `#1`) so the active Hitch shell is visible at a glance.

While a session is active, other processes running as the same Windows user can use:

```powershell
hitch.exe status
hitch.exe context
hitch.exe capture -t <session-id> --tail 80
hitch.exe send-keys -t <session-id> "Get-Location" Enter
hitch.exe kill <session-id>
```

Run `hitch.exe setup` for an explicit, confirmed installation of the PowerShell profiles and CMD wrapper. Setup backs up modified files and adds its wrapper directory to the user PATH; `hitch.exe setup --yes` skips only the confirmation prompt. The built-in `hitch.exe --skill` prints agent instructions. Install the reusable skill separately with the npx skills CLI.

## Install the agent skill

The root `SKILL.md` is the source of truth for agent instructions. `hitch --skill` prints that same content. To install it for OpenCode with the [npx skills CLI](https://github.com/vercel-labs/skills):

```powershell
npx skills add kbohara315/hitch-cli-win --skill hitch --agent opencode
```

Omit `--agent opencode` to let the CLI detect installed agents, or add `--global` to install the skill for your user instead of the current project.

Session metadata and bounded transcripts are stored under `%LOCALAPPDATA%\Hitch\sessions` on Windows. Linux uses `$XDG_RUNTIME_DIR/hitch/sessions`, falling back to `$XDG_STATE_HOME/hitch/sessions` or `~/.local/state/hitch/sessions`. Active data is removed when the managed shell ends; dead session directories are pruned the next time Hitch lists sessions.

## Platform support

Windows sessions use PowerShell or Command Prompt inside ConPTY. Linux sessions use an interactive Bash child hosted by a small native `forkpty` helper; terminal resize, Ctrl-C, session stop, same-user IPC, cwd markers, and process-group cleanup are supported. Session metadata and sockets are private to the current user. macOS and Linux ARM64 are not supported yet.
