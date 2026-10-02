# Restrict session control to the same Windows user

Agent-facing commands communicate with session hosts over Windows named pipes, and only processes running as the same Windows user may inspect or control those sessions. Session data lives under that user's `%LOCALAPPDATA%\Hitch\sessions`. This keeps the control plane local without opening TCP ports and makes the same-user boundary explicit; cross-user and remote control are outside the first release.
