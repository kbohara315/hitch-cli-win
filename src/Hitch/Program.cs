using Hitch.Protocol;
using Hitch.Setup;
using Hitch.Sessions;

namespace Hitch;

internal static class Program
{
    private static readonly SessionStore Store = new();

    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await RunAsync(args);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or
                                           UnauthorizedAccessException or FileNotFoundException or
                                           PlatformNotSupportedException)
        {
            Console.Error.WriteLine($"hitch: {exception.Message}");
            return 1;
        }
    }

    private static async Task<int> RunAsync(string[] args)
    {
        var invocation = Parse(args);
        if (invocation.Kind == CommandKind.Help)
        {
            PrintHelp();
            return 0;
        }
        if (invocation.Kind == CommandKind.Version)
        {
            Console.WriteLine($"hitch {typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"}");
            return 0;
        }
        if (invocation.Kind == CommandKind.Skill)
        {
            Console.Write(GetAgentInstructions());
            return 0;
        }

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Hitch currently supports Windows x64 and Linux x64 only.");

        return invocation.Kind switch
        {
            CommandKind.Start => await StartAsync(invocation.ShellPath),
            CommandKind.Stop => await StopCurrentAsync(),
            CommandKind.Status => await StatusAsync(invocation.Arguments),
            CommandKind.Context => await ContextAsync(invocation.Arguments),
            CommandKind.Capture => await CaptureAsync(invocation.Arguments),
            CommandKind.SendKeys => await SendKeysAsync(invocation.Arguments),
            CommandKind.Kill => await KillAsync(invocation.Arguments),
            CommandKind.Setup => await SetupAsync(invocation.Arguments),
            _ => Unknown((invocation.Command ?? "").ToLowerInvariant())
        };
    }

    internal enum CommandKind { Start, Help, Version, Skill, Stop, Status, Context, Capture, SendKeys, Kill, Setup, Unknown }

    internal sealed record Invocation(CommandKind Kind, string? Command, string[] Arguments, string? ShellPath);

    internal static Invocation Parse(string[] args)
    {
        if (args.Length == 0)
            return new Invocation(CommandKind.Start, null, [], null);

        var command = args[0];
        if (args.Length == 1)
        {
            if (command is "-h" or "--help" or "help")
                return new Invocation(CommandKind.Help, command, [], null);
            if (command == "--version") return new Invocation(CommandKind.Version, command, [], null);
            if (command == "--skill") return new Invocation(CommandKind.Skill, command, [], null);
        }

        var kind = command.ToLowerInvariant() switch
        {
            "on" or "start" => CommandKind.Start,
            "off" or "stop" => CommandKind.Stop,
            "status" => CommandKind.Status,
            "context" => CommandKind.Context,
            "capture" => CommandKind.Capture,
            "send-keys" => CommandKind.SendKeys,
            "kill" => CommandKind.Kill,
            "setup" => CommandKind.Setup,
            _ => CommandKind.Unknown
        };
        var arguments = args.Skip(1).ToArray();
        var shellPath = kind == CommandKind.Start ? OptionValue(arguments, "--shell") : null;
        return new Invocation(kind, command, arguments, shellPath);
    }

    private static async Task<int> StartAsync(string? shellPath)
    {
        if (Environment.GetEnvironmentVariable("HITCH_SESSION") is { Length: > 0 } currentSession)
        {
            Console.WriteLine($"This managed shell is already shared as session {currentSession}.");
            return 0;
        }
        var shell = ManagedShell.Resolve(shellPath);
        var store = Store;
        var session = new SessionHost(store);
        return await session.RunAsync(shell.Executable, shell.Arguments);
    }

    private static async Task<int> StopCurrentAsync()
    {
        var pipe = Environment.GetEnvironmentVariable("HITCH_PIPE");
        if (string.IsNullOrWhiteSpace(pipe))
        {
            Console.WriteLine("This process is not inside a Hitch managed shell.");
            return 1;
        }
        var response = await PipeClient.SendAsync(pipe, new PipeRequest("stop"));
        if (!response.Ok)
        {
            Console.Error.WriteLine(response.Message ?? "Session stop failed.");
            return 1;
        }
        if (!string.IsNullOrWhiteSpace(response.Message))
            Console.WriteLine(response.Message);
        await Console.Out.FlushAsync();

        // Keep the child command from returning to PowerShell/CMD and painting another prompt
        // between the stop acknowledgement and the host shutting down the managed shell.
        if (int.TryParse(Environment.GetEnvironmentVariable("HITCH_HOST_PID"), out var hostId)
            && hostId != Environment.ProcessId)
        {
            try
            {
                using var host = System.Diagnostics.Process.GetProcessById(hostId);
                await host.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException
                                             or System.ComponentModel.Win32Exception or TimeoutException)
            {
                // The managed shell host normally terminates this process tree first.
            }
        }
        return 0;
    }

    private static async Task<int> StatusAsync(string[] args)
    {
        var id = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        if (id is null && Environment.GetEnvironmentVariable("HITCH_PIPE") is { Length: > 0 } currentPipe)
        {
            var currentId = Environment.GetEnvironmentVariable("HITCH_SESSION") ?? "current";
            return await PrintResponseAsync(await PipeClient.SendAsync(currentPipe, new PipeRequest("status")), currentId);
        }
        var record = ResolveSession(id);
        if (record is null)
        {
            PrintSessions();
            return 1;
        }
        return await PrintResponseAsync(await PipeClient.SendAsync(record.PipeName, new PipeRequest("status")), record.Id);
    }

    private static async Task<int> ContextAsync(string[] args)
    {
        var id = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        if (id is null)
        {
            var sessions = Store.ListLive();
            if (sessions.Count == 0)
            {
                Console.WriteLine("No shared sessions are running.");
                return 0;
            }
            foreach (var session in sessions)
            {
                var response = await PipeClient.SendAsync(session.PipeName, new PipeRequest("context", Tail: 20));
                if (response.Output is not null)
                    Console.WriteLine(response.Output);
                Console.WriteLine();
            }
            return 0;
        }
        var record = ResolveSession(id);
        if (record is null)
            return 1;
        return await PrintResponseAsync(await PipeClient.SendAsync(record.PipeName, new PipeRequest("context", Tail: 80)), record.Id);
    }

    private static async Task<int> CaptureAsync(string[] args)
    {
        var target = OptionValue(args, "--target") ?? OptionValue(args, "-t")
                     ?? args.FirstOrDefault(argument => !argument.StartsWith('-'));
        var record = ResolveSession(target);
        if (record is null)
            return 1;
        var tailText = OptionValue(args, "--tail");
        var tail = int.TryParse(tailText, out var count) ? Math.Clamp(count, 1, 1000) : 80;
        var response = await PipeClient.SendAsync(record.PipeName, new PipeRequest("capture", Tail: tail));
        if (response.Output is not null)
            Console.WriteLine(response.Output);
        if (!response.Ok)
            Console.Error.WriteLine(response.Message ?? "Capture failed.");
        return response.Ok ? 0 : 1;
    }

    private static async Task<int> SendKeysAsync(string[] args)
    {
        var target = OptionValue(args, "--target") ?? OptionValue(args, "-t");
        if (target is null)
        {
            Console.Error.WriteLine("Usage: hitch send-keys -t <session-id> [--force] <text-or-keys...>");
            return 2;
        }
        var force = args.Contains("--force", StringComparer.OrdinalIgnoreCase);
        var keys = CollectTrailingValues(args, "--target", "-t", "--force", "--tail");
        var record = ResolveSession(target);
        if (record is null)
            return 1;
        var response = await PipeClient.SendAsync(record.PipeName, new PipeRequest("send-keys", keys, force));
        if (response.Message is not null)
            Console.WriteLine(response.Message);
        if (response.Ok)
        {
            var tail = await PipeClient.SendAsync(record.PipeName, new PipeRequest("capture", Tail: 20));
            if (!string.IsNullOrWhiteSpace(tail.Output))
                Console.WriteLine(tail.Output);
        }
        return response.Ok ? 0 : 1;
    }

    private static async Task<int> KillAsync(string[] args)
    {
        var id = args.FirstOrDefault(argument => !argument.StartsWith('-'));
        var record = ResolveSession(id);
        if (record is null)
            return 1;
        return await PrintResponseAsync(await PipeClient.SendAsync(record.PipeName, new PipeRequest("stop")), record.Id);
    }

    private static async Task<int> SetupAsync(string[] args)
    {
        if (args.FirstOrDefault()?.Equals("skill", StringComparison.OrdinalIgnoreCase) == true)
        {
            Console.WriteLine("Install the Hitch agent skill with `npx skills add kbohara315/hitch-cli-win --skill hitch`, or run `hitch --skill` to print its instructions.");
            return 0;
        }
        var nonInteractive = args.Any(argument => argument.Equals("--yes", StringComparison.OrdinalIgnoreCase)
            || argument.Equals("-y", StringComparison.OrdinalIgnoreCase));
        var installer = new ShellIntegrationInstaller();
        if (nonInteractive)
            await installer.InstallNonInteractiveAsync();
        else
            await installer.InstallAsync();
        return 0;
    }

    private static SessionRecord? ResolveSession(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            var sessions = Store.ListLive();
            if (sessions.Count == 1)
                return sessions[0];
            if (sessions.Count > 1)
                Console.Error.WriteLine("More than one shared session is live; specify its session ID.");
            else
                Console.Error.WriteLine("No shared session is running.");
            return null;
        }

        var record = Store.Find(id);
        if (record is null)
            Console.Error.WriteLine($"No live session matches '{id}'.");
        return record;
    }

    private static void PrintSessions()
    {
        var sessions = Store.ListLive();
        if (sessions.Count == 0)
        {
            Console.WriteLine("No shared sessions are running.");
            return;
        }
        Console.WriteLine("SESSION ID                         SHELL          DIRECTORY");
        foreach (var session in sessions)
            Console.WriteLine($"{session.Id,-34} {Path.GetFileName(session.Shell),-14} {session.WorkingDirectory}");
    }

    private static async Task<int> PrintResponseAsync(PipeResponse response, string? id = null)
    {
        if (!response.Ok)
        {
            Console.Error.WriteLine(response.Message ?? "Session command failed.");
            return 1;
        }
        if (!string.IsNullOrWhiteSpace(response.Output))
            Console.WriteLine(response.Output);
        else if (!string.IsNullOrWhiteSpace(response.Message))
            Console.WriteLine(response.Message);
        else if (id is not null)
            Console.WriteLine($"session: {id}{Environment.NewLine}cwd: {response.WorkingDirectory}{Environment.NewLine}command running: {response.CommandRunning}");
        await Task.CompletedTask;
        return 0;
    }

    private static string? OptionValue(string[] args, string option)
    {
        for (var index = 0; index < args.Length - 1; index++)
            if (args[index].Equals(option, StringComparison.OrdinalIgnoreCase))
                return args[index + 1];
        return null;
    }

    private static string[] CollectTrailingValues(string[] args, params string[] options)
    {
        var values = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (options.Contains(args[index], StringComparer.OrdinalIgnoreCase))
            {
                if (args[index] is "--target" or "-t" or "--tail")
                    index++;
                continue;
            }
            values.Add(args[index]);
        }
        return values.ToArray();
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'. Run `hitch --help` for usage.");
        return 2;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Hitch — share managed shell sessions with coding agents on Windows and Linux.");
        Console.WriteLine();
        Console.WriteLine("Usage: hitch [COMMAND]");
        Console.WriteLine("  hitch [on] [--shell PATH]       Start and attach a managed shell");
        Console.WriteLine("  hitch off                       Stop the current shared session");
        Console.WriteLine("  hitch status [SESSION-ID]       Show session state (list if ambiguous)");
        Console.WriteLine("  hitch context [SESSION-ID]      Show current directory, process state, and recent output");
        Console.WriteLine("  hitch capture -t SESSION-ID     Print recent transcript output");
        Console.WriteLine("  hitch send-keys -t ID [--force] Send text or keys to a managed shell");
        Console.WriteLine("  hitch kill SESSION-ID           End a managed shell session");
        Console.WriteLine("  hitch setup [--yes]             Install shell integration (PowerShell/CMD on Windows, Bash on Linux)");
        Console.WriteLine("  hitch --skill                   Print built-in agent instructions");
    }

    internal static string GetAgentInstructions()
    {
        using var stream = typeof(Program).Assembly.GetManifestResourceStream("Hitch.AgentSkills.hitch.SKILL.md")
            ?? throw new InvalidOperationException("The embedded Hitch agent skill could not be found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
