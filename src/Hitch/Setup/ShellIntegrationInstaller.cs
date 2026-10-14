namespace Hitch.Setup;

public sealed class ShellIntegrationInstaller
{
    private const string StartMarker = "# >>> hitch shell integration >>>";
    private const string EndMarker = "# <<< hitch shell integration <<<";
    private readonly string _root;
    private readonly string _documentsDirectory;
    private readonly Action<string> _addUserPath;
    private readonly HashSet<string> _addedUserPaths = new(StringComparer.OrdinalIgnoreCase);

    public ShellIntegrationInstaller(string? root = null, string? documentsDirectory = null, Action<string>? addUserPath = null)
    {
        _root = root ?? (OperatingSystem.IsLinux()
            ? Path.Combine(Environment.GetEnvironmentVariable("HOME") ?? throw new InvalidOperationException("HOME must be set for Hitch setup."), ".local", "share", "hitch")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hitch"));
        _documentsDirectory = documentsDirectory ?? (OperatingSystem.IsLinux()
            ? Environment.GetEnvironmentVariable("HOME") ?? string.Empty
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        _addUserPath = addUserPath ?? AddUserPath;
    }

    public Task<bool> InstallAsync(string? executablePath = null, CancellationToken cancellationToken = default)
        => InstallCoreAsync(executablePath, requireConfirmation: true, cancellationToken);

    public Task<bool> InstallNonInteractiveAsync(string? executablePath = null, CancellationToken cancellationToken = default)
        => InstallCoreAsync(executablePath, requireConfirmation: false, cancellationToken);

    private async Task<bool> InstallCoreAsync(string? executablePath, bool requireConfirmation, CancellationToken cancellationToken)
    {
        var executable = executablePath ?? ResolveExecutable();
        if (requireConfirmation)
        {
            Console.WriteLine(OperatingSystem.IsLinux()
                ? "Hitch setup will install Bash integration, add Hitch to ~/.local/bin, and back up files before changing them."
                : "Hitch setup will install PowerShell profile functions and a CMD wrapper, add Hitch to your user PATH, and back up files before changing them.");
            Console.Write("Continue? [y/N] ");
            var answer = Console.ReadLine();
            if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("No changes made.");
                return false;
            }
        }

        if (OperatingSystem.IsLinux())
            return await InstallBashAsync(executable, cancellationToken);

        var appDirectory = Path.Combine(_root, "app");
        var binDirectory = Path.Combine(_root, "bin");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(binDirectory);
        var installedExecutable = Path.Combine(appDirectory, "hitch.exe");
        if (!FilesMatch(executable, installedExecutable))
        {
            BackupIfPresent(installedExecutable);
            File.Copy(executable, installedExecutable, overwrite: true);
        }

        var profiles = new[]
        {
            Path.Combine(_documentsDirectory, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
            Path.Combine(_documentsDirectory, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1")
        };
        foreach (var profile in profiles)
            InstallPowerShellProfile(profile, installedExecutable);

        var commandWrapper = Path.Combine(binDirectory, "hitch.cmd");
        await WriteIfChangedAsync(commandWrapper, CreateCommandWrapper(installedExecutable), cancellationToken);
        if (_addedUserPaths.Add(Path.GetFullPath(binDirectory)))
            _addUserPath(binDirectory);
        Console.WriteLine("Shell integration installed. Restart open PowerShell/CMD windows before using the cwd-sync wrappers.");
        return true;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private async Task<bool> InstallBashAsync(string executable, CancellationToken cancellationToken)
    {
        var appDirectory = Path.Combine(_root, "app");
        var binDirectory = Path.Combine(_documentsDirectory, ".local", "bin");
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(binDirectory);
        var installedExecutable = Path.Combine(appDirectory, "hitch");
        if (!FilesMatch(executable, installedExecutable))
        {
            BackupIfPresent(installedExecutable);
            File.Copy(executable, installedExecutable, overwrite: true);
            File.SetUnixFileMode(installedExecutable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        var helperSource = Path.Combine(AppContext.BaseDirectory, "hitch-pty");
        if (File.Exists(helperSource))
        {
            var helperTarget = Path.Combine(appDirectory, "hitch-pty");
            if (!FilesMatch(helperSource, helperTarget))
            {
                BackupIfPresent(helperTarget);
                File.Copy(helperSource, helperTarget, overwrite: true);
            }
            File.SetUnixFileMode(helperTarget, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        else
        {
            throw new FileNotFoundException($"Linux PTY helper was not found at '{helperSource}'. Install the complete Hitch Linux package.");
        }
        var commandPath = Path.Combine(binDirectory, "hitch");
        var commandLinkTarget = new FileInfo(commandPath).LinkTarget;
        var pointsToInstalled = commandLinkTarget is not null
            && Path.GetFullPath(Path.Combine(binDirectory, commandLinkTarget)) == Path.GetFullPath(installedExecutable);
        if (!pointsToInstalled && (File.Exists(commandPath) || commandLinkTarget is not null))
        {
            if (commandLinkTarget is not null)
            {
                var backupDirectory = Path.Combine(_root, "backups");
                Directory.CreateDirectory(backupDirectory);
                var backup = Path.Combine(backupDirectory, $"hitch-link-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.txt");
                await File.WriteAllTextAsync(backup, commandLinkTarget, cancellationToken);
            }
            else BackupIfPresent(commandPath);
            File.Delete(commandPath);
        }
        if (!pointsToInstalled)
            File.CreateSymbolicLink(commandPath, installedExecutable);

        var profile = Path.Combine(_documentsDirectory, ".bashrc");
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        var oldText = File.Exists(profile) ? await File.ReadAllTextAsync(profile, cancellationToken) : string.Empty;
        var block = CreateBashBlock(installedExecutable);
        await WriteIfChangedAsync(profile, ReplaceMarkedBlock(oldText, block), cancellationToken);
        Console.WriteLine("Bash integration installed. Restart Bash or source ~/.bashrc.");
        return true;
    }

    private static string CreateBashBlock(string executable)
    {
        var quoted = "'" + executable.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return $$"""
            {{StartMarker}}
            export PATH="$HOME/.local/bin:$PATH"
            hitch() {
                local starts_session=0
                if (($# == 0)) || [[ "$1" == on || "$1" == start ]]; then starts_session=1; fi
                if (( ! starts_session )); then {{quoted}} "$@"; return $?; fi
                local sync_file
                sync_file=$(mktemp "${TMPDIR:-/tmp}/hitch-cwd.XXXXXX") || return 1
                local had_sync_file=0 old_sync_file=${HITCH_CWD_SYNC_FILE-}
                [[ ${HITCH_CWD_SYNC_FILE+x} ]] && had_sync_file=1
                export HITCH_CWD_SYNC_FILE="$sync_file"
                if (($# == 0)); then {{quoted}} on --shell "$(command -v bash)"
                else {{quoted}} "$@" --shell "$(command -v bash)"; fi
                local result=$?
                if ((had_sync_file)); then export HITCH_CWD_SYNC_FILE="$old_sync_file"; else unset HITCH_CWD_SYNC_FILE; fi
                if [[ -s "$sync_file" ]]; then cd -- "$(cat -- "$sync_file")"; fi
                rm -f -- "$sync_file"
                return "$result"
            }
            unhitch() { hitch off; }
            {{EndMarker}}
            """;
    }

    private void InstallPowerShellProfile(string path, string executable)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var oldText = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        var block = CreatePowerShellBlock(executable);
        WriteIfChanged(path, ReplaceMarkedBlock(oldText, block));
    }

    private static string CreatePowerShellBlock(string executable)
    {
        var quotedExe = "'" + executable.Replace("'", "''", StringComparison.Ordinal) + "'";
        return $$"""
            {{StartMarker}}
            function global:hitch {
                param([Parameter(ValueFromRemainingArguments = $true)][string[]] $HitchArgs)
                $exe = {{quotedExe}}
                $startsSession = ($HitchArgs.Count -eq 0) -or ($HitchArgs[0] -in @('on', 'start'))
                if (-not $startsSession) { & $exe @HitchArgs; return $LASTEXITCODE }
                $syncFile = Join-Path ([IO.Path]::GetTempPath()) ("hitch-cwd-{0}.txt" -f [guid]::NewGuid().ToString('N'))
                $shell = Join-Path $PSHOME ([IO.Path]::GetFileName((Get-Process -Id $PID).Path))
                $oldSync = $env:HITCH_CWD_SYNC_FILE
                $env:HITCH_CWD_SYNC_FILE = $syncFile
                try {
                    if ($HitchArgs.Count -eq 0) { & $exe on --shell $shell }
                    else { & $exe @HitchArgs --shell $shell }
                    if (Test-Path -LiteralPath $syncFile) {
                        $finalDirectory = (Get-Content -LiteralPath $syncFile -Raw).Trim()
                        if (Test-Path -LiteralPath $finalDirectory -PathType Container) { Set-Location -LiteralPath $finalDirectory }
                    }
                }
                finally {
                    Remove-Item -LiteralPath $syncFile -Force -ErrorAction SilentlyContinue
                    if ($null -eq $oldSync) { Remove-Item Env:HITCH_CWD_SYNC_FILE -ErrorAction SilentlyContinue }
                    else { $env:HITCH_CWD_SYNC_FILE = $oldSync }
                }
                return $LASTEXITCODE
            }
            function global:unhitch { hitch off }
            {{EndMarker}}
            """;
    }

    private static string CreateCommandWrapper(string executable)
    {
        var escaped = executable.Replace("%", "%%", StringComparison.Ordinal);
        return $"""
            @echo off
            if "%~1"=="" goto hitch_start
            if /i "%~1"=="on" goto hitch_start
            if /i "%~1"=="start" goto hitch_start
            "{escaped}" %*
            exit /b %ERRORLEVEL%
            :hitch_start
            setlocal
            set "HITCH_CWD_SYNC_FILE=%TEMP%\hitch-cwd-%RANDOM%-%RANDOM%.txt"
            if "%~1"=="" ("{escaped}" on --shell "%ComSpec%") else ("{escaped}" %* --shell "%ComSpec%")
            set "HITCH_EXIT_CODE=%ERRORLEVEL%"
            if exist "%HITCH_CWD_SYNC_FILE%" set /p "HITCH_FINAL_CWD="<"%HITCH_CWD_SYNC_FILE%"
            if defined HITCH_FINAL_CWD cd /d "%HITCH_FINAL_CWD%"
            if exist "%HITCH_CWD_SYNC_FILE%" del "%HITCH_CWD_SYNC_FILE%" >nul 2>nul
            endlocal & exit /b %HITCH_EXIT_CODE%
            """;
    }

    private void AddUserPath(string directory)
    {
        var oldPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? string.Empty;
        var entries = oldPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (entries.Any(entry => string.Equals(entry.TrimEnd(Path.DirectorySeparatorChar), directory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)))
            return;

        var backupDirectory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backupDirectory);
        File.WriteAllText(Path.Combine(backupDirectory, $"user-path-{DateTimeOffset.UtcNow:yyyyMMddHHmmssfff}.txt"), oldPath);
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, new[] { directory }.Concat(entries)), EnvironmentVariableTarget.User);
    }

    private void BackupIfPresent(string path)
    {
        if (!File.Exists(path))
            return;
        var backupDirectory = Path.Combine(_root, "backups");
        Directory.CreateDirectory(backupDirectory);
        var parent = Path.GetFileName(Path.GetDirectoryName(path)) ?? "root";
        var name = parent + "-" + Path.GetFileName(path) + "." + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff") + ".bak";
        File.Copy(path, Path.Combine(backupDirectory, name), overwrite: false);
    }

    private static bool FilesMatch(string source, string target)
    {
        if (!File.Exists(target))
            return false;
        using var sourceStream = File.OpenRead(source);
        using var targetStream = File.OpenRead(target);
        if (sourceStream.Length != targetStream.Length)
            return false;
        var left = new byte[81920];
        var right = new byte[81920];
        int read;
        while ((read = sourceStream.Read(left, 0, left.Length)) > 0)
        {
            if (targetStream.Read(right, 0, read) != read || !left.AsSpan(0, read).SequenceEqual(right.AsSpan(0, read)))
                return false;
        }
        return true;
    }

    private void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), content, StringComparison.Ordinal))
            return;
        BackupIfPresent(path);
        File.WriteAllText(path, content);
    }

    private async Task WriteIfChangedAsync(string path, string content, CancellationToken cancellationToken)
    {
        if (File.Exists(path) && string.Equals(await File.ReadAllTextAsync(path, cancellationToken), content, StringComparison.Ordinal))
            return;
        BackupIfPresent(path);
        await File.WriteAllTextAsync(path, content, cancellationToken);
    }

    private static string ReplaceMarkedBlock(string content, string block)
    {
        var start = content.IndexOf(StartMarker, StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = content.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end >= 0)
                return content[..start] + block + content[(end + EndMarker.Length)..];
        }
        return content.TrimEnd() + (content.Length == 0 ? string.Empty : Environment.NewLine + Environment.NewLine)
               + block + Environment.NewLine;
    }

    private static string ResolveExecutable()
    {
        var path = Environment.ProcessPath;
        if (path is not null && Path.GetFileNameWithoutExtension(path).Equals("hitch", StringComparison.OrdinalIgnoreCase))
            return path;
        var appHost = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsLinux() ? "hitch" : "hitch.exe");
        if (File.Exists(appHost))
            return appHost;
        throw new InvalidOperationException($"Could not locate the self-contained hitch.exe under '{AppContext.BaseDirectory}'.");
    }
}
