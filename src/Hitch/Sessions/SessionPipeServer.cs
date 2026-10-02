using System.IO.Pipes;
using Hitch.Native;
using Hitch.Protocol;

namespace Hitch.Sessions;

public sealed class SessionPipeServer
{
    private readonly string _pipeName;
    private readonly string _sessionId;
    private readonly SessionStore _store;
    private readonly IConPtySession _shell;
    private readonly BoundedOutputLog _outputLog;
    private readonly BoundedOutputLog _activeOutputLog;
    private readonly Action _requestStop;

    public SessionPipeServer(
        string pipeName,
        string sessionId,
        SessionStore store,
        IConPtySession shell,
        BoundedOutputLog outputLog,
        BoundedOutputLog activeOutputLog,
        Action requestStop)
    {
        _pipeName = pipeName;
        _sessionId = sessionId;
        _store = store;
        _shell = shell;
        _outputLog = outputLog;
        _activeOutputLog = activeOutputLog;
        _requestStop = requestStop;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var pipe = new NamedPipeServerStream(
                _pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                inBufferSize: 4096,
                outBufferSize: 4096);
            try
            {
                await pipe.WaitForConnectionAsync(cancellationToken);
                var request = await FramedJson.ReadAsync<PipeRequest>(pipe, cancellationToken);
                if (request is null)
                    continue;
                var response = await HandleAsync(request, cancellationToken);
                await FramedJson.WriteAsync(pipe, response, cancellationToken);
                if (request.Action.Equals("stop", StringComparison.OrdinalIgnoreCase))
                {
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(100);
                        _requestStop();
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException)
            {
                // A disconnected agent only drops its own command request.
            }
            catch (InvalidDataException)
            {
                // Malformed local client data must not take down the managed shell.
            }
        }
    }

    private async Task<PipeResponse> HandleAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var state = _store.ReadState(_sessionId);
        switch (request.Action.ToLowerInvariant())
        {
            case "status":
                return new PipeResponse(true, SessionId: _sessionId,
                    WorkingDirectory: state.CurrentDirectory, CommandRunning: state.CommandRunning,
                    Message: state.ActiveCommand);

            case "context":
            case "capture":
                {
                    var raw = await _outputLog.ReadTextAsync(cancellationToken);
                    var output = TerminalTextRenderer.Tail(raw, request.Tail ?? 80);
                    if (request.Action.Equals("context", StringComparison.OrdinalIgnoreCase))
                    {
                        var running = state.CommandRunning ? $"running: {state.ActiveCommand}" : "idle";
                        var activeOutput = state.CommandRunning
                            ? TerminalTextRenderer.Tail(await _activeOutputLog.ReadTextAsync(cancellationToken), 5)
                            : string.Empty;
                        output = $"session: {_sessionId}{Environment.NewLine}cwd: {state.CurrentDirectory}{Environment.NewLine}command: {running}"
                                 + (activeOutput.Length > 0 ? $"{Environment.NewLine}--- active output ---{Environment.NewLine}{activeOutput}" : string.Empty)
                                 + (output.Length > 0 ? $"{Environment.NewLine}--- recent output ---{Environment.NewLine}{output}" : string.Empty);
                    }
                    return new PipeResponse(true, SessionId: _sessionId, Output: output,
                        WorkingDirectory: state.CurrentDirectory, CommandRunning: state.CommandRunning);
                }

            case "send-keys":
                {
                    var keys = request.Keys ?? [];
                    if (keys.Length == 0)
                        return new PipeResponse(false, "Provide text or keys to send.");
                    if (state.CommandRunning && !request.Force
                        && !KeyEncoder.IsControlOnly(keys)
                        && !KeyEncoder.StartsWithInterrupt(keys))
                        return new PipeResponse(false, "A foreground process is running. Use --force to send text anyway.",
                            SessionId: _sessionId, WorkingDirectory: state.CurrentDirectory, CommandRunning: true);

                    if (keys.Any(key => key is "Enter" or "C-m"))
                        _store.SaveState(_sessionId, SessionActivity.Submitted(state, DateTimeOffset.UtcNow));
                    await _shell.WriteInputAsync(KeyEncoder.Encode(keys), cancellationToken);
                    return new PipeResponse(true, "Input sent.", SessionId: _sessionId);
                }

            case "stop":
                return new PipeResponse(true, "Stopping shared session.", SessionId: _sessionId);

            default:
                return new PipeResponse(false, $"Unknown session action '{request.Action}'.");
        }
    }
}
