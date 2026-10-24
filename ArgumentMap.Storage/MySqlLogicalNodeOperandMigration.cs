using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeOperandPending = 3001;
    private void UpgradeLogicalNodeOperandKind(MySqlConnection connection)
    {
        using var version = connection.CreateCommand(); version.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current is not (30 or 31 or 32 or 33 or 34 or 35 or LogicalNodeOperandPending)) throw new InvalidOperationException("LN6 OperandKind 架构版本不兼容。");
        var hasColumn = ColumnExists(connection, "LogicalNodes", "OperandKind");
        using var checks = connection.CreateCommand();
        checks.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES' AND CONSTRAINT_NAME IN ('CK_LN_OperandKind','CK_LN_OperandCase')";
        checks.Parameters.AddWithValue("@db", DatabaseName);
        var ready = hasColumn && Convert.ToInt32(checks.ExecuteScalar()) == 2;
        if (current is 31 or 32 or 33 or 34 or 35) { if (!ready) throw new InvalidOperationException("缺少 LN6 OperandKind 数据库约束。"); return; }
        if (!ready)
        {
            if (current == 30) BackupLogicalNodeMigration(connection);
            MigrationExecute(connection, "UPDATE MapMetadata SET SchemaVersion=3001 WHERE Id=1 AND SchemaVersion=30");
            var changes = new List<string>();
            if (!hasColumn) changes.Add("ADD COLUMN OperandKind VARCHAR(8) CHARACTER SET ascii COLLATE ascii_bin NOT NULL DEFAULT 'Custom' COMMENT 'LN6: Class/Instance/Custom'");
            foreach (var definition in new[]
            {
                "CK_LN_OperandKind CHECK ((SideLength=6 AND CONVERT(OperandKind USING binary) IN (_binary'Class',_binary'Instance',_binary'Custom')) OR (SideLength<>6 AND CONVERT(OperandKind USING binary)=_binary'Custom'))",
                "CK_LN_OperandCase CHECK (SideLength<>6 OR CONVERT(OperandKind USING binary)=_binary'Custom' OR (CONVERT(OperandKind USING binary)=_binary'Class' AND CONVERT(LogicLanguage USING binary)=CONVERT(UPPER(LogicLanguage) USING binary)) OR (CONVERT(OperandKind USING binary)=_binary'Instance' AND CONVERT(LogicLanguage USING binary)=CONVERT(LOWER(LogicLanguage) USING binary)))"
            })
            {
                using var exists = connection.CreateCommand();
                exists.CommandText = "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME=@name";
                exists.Parameters.AddWithValue("@db", DatabaseName); exists.Parameters.AddWithValue("@name", definition.Split(' ')[0]);
                if (Convert.ToInt32(exists.ExecuteScalar()) == 0) changes.Add("ADD CONSTRAINT " + definition);
            }
            if (changes.Count > 0) MigrationExecute(connection, "ALTER TABLE LogicalNodes " + string.Join(",", changes));
        }
        MigrationExecute(connection, "UPDATE MapMetadata SET SchemaVersion=31 WHERE Id=1 AND SchemaVersion IN (30,3001)");
    }
}
