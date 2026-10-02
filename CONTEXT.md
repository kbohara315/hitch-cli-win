# Hitch for Windows

This context defines the language for the Windows-first C# terminal-sharing tool and its agent-facing session commands.

## Language

**Shared session**:
A Hitch-managed shell and its terminal input/output, available to the human using it and to Hitch commands run by the same Windows user.
_Avoid_: Shared terminal (ambiguous between the terminal emulator and the shell session)

**Managed shell**:
The nested PowerShell or Command Prompt process started and supervised by Hitch inside a shared session.
_Avoid_: Current shell (suggests Hitch attaches to the shell that launched it)

**Session ID**:
The identifier used by Hitch commands to select one of the user's concurrent shared sessions.
_Avoid_: Terminal number (sounds like an OS-assigned console index)
