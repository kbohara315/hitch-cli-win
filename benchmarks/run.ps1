[CmdletBinding()]
param(
    [string] $HitchPath,
    [ValidateSet('powershell', 'cmd', 'pwsh')]
    [string[]] $Shells = @('powershell', 'cmd'),
    [int[]] $Concurrency = @(1, 5, 10, 20),
    [ValidateRange(1, 50)]
    [int] $Samples = 5,
    [ValidateRange(1, 300)]
    [int] $IdleSeconds = 10,
    [ValidateRange(100, 100000)]
    [int] $OutputLines = 500,
    [string] $ResultsPath
)

$ErrorActionPreference = 'Stop'

if (-not $HitchPath) {
    $installed = Join-Path $env:LOCALAPPDATA 'Hitch\app\hitch.exe'
    $built = Join-Path $PSScriptRoot '..\src\Hitch\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\hitch.exe'
    if (Test-Path -LiteralPath $installed) { $HitchPath = $installed }
    elseif (Test-Path -LiteralPath $built) { $HitchPath = (Resolve-Path -LiteralPath $built).Path }
    else { throw 'Could not find Hitch. Publish it or pass -HitchPath.' }
}
$HitchPath = (Resolve-Path -LiteralPath $HitchPath).Path
if (-not $ResultsPath) {
    $resultDirectory = Join-Path $env:LOCALAPPDATA 'Hitch\benchmarks'
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    $ResultsPath = Join-Path $resultDirectory ('run-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.json')
}

$sessionRoot = Join-Path $env:LOCALAPPDATA 'Hitch\sessions'
$systemRoot = [Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
$shellPaths = @{
    powershell = Join-Path $systemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    cmd = $env:ComSpec
}
if (Get-Command pwsh.exe -ErrorAction SilentlyContinue) {
    $shellPaths.pwsh = (Get-Command pwsh.exe).Source
}
foreach ($shellName in $Shells) {
    if (-not $shellPaths.ContainsKey($shellName) -or -not (Test-Path -LiteralPath $shellPaths[$shellName])) {
        throw "Requested shell '$shellName' is not installed or could not be located."
    }
}

function Get-LiveSessionRecords {
    if (-not (Test-Path -LiteralPath $sessionRoot)) { return @() }
    $records = @()
    $files = @(Get-ChildItem -LiteralPath $sessionRoot -Filter 'session.json' -File -Recurse -ErrorAction SilentlyContinue)
    Write-Verbose "Scanning $($files.Count) session record(s) under $sessionRoot."
    foreach ($file in $files) {
        try {
            $record = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json
            $process = Get-Process -Id ([int]$record.ProcessId) -ErrorAction SilentlyContinue
            if ($process) { $records += $record }
        } catch { }
    }
    return $records
}

function Quote-ProcessArgument([string] $value) {
    if ($value -notmatch '[\s"]') { return $value }
    $quoted = $value -replace '(\\*)"', '$1$1\"'
    $quoted = $quoted -replace '(\\+)$', '$1$1'
    return '"' + $quoted + '"'
}

function Start-HitchCommand([string[]] $arguments) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = $HitchPath
    $startInfo.Arguments = (($arguments | ForEach-Object { Quote-ProcessArgument $_ }) -join ' ')
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $process = New-Object System.Diagnostics.Process
    $process.StartInfo = $startInfo
    if (-not $process.Start()) { throw "Could not start Hitch command: $($arguments -join ' ')" }
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    return [pscustomobject]@{ Process = $process; Stdout = $stdout; Stderr = $stderr; Started = [DateTime]::UtcNow }
}

function Complete-HitchCommand($command, [int] $TimeoutSeconds = 30) {
    if (-not $command.Process.WaitForExit($TimeoutSeconds * 1000)) {
        try { $command.Process.Kill() } catch { }
        throw "Hitch command timed out after $TimeoutSeconds seconds."
    }
    $command.Process.WaitForExit()
    return [pscustomobject]@{
        ExitCode = $command.Process.ExitCode
        Output = $command.Stdout.Result + $command.Stderr.Result
        ElapsedMs = ([DateTime]::UtcNow - $command.Started).TotalMilliseconds
    }
}

function Get-Percentile([double[]] $values, [double] $percentile) {
    if (-not $values -or $values.Count -eq 0) { return $null }
    $sorted = @($values | Sort-Object)
    $index = [Math]::Min($sorted.Count - 1, [Math]::Ceiling($percentile * $sorted.Count) - 1)
    return [Math]::Round($sorted[$index], 2)
}

function Get-Stats([double[]] $values) {
    return [pscustomobject]@{
        Samples = $values.Count
        Median = Get-Percentile $values 0.50
        P95 = Get-Percentile $values 0.95
        Minimum = if ($values.Count) { [Math]::Round(($values | Measure-Object -Minimum).Minimum, 2) } else { $null }
        Maximum = if ($values.Count) { [Math]::Round(($values | Measure-Object -Maximum).Maximum, 2) } else { $null }
    }
}

function Get-SessionRecordsForHosts([int[]] $hostIds) {
    $hostSet = @{}; foreach ($id in $hostIds) { $hostSet[[string]$id] = $true }
    $records = @(Get-LiveSessionRecords | Where-Object { $hostSet.ContainsKey([string]$_.ProcessId) })
    Write-Verbose "Matched $($records.Count) record(s) for host PID(s): $($hostIds -join ', ')."
    return ,$records
}

function Wait-ForSessionRecords([int[]] $hostIds, [int] $TimeoutSeconds = 45) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $records = Get-SessionRecordsForHosts $hostIds
        foreach ($record in $records) {
            $hostEntry = $script:activeHosts | Where-Object { $_.Process.Id -eq [int]$record.ProcessId } | Select-Object -First 1
            if ($hostEntry -and -not $hostEntry.Session) {
                $hostEntry.Session = $record
                $hostEntry.StartupMs = ([DateTime]::UtcNow - $hostEntry.StartedAt).TotalMilliseconds
            }
        }
        $readyHosts = @($script:activeHosts | Where-Object { $hostIds -contains $_.Process.Id -and $_.Session })
        if ($readyHosts.Count -ge $hostIds.Count) {
            return ,$records
        }
        foreach ($hostEntry in $script:activeHosts) {
            if (-not $hostEntry.Process.HasExited) { continue }
            throw "Hitch host exited before becoming ready (exit $($hostEntry.Process.ExitCode))."
        }
        Start-Sleep -Milliseconds 50
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw "Only $($records.Count) of $($hostIds.Count) managed shells became ready in $TimeoutSeconds seconds."
}

function Get-ProcessTreeSnapshot([int[]] $rootIds) {
    $all = @(Get-CimInstance Win32_Process)
    $known = @{}; foreach ($id in $rootIds) { $known[[string]$id] = $true }
    $changed = $true
    while ($changed) {
        $changed = $false
        foreach ($process in $all) {
            if ($known.ContainsKey([string]$process.ProcessId)) { continue }
            if ($known.ContainsKey([string]$process.ParentProcessId)) {
                $known[[string]$process.ProcessId] = $true
                $changed = $true
            }
        }
    }
    $cpu = 0.0; $workingSet = [long]0; $privateBytes = [long]0
    foreach ($id in @($known.Keys)) {
        $process = Get-Process -Id ([int]$id) -ErrorAction SilentlyContinue
        if ($process) {
            $cpu += $process.TotalProcessorTime.TotalSeconds
            $workingSet += $process.WorkingSet64
            $privateBytes += $process.PrivateMemorySize64
        }
    }
    return [pscustomobject]@{ CpuSeconds = $cpu; WorkingSetBytes = $workingSet; PrivateBytes = $privateBytes }
}

function Test-NewOutputContainsResponse($item) {
    if (-not (Test-Path -LiteralPath $item.Path)) { return $false }
    $stream = $null
    try {
        $stream = [IO.File]::Open($item.Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        if ($stream.Length -lt $item.Offset) {
            $item.Offset = 0
            $item.PendingText = ''
        }
        $stream.Position = $item.Offset
        $buffer = New-Object byte[] 8192
        $builder = New-Object System.Text.StringBuilder
        while (($read = $stream.Read($buffer, 0, $buffer.Length)) -gt 0) {
            [void]$builder.Append([Text.Encoding]::ASCII.GetString($buffer, 0, $read))
        }
        $item.Offset = $stream.Position
        $text = [Text.RegularExpressions.Regex]::Replace(
            $builder.ToString(),
            '\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07]*(?:\x07|\x1B\\))|[\x00-\x1F\x7F]',
            '')
        $item.PendingText += $text
        if ($item.PendingText.Length -gt 16384) {
            $item.PendingText = $item.PendingText.Substring($item.PendingText.Length - 16384)
        }
        if ($item.IsRegex) {
            $match = [regex]::Match($item.PendingText, $item.Marker)
            $item.MarkerIndex = $match.Index
            $item.MarkerLength = $match.Length
            if (-not $match.Success) { return $false }
            $markerEnd = $match.Index + $match.Length
        } else {
            $markerIndex = $item.PendingText.LastIndexOf($item.Marker, [StringComparison]::Ordinal)
            $item.MarkerIndex = $markerIndex
            $item.MarkerLength = $item.Marker.Length
            if ($markerIndex -lt 0) { return $false }
            $markerEnd = $markerIndex + $item.Marker.Length
        }
        $item.PromptIndex = $item.PendingText.LastIndexOf('#' + [string]$item.Session.Id, [StringComparison]::Ordinal)
        return $item.PromptIndex -ge $markerEnd
    } catch {
        return $false
    } finally {
        if ($stream) { $stream.Dispose() }
    }
}

function Send-And-Measure([object[]] $sessions, [string] $shellName, [string] $commandForId, [int] $TimeoutSeconds = 60,
    [string] $ExpectedMarker, [int] $SettleMilliseconds = 150) {
    $latencies = @()
    foreach ($session in $sessions) {
        if ($ExpectedMarker) {
            $marker = $ExpectedMarker
            $isRegex = $false
            $shellCommand = $commandForId
        } else {
            $prefix = 'HITCH_BENCH_' + [Guid]::NewGuid().ToString('N').Substring(0, 12) + '_'
            $value = Get-Random -Minimum 100000 -Maximum 999999
            $isRegex = $shellName -eq 'cmd'
            $marker = if ($isRegex) { [regex]::Escape($prefix) + '\d{1,5}' } else { $prefix + $value }
            $shellCommand = $commandForId.Replace('{prefix}', $prefix).Replace('{value}', [string]$value)
        }
        $id = [string]$session.Id
        $path = Join-Path (Join-Path $sessionRoot $id) 'output.log'
        $offset = if (Test-Path -LiteralPath $path) { (Get-Item -LiteralPath $path).Length } else { 0L }
        $client = Start-HitchCommand -arguments @('send-keys', '-t', $id, '--force', $shellCommand, 'Enter')
        $clientResult = Complete-HitchCommand $client $TimeoutSeconds
        if ($clientResult.ExitCode -ne 0) {
            throw "send-keys failed for session ${id}: $($clientResult.Output)"
        }

        $timer = [Diagnostics.Stopwatch]::StartNew()
        $completed = $false
        $item = [pscustomobject]@{ Session = $session; Marker = $marker; IsRegex = $isRegex; Path = $path; Offset = $offset; PendingText = ''; MarkerIndex = -1; MarkerLength = 0; PromptIndex = -1 }
        while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
            if (Test-NewOutputContainsResponse $item) {
                $latencies += [Math]::Round(([DateTime]::UtcNow - $client.Started).TotalMilliseconds, 2)
                $completed = $true
                break
            }
            Start-Sleep -Milliseconds 20
        }
        if (-not $completed) {
            throw "Session $id did not complete the workload in $TimeoutSeconds seconds. Expected [$marker] at $($item.MarkerIndex)+$($item.MarkerLength); prompt #$id at $($item.PromptIndex). Last output: $($item.PendingText)"
        }
        if ($SettleMilliseconds -gt 0) { Start-Sleep -Milliseconds $SettleMilliseconds }
    }
    return $latencies
}

if ((Get-LiveSessionRecords).Count -gt 0) {
    throw 'Stop existing Hitch sessions before running the benchmark; the runner will not disturb them.'
}

$script:activeHosts = @()
$results = [System.Collections.Generic.List[object]]::new()
try {
    $versionSamples = @()
    1..10 | ForEach-Object {
        $command = Start-HitchCommand -arguments @('--version')
        $result = Complete-HitchCommand $command
        if ($result.ExitCode -ne 0 -or $result.Output -notmatch '^hitch\s') { throw "Version command failed: $($result.Output)" }
        $versionSamples += $result.ElapsedMs
    }
    $results.Add([pscustomobject]@{ Type = 'cli-startup'; Shell = 'all'; Concurrency = 0; Metric = 'process-wall-ms'; Stats = Get-Stats -values $versionSamples })

    foreach ($shellName in $Shells) {
        $shellPath = $shellPaths[$shellName]
        foreach ($count in $Concurrency) {
            $groupHosts = @()
            $startupSamples = @()
            for ($index = 0; $index -lt $count; $index++) {
                $arguments = 'on --shell ' + (Quote-ProcessArgument $shellPath)
                $startedAt = [DateTime]::UtcNow
                $process = Start-Process -FilePath $HitchPath -ArgumentList $arguments -PassThru -WindowStyle Hidden
                if (-not $process) { throw "Could not start $shellName Hitch host." }
                $hostEntry = [pscustomobject]@{ Process = $process; StartedAt = $startedAt; StartupMs = $null; Session = $null }
                $script:activeHosts += $hostEntry
                $groupHosts += $hostEntry
            }
            $hostIds = @($groupHosts | ForEach-Object { $_.Process.Id })
            $records = Wait-ForSessionRecords -hostIds $hostIds
            foreach ($hostEntry in $groupHosts) {
                if (-not $hostEntry.Session) { throw "Could not match a $shellName session to host process $($hostEntry.Process.Id)." }
                $startupSamples += $hostEntry.StartupMs
            }
            $sessions = @($groupHosts | ForEach-Object { $_.Session })
            $results.Add([pscustomobject]@{ Type = 'session-startup'; Shell = $shellName; Concurrency = $count; Metric = 'host-to-record-ms'; Stats = Get-Stats -values $startupSamples })

            # Confirm each shell is ready for typed commands before starting measurements.
            $echoCommand = if ($shellName -eq 'cmd') { 'echo {prefix}%RANDOM%' } else { "Write-Output ('{prefix}'+{value})" }
            $readySamples = Send-And-Measure $sessions $shellName $echoCommand
            $latencySamples = @()
            for ($sample = 0; $sample -lt $Samples; $sample++) {
                $latencySamples += Send-And-Measure $sessions $shellName $echoCommand
            }
            $results.Add([pscustomobject]@{ Type = 'interactive-response'; Shell = $shellName; Concurrency = $count; Metric = 'simultaneous-command-to-log-ms'; Stats = Get-Stats -values $latencySamples })

            $beforeIdle = Get-ProcessTreeSnapshot -rootIds $hostIds
            Start-Sleep -Seconds $IdleSeconds
            $afterIdle = Get-ProcessTreeSnapshot -rootIds $hostIds
            $cpuPercent = (($afterIdle.CpuSeconds - $beforeIdle.CpuSeconds) / $IdleSeconds / [Environment]::ProcessorCount) * 100
            $results.Add([pscustomobject]@{
                Type = 'idle-resources'; Shell = $shellName; Concurrency = $count; Seconds = $IdleSeconds
                CpuPercentOfMachine = [Math]::Round($cpuPercent, 3)
                WorkingSetMiB = [Math]::Round($afterIdle.WorkingSetBytes / 1MB, 2)
                PrivateMiB = [Math]::Round($afterIdle.PrivateBytes / 1MB, 2)
            })

            if ($shellName -eq 'cmd') {
                $stressMarker = 'HITCH_STRESS_' + $OutputLines
                $outputCommand = 'for /L %i in (1,1,' + $OutputLines + ') do @echo HITCH_STRESS_%i'
            } else {
                $outputCommand = 'for($i=1;$i -le ' + $OutputLines + ';$i++){[Console]::WriteLine(''HITCH_STRESS_''+$i)};[Console]::WriteLine(''HITCH_STRESS_''+($i-1))'
                $stressMarker = 'HITCH_STRESS_' + $OutputLines
            }
            $outputBaseline = @{}
            foreach ($session in $sessions) {
                $outputFile = Join-Path (Join-Path $sessionRoot $session.Id) 'output.log'
                $outputBaseline[[string]$session.Id] = if (Test-Path -LiteralPath $outputFile) { (Get-Item -LiteralPath $outputFile).Length } else { 0L }
            }
            $beforeStress = Get-ProcessTreeSnapshot -rootIds $hostIds
            $outputTimer = [Diagnostics.Stopwatch]::StartNew()
            $outputSamples = Send-And-Measure $sessions $shellName $outputCommand 120 $stressMarker -SettleMilliseconds 0
            $outputTimer.Stop()
            $afterStress = Get-ProcessTreeSnapshot -rootIds $hostIds
            $bytes = 0L
            foreach ($session in $sessions) {
                $outputFile = Join-Path (Join-Path $sessionRoot $session.Id) 'output.log'
                if (Test-Path -LiteralPath $outputFile) { $bytes += [Math]::Max(0, (Get-Item -LiteralPath $outputFile).Length - $outputBaseline[[string]$session.Id]) }
            }
            $results.Add([pscustomobject]@{
                Type = 'output-stress'; Shell = $shellName; Concurrency = $count; LinesPerSession = $OutputLines
                ElapsedSeconds = [Math]::Round($outputTimer.Elapsed.TotalSeconds, 3)
                LoggedBytes = $bytes
                MiBPerSecond = if ($outputTimer.Elapsed.TotalSeconds -gt 0) { [Math]::Round(($bytes / 1MB) / $outputTimer.Elapsed.TotalSeconds, 2) } else { 0 }
                CpuPercentOfMachine = if ($outputTimer.Elapsed.TotalSeconds -gt 0) {
                    [Math]::Round((($afterStress.CpuSeconds - $beforeStress.CpuSeconds) / $outputTimer.Elapsed.TotalSeconds / [Environment]::ProcessorCount) * 100, 3)
                } else { 0 }
                CommandToOutputMs = Get-Stats -values $outputSamples
            })

            # Measure a representative user-facing capture command while the session is active.
            $captureSamples = @()
            foreach ($session in $sessions) {
                $capture = Start-HitchCommand -arguments @('capture', '-t', [string]$session.Id, '--tail', '20')
                $captureResult = Complete-HitchCommand $capture
                if ($captureResult.ExitCode -ne 0) { throw "capture failed for $shellName session $($session.Id): $($captureResult.Output)" }
                $captureSamples += $captureResult.ElapsedMs
            }
            $results.Add([pscustomobject]@{ Type = 'capture-command'; Shell = $shellName; Concurrency = $count; Metric = 'process-wall-ms'; Stats = Get-Stats -values $captureSamples })

            foreach ($session in $sessions) {
                $stopCommand = Start-HitchCommand -arguments @('kill', [string]$session.Id)
                $stopResult = Complete-HitchCommand $stopCommand
                if ($stopResult.ExitCode -ne 0) { Write-Warning "Cleanup command for session $($session.Id) returned $($stopResult.ExitCode): $($stopResult.Output)" }
            }
            foreach ($hostEntry in $groupHosts) {
                if (-not $hostEntry.Process.WaitForExit(5000)) { try { & taskkill.exe /PID $hostEntry.Process.Id /T /F | Out-Null } catch { } }
            }
            $groupProcessIds = @($groupHosts | ForEach-Object { $_.Process.Id })
            $script:activeHosts = @($script:activeHosts | Where-Object { $groupProcessIds -notcontains $_.Process.Id })
        }
    }

    $computer = Get-CimInstance Win32_ComputerSystem
    $processor = Get-CimInstance Win32_Processor | Select-Object -First 1
    $resultDocument = [pscustomobject]@{
        SchemaVersion = 1
        CapturedAtUtc = [DateTime]::UtcNow.ToString('o')
        HitchPath = $HitchPath
        HitchSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $HitchPath).Hash
        OS = [Environment]::OSVersion.VersionString
        Processor = $processor.Name
        LogicalProcessors = [Environment]::ProcessorCount
        MemoryMiB = [Math]::Round($computer.TotalPhysicalMemory / 1MB)
        Shells = $Shells
        Concurrency = $Concurrency
        Samples = $Samples
        Results = @($results)
    }
    $resultDocument | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ResultsPath -Encoding UTF8
    Write-Host "Benchmark results: $ResultsPath"
    $results | Format-Table Type,Shell,Concurrency,Metric,@{Label='Median';Expression={$_.Stats.Median}},@{Label='P95';Expression={$_.Stats.P95}},MiBPerSecond,CpuPercentOfMachine,PrivateMiB -AutoSize
}
finally {
    foreach ($hostEntry in @($script:activeHosts)) {
        if ($hostEntry.Session) {
            try {
                $cleanup = Start-HitchCommand -arguments @('kill', [string]$hostEntry.Session.Id)
                $null = Complete-HitchCommand $cleanup 10
            } catch { }
        }
        if ($hostEntry.Process -and -not $hostEntry.Process.HasExited) {
            try { if (-not $hostEntry.Process.WaitForExit(5000)) { & taskkill.exe /PID $hostEntry.Process.Id /T /F | Out-Null } } catch { }
        }
    }
}
