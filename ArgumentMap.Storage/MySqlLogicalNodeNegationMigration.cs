using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeNegationPending=3501;
    private void UpgradeLogicalNodeNegation(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        if(current is not (35 or 36 or 37 or 38 or LogicalNodeNegationPending))throw new InvalidOperationException("LN8 非运算符架构版本不兼容。");
        using var check=connection.CreateCommand();
        check.CommandText="SELECT c.CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS c JOIN information_schema.TABLE_CONSTRAINTS t ON t.CONSTRAINT_SCHEMA=c.CONSTRAINT_SCHEMA AND t.CONSTRAINT_NAME=c.CONSTRAINT_NAME WHERE t.CONSTRAINT_SCHEMA=@db AND t.TABLE_NAME='LogicalNodes' AND t.CONSTRAINT_NAME='CK_LN_RelationValue' AND t.CONSTRAINT_TYPE='CHECK' AND t.ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var clause=check.ExecuteScalar() as string;
        if(clause is null||new[]{"3D","E28692","E28694","E28A87","E28A8B","E28A86","E28A8A"}.Any(hex=>!clause.Contains(hex,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("缺少完整的 LN8 关系符号约束，数据库架构不完整。");
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_RelationLogic' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        if(Convert.ToInt32(check.ExecuteScalar())!=1)throw new InvalidOperationException("缺少 LN8 值与逻辑语言一致约束，数据库架构不完整。");
        if(current is 36 or 37 or 38)
        {
            if(!clause.Contains("C2AC",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("LN8 非运算符约束尚未完整升级，数据库架构不完整。");
            return;
        }
        BackupLogicalNodeMigration(connection);
        // The shared schema lease and closed marker exclude old and new writers
        // across the atomic DDL and version publication. No map rows are changed.
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=3501 WHERE Id=1 AND SchemaVersion=35");
        MigrationExecute(connection,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationValue, ADD CONSTRAINT CK_LN_RelationValue CHECK (SideLength<>8 OR CONVERT(`Value` USING binary) IN (X'3D',X'E28692',X'E28694',X'E28A87',X'E28A8B',X'E28A86',X'E28A8A',X'C2AC'))");
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=36 WHERE Id=1 AND SchemaVersion=3501");
    }
}
