using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeEquivalencePending=2801;
    private void UpgradeLogicalNodeEquivalence(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var check=connection.CreateCommand();
        check.CommandText="SELECT c.CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS c JOIN information_schema.TABLE_CONSTRAINTS t ON t.CONSTRAINT_SCHEMA=c.CONSTRAINT_SCHEMA AND t.CONSTRAINT_NAME=c.CONSTRAINT_NAME WHERE t.CONSTRAINT_SCHEMA=@db AND t.TABLE_NAME='LogicalNodes' AND t.CONSTRAINT_NAME='CK_LN_RelationValue' AND t.CONSTRAINT_TYPE='CHECK' AND t.ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var clause=check.ExecuteScalar() as string;
        if(clause is null)throw new InvalidOperationException("缺少 LN8 关系符号约束，数据库架构不完整。");
        if(current is 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38)
        {
            if(new[]{"3D","E28692","E28694","E28A87","E28A8B","E28A86","E28A8A"}.Any(hex=>!clause.Contains(hex,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("LN8 等价及关系符号约束尚未完整升级，数据库架构不完整。");
            return;
        }
        if(current is not (28 or LogicalNodeEquivalencePending))throw new InvalidOperationException("LN8 等价关系架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        // The schema lease spans the closed marker, atomic DDL and publication.
        // No map row is changed, and an interrupted upgrade remains retryable.
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=2801 WHERE Id=1 AND SchemaVersion=28");
        MigrationExecute(connection,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationValue, ADD CONSTRAINT CK_LN_RelationValue CHECK (SideLength<>8 OR CONVERT(`Value` USING binary) IN (X'3D',X'E28692',X'E28694',X'E28A87',X'E28A8B',X'E28A86',X'E28A8A',X'C2AC'))");
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=29 WHERE Id=1 AND SchemaVersion=2801");
    }
}
