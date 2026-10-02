namespace Hitch.Sessions;

/// <summary>Keeps the current managed-shell activity state in memory for concurrent session paths.</summary>
internal sealed class SessionActivityTracker(SessionState initialState)
{
    private readonly object _gate = new();
    private SessionState _current = initialState;

    public SessionState Current
    {
        get
        {
            lock (_gate)
                return _current;
        }
    }

    public SessionState Submitted(DateTimeOffset now) => Update(state => SessionActivity.Submitted(state, now));

    public SessionState PromptObserved(string currentDirectory, DateTimeOffset now) =>
        Update(state => SessionActivity.PromptObserved(state, currentDirectory, now));

    public SessionState ProcessObserved(string? activeProcess, DateTimeOffset now) =>
        Update(state => SessionActivity.ProcessObserved(state, activeProcess, now));

    private SessionState Update(Func<SessionState, SessionState> transition)
    {
        lock (_gate)
        {
            _current = transition(_current);
            return _current;
        }
    }
}
