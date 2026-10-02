# Treat session data as ephemeral

Store active session metadata and bounded output under `%LOCALAPPDATA%\Hitch\sessions`, remove it after a normal session end, and prune data only after confirming a session is stale on a later Hitch run. This preserves useful agent context while a shell is live without retaining transcripts after exit; persistent session history is outside the first release.
