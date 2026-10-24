using System.Globalization;
using System.Text;
using System.Text.Json;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeUniqueValueMigrationPending = 1801;
    private static string WithoutLogicalNodeValueUniqueness(string sql) => string.Join("\n", sql.Split('\n')
        .Where(line => !line.TrimStart().StartsWith("Value12Key ", StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("UNIQUE KEY UQ_LN_Value12 ", StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("CONSTRAINT CK_LN_Value12Upper ", StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("Logic6Key ",StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("UNIQUE KEY UQ_LN_Logic6 ",StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("CONSTRAINT CK_LN_Logic6Required ",StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("CONSTRAINT CK_LN_BranchValue ",StringComparison.Ordinal) &&
            !line.TrimStart().StartsWith("CONSTRAINT CK_LN_Logic12Required ",StringComparison.Ordinal)));
    public string? LogicalNodeUniqueValueBackupPath { get; private set; }

    private void UpgradeLogicalNodeUniqueValues(MySqlConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current = Convert.ToInt32(version.ExecuteScalar());
        var hasColumn = ColumnExists(connection, "LogicalNodes", "Value12Key");
        var hasIndex = HasLogicalNodeValueIndex(connection);
        if (current is 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38)
        {
            if (!hasColumn || !hasIndex) throw new InvalidOperationException("缺少边长 12 的 LN 值唯一约束，数据库架构不完整。");
            return;
        }
        if (current is not (18 or LogicalNodeUniqueValueMigrationPending))
            throw new InvalidOperationException("LN 值唯一约束架构版本不兼容。");

        // The schema lease excludes all application writers throughout backup, DML and
        // atomic DDL. MySQL DDL commits implicitly: publish a retryable, closed marker
        // with the entire data repair before installing the constraint.
        BackupLogicalNodeMigration(connection);
        using (var transaction = connection.BeginTransaction())
        try
        {
            var rows = new List<(string Code, string Value, long Version)>();
            using (var read = Command(connection, transaction,
                "SELECT Code,`Value`,Version FROM LogicalNodes WHERE SideLength=12 ORDER BY Code FOR UPDATE"))
            using (var reader = read.ExecuteReader())
                while (reader.Read()) rows.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            var occupied = rows.Select(n => n.Value).ToHashSet(StringComparer.Ordinal);
            var repairs = new List<LogicalNodeValueRepair>();
            foreach (var group in rows.GroupBy(n => n.Value, StringComparer.Ordinal).Where(g => g.Count() > 1))
            {
                long suffix = 0;
                foreach (var row in group)
                {
                    string candidate;
                    do
                    {
                        var number = (++suffix).ToString(CultureInfo.InvariantCulture);
                        candidate = LogicalNodeValuePrefix(row.Value, 32 - number.Length) + number;
                    } while (!occupied.Add(candidate));
                    repairs.Add(new(row.Code, row.Value, candidate, row.Version, checked(row.Version + 1)));
                }
            }
            var directory = ProjectPaths.BackupDirectory;
            Directory.CreateDirectory(directory);
            LogicalNodeUniqueValueBackupPath = Path.Combine(directory,
                $"ln12-unique-values-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            // This is the repair plan written before mutation; a failed DML transaction
            // leaves all rows unchanged. The schema version records publication status.
            File.WriteAllText(LogicalNodeUniqueValueBackupPath, JsonSerializer.Serialize(new
            {
                Database = DatabaseName, FromVersion = current, TargetVersion = 19,
                Kind = "RepairPlanBeforeCommit", FullBackup = LogicalNodeMigrationBackupPath, Repairs = repairs
            }, new JsonSerializerOptions { WriteIndented = true }));
            Execute(connection, transaction, "UPDATE MapMetadata SET SchemaVersion=1801 WHERE Id=1");
            foreach (var repair in repairs)
                Execute(connection, transaction,
                    "UPDATE LogicalNodes SET `Value`=@value,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                    ("@value", repair.NewValue), ("@version", repair.NewVersion), ("@code", repair.Code));
            transaction.Commit();
        }
        catch { transaction.Rollback(); throw; }

        var clauses = new List<string>();
        if (!hasColumn) clauses.Add("ADD COLUMN Value12Key VARBINARY(128) GENERATED ALWAYS AS (CASE WHEN SideLength=12 THEN CONVERT(`Value` USING binary) ELSE NULL END) STORED");
        if (!hasIndex) clauses.Add("ADD UNIQUE KEY UQ_LN_Value12 (Value12Key)");
        if (clauses.Count > 0) MigrationExecute(connection, "ALTER TABLE LogicalNodes " + string.Join(", ", clauses));
        MigrationExecute(connection, "UPDATE MapMetadata SET SchemaVersion=19 WHERE Id=1 AND SchemaVersion=1801");
    }

    private bool HasLogicalNodeValueIndex(MySqlConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*)=1 AND COALESCE(SUM(NON_UNIQUE=0 AND COLUMN_NAME='Value12Key' AND SUB_PART IS NULL),0)=1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND INDEX_NAME='UQ_LN_Value12'";
        command.Parameters.AddWithValue("@db", DatabaseName);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static string LogicalNodeValuePrefix(string value, int maxLength)
    {
        var result = new StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (result.Length + rune.Utf16SequenceLength > maxLength) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }

    private sealed record LogicalNodeValueRepair(string Code, string OldValue, string NewValue, long OldVersion, long NewVersion);
}
