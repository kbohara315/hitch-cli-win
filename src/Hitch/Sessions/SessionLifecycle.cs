namespace Hitch.Sessions;

/// <summary>Coordinates the managed shell's competing stop signals and ordered shutdown.</summary>
internal static class SessionLifecycle
{
    public static async Task<int> RunAsync(
        Task<int> exitTask,
        Task inputTask,
        Task stopTask,
        Action killTree,
        Action closePseudoConsole,
        Task outputTask,
        Action cancelBackgroundWork,
        Task[] backgroundTasks,
        Func<ValueTask> disposeShell)
    {
        var completed = await Task.WhenAny(exitTask, inputTask, stopTask);
        var exitCode = 0;
        try
        {
            if (completed == exitTask)
                exitCode = await exitTask;
            killTree();

            await IgnoreShutdownFailureAsync(exitTask, TimeSpan.FromSeconds(2),
                ex => ex is OperationCanceledException or TimeoutException or InvalidOperationException);
            closePseudoConsole();
            await IgnoreShutdownFailureAsync(outputTask, TimeSpan.FromSeconds(2),
                ex => ex is OperationCanceledException or TimeoutException or IOException);
        }
        finally
        {
            cancelBackgroundWork();
            // Console input can remain pending on synchronous handles; it is intentionally not awaited.
            await IgnoreShutdownFailureAsync(Task.WhenAll(backgroundTasks), TimeSpan.FromSeconds(2),
                ex => ex is OperationCanceledException or TimeoutException or IOException);
            await disposeShell();
        }

        return exitCode;
    }

    private static async Task IgnoreShutdownFailureAsync(Task task, TimeSpan timeout, Func<Exception, bool> expected)
    {
        try { await task.WaitAsync(timeout, CancellationToken.None); }
        catch (Exception exception) when (expected(exception)) { }
    }
}
