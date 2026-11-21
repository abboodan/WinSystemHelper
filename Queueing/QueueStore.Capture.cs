using Microsoft.Data.Sqlite;
using System.Security.AccessControl;

namespace WinSystemHelper;

public sealed partial class QueueStore
{
    public async Task<CaptureReservation?> ReserveAsync(long chat, string kind, long bytes, int duration, string? userSid)
    {
        return await Call(db =>
        {
            if (bytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(bytes));
            }

            if (bytes > Limit)
            {
                SetPaused(db, true);
                return null;
            }

            if (_paused && _bytes < Limit * 0.8)
            {
                SetPaused(db, false);
            }

            if (_paused || _bytes + bytes > Limit)
            {
                SetPaused(db, true);
                return null;
            }

            string id = Guid.NewGuid().ToString("N");
            string directory = Path.Combine(Root, "capture", id);
            Directory.CreateDirectory(directory);
            SecureDirectory(directory, userSid);
            string path = Path.Combine(directory, kind == QueueItemKind.Voice ? "capture.wav" : "capture.jpg");
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
        if (_paused == paused)
        {
            return;
        }

        Exec(db, "UPDATE meta SET value=$paused WHERE key='paused'", ("$paused", paused ? 1 : 0));
        _paused = paused;
    }

    public async Task CommitCaptureAsync(CaptureReservation reservation)
    {
        await Call(db =>
        {
            Promote(db, reservation.Id);
            return true;
        });
        MediaChanged.Pulse();
        SpaceChanged.Pulse();
    }

    private void Promote(SqliteConnection db, string id)
    {
        OutboxItem item;
        using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE id=$id", ("$id", id)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read())
            {
                return;
            }

            item = Read(r);
        }

        if (item.State != OutboxState.Reserved)
        {
            return;
        }

        string source = item.Path!;
        string final = Path.Combine(Root, "media", id + (item.Kind == QueueItemKind.Voice ? ".wav" : ".jpg"));
        string existing = File.Exists(final) ? final : source;
        var file = new FileInfo(existing);
        if (!file.Exists || file.Length == 0 || file.Length > item.Bytes ||
            (!File.Exists(final) && !File.Exists(source + ".complete")))
        {
            throw new IOException("Capture incomplete or exceeds its reserved capacity.");
        }

        if (!File.Exists(final))
        {
            File.Move(source, final);
        }

        if (OperatingSystem.IsWindows())
        {
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(false, false);
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
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    items.Add(Read(r));
                }
            }
            if (items.Count == 0)
            {
                break;
            }

            foreach (var item in items)
            {
                last = item.Id;
                try
                {
                    Promote(db, item.Id);
                }
                catch (IOException)
                {
                    Exec(db, "UPDATE outbox SET state='failed',error='Incomplete capture; review or delete' WHERE id=$id", ("$id", item.Id));
                }
            }
        }
    }

    public Task FailCaptureAsync(string id, string? error = null) => Call(db => Exec(db,
        "UPDATE outbox SET state='failed',error=$error WHERE id=$id AND state='reserved'",
        ("$error", error ?? "Capture did not complete"), ("$id", id)));

    private void TryDeleteCaptureDirectory(string path)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is null || !directory.StartsWith(Path.Combine(Root, "capture") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            Directory.Delete(directory, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
