using Microsoft.Data.Sqlite;

namespace WinSystemHelper;

public sealed partial class QueueStore
{
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
                {
                    InsertText(db, chat, "Command queue full. Please retry later; /mic stop and /queue remain available.", null,
                        $"command-capacity:{chat}:{Now / 60000}", null, true);
                }
                else
                {
                    Exec(db, "INSERT INTO commands VALUES($id,$chat,$payload,$ro,$priority,$created,'pending',NULL)",
                        ("$id", updateId), ("$chat", chat), ("$payload", payload), ("$ro", readOnly ? 1 : 0),
                        ("$priority", priority ? 1 : 0), ("$created", created));
                }
            }

            Exec(db, "UPDATE meta SET value=MAX(value,$offset) WHERE key='offset'", ("$offset", updateId + 1));
            tx.Commit();
            return true;
        });
        CommandsChanged.Pulse();
        ControlChanged.Pulse();
        TextChanged.Pulse();
    }

    public Task<StoredCommand?> ClaimCommandAsync(bool priority) => Call(db =>
    {
        StoredCommand? result = null;
        using (var cmd = Command(db, "SELECT id,chat,payload,readonly,priority,created FROM commands WHERE state='pending' AND priority=$p ORDER BY id LIMIT 1", ("$p", priority ? 1 : 0)))
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
            {
                result = new(r.GetInt32(0), r.GetInt64(1), r.GetString(2), r.GetBoolean(3), r.GetBoolean(4), r.GetInt64(5));
            }
        }
        if (result is not null)
        {
            Exec(db, "UPDATE commands SET state='running' WHERE id=$id", ("$id", result.UpdateId));
        }

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
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    items.Add((r.GetInt32(0), r.GetInt64(1)));
                }
            }
            foreach (var group in items.GroupBy(i => i.Item2))
            {
                string ids = string.Join(", ", group.Take(20).Select(i => i.Item1));
                try
                {
                    InsertText(db, group.Key, $"Interrupted commands ({group.Count()}): {ids}. Their result is unknown; they will NOT be executed again automatically.",
                        null, $"unknown:{group.Key}:{group.First().Item1}", null, true);
                    foreach (var (id, _) in group)
                    {
                        Exec(db, "UPDATE commands SET finished=$now,payload='' WHERE id=$id", ("$id", id), ("$now", Now));
                    }
                }
                catch (QueueCapacityException)
                {
                    break;
                }
            }
            return true;
        });
        TextChanged.Pulse();
    }
}
