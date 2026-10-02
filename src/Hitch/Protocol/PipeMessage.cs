namespace Hitch.Protocol;

public sealed record PipeRequest(
    string Action,
    string[]? Keys = null,
    bool Force = false,
    int? Tail = null);

public sealed record PipeResponse(
    bool Ok,
    string? Message = null,
    string? Output = null,
    string? SessionId = null,
    string? WorkingDirectory = null,
    bool CommandRunning = false);
