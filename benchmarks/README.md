# Hitch performance benchmarks

Run the end-to-end benchmark from PowerShell 5.1 in a quiet Windows session. It launches Hitch itself, controls managed shells through the same CLI and named-pipe path used by callers, and stores machine-specific results under `%LOCALAPPDATA%\Hitch\benchmarks` (not in the repository).

```powershell
.\benchmarks\run.ps1
```

The default matrix covers Windows PowerShell 5.1 and Command Prompt at 1, 5, 10, and 20 simultaneous shared sessions. It measures command startup, managed-session startup, interactive command-to-output latency, output throughput, plus idle CPU and memory. Requests are exercised one shell at a time while all sessions remain open, avoiding queued input from skewing response measurements. Workloads use harmless echo/write commands. Sessions started by the runner are stopped and cleaned up in a `finally` block; the runner refuses to start if another live Hitch session already exists.

Run a smaller smoke test or choose a particular published executable:

```powershell
.\benchmarks\run.ps1 -Concurrency 1,10 -Samples 3 -OutputLines 500
.\benchmarks\run.ps1 -HitchPath C:\path\to\hitch.exe
```

`pwsh` can be selected with `-Shells powershell,cmd,pwsh` when PowerShell 7 is installed. Keep the machine plugged into power and close unusually heavy background workloads for comparisons. Run the same matrix before and after each optimization; compare repeated-run medians and p95 latency, throughput, CPU, and memory. Timing results are diagnostic and are not suitable as CI pass/fail thresholds.
