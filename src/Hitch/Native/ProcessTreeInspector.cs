using System.Runtime.InteropServices;

namespace Hitch.Native;

public static class ProcessTreeInspector
{
    private const uint SnapshotProcesses = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    public static int? GetParentProcessId(int processId)
    {
        if (OperatingSystem.IsLinux())
            return GetLinuxParentProcessId(processId);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process inspection supports Windows and Linux only.");
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle)
            return null;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return null;
            do
            {
                if (entry.ProcessId == (uint)processId)
                    return unchecked((int)entry.ParentProcessId);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            return null;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    public static IReadOnlyList<string> GetDescendantNames(int rootProcessId)
    {
        if (OperatingSystem.IsLinux())
            return GetLinuxDescendantNames(rootProcessId);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Process inspection supports Windows and Linux only.");
        var snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
        if (snapshot == InvalidHandle)
            return [];

        try
        {
            var entries = new List<(int Pid, int ParentPid, string Name)>();
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry))
                return [];
            do
            {
                entries.Add((unchecked((int)entry.ProcessId), unchecked((int)entry.ParentProcessId), entry.ExecutableFile));
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));

            var descendants = new HashSet<int> { rootProcessId };
            var names = new List<string>();
            bool changed;
            do
            {
                changed = false;
                foreach (var item in entries)
                {
                    if (!descendants.Contains(item.ParentPid) || !descendants.Add(item.Pid))
                        continue;
                    changed = true;
                    if (item.Pid != rootProcessId)
                        names.Add(Path.GetFileNameWithoutExtension(item.Name));
                }
            } while (changed);
            return names;
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }
    }

    private static int? GetLinuxParentProcessId(int processId)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{processId}/stat");
            var close = stat.LastIndexOf(')');
            var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return fields.Length > 1 && int.TryParse(fields[1], out var parent) ? parent : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IReadOnlyList<string> GetLinuxDescendantNames(int rootProcessId)
    {
        try
        {
            var processes = new List<(int Pid, int Parent, string Name)>();
            foreach (var directory in Directory.EnumerateDirectories("/proc"))
            {
                if (!int.TryParse(Path.GetFileName(directory), out var pid)) continue;
                try
                {
                    var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                    var close = stat.LastIndexOf(')');
                    if (close < 0) continue;
                    var fields = stat[(close + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length < 2 || !int.TryParse(fields[1], out var parent)) continue;
                    var name = File.ReadAllText(Path.Combine(directory, "comm")).Trim();
                    processes.Add((pid, parent, name));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            var descendants = new HashSet<int> { rootProcessId };
            var names = new List<string>();
            bool changed;
            do
            {
                changed = false;
                foreach (var process in processes)
                {
                    if (descendants.Contains(process.Pid) || !descendants.Contains(process.Parent)) continue;
                    descendants.Add(process.Pid);
                    names.Add(process.Name);
                    changed = true;
                }
            } while (changed);
            return names;
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
