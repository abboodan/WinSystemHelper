using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;

namespace WinSystemHelper;

/// <summary>One dedicated SQLite owner; files are streamed, never stored as database blobs.</summary>
public sealed class QueueStore : IDisposable
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

    public QueueStore(AppConfiguration configuration) : this(DefaultRoot(configuration), () => configuration.OutboxMaxSizeMB) { }

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
        if (!OperatingSystem.IsWindows()) return;
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var sid in new[] { new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), WindowsIdentity.GetCurrent().User! })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
        if (captureUserSid is not null)
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(captureUserSid), FileSystemRights.Modify,
                inheritance, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }

    private void Run()
    {
        try
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = Path.Combine(Root, "queue.db"), Pooling = false }.ToString());
            db.Open();
            Exec(db, "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000; PRAGMA cache_size=-2048;");
            long version = Scalar(db, "PRAGMA user_version;");
            if (version > 1) throw new InvalidOperationException("Queue database belongs to a newer application. Preserving it.");
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
                UPDATE outbox SET state='ready' WHERE state='sending';
                UPDATE outbox SET state='deleted' WHERE kind='text' AND expires IS NOT NULL;
                UPDATE commands SET state=CASE WHEN readonly=1 THEN 'pending' ELSE 'unknown' END
                  WHERE state='running';
                """);
            _bytes = Scalar(db, "SELECT COALESCE(SUM(bytes),0) FROM outbox WHERE path IS NOT NULL;");
            _paused = Scalar(db, "SELECT value FROM meta WHERE key='paused'") != 0;
            RecoverCaptures(db);
            _started.TrySetResult();
            while (_work.Reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                while (_work.Reader.TryRead(out var action)) action(db);
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
            try { result.TrySetResult(work(db)); }
            catch (Exception ex) { result.TrySetException(ex); }
        }).ConfigureAwait(false);
        return await result.Task.ConfigureAwait(false);
    }

    internal static SqliteCommand Command(SqliteConnection db, string sql, params (string, object?)[] args)
    {
        var cmd = db.CreateCommand(); cmd.CommandText = sql;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
    internal static int Exec(SqliteConnection db, string sql, params (string, object?)[] args)
    { using var cmd = Command(db, sql, args); return cmd.ExecuteNonQuery(); }
    internal static long Scalar(SqliteConnection db, string sql, params (string, object?)[] args)
    { using var cmd = Command(db, sql, args); return Convert.ToInt64(cmd.ExecuteScalar()); }
    private const string Columns = "id,chat,kind,text,path,bytes,duration,markup,state,attempts,created,due,expires,error";
    private static OutboxItem Read(SqliteDataReader r) => new(r.GetString(0), r.GetInt64(1), r.GetString(2),
        r.GetString(3), r.IsDBNull(4) ? null : r.GetString(4), r.GetInt64(5), r.GetInt32(6),
        r.IsDBNull(7) ? null : r.GetString(7), r.GetString(8), r.GetInt32(9), r.GetInt64(10), r.GetInt64(11),
        r.IsDBNull(12) ? null : r.GetInt64(12), r.IsDBNull(13) ? null : r.GetString(13));

    public async Task EnqueueTextAsync(long chat, string text, string? markup = null, string? dedupe = null,
        long? expires = null, bool priority = false)
    {
        await Call(db => InsertText(db, chat, text, markup, dedupe, expires, priority));
        TextChanged.Pulse();
    }

    public async Task EnqueueBroadcastAsync(IReadOnlyCollection<long> chats, string text, string key,
        long? expires = null, bool priority = false)
    {
        await Call(db =>
        {
            using var transaction = db.BeginTransaction();
            foreach (long chat in chats)
                for (int offset = 0, part = 0; offset < text.Length; part++)
                {
                    int length = Math.Min(4000, text.Length - offset);
                    if (char.IsHighSurrogate(text[offset + length - 1])) length--;
                    InsertText(db, chat, text.Substring(offset, length), null, $"{key}:{chat}:{part}", expires, priority);
                    offset += length;
                }
            transaction.Commit();
            return true;
        });
        TextChanged.Pulse();
    }

    private static bool InsertText(SqliteConnection db, long chat, string text, string? markup,
        string? dedupe, long? expires, bool priority)
    {
        if (dedupe is not null && Scalar(db, "SELECT COUNT(*) FROM outbox WHERE dedupe=$key", ("$key", dedupe)) > 0) return true;
        long count = Scalar(db, "SELECT COUNT(*) FROM outbox WHERE kind='text' AND state NOT IN ('sent','deleted')");
        if (count >= (priority ? 2000 : 1900)) throw new QueueCapacityException();
        if (text.Length > 4096) throw new ArgumentException("Telegram text must be split before storing.");
        Exec(db, """
            INSERT INTO outbox(id,chat,kind,text,markup,state,created,due,dedupe,expires,priority)
            VALUES($id,$chat,'text',$text,$markup,'ready',$now,$now,$key,$expires,$priority)
            """, ("$id", Guid.NewGuid().ToString("N")), ("$chat", chat), ("$text", text), ("$markup", markup),
            ("$now", Now), ("$key", dedupe), ("$expires", expires), ("$priority", priority ? 1 : 0));
        return true;
    }

    public async Task<CaptureReservation?> ReserveAsync(long chat, string kind, long bytes, int duration, string? userSid)
    {
        return await Call(db =>
        {
            if (bytes <= 0) throw new ArgumentOutOfRangeException(nameof(bytes));
            if (bytes > Limit) { SetPaused(db, true); return null; }
            if (_paused && _bytes < Limit * 0.8) SetPaused(db, false);
            if (_paused || _bytes + bytes > Limit)
            { SetPaused(db, true); return null; }
            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Root, "capture", id);
            Directory.CreateDirectory(directory);
            SecureDirectory(directory, userSid);
            string path = Path.Combine(directory, kind == "voice" ? "capture.wav" : "capture.jpg");
            Exec(db, """
                INSERT INTO outbox(id,chat,kind,text,path,bytes,duration,state,created,due)
                VALUES($id,$chat,$kind,'',$path,$bytes,$duration,'reserved',$now,$now)
                """, ("$id", id), ("$chat", chat), ("$kind", kind), ("$path", path), ("$bytes", bytes),
                ("$duration", duration), ("$now", Now));
            _bytes += bytes;
            return new CaptureReservation(id, path, bytes);
        });
    }

    private void SetPaused(SqliteConnection db, bool paused)
    {
        if (_paused == paused) return;
        Exec(db, "UPDATE meta SET value=$paused WHERE key='paused'", ("$paused", paused ? 1 : 0));
        _paused = paused;
    }

    public async Task CommitCaptureAsync(CaptureReservation reservation)
    {
        await Call(db => { Promote(db, reservation.Id); return true; });
        MediaChanged.Pulse(); SpaceChanged.Pulse();
    }

    private void Promote(SqliteConnection db, string id)
    {
        OutboxItem item;
        using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE id=$id", ("$id", id)))
        using (var r = cmd.ExecuteReader()) { if (!r.Read()) return; item = Read(r); }
        if (item.State != "reserved") return;
        string source = item.Path!;
        string final = Path.Combine(Root, "media", id + (item.Kind == "voice" ? ".wav" : ".jpg"));
        string existing = File.Exists(final) ? final : source;
        var file = new FileInfo(existing);
        if (!file.Exists || file.Length == 0 || file.Length > item.Bytes ||
            (!File.Exists(final) && !File.Exists(source + ".complete")))
            throw new IOException("Capture incomplete or exceeds its reserved capacity.");
        if (!File.Exists(final)) File.Move(source, final);
        if (OperatingSystem.IsWindows())
        {
            var acl = new FileSecurity(); acl.SetAccessRuleProtection(false, false);
            new FileInfo(final).SetAccessControl(acl);
        }
        long actual = new FileInfo(final).Length;
        Exec(db, "UPDATE outbox SET path=$path,bytes=$bytes,text=$text,state='ready' WHERE id=$id",
            ("$path", final), ("$bytes", actual), ("$id", id),
            ("$text", $"{item.Kind} | {id} | Captured {DateTimeOffset.FromUnixTimeMilliseconds(item.Created):yyyy-MM-dd HH:mm:ss zzz}"));
        _bytes += actual - item.Bytes;
        TryDeleteCaptureDirectory(source);
    }

    private void RecoverCaptures(SqliteConnection db)
    {
        // Startup only, paged, and never reads media bodies into memory.
        string last = "";
        while (true)
        {
            var items = new List<OutboxItem>();
            using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE state='reserved' AND id>$last ORDER BY id LIMIT 64", ("$last", last)))
            using (var r = cmd.ExecuteReader()) while (r.Read()) items.Add(Read(r));
            if (items.Count == 0) break;
            foreach (var item in items)
            {
                last = item.Id;
                try { Promote(db, item.Id); }
                catch (IOException) { Exec(db, "UPDATE outbox SET state='failed',error='Incomplete capture; review or delete' WHERE id=$id", ("$id", item.Id)); }
            }
        }
    }

    public Task FailCaptureAsync(string id) => Call(db => Exec(db,
        "UPDATE outbox SET state='failed',error='Capture did not complete' WHERE id=$id AND state='reserved'", ("$id", id)));

    public Task<OutboxItem?> ClaimAsync(bool media) => Call(db =>
    {
        Exec(db, "UPDATE outbox SET state='deleted' WHERE kind='text' AND state='ready' AND expires IS NOT NULL AND expires<$now", ("$now", Now));
        OutboxItem? item = null;
        using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE kind{(media ? "<>" : "=")}'text' AND state='ready' {(media ? "" : "AND due<=$now")} ORDER BY priority DESC,created,id LIMIT 1", ("$now", Now)))
        using (var r = cmd.ExecuteReader()) if (r.Read()) item = Read(r);
        if (item is not null && item.Due > Now) return null;
        if (item is not null) Exec(db, "UPDATE outbox SET state='sending',attempts=attempts+1 WHERE id=$id", ("$id", item.Id));
        return item;
    });

    public Task<TimeSpan> NextDelayAsync(bool media) => Call(db =>
    {
        using var cmd = Command(db, media
            ? "SELECT due FROM outbox WHERE kind<>'text' AND state='ready' ORDER BY created,id LIMIT 1"
            : "SELECT MIN(due) FROM outbox WHERE kind='text' AND state='ready'");
        object? value = cmd.ExecuteScalar();
        return value is null or DBNull ? TimeSpan.FromMinutes(5) : TimeSpan.FromMilliseconds(Math.Clamp(Convert.ToInt64(value) - Now, 20, 300000));
    });

    public async Task CompleteAsync(string id)
    {
        await Call(db => Exec(db, "UPDATE outbox SET state='sent',error=NULL WHERE id=$id", ("$id", id)));
        await CleanupAsync();
    }

    public async Task CleanupAsync()
    {
        bool released = await Call(db =>
        {
            long previousBytes = _bytes;
            var items = new List<OutboxItem>();
            using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE state IN ('sent','deleted') AND path IS NOT NULL LIMIT 64"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) items.Add(Read(r));
            foreach (var item in items)
            {
                try
                {
                    // Paths originate only in this store; never accept filesystem paths from bot commands.
                    if (item.Path!.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    { File.Delete(item.Path); TryDeleteCaptureDirectory(item.Path); }
                    else throw new IOException("Invalid stored path.");
                    Exec(db, "UPDATE outbox SET path=NULL,bytes=0 WHERE id=$id", ("$id", item.Id));
                    _bytes -= item.Bytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            if (Now >= _nextPruneAt)
            {
                _nextPruneAt = Now + 60000;
                Exec(db, """
                DELETE FROM outbox WHERE id IN (SELECT id FROM outbox WHERE state IN ('sent','deleted') AND path IS NULL AND created<$cutoff LIMIT 100);
                DELETE FROM commands WHERE id IN (SELECT id FROM commands WHERE state NOT IN ('running','pending','unknown') AND finished<$cutoff LIMIT 100);
                DELETE FROM outbox WHERE id IN (SELECT id FROM outbox WHERE state IN ('sent','deleted') AND path IS NULL ORDER BY created DESC LIMIT -1 OFFSET 10000);
                DELETE FROM commands WHERE id IN (SELECT id FROM commands WHERE state NOT IN ('running','pending') ORDER BY id DESC LIMIT -1 OFFSET 10000);
                """, ("$cutoff", Now - 86400000));
            }
            return previousBytes != _bytes;
        });
        if (released) SpaceChanged.Pulse();
    }

    private void TryDeleteCaptureDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !directory.StartsWith(Path.Combine(Root, "capture") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
        try { Directory.Delete(directory, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public Task RetryLaterAsync(string id, TimeSpan delay, string error, bool permanent) => Call(db => Exec(db,
        "UPDATE outbox SET state=$state,due=$due,error=$error WHERE id=$id AND state='sending'",
        ("$state", permanent ? "failed" : "ready"), ("$due", Now + (long)delay.TotalMilliseconds), ("$error", error), ("$id", id)));

    public async Task<bool> ManageAsync(string id, long chat, bool delete)
    {
        bool changed = await Call(db => Exec(db, delete
            ? "UPDATE outbox SET state='deleted' WHERE id=$id AND chat=$chat AND state IN ('failed','ready')"
            : "UPDATE outbox SET state='ready',due=$now,error=NULL WHERE id=$id AND chat=$chat AND state='failed' AND (path IS NULL OR path LIKE $media)",
            ("$id", id), ("$chat", chat), ("$now", Now), ("$media", Path.Combine(Root, "media") + "%")) > 0);
        await CleanupAsync(); TextChanged.Pulse(); MediaChanged.Pulse(); return changed;
    }

    public Task<QueueSnapshot> SnapshotAsync(long? chat = null) => Call(db =>
    {
        var problems = new List<OutboxItem>();
        using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE state='failed' AND ($chat IS NULL OR chat=$chat) ORDER BY created LIMIT 10", ("$chat", chat)))
        using (var r = cmd.ExecuteReader()) while (r.Read()) problems.Add(Read(r));
        return new QueueSnapshot(_bytes, Limit,
            (int)Scalar(db, "SELECT COUNT(*) FROM outbox WHERE state IN ('ready','sending','reserved')"),
            (int)Scalar(db, "SELECT COUNT(*) FROM outbox WHERE state='failed'"),
            (int)Scalar(db, "SELECT COUNT(*) FROM commands WHERE state IN ('pending','running')"), _paused, problems);
    });

    public Task<int> OffsetAsync() => Call(db => (int)Scalar(db, "SELECT value FROM meta WHERE key='offset'"));

    public async Task AcceptAsync(int updateId, long chat, string? payload, bool readOnly, bool priority, long created)
    {
        await Call(db =>
        {
            using var tx = db.BeginTransaction();
            if (payload is not null && Scalar(db, "SELECT COUNT(*) FROM commands WHERE id=$id", ("$id", updateId)) == 0)
            {
                long count = Scalar(db, "SELECT COUNT(*) FROM commands WHERE state IN ('pending','running')");
                if (count >= (priority ? 1000 : 950))
                    InsertText(db, chat, "Command queue full. Please retry later; /mic stop and /queue remain available.", null,
                        $"command-capacity:{chat}:{Now / 60000}", null, true);
                else
                    Exec(db, "INSERT INTO commands VALUES($id,$chat,$payload,$ro,$priority,$created,'pending',NULL)",
                        ("$id", updateId), ("$chat", chat), ("$payload", payload), ("$ro", readOnly ? 1 : 0),
                        ("$priority", priority ? 1 : 0), ("$created", created));
            }
            Exec(db, "UPDATE meta SET value=MAX(value,$offset) WHERE key='offset'", ("$offset", updateId + 1));
            tx.Commit(); return true;
        });
        CommandsChanged.Pulse(); ControlChanged.Pulse(); TextChanged.Pulse();
    }

    public Task<StoredCommand?> ClaimCommandAsync(bool priority) => Call(db =>
    {
        StoredCommand? result = null;
        using (var cmd = Command(db, "SELECT id,chat,payload,readonly,priority,created FROM commands WHERE state='pending' AND priority=$p ORDER BY id LIMIT 1", ("$p", priority ? 1 : 0)))
        using (var r = cmd.ExecuteReader()) if (r.Read()) result = new(r.GetInt32(0), r.GetInt64(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetInt64(5));
        if (result is not null) Exec(db, "UPDATE commands SET state='running' WHERE id=$id", ("$id", result.UpdateId));
        return result;
    });

    public Task FinishCommandAsync(int id, string state) => Call(db => Exec(db,
        "UPDATE commands SET state=$state,finished=$now,payload='' WHERE id=$id", ("$id", id), ("$state", state), ("$now", Now)));

    public Task CancelOlderRecordingCommandsAsync(int stopUpdateId) => Call(db => Exec(db, """
        UPDATE commands SET state='expired',finished=$now,payload=''
        WHERE state='pending' AND id<$id AND
          LOWER(CASE WHEN json_valid(payload) THEN COALESCE(json_extract(payload,'$.Message.Text'),json_extract(payload,'$.message.text')) ELSE '' END) LIKE '/mic%'
        """, ("$now", Now), ("$id", stopUpdateId)));

    public async Task ReportUnknownAsync()
    {
        await Call(db =>
        {
            var items = new List<(int, long)>();
            using (var cmd = Command(db, "SELECT id,chat FROM commands WHERE state='unknown' AND finished IS NULL LIMIT 1000"))
            using (var r = cmd.ExecuteReader()) while (r.Read()) items.Add((r.GetInt32(0), r.GetInt64(1)));
            foreach (var group in items.GroupBy(i => i.Item2))
            {
                string ids = string.Join(", ", group.Take(20).Select(i => i.Item1));
                try
                {
                    InsertText(db, group.Key, $"Interrupted commands ({group.Count()}): {ids}. Their result is unknown; they will NOT be executed again automatically.",
                        null, $"unknown:{group.Key}:{group.First().Item1}", null, true);
                    foreach (var (id, _) in group)
                        Exec(db, "UPDATE commands SET finished=$now,payload='' WHERE id=$id", ("$id", id), ("$now", Now));
                }
                catch (QueueCapacityException) { break; }
            }
            return true;
        });
        TextChanged.Pulse();
    }

    public void ConfigurationChanged() { SpaceChanged.Pulse(); }
    public async Task ConnectedAsync()
    {
        // Do not reset rate-limit Retry-After deadlines on ordinary polling successes.
        await Call(db => Exec(db, "UPDATE outbox SET due=$now WHERE state='ready' AND error='network'", ("$now", Now)));
        Interlocked.Increment(ref _connectionGeneration);
        TextChanged.Pulse(); MediaChanged.Pulse();
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true;
        _work.Writer.TryComplete(); _thread.Join(TimeSpan.FromSeconds(10));
        TextChanged.Dispose(); MediaChanged.Dispose(); CommandsChanged.Dispose(); ControlChanged.Dispose(); SpaceChanged.Dispose();
    }
}
