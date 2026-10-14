using System.Diagnostics;
using System.Text.Json;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace Hitch.Sessions;

public sealed class SessionStore
{
    private static readonly UnixFileMode PrivateDirectoryMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private static readonly UnixFileMode PrivateFileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private static readonly ConcurrentDictionary<string, object> LinuxIdLocks = new(StringComparer.Ordinal);
    internal static readonly TimeSpan IncompleteRecordGracePeriod = TimeSpan.FromSeconds(5);
    private readonly ConcurrentDictionary<string, object> _stateLocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _root;

    public SessionStore(string? root = null)
    {
        _root = root ?? GetDefaultRoot();
    }

    public string Root => _root;

    private static string GetDefaultRoot()
    {
        if (OperatingSystem.IsLinux())
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrWhiteSpace(runtime))
                return Path.Combine(runtime, "hitch", "sessions");

            var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
            var home = Environment.GetEnvironmentVariable("HOME");
            var root = !string.IsNullOrWhiteSpace(stateHome) ? stateHome
                : !string.IsNullOrWhiteSpace(home) ? Path.Combine(home, ".local", "state")
                : throw new InvalidOperationException("HOME or XDG_STATE_HOME must be set to store Hitch sessions securely.");
            return Path.Combine(root, "hitch", "sessions");
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hitch", "sessions");
    }

    public string SessionDirectory(string id) => Path.Combine(_root, id);

    public string RecordPath(string id) => Path.Combine(SessionDirectory(id), "session.json");

    public string StatePath(string id) => Path.Combine(SessionDirectory(id), "state.json");

    public string OutputPath(string id) => Path.Combine(SessionDirectory(id), "output.log");

    public string ActiveOutputPath(string id) => Path.Combine(SessionDirectory(id), "active-output.log");

    public void Save(SessionRecord record)
    {
        EnsurePrivateDirectory(SessionDirectory(record.Id));
        WriteJsonAtomically(RecordPath(record.Id), record);
        SaveState(record.Id, new SessionState(false, null, record.WorkingDirectory, DateTimeOffset.UtcNow));
    }

    public void SaveState(string id, SessionState state)
    {
        EnsurePrivateDirectory(SessionDirectory(id));
        lock (_stateLocks.GetOrAdd(id, static _ => new object()))
            WriteJsonAtomically(StatePath(id), state);
    }

    public SessionState ReadState(string id)
    {
        lock (_stateLocks.GetOrAdd(id, static _ => new object()))
        {
            try
            {
                return JsonSerializer.Deserialize(File.ReadAllText(StatePath(id)), SessionStoreJsonContext.Default.SessionState)
                    ?? new SessionState(false, null, string.Empty, DateTimeOffset.UtcNow);
            }
            catch (IOException)
            {
                return new SessionState(false, null, string.Empty, DateTimeOffset.UtcNow);
            }
            catch (JsonException)
            {
                return new SessionState(false, null, string.Empty, DateTimeOffset.UtcNow);
            }
        }
    }

    public IReadOnlyList<SessionRecord> ListLive()
    {
        if (OperatingSystem.IsLinux()) EnsurePrivateDirectory(_root);
        else Directory.CreateDirectory(_root);
        var live = new List<SessionRecord>();
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            var path = Path.Combine(directory, "session.json");
            if (!File.Exists(path))
            {
                if (IsPastStartupGracePeriod(Directory.GetLastWriteTimeUtc(directory), DateTime.UtcNow))
                    TryDeleteDirectory(directory);
                continue;
            }
            SessionRecord? record;
            try
            {
                record = JsonSerializer.Deserialize(File.ReadAllText(path), SessionStoreJsonContext.Default.SessionRecord);
            }
            catch (IOException)
            {
                continue;
            }
            catch (JsonException)
            {
                // A directory without a readable record may be an interrupted startup.
                // Preserve the grace period so another host can finish initialization.
                if (IsPastStartupGracePeriod(File.GetLastWriteTimeUtc(path), DateTime.UtcNow))
                    TryDeleteDirectory(directory);
                continue;
            }

            if (record is not null && IsSameRunningProcess(record))
                live.Add(record);
            else
                TryDeleteDirectory(directory);
        }

        return live.OrderBy(record => record.StartedAt).ToArray();
    }

    public SessionRecord? Find(string id)
    {
        var sessions = ListLive();
        var exact = sessions.FirstOrDefault(record => record.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return exact;

        var matches = sessions.Where(record => record.Id.StartsWith(id, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    public void Delete(string id) => TryDeleteDirectory(SessionDirectory(id));

    public string AllocateId()
    {
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(_root))))[..16];
        if (OperatingSystem.IsLinux())
            return AllocateLinuxId(identity);
        using var mutex = new Mutex(false, $"Local\\Hitch.SessionIds.{identity}");
        var acquired = false;
        try
        {
            try
            {
                acquired = mutex.WaitOne(TimeSpan.FromSeconds(5));
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
                throw new IOException("Timed out allocating a shared session ID.");

            // Listing also prunes confirmed stale records before reusing their IDs.
            _ = ListLive();
            var id = 1;
            while (Directory.Exists(SessionDirectory(id.ToString())))
                id++;
            var sessionId = id.ToString();
            Directory.CreateDirectory(SessionDirectory(sessionId));
            return sessionId;
        }
        finally
        {
            if (acquired)
                mutex.ReleaseMutex();
        }
    }

    [SupportedOSPlatform("linux")]
    private string AllocateLinuxId(string identity)
    {
        EnsurePrivateDirectory(_root);
        var lockPath = Path.Combine(_root, $".ids-{identity}.lock");
        lock (LinuxIdLocks.GetOrAdd(lockPath, static _ => new object()))
        {
            using var file = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            File.SetUnixFileMode(lockPath, PrivateFileMode);
            var locked = false;
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while (!locked)
                {
                    try { file.Lock(0, 1); locked = true; }
                    catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
                }
                _ = ListLive();
                var id = 1;
                while (Directory.Exists(SessionDirectory(id.ToString())))
                    id++;
                var sessionId = id.ToString();
                EnsurePrivateDirectory(SessionDirectory(sessionId));
                return sessionId;
            }
            finally
            {
                if (locked)
                    file.Unlock(0, 1);
            }
        }
    }

    private static void EnsurePrivateDirectory(string path)
    {
        if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(path, PrivateDirectoryMode);
            File.SetUnixFileMode(path, PrivateDirectoryMode);
        }
        else
        {
            Directory.CreateDirectory(path);
        }
    }

    internal static bool IsPastStartupGracePeriod(DateTime lastWriteUtc, DateTime nowUtc) =>
        lastWriteUtc < nowUtc - IncompleteRecordGracePeriod;

    private static bool IsSameRunningProcess(SessionRecord record)
    {
        try
        {
            using var process = Process.GetProcessById(record.ProcessId);
            if (process.HasExited)
                return false;
            // PID alone is reusable; matching the recorded start time avoids treating a later
            // unrelated process as a live session after a host crash.
            return Math.Abs((process.StartTime.ToUniversalTime() - record.StartedAt.UtcDateTime).TotalSeconds) < 10;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static void WriteJsonAtomically<T>(string path, T value)
    {
        var temporary = path + ".tmp";
        System.Text.Json.Serialization.Metadata.JsonTypeInfo typeInfo = value switch
        {
            SessionRecord record => SessionStoreJsonContext.Default.SessionRecord,
            SessionState state => SessionStoreJsonContext.Default.SessionState,
            _ => throw new NotSupportedException($"Session JSON type {typeof(T)} is not registered.")
        };
        File.WriteAllText(temporary, JsonSerializer.Serialize(value, typeInfo));
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(temporary, PrivateFileMode);
        File.Move(temporary, path, overwrite: true);
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A live host can briefly hold a file while it is shutting down.
        }
        catch (UnauthorizedAccessException)
        {
            // Leave inaccessible stale data for a later cleanup attempt.
        }
    }
}
