# Hitch

This context defines the language for Hitch's Windows and Linux shared-session tool and its agent-facing commands.

## Language

**Shared session**:
A Hitch-managed shell and its terminal input/output, available to the human using it and to Hitch commands run by the same operating-system user.
_Avoid_: Shared terminal (ambiguous between the terminal emulator and the shell session)

**Managed shell**:
The nested PowerShell/Command Prompt process on Windows or Bash process on Linux started and supervised by Hitch inside a shared session.
_Avoid_: Current shell (suggests Hitch attaches to the shell that launched it)

**Session ID**:
The identifier used by Hitch commands to select one of the user's concurrent shared sessions.
_Avoid_: Terminal number (sounds like an OS-assigned console index)
