using System.Runtime.InteropServices;

namespace Hitch.Native;

public sealed class ConsoleModeScope : IDisposable
{
    private readonly IntPtr _input;
    private readonly IntPtr _output;
    private readonly uint _originalInputMode;
    private readonly uint _originalOutputMode;
    private readonly bool _restoreInput;
    private readonly bool _restoreOutput;
    private bool _disposed;

    private ConsoleModeScope()
    {
        _input = GetStdHandle(StdInputHandle);
        _output = GetStdHandle(StdOutputHandle);
        if (GetConsoleMode(_input, out _originalInputMode))
        {
            var mode = (_originalInputMode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput))
                       | EnableExtendedFlags | EnableVirtualTerminalInput;
            _restoreInput = SetConsoleMode(_input, mode);
        }
        if (GetConsoleMode(_output, out _originalOutputMode))
            _restoreOutput = SetConsoleMode(_output, GetVirtualTerminalOutputMode(_originalOutputMode));
    }

    public static IDisposable Enter() => OperatingSystem.IsWindows()
        ? new ConsoleModeScope()
        : OperatingSystem.IsLinux() ? LinuxTerminalModeScope.Enter()
        : throw new PlatformNotSupportedException("Hitch terminal sessions support Windows and Linux only.");

    public static uint GetVirtualTerminalOutputMode(uint originalMode) =>
        originalMode | EnableProcessedOutput | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn;

    public static (short Columns, short Rows) GetSize()
    {
        if (OperatingSystem.IsLinux())
            return LinuxTerminalModeScope.GetSize();
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Hitch terminal sessions support Windows and Linux only.");
        var output = GetStdHandle(StdOutputHandle);
        if (GetConsoleScreenBufferInfo(output, out var info))
            return ((short)Math.Max(1, info.Window.Right - info.Window.Left + 1),
                (short)Math.Max(1, info.Window.Bottom - info.Window.Top + 1));
        return (120, 30);
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_restoreInput)
            _ = SetConsoleMode(_input, _originalInputMode);
        if (_restoreOutput)
            _ = SetConsoleMode(_output, _originalOutputMode);
    }

    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const uint EnableProcessedInput = 0x0001;
    private const uint EnableLineInput = 0x0002;
    private const uint EnableEchoInput = 0x0004;
    private const uint EnableExtendedFlags = 0x0080;
    private const uint EnableProcessedOutput = 0x0001;
    private const uint EnableVirtualTerminalInput = 0x0200;
    private const uint EnableVirtualTerminalProcessing = 0x0004;
    private const uint DisableNewlineAutoReturn = 0x0008;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetConsoleMode(IntPtr handle, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleScreenBufferInfo(IntPtr handle, out ConsoleBufferInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ConsoleBufferInfo
    {
        public Coord Size;
        public Coord CursorPosition;
        public ushort Attributes;
        public SmallRect Window;
        public Coord MaximumWindowSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SmallRect
    {
        public short Left;
        public short Top;
        public short Right;
        public short Bottom;
    }
}
