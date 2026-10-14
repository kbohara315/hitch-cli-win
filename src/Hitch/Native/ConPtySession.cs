using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hitch.Native;

public interface IManagedTerminalSession : IAsyncDisposable
{
    int ProcessId { get; }
    bool HasExited { get; }
    Task<int> WaitForExitAsync(CancellationToken cancellationToken = default);
    Task WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default);
    Task PumpOutputAsync(Func<ReadOnlyMemory<byte>, Task> onOutput, CancellationToken cancellationToken = default);
    void Resize(short columns, short rows);
    void KillTree();
    void ClosePseudoConsole();
}

public sealed class ConPtySession : IManagedTerminalSession
{
    private IntPtr _pseudoConsole;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Process _process;
    private readonly IntPtr _processHandle;
    private bool _disposed;

    private ConPtySession(IntPtr pseudoConsole, FileStream input, FileStream output, Process process, IntPtr processHandle)
    {
        _pseudoConsole = pseudoConsole;
        _input = input;
        _output = output;
        _process = process;
        _processHandle = processHandle;
    }

    public int ProcessId => _process.Id;
    public bool HasExited => _process.HasExited;

    public static ConPtySession Start(
        string shell,
        string arguments,
        string workingDirectory,
        short columns,
        short rows,
        bool redirectStandardOutput = true)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("ConPTY is available only on Windows.");
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
            throw new PlatformNotSupportedException("Hitch requires Windows 10 version 1809 or later (ConPTY).");

        IntPtr inputRead = IntPtr.Zero;
        IntPtr inputWrite = IntPtr.Zero;
        IntPtr outputRead = IntPtr.Zero;
        IntPtr outputWrite = IntPtr.Zero;
        IntPtr pseudoConsole = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr inheritedHandles = IntPtr.Zero;
        IntPtr startupInformation = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;

        try
        {
            if (!NativeMethods.CreatePipe(out inputRead, out inputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create ConPTY input pipe.");
            if (!NativeMethods.CreatePipe(out outputRead, out outputWrite, IntPtr.Zero, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create ConPTY output pipe.");

            var size = new Coord(columns, rows);
            var result = NativeMethods.CreatePseudoConsole(size, inputRead, outputWrite, 0, out pseudoConsole);
            if (result != 0)
                Marshal.ThrowExceptionForHR(result);

            var attributeListSize = IntPtr.Zero;
            var attributeCount = redirectStandardOutput ? 2 : 1;
            _ = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, attributeCount, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, attributeCount, 0, ref attributeListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize process attributes.");

            if (!NativeMethods.UpdateProcThreadAttribute(
                    attributeList, 0, new IntPtr(NativeMethods.ProcThreadAttributePseudoConsole),
                    pseudoConsole, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not associate the child process with ConPTY.");

            if (redirectStandardOutput)
            {
                // Limit inheritance to ConPTY's pipe ends; raw output handles are used as stdio.
                inheritedHandles = Marshal.AllocHGlobal(2 * IntPtr.Size);
                Marshal.WriteIntPtr(inheritedHandles, 0, inputRead);
                Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, outputWrite);
                if (!NativeMethods.UpdateProcThreadAttribute(
                        attributeList, 0, new IntPtr(NativeMethods.ProcThreadAttributeHandleList),
                        inheritedHandles, new IntPtr(2 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict inherited ConPTY handles.");

                if (!NativeMethods.SetHandleInformation(inputRead, NativeMethods.HandleFlagInherit, NativeMethods.HandleFlagInherit)
                    || !NativeMethods.SetHandleInformation(outputWrite, NativeMethods.HandleFlagInherit, NativeMethods.HandleFlagInherit))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not prepare ConPTY standard handles.");
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = redirectStandardOutput ? NativeMethods.StartfUseStdHandles : 0,
                    StandardOutput = redirectStandardOutput ? outputWrite : IntPtr.Zero,
                    StandardError = redirectStandardOutput ? outputWrite : IntPtr.Zero
                },
                AttributeList = attributeList
            };
            startupInformation = Marshal.AllocHGlobal(Marshal.SizeOf<StartupInfoEx>());
            Marshal.StructureToPtr(startup, startupInformation, fDeleteOld: false);
            var commandLine = $"\"{shell}\" {arguments}".TrimEnd();
            var securityAttributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>() };
            const uint extendedStartupInfo = 0x00080000;
            // Leave standard input unset: ConPTY routes interactive console input through HPCON.
            if (!NativeMethods.CreateProcess(
                    null, commandLine, ref securityAttributes, ref securityAttributes, redirectStandardOutput, extendedStartupInfo,
                    IntPtr.Zero, workingDirectory, startupInformation, out var processInfo))
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start managed shell '{shell}'.");
            Marshal.FreeHGlobal(startupInformation);
            startupInformation = IntPtr.Zero;
            Marshal.FreeHGlobal(inheritedHandles);
            inheritedHandles = IntPtr.Zero;

            processHandle = processInfo.Process;
            threadHandle = processInfo.Thread;
            NativeMethods.CloseHandle(inputRead);
            inputRead = IntPtr.Zero;
            NativeMethods.CloseHandle(outputWrite);
            outputWrite = IntPtr.Zero;
            var process = Process.GetProcessById(unchecked((int)processInfo.ProcessId));
            NativeMethods.CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;

            var inputStream = new FileStream(new SafeFileHandle(inputWrite, ownsHandle: true), FileAccess.Write, 4096, isAsync: false);
            inputWrite = IntPtr.Zero;
            var outputStream = new FileStream(new SafeFileHandle(outputRead, ownsHandle: true), FileAccess.Read, 4096, isAsync: false);
            outputRead = IntPtr.Zero;

            var session = new ConPtySession(pseudoConsole, inputStream, outputStream, process, processHandle);
            pseudoConsole = IntPtr.Zero;
            processHandle = IntPtr.Zero;
            return session;
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (startupInformation != IntPtr.Zero)
                Marshal.FreeHGlobal(startupInformation);
            if (inheritedHandles != IntPtr.Zero)
                Marshal.FreeHGlobal(inheritedHandles);
            CloseIfSet(inputRead);
            CloseIfSet(inputWrite);
            CloseIfSet(outputRead);
            CloseIfSet(outputWrite);
            CloseIfSet(threadHandle);
            CloseIfSet(processHandle);
            if (pseudoConsole != IntPtr.Zero)
                NativeMethods.ClosePseudoConsole(pseudoConsole);
        }
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken = default) => WaitCoreAsync(cancellationToken);

    private async Task<int> WaitCoreAsync(CancellationToken cancellationToken)
    {
        await _process.WaitForExitAsync(cancellationToken);
        if (!NativeMethods.GetExitCodeProcess(_processHandle, out var exitCode))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not read the managed shell exit code.");
        return unchecked((int)exitCode);
    }

    public async Task WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken = default)
    {
        await _input.WriteAsync(bytes, cancellationToken);
        await _input.FlushAsync(cancellationToken);
    }

    public async Task PumpOutputAsync(Func<ReadOnlyMemory<byte>, Task> onOutput, CancellationToken cancellationToken = default)
    {
        var buffer = new byte[4096];
        while (true)
        {
            int read;
            try
            {
                read = await _output.ReadAsync(buffer, cancellationToken);
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            if (read == 0)
                return;
            await onOutput(buffer.AsMemory(0, read));
        }
    }

    public void Resize(short columns, short rows)
    {
        if (_disposed || _pseudoConsole == IntPtr.Zero)
            return;
        var result = NativeMethods.ResizePseudoConsole(_pseudoConsole, new Coord(columns, rows));
        if (result != 0)
            Marshal.ThrowExceptionForHR(result);
    }

    public void KillTree()
    {
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void ClosePseudoConsole()
    {
        if (_pseudoConsole == IntPtr.Zero)
            return;
        NativeMethods.ClosePseudoConsole(_pseudoConsole);
        _pseudoConsole = IntPtr.Zero;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;
        ClosePseudoConsole();
        await _input.DisposeAsync();
        await _output.DisposeAsync();
        _process.Dispose();
        if (_processHandle != IntPtr.Zero)
            NativeMethods.CloseHandle(_processHandle);
    }

    private static void CloseIfSet(IntPtr handle)
    {
        if (handle != IntPtr.Zero && handle != new IntPtr(-1))
            NativeMethods.CloseHandle(handle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Cb;
        public IntPtr Reserved;
        public IntPtr Desktop;
        public IntPtr Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public IntPtr Reserved2Pointer;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    private static class NativeMethods
    {
        public const long ProcThreadAttributePseudoConsole = 0x00020016;
        public const long ProcThreadAttributeHandleList = 0x00020002;
        public const uint HandleFlagInherit = 0x00000001;
        public const int StartfUseStdHandles = 0x00000100;

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreatePipe(out IntPtr readPipe, out IntPtr writePipe, IntPtr attributes, int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int CreatePseudoConsole(Coord size, IntPtr input, IntPtr output, uint flags, out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        public static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern int ResizePseudoConsole(IntPtr pseudoConsole, Coord size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        public static extern void DeleteProcThreadAttributeList(IntPtr list);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CreateProcess(string? applicationName, string commandLine, ref SecurityAttributes processAttributes, ref SecurityAttributes threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory, IntPtr startupInfo, out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    }
}
