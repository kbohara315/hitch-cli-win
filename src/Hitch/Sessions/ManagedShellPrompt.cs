namespace Hitch.Sessions;

public static class ManagedShellPrompt
{
    public const string PowerShellBootstrap = """
        $global:__hitchManagedOriginalPrompt = (Get-Command prompt -CommandType Function).ScriptBlock
        function global:prompt {
            $cwd = [uri]::EscapeDataString((Get-Location).Path)
            [Console]::Write("$([char]27)]7;file://localhost/$cwd$([char]7)")
            $basePrompt = & $global:__hitchManagedOriginalPrompt
            if ($env:HITCH_SESSION) { return "$([char]27)[32m#$($env:HITCH_SESSION)$([char]27)[0m $basePrompt" }
            return $basePrompt
        }
        function global:hitch {
            param([Parameter(ValueFromRemainingArguments = $true)][string[]] $HitchArgs)
            & $env:HITCH_EXE @HitchArgs
            return $LASTEXITCODE
        }
        function global:unhitch { hitch off }
        try {
            Set-PSReadLineKeyHandler -Chord 'Ctrl+\' -BriefDescription 'Stop Hitch session' -ScriptBlock { & $env:HITCH_EXE off }
        } catch { }
        """;
}
