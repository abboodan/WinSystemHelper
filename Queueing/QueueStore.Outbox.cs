using Microsoft.Data.Sqlite;

namespace WinSystemHelper;

public sealed partial class QueueStore
{
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
            {
                for (int offset = 0, part = 0; offset < text.Length; part++)
                {
                    int length = Math.Min(4000, text.Length - offset);
                    if (char.IsHighSurrogate(text[offset + length - 1]))
                    {
                        length--;
                    }

                    InsertText(db, chat, text.Substring(offset, length), null, $"{key}:{chat}:{part}", expires, priority);
                    offset += length;
                }
            }

            transaction.Commit();
            return true;
        });
        TextChanged.Pulse();
    }

    private static bool InsertText(SqliteConnection db, long chat, string text, string? markup,
        string? dedupe, long? expires, bool priority)
    {
        if (dedupe is not null && Scalar(db, "SELECT COUNT(*) FROM outbox WHERE dedupe=$key", ("$key", dedupe)) > 0)
        {
            return true;
        }

        long count = Scalar(db, "SELECT COUNT(*) FROM outbox WHERE kind='text' AND state NOT IN ('sent','deleted')");
        if (count >= (priority ? 2000 : 1900))
        {
            throw new QueueCapacityException();
        }

        if (text.Length > 4096)
        {
            throw new ArgumentException("Telegram text must be split before storing.");
        }

        Exec(db, """
            INSERT INTO outbox(id,chat,kind,text,markup,state,created,due,dedupe,expires,priority)
            VALUES($id,$chat,'text',$text,$markup,'ready',$now,$now,$key,$expires,$priority)
            """, ("$id", Guid.NewGuid().ToString("N")), ("$chat", chat), ("$text", text), ("$markup", markup),
            ("$now", Now), ("$key", dedupe), ("$expires", expires), ("$priority", priority ? 1 : 0));
        return true;
    }

    public Task<OutboxItem?> ClaimAsync(bool media) => Call(db =>
    {
        Exec(db, "UPDATE outbox SET state='deleted' WHERE kind='text' AND state='ready' AND expires IS NOT NULL AND expires<$now", ("$now", Now));
        OutboxItem? item = null;
        using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE kind{(media ? "<>" : "=")}'text' AND state='ready' {(media ? "" : "AND due<=$now")} ORDER BY priority DESC,created,id LIMIT 1", ("$now", Now)))
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
            {
                item = Read(r);
            }
        }
        if (item is not null && item.Due > Now)
        {
            return null;
        }

        if (item is not null)
        {
            Exec(db, "UPDATE outbox SET state='sending',attempts=attempts+1 WHERE id=$id", ("$id", item.Id));
        }

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
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    items.Add(Read(r));
                }
            }
            foreach (var item in items)
            {
                try
                {
                    // Paths originate only in this store; never accept filesystem paths from bot commands.
                    if (item.Path!.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(item.Path);
                        TryDeleteCaptureDirectory(item.Path);
                    }
                    else
                    {
                        throw new IOException("Invalid stored path.");
                    }

                    Exec(db, "UPDATE outbox SET path=NULL,bytes=0 WHERE id=$id", ("$id", item.Id));
                    _bytes -= item.Bytes;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
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
        if (released)
        {
            SpaceChanged.Pulse();
        }
    }

    public Task RetryLaterAsync(string id, TimeSpan delay, string error, bool permanent) => Call(db => Exec(db,
        "UPDATE outbox SET state=$state,due=$due,error=$error WHERE id=$id AND state='sending'",
        ("$state", permanent ? OutboxState.Failed : OutboxState.Ready), ("$due", Now + (long)delay.TotalMilliseconds), ("$error", error), ("$id", id)));

    public async Task<bool> ManageAsync(string id, long chat, bool delete)
    {
        bool changed = await Call(db => Exec(db, delete
            ? "UPDATE outbox SET state='deleted' WHERE id=$id AND chat=$chat AND state IN ('failed','ready')"
            : "UPDATE outbox SET state='ready',due=$now,error=NULL WHERE id=$id AND chat=$chat AND state='failed' AND (path IS NULL OR path LIKE $media)",
            ("$id", id), ("$chat", chat), ("$now", Now), ("$media", Path.Combine(Root, "media") + "%")) > 0);
        await CleanupAsync();
        TextChanged.Pulse();
        MediaChanged.Pulse();
        return changed;
    }

    public Task<QueueSnapshot> SnapshotAsync(long? chat = null) => Call(db =>
    {
        var problems = new List<OutboxItem>();
        using (var cmd = Command(db, $"SELECT {Columns} FROM outbox WHERE state='failed' AND ($chat IS NULL OR chat=$chat) ORDER BY created DESC, rowid DESC LIMIT 10", ("$chat", chat)))
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                problems.Add(Read(r));
            }
        }
        return new QueueSnapshot(_bytes, Limit,
            (int)Scalar(db, "SELECT COUNT(*) FROM outbox WHERE state IN ('ready','sending','reserved')"),
            (int)Scalar(db, "SELECT COUNT(*) FROM outbox WHERE state='failed'"),
            (int)Scalar(db, "SELECT COUNT(*) FROM commands WHERE state IN ('pending','running')"), _paused, problems);
    });
}
