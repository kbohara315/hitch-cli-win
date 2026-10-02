# Hitch for Windows

A Windows-first C# implementation of Hitch’s shared-session model. Hitch launches a nested PowerShell or Command Prompt in a ConPTY session, mirrors the managed shell to the invoking terminal, and exposes same-user agent commands over an ACL-restricted named pipe.

## Requirements

- Windows 10 version 1809 (build 17763) or newer
- .NET 10 SDK to build
- Windows x64 for the initial release target

## Build and test

```powershell
dotnet build Hitch.sln
dotnet test Hitch.sln
```

Publish the self-contained single-file executable with:

```powershell
dotnet publish src/Hitch/Hitch.csproj -c Release -r win-x64
```

The executable is written to `src/Hitch/bin/Release/net10.0-windows10.0.17763.0/win-x64/publish/hitch.exe`.

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

Run `hitch.exe setup` for an explicit, confirmed installation of the PowerShell profiles and CMD wrapper. Setup backs up modified files and adds its wrapper directory to the user PATH. The built-in `hitch.exe --skill` prints agent command instructions; managed skill installation is deferred.

Session metadata and bounded transcripts are stored under `%LOCALAPPDATA%\Hitch\sessions` while active, then removed when the managed shell ends. Dead session directories are pruned the next time Hitch lists sessions.

## Current implementation boundary

The native ConPTY adapter, per-session host loop, terminal byte forwarding/resizing, current-user named-pipe protocol, session files, command/context/capture commands, guarded `send-keys`, shell setup, and cleanup path are implemented. This is an initial vertical slice; detailed parity features such as alternate-screen filtering, richer wait modes, and a managed agent-skill installer remain follow-up work.
