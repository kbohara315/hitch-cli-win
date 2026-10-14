namespace Hitch.Sessions;

public static class ManagedShellPrompt
{
    public const string BashBootstrap = """
        if [[ -n "$HOME" && -r "$HOME/.bashrc" && "$HOME/.bashrc" != "$BASH_SOURCE" ]]; then source "$HOME/.bashrc"; fi
        __hitch_original_prompt_command=("${PROMPT_COMMAND[@]-}")
        __hitch_base_ps1=${PS1-}
        __hitch_prompt_command() {
            local __hitch_command
            for __hitch_command in "${__hitch_original_prompt_command[@]}"; do eval "$__hitch_command"; done
            printf '\033]7;file://localhost%s\007' "$PWD"
            if [[ -n "$HITCH_SESSION" ]]; then PS1="\\[\\e[32m\\]#$HITCH_SESSION\\[\\e[0m\\] $__hitch_base_ps1"; fi
        }
        PROMPT_COMMAND=__hitch_prompt_command
        hitch() { "$HITCH_EXE" "$@"; }
        unhitch() { hitch off; }
        """;

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
