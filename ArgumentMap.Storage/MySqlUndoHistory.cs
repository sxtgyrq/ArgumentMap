using System.Text.Json;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private static void InitializeUndoHistory(MySqlConnection database)
    {
        RunSchema(database, "Schema.Undo.sql");
        using var upgrade = database.CreateCommand();
        upgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=30 WHERE Id=1 AND SchemaVersion=29";
        upgrade.ExecuteNonQuery();
    }

    private static UndoHistoryState ReadUndoHistory(MySqlConnection connection, MySqlTransaction transaction) => new(
        owner =>
        {
            using var command = Command(connection, transaction,
                "SELECT OperationId,Owner,Operation,Sequence,Payload FROM MapUndoHistory WHERE Owner=@owner AND IsUndone=0 ORDER BY Sequence DESC LIMIT 1");
            command.Parameters.AddWithValue("@owner", owner);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            var payload = JsonSerializer.Deserialize<UndoPayload>(reader.GetString(4))
                ?? throw new InvalidOperationException("撤销记录格式无效。");
            return new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), payload.Delta, payload.Stamps);
        },
        key =>
        {
            using var command = Command(connection, transaction, "SELECT OperationId FROM MapEditStamps WHERE ObjectKey=@key");
            command.Parameters.AddWithValue("@key", key);
            return command.ExecuteScalar() as string;
        },
        (owner, requestId) =>
        {
            using var command = Command(connection, transaction,
                "SELECT OperationId,Operation,Sequence,Payload FROM MapUndoHistory WHERE Owner=@owner AND UndoRequestId=@request AND IsUndone=1");
            command.Parameters.AddWithValue("@owner", owner); command.Parameters.AddWithValue("@request", requestId);
            using var reader = command.ExecuteReader(); if (!reader.Read()) return null;
            var payload = JsonSerializer.Deserialize<UndoPayload>(reader.GetString(3)) ?? throw new InvalidOperationException("撤销记录格式无效。");
            return new(reader.GetString(0), owner, reader.GetString(1), reader.GetInt64(2), payload.Delta, payload.Stamps, true, requestId);
        });

    private sealed record UndoPayload(MapUndoDelta Delta, UndoStamp[] Stamps);

    private static void PersistUndoHistory(MySqlConnection connection, MySqlTransaction transaction, UndoHistoryState history)
    {
        foreach (var entry in history.Added)
        {
            Execute(connection, transaction,
                "INSERT INTO MapUndoHistory (OperationId,Owner,Operation,Payload) VALUES (@id,@owner,@operation,@payload)",
                ("@id", entry.Id), ("@owner", entry.Owner), ("@operation", entry.Operation),
                ("@payload", JsonSerializer.Serialize(new UndoPayload(entry.Delta, entry.Stamps))));
            // Bounded storage per account; retain tombstone stamps to detect code/coordinate reuse.
            Execute(connection, transaction,
                "DELETE FROM MapUndoHistory WHERE Owner=@owner AND Sequence < COALESCE((SELECT cutoff FROM " +
                "(SELECT Sequence AS cutoff FROM MapUndoHistory WHERE Owner=@owner ORDER BY Sequence DESC LIMIT 1 OFFSET 99) AS retained),0)",
                ("@owner", entry.Owner));
        }
        foreach (var id in history.Consumed)
            Execute(connection, transaction, "UPDATE MapUndoHistory SET IsUndone=1,UndoRequestId=@request WHERE OperationId=@id",
                ("@id", id), ("@request", history.ConsumedRequests[id]));
        foreach (var (key, id) in history.StampWrites)
        {
            if (id is null) Execute(connection, transaction, "DELETE FROM MapEditStamps WHERE ObjectKey=@key", ("@key", key));
            else Execute(connection, transaction,
                "INSERT INTO MapEditStamps (ObjectKey,OperationId) VALUES (@key,@id) ON DUPLICATE KEY UPDATE OperationId=@id",
                ("@key", key), ("@id", id));
        }
    }
}
