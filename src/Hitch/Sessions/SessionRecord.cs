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
