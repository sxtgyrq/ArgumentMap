using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int CrossIntegerPending = 3701;
    private const string CrossIntegerCheckName = "CK_Cross_IntegerCoordinates";
    private const string CrossIntegerCheck = "X BETWEEN 0 AND 500000 AND Y BETWEEN 0 AND 500000 AND X=FLOOR(X) AND Y=FLOOR(Y)";
    public string? CrossIntegerMigrationBackupPath { get; private set; }

    private void UpgradeCrossIntegerCoordinates(MySqlConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current is not (37 or 38 or CrossIntegerPending)) throw new InvalidOperationException("Cross 整数坐标架构版本不兼容。");
        if (current == 38) { VerifyCrossIntegerCoordinates(connection); return; }

        // All map writers take the same schema lease. Save the complete map and
        // journal before retiring fractional identities; never round them into
        // nearby integer crossings or recalculate surviving versions/settings.
        BackupLogicalNodeMigration(connection);
        CrossIntegerMigrationBackupPath = LogicalNodeMigrationBackupPath;
        MigrationExecute(connection, "UPDATE MapMetadata SET SchemaVersion=3701 WHERE Id=1 AND SchemaVersion=37");
        using (var transaction = connection.BeginTransaction())
        try
        {
            Execute(connection, transaction, "DELETE FROM Crosses WHERE X<>FLOOR(X) OR Y<>FLOOR(Y)");
            var history = new List<(long Sequence, string Payload, bool IsUndone)>();
            using (var read = Command(connection, transaction, "SELECT Sequence,Payload,IsUndone FROM MapUndoHistory ORDER BY Sequence FOR UPDATE"))
            using (var reader = read.ExecuteReader())
                while (reader.Read()) history.Add((reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
            foreach (var row in history)
            {
                var payload = JsonSerializer.Deserialize<UndoPayload>(row.Payload) ?? throw new InvalidOperationException("撤销记录格式无效。");
                var before = payload.Delta.BeforeCrosses.Where(c => c.Key.Position.IsInteger).ToArray();
                var after = payload.Delta.AfterCrosses.Where(c => c.Key.Position.IsInteger).ToArray();
                var stamps = payload.Stamps.Where(s => !RetiredCrossStamp(s.Key)).ToArray();
                if (before.Length == payload.Delta.BeforeCrosses.Length && after.Length == payload.Delta.AfterCrosses.Length && stamps.Length == payload.Stamps.Length) continue;
                if (!row.IsUndone && payload.Delta.Lines.Length == 0 && payload.Delta.Nodes.Length == 0 && before.Length == 0 && after.Length == 0 && stamps.Length == 0)
                {
                    // A retired-Cross-only edit must not hide the owner's next
                    // useful undo entry behind an empty compensation.
                    Execute(connection, transaction, "DELETE FROM MapUndoHistory WHERE Sequence=@sequence", ("@sequence", row.Sequence));
                    continue;
                }
                // Keep completed undo tombstones, even when their payload is now
                // empty. Their request IDs make a lost-response retry idempotent
                // instead of accidentally undoing the next surviving operation.
                var updated = payload with { Delta = payload.Delta with { BeforeCrosses = before, AfterCrosses = after }, Stamps = stamps };
                Execute(connection, transaction, "UPDATE MapUndoHistory SET Payload=@payload WHERE Sequence=@sequence",
                    ("@payload", JsonSerializer.Serialize(updated)), ("@sequence", row.Sequence));
            }
            var retiredStamps = new List<string>();
            using (var read = Command(connection, transaction, "SELECT ObjectKey FROM MapEditStamps WHERE ObjectKey LIKE 'C:%' FOR UPDATE"))
            using (var reader = read.ExecuteReader())
                while (reader.Read()) if (RetiredCrossStamp(reader.GetString(0))) retiredStamps.Add(reader.GetString(0));
            foreach (var key in retiredStamps)
                Execute(connection, transaction, "DELETE FROM MapEditStamps WHERE ObjectKey=@key", ("@key", key));
            transaction.Commit();
        }
        catch { transaction.Rollback(); throw; }

        // MySQL DDL commits implicitly. The pending marker remains closed across
        // the data transaction, this atomic ALTER and version publication. Retry
        // only repeats idempotent filtering; integer rows retain every value.
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='Crosses' AND CONSTRAINT_NAME=@name AND CONSTRAINT_TYPE='CHECK'";
        check.Parameters.AddWithValue("@db", DatabaseName); check.Parameters.AddWithValue("@name", CrossIntegerCheckName);
        var drop = Convert.ToInt32(check.ExecuteScalar()) > 0 ? "DROP CHECK " + CrossIntegerCheckName + ", " : "";
        MigrationExecute(connection, "ALTER TABLE Crosses " + drop + "MODIFY COLUMN X INT NOT NULL, MODIFY COLUMN Y INT NOT NULL, ADD CONSTRAINT " + CrossIntegerCheckName + " CHECK (" + CrossIntegerCheck + "), COMMENT='整数交点及通行关系，按线路对及整数坐标识别'");
        VerifyCrossIntegerCoordinates(connection);
        MigrationExecute(connection, "UPDATE MapMetadata SET SchemaVersion=38 WHERE Id=1 AND SchemaVersion=3701");
    }

    private void VerifyCrossIntegerCoordinates(MySqlConnection connection)
    {
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND COLUMN_NAME IN ('X','Y') AND DATA_TYPE='int' AND IS_NULLABLE='NO'";
        check.Parameters.AddWithValue("@db", DatabaseName);
        if (Convert.ToInt32(check.ExecuteScalar()) != 2) throw new InvalidOperationException("Cross 坐标字段必须为 INT，数据库架构不完整。");
        check.CommandText = "SELECT c.CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS c JOIN information_schema.TABLE_CONSTRAINTS t ON t.CONSTRAINT_SCHEMA=c.CONSTRAINT_SCHEMA AND t.CONSTRAINT_NAME=c.CONSTRAINT_NAME WHERE t.CONSTRAINT_SCHEMA=@db AND t.TABLE_NAME='Crosses' AND t.CONSTRAINT_NAME=@name AND t.CONSTRAINT_TYPE='CHECK' AND t.ENFORCED='YES'";
        check.Parameters.AddWithValue("@name", CrossIntegerCheckName);
        var clause = check.ExecuteScalar() as string;
        static string Normalize(string text) => Regex.Replace(text, @"[\s`()]+", "").ToUpperInvariant();
        if (clause is null || Normalize(clause) != Normalize(CrossIntegerCheck))
            throw new InvalidOperationException("缺少完整的 Cross 整数坐标约束，数据库架构不完整。");
    }

    private static bool RetiredCrossStamp(string key)
    {
        if (!key.StartsWith("C:", StringComparison.Ordinal)) return false;
        var fields = key.Split(':');
        if (fields.Length != 5 || !int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var x2) ||
            !int.TryParse(fields[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y2))
            throw new InvalidOperationException("Cross 撤销标记格式无效，升级已停止。");
        return x2 % 2 != 0 || y2 % 2 != 0;
    }

    // v16 must copy old half-integer identities intact. The dedicated v38
    // migration backs up and retires them only after all historical upgrades.
    private static string WithLegacyCrossCoordinates(string sql) => Regex.Replace(sql,
        @"CREATE TABLE IF NOT EXISTS Crosses \([\s\S]*?;", match => match.Value
            .Replace("X INT NOT NULL", "X DECIMAL(7,1) NOT NULL")
            .Replace("Y INT NOT NULL", "Y DECIMAL(7,1) NOT NULL")
            .Replace("    CONSTRAINT " + CrossIntegerCheckName + " CHECK (" + CrossIntegerCheck + "),", ""));
}
