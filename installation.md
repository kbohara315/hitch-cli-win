# Install Hitch for Windows

This guide installs Hitch for the **current Windows user** and makes the command available as `hitch` in PowerShell and Command Prompt.

## Requirements

- Windows 10 version 1809 (build 17763) or newer
- Windows x64
- .NET 10 SDK only if building Hitch from source; the published executable is self-contained

## Install from this repository

For the one-command install of the latest release, open PowerShell on Windows x64 (Windows 10 version 1809 or newer) and run:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "`$script = (Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/kbohara315/hitch-cli-win/main/scripts/install.ps1').Content; Invoke-Expression `$script"
```

It downloads and SHA256-verifies the self-contained Native AOT release, installs for the current user in `%LOCALAPPDATA%\Hitch`, and reuses Hitch's setup to configure PowerShell/CMD integration and user PATH with backups. No .NET runtime/SDK or administrator access is required. Open a fresh shell and type `hitch`.

The source-build steps below remain available:

Open PowerShell in the repository root and publish the self-contained executable:

```powershell
dotnet publish .\src\Hitch\Hitch.csproj -c Release -r win-x64
```

Run the setup wizard from the published executable:

```powershell
& ".\src\Hitch\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\hitch.exe" setup
```

Review the prompt and enter `y` to continue. Setup then:

1. Copies `hitch.exe` to `%LOCALAPPDATA%\Hitch\app\hitch.exe`.
2. Adds `%LOCALAPPDATA%\Hitch\bin` to the **current user's** PATH and installs `hitch.cmd` there for Command Prompt.
3. Adds the Hitch function to the Windows PowerShell 5.1 and PowerShell 7 profile files, preserving other profile content.
4. Backs up files it replaces under `%LOCALAPPDATA%\Hitch\backups` and records the previous user PATH there if it changes it.

Setup does not require administrator access. Restart open PowerShell and CMD windows after setup so they load the updated profile and PATH.

## Install the agent skill

The shell installer does not install agent skills. To install the repository's Hitch skill for OpenCode, run:

```powershell
npx skills add kbohara315/hitch-cli-win --skill hitch --agent opencode
```

Omit `--agent opencode` to let `npx skills` detect installed agents, or add `--global` for a user-wide skill. `hitch --skill` prints the same instructions without installing the skill.

## Verify the installation

In a **new** PowerShell window:

```powershell
Get-Command hitch
hitch --version
```

In a **new** Command Prompt window:

```bat
where hitch
hitch --version
```

Both shells should resolve Hitch to the per-user installation and print `hitch 0.1.0`.

## Start and stop a shared session

Start a shared session in PowerShell or CMD:

```text
hitch
```

Hitch starts a nested PowerShell or Command Prompt matching the shell that invoked it. You can also select the shell explicitly:

```powershell
# PowerShell 7
hitch on --shell "$PSHOME\pwsh.exe"

# Windows PowerShell 5.1
hitch on --shell "$PSHOME\powershell.exe"

# From PowerShell, start Command Prompt
hitch on --shell "$env:ComSpec"
```

The managed prompt shows the shared session ID in green, such as `#1`. Inside the managed shell, stop sharing and return to the invoking shell with:

```text
hitch off
```

`Ctrl+\` is also supported as a stop shortcut. When the PowerShell key handler is available, Hitch binds that chord to stop the session.

The shell integration syncs the managed shell's final working directory back to the invoking shell. Without integration, direct invocation still starts a session, but the parent working directory is not synchronized.

## Agent commands

Hitch commands can inspect or control sessions belonging to the same Windows user:

```powershell
hitch status
hitch context
hitch context 1
hitch capture -t 1 --tail 80
hitch send-keys -t 1 "Get-Location" Enter
hitch kill 1
```

If a foreground process is running, Hitch refuses ordinary `send-keys` input unless `--force` is specified. Control keys such as `C-c` remain allowed:

```powershell
hitch send-keys -t 1 C-c
hitch send-keys -t 1 --force "input for the running process" Enter
```

Session metadata and bounded transcripts live under `%LOCALAPPDATA%\Hitch\sessions` while a managed shell is active. Hitch removes them when the session ends and prunes confirmed-stale sessions on a later run.

## Update the installed executable

Publish a new version, stop all active Hitch sessions, then copy the new executable over the installed one:

```powershell
dotnet publish .\src\Hitch\Hitch.csproj -c Release -r win-x64
Copy-Item -Force `
  .\src\Hitch\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\hitch.exe `
  "$env:LOCALAPPDATA\Hitch\app\hitch.exe"
```

The existing shell wrappers continue to use the installed path; restart open Hitch sessions after updating.
