namespace Hitch.Sessions;

public sealed record SessionRecord(
    string Id,
    int ProcessId,
    string Shell,
    string WorkingDirectory,
    string PipeName,
    DateTimeOffset StartedAt);

public sealed record SessionState(
    bool CommandRunning,
    string? ActiveCommand,
    string CurrentDirectory,
    DateTimeOffset UpdatedAt,
    bool AtPrompt = false);

internal static class SessionActivity
{
    public static SessionState Submitted(SessionState state, DateTimeOffset now) => state with
    {
        CommandRunning = true,
        ActiveCommand = "shell command",
        AtPrompt = false,
        UpdatedAt = now
    };

    public static SessionState PromptObserved(SessionState state, string currentDirectory, DateTimeOffset now) => state with
    {
        CurrentDirectory = currentDirectory,
        CommandRunning = false,
        ActiveCommand = null,
        AtPrompt = true,
        UpdatedAt = now
    };

    public static SessionState ProcessObserved(SessionState state, string? activeProcess, DateTimeOffset now)
    {
        var running = activeProcess is not null || !state.AtPrompt;
        var activeCommand = activeProcess ?? (running ? state.ActiveCommand ?? "shell input" : null);
        if (state.CommandRunning == running && state.ActiveCommand == activeCommand)
            return state;

        return state with
        {
            CommandRunning = running,
            ActiveCommand = activeCommand,
            UpdatedAt = now
        };
    }
}
