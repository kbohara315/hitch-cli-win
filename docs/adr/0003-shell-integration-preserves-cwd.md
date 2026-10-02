# Preserve the invoking shell's working directory

When the user exits a managed shell, Hitch should return its final working directory to the shell that invoked it. Because a child process cannot change its parent's working directory directly, guided setup installs a PowerShell function and a CMD wrapper only after confirmation and creates a backup before editing integration files; the agent skill installer remains out of the first release.
