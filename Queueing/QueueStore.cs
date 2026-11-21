using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace WinSystemHelper;

/// <summary>One dedicated SQLite owner; files are streamed, never stored as database blobs.</summary>
public sealed partial class QueueStore : IDisposable
{
    private readonly Channel<Action<SqliteConnection>> _work = Channel.CreateBounded<Action<SqliteConnection>>(
        new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<int> _limit;
    private long _bytes;
    private bool _paused;
    private bool _disposed;
    private long _connectionGeneration;
    private long _nextPruneAt;
    public long ConnectionGeneration => Interlocked.Read(ref _connectionGeneration);
    public string Root { get; }
    public QueueSignal TextChanged { get; } = new();
    public QueueSignal MediaChanged { get; } = new();
    public QueueSignal CommandsChanged { get; } = new();
    public QueueSignal ControlChanged { get; } = new();
    public QueueSignal SpaceChanged { get; } = new();
    public long Limit => checked((long)Math.Clamp(_limit(), 1, 65536) * 1024 * 1024);
    public bool Paused => Volatile.Read(ref _paused);
    public static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public QueueStore(AppConfiguration configuration)
        : this(DefaultRoot(configuration), () => configuration.OutboxMaxSizeMB)
    {
    }

    public QueueStore(string root, Func<int> limit)
    {
        Root = Path.GetFullPath(root);
        _limit = limit;
        Directory.CreateDirectory(Root);
        SecureDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, "media"));
        Directory.CreateDirectory(Path.Combine(Root, "capture"));
        _thread = new Thread(Run) { IsBackground = true, Name = "WinSystemHelper queue storage" };
        _thread.Start();
    }

    private static string DefaultRoot(AppConfiguration configuration)
    {
        // Telegram token rotation keeps the same bot id; a different bot gets a different store.
        string botId = configuration.BotToken.Split(':')[0];
        string scope = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(botId)))[..24];
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "WinSystemHelper", "Queue", scope);
    }

    public static void SecureDirectory(string path, string? captureUserSid = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        // A folder pre-created by another account stays under that account's control
        // unless ownership moves too. Only an elevated process is allowed to assign it.
        using (WindowsIdentity current = WindowsIdentity.GetCurrent())
        {
            if (current.IsSystem || new WindowsPrincipal(current).IsInRole(WindowsBuiltInRole.Administrator))
            {
                acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
            }
        }

        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        SecurityIdentifier[] trustedSids =
        [
            new(WellKnownSidType.LocalSystemSid, null),
            new(WellKnownSidType.BuiltinAdministratorsSid, null),
            WindowsIdentity.GetCurrent().User!
        ];
        foreach (SecurityIdentifier sid in trustedSids)
        {
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        }

        if (captureUserSid is not null)
        {
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(captureUserSid), FileSystemRights.Modify,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        }

        new DirectoryInfo(path).SetAccessControl(acl);
    }

    private void Run()
    {
        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(Root, "queue.db"),
                Pooling = false
            };
            using var db = new SqliteConnection(connectionString.ToString());
            db.Open();
            Exec(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA cache_size=-2048;");
            long version = Scalar(db, "PRAGMA user_version;");
            if (version > 1)
            {
                throw new InvalidOperationException("Queue database belongs to a newer application. Preserving it.");
            }

            Exec(db, """
                CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value INTEGER NOT NULL);
                INSERT OR IGNORE INTO meta VALUES('offset',0);
                INSERT OR IGNORE INTO meta VALUES('paused',0);
                CREATE TABLE IF NOT EXISTS outbox(
                  id TEXT PRIMARY KEY, chat INTEGER NOT NULL,kind TEXT NOT NULL,text TEXT NOT NULL,
                  path TEXT,bytes INTEGER NOT NULL DEFAULT 0,duration INTEGER NOT NULL DEFAULT 0,markup TEXT,
                  state TEXT NOT NULL,attempts INTEGER NOT NULL DEFAULT 0,created INTEGER NOT NULL,due INTEGER NOT NULL,
                  expires INTEGER,error TEXT,dedupe TEXT UNIQUE,priority INTEGER NOT NULL DEFAULT 0);
                CREATE INDEX IF NOT EXISTS outbox_due ON outbox(kind,state,due,created);
                CREATE INDEX IF NOT EXISTS outbox_cleanup ON outbox(state,created);
                CREATE TABLE IF NOT EXISTS commands(
                  id INTEGER PRIMARY KEY,chat INTEGER NOT NULL,payload TEXT NOT NULL,readonly INTEGER NOT NULL,
                  priority INTEGER NOT NULL,created INTEGER NOT NULL,state TEXT NOT NULL,finished INTEGER);
                CREATE INDEX IF NOT EXISTS commands_ready ON commands(priority,state,id);
                PRAGMA user_version=1;
                UPDATE outbox SET state=CASE WHEN error=$progressInFlight THEN 'deleted' ELSE 'ready' END,
                  error=CASE WHEN error=$progressInFlight THEN NULL ELSE error END
                  WHERE state='sending';
                UPDATE outbox SET state='deleted' WHERE kind='text' AND expires IS NOT NULL;
                UPDATE commands SET state=CASE WHEN readonly=1 THEN 'pending' ELSE 'unknown' END
                  WHERE state='running';
                """, ("$progressInFlight", ProgressSendInFlight));
            _bytes = Scalar(db, "SELECT COALESCE(SUM(bytes),0) FROM outbox WHERE path IS NOT NULL;");
            _paused = Scalar(db, "SELECT value FROM meta WHERE key='paused'") != 0;
            RecoverCaptures(db);
            _started.TrySetResult();
            while (_work.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (_work.Reader.TryRead(out var action))
                {
                    action(db);
                }
            }
        }
        catch (Exception ex)
        {
            _started.TrySetException(ex);
            _work.Writer.TryComplete(ex);
        }
    }

    private async Task<T> Call<T>(Func<SqliteConnection, T> work)
    {
        await _started.Task.ConfigureAwait(false);
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _work.Writer.WriteAsync(db =>
        {
            try
            {
                result.TrySetResult(work(db));
            }
            catch (Exception ex)
            {
                result.TrySetException(ex);
            }
        }).ConfigureAwait(false);
        return await result.Task.ConfigureAwait(false);
    }

    internal static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
        return cmd;
    }
    internal static int Exec(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(db, sql, args);
        return cmd.ExecuteNonQuery();
    }

    internal static long Scalar(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        using var cmd = Command(db, sql, args);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void ConfigurationChanged() => SpaceChanged.Pulse();

    public async Task ConnectedAsync()
    {
        // Do not reset rate-limit Retry-After deadlines on ordinary polling successes.
        await Call(db => Exec(db, "UPDATE outbox SET due=$now WHERE state='ready' AND error='network'", ("$now", Now)));
        Interlocked.Increment(ref _connectionGeneration);
        TextChanged.Pulse();
        MediaChanged.Pulse();
    }
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _work.Writer.TryComplete();
        _thread.Join(TimeSpan.FromSeconds(10));
        TextChanged.Dispose();
        MediaChanged.Dispose();
        CommandsChanged.Dispose();
        ControlChanged.Dispose();
        SpaceChanged.Dispose();
    }
}
