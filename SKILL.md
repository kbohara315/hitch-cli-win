---
name: hitch
description: Inspect and operate a user's Hitch shared sessions on Windows or Linux x64. Use when asked to collaborate in a managed shell, inspect a running command, send shell input, capture output, or manage a long-running process the user wants to share.
---

# Hitch shared sessions

Use Hitch when the user wants to share shell work or when inspecting an existing shared session can avoid duplicating a long-running process. Use the agent's normal shell for private, short commands.

## Terms

- A **shared session** is a Hitch-managed shell and its input/output, available to the user and Hitch commands run by that same operating-system user.
- A **managed shell** is the nested PowerShell/Command Prompt process on Windows or Bash process on Linux that Hitch starts and supervises.
- A **Session ID** selects one of the user's concurrent shared sessions. Discover it; do not guess it or call it a terminal number.

## Workflow

1. Run `hitch status` to list live shared sessions. Run `hitch context` to inspect their current directories, process state, and recent output; use `hitch context <Session ID>` to focus on one.
2. Use a shared session only when the user wants that work visible or it is already running there. Check its context before sending input so you do not duplicate a server, build, test, tunnel, or other foreground process.
3. At a shell prompt, send a command with `hitch send-keys -t <Session ID> <text-or-keys...> Enter`. Examples:

   ```powershell
   hitch send-keys -t 2 "Get-Location" Enter
   hitch send-keys -t 2 "dotnet test Hitch.sln" Enter
   ```

   ```sh
   hitch send-keys -t 3 "echo ready" Enter
   ```

4. Inspect new output with `hitch capture -t <Session ID> --tail 80` or `hitch context <Session ID>`. Prefer these over repeatedly resending a command. Hitch has no `--wait` option; after starting long work, allow it to run, then inspect it.
5. Stop only the intended shared session. Run `hitch off` inside its managed shell, or `hitch kill <Session ID>` from outside when the user asks to end that session.

## Input and safety

- Hitch rejects ordinary text input while a foreground process is running. Treat that as a signal to inspect the session, not to retry. Use `--force` only when the user explicitly wants input delivered to that foreground process; it does not start a separate shell command.
- Control keys such as `C-c` can interrupt a process. Send them only when the user requests an interrupt or it is clearly necessary to stop work.
- Do not send commands to an unknown or unrelated session. Check `hitch status` and `hitch context <Session ID>` first.
- `hitch off` applies to the current managed shell; use `hitch kill <Session ID>` to select a different session.
- Shell integration syncs the managed shell's final working directory back to the invoking shell. Directly launching `hitch.exe on` on Windows or `hitch on` outside the Bash function bypasses that wrapper behavior.

## If Hitch reports no session

Run `hitch status` and use the Session ID it prints. If no shared session is listed, ask the user to start one with `hitch` or `hitch on` in PowerShell, Command Prompt, or Bash. Do not substitute unsupported options such as `context --all`, `send-keys --wait`, or `capture-pane`.
