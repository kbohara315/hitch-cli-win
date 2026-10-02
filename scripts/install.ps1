param(
    [string] $Repository = 'kbohara315/hitch-cli-win'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

if ($PSVersionTable.PSVersion.Major -ge 6 -and -not $IsWindows) {
    throw 'Hitch currently supports Windows x64 only.'
}
if ($PSVersionTable.PSVersion.Major -lt 6 -and [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    throw 'Hitch currently supports Windows x64 only.'
}
if ([Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne [Runtime.InteropServices.Architecture]::X64) {
    throw 'Hitch requires a Windows x64 operating system.'
}
$windowsVersion = [Environment]::OSVersion.Version
if ($windowsVersion.Major -lt 10 -or ($windowsVersion.Major -eq 10 -and $windowsVersion.Build -lt 17763)) {
    throw 'Hitch requires Windows 10 version 1809 (build 17763) or newer.'
}

$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers @{ 'User-Agent' = 'Hitch-Installer' }
if (-not $release.tag_name -or $release.tag_name -notmatch '^v?\d+\.\d+\.\d+') {
    throw 'The latest GitHub release has no valid version tag.'
}
$assets = @{}
foreach ($asset in $release.assets) { $assets[$asset.name] = $asset.browser_download_url }
if (-not $assets.ContainsKey('hitch.exe') -or -not $assets.ContainsKey('hitch.exe.sha256')) {
    throw 'The latest release is missing hitch.exe or hitch.exe.sha256.'
}

$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) ("hitch-install-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempDirectory | Out-Null
try {
    $exe = Join-Path $tempDirectory 'hitch.exe'
    $checksumFile = Join-Path $tempDirectory 'hitch.exe.sha256'
    Invoke-WebRequest -Uri $assets['hitch.exe'] -OutFile $exe -Headers @{ 'User-Agent' = 'Hitch-Installer' }
    Invoke-WebRequest -Uri $assets['hitch.exe.sha256'] -OutFile $checksumFile -Headers @{ 'User-Agent' = 'Hitch-Installer' }
    $expected = ([IO.File]::ReadAllText($checksumFile).Trim() -split '\s+')[0].Trim('*').ToLowerInvariant()
    if ($expected -notmatch '^[0-9a-f]{64}$') { throw 'The published SHA256 checksum is malformed.' }
    $actual = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $expected) { throw 'SHA256 verification failed; Hitch was not installed.' }

    $versionOutput = & $exe --version 2>&1
    if ($LASTEXITCODE -ne 0 -or ($versionOutput -join "`n") -notmatch '^hitch \d+\.\d+\.\d+$') {
        throw 'The downloaded hitch.exe failed its version smoke test; Hitch was not installed.'
    }

    & $exe setup --yes
    if ($LASTEXITCODE -ne 0) { throw "Hitch setup failed with exit code $LASTEXITCODE." }
    Write-Host "Installed Hitch $($release.tag_name). Open a new PowerShell or Command Prompt window, then run: hitch"
}
finally {
    Remove-Item -LiteralPath $tempDirectory -Recurse -Force -ErrorAction SilentlyContinue
}
