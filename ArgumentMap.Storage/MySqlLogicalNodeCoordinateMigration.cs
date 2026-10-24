using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeCoordinatesPending=2501;
    private void UpgradeLogicalNodeCoordinates(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var columns=connection.CreateCommand();
        columns.CommandText="SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND COLUMN_NAME IN ('X','Y') AND DATA_TYPE='decimal' AND NUMERIC_PRECISION=34 AND NUMERIC_SCALE=28 AND IS_NULLABLE='NO'";
        columns.Parameters.AddWithValue("@db",DatabaseName);var ready=Convert.ToInt32(columns.ExecuteScalar())==2;
        using var check=connection.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_CenterHalf' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var hasCheck=Convert.ToInt32(check.ExecuteScalar())==1;
        if(current is 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35)
        {if(!ready||!hasCheck)throw new InvalidOperationException("LN 中心坐标精度或半整数约束缺失，数据库架构不完整。");return;}
        if(current is not (25 or LogicalNodeCoordinatesPending))throw new InvalidOperationException("LN 中心坐标架构版本不兼容。");
        using var invalid=connection.CreateCommand();
        invalid.CommandText="SELECT Code FROM LogicalNodes WHERE X IS NULL OR Y IS NULL OR MOD(X,1)<>0.5 OR MOD(Y,1)<>0.5 LIMIT 1";
        if(invalid.ExecuteScalar() is string code)throw new InvalidOperationException($"LN {code} 的中心坐标不符合整数＋0.5；升级已停止，未自动移动节点。");
        BackupLogicalNodeMigration(connection);
        // Keep every .NET decimal input digit until CHECK evaluates it. DECIMAL(7,1)
        // used to round 100.51 to 100.5 before CHECK, incorrectly accepting the write.
        // One atomic ALTER changes both axes and the CHECK; a closed version marker
        // makes implicit-DDL commits and interrupted publication safely retryable.
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=2501 WHERE Id=1 AND SchemaVersion=25");
        var clauses=new List<string>();
        if(!ready){clauses.Add("MODIFY COLUMN X DECIMAL(34,28) NOT NULL");clauses.Add("MODIFY COLUMN Y DECIMAL(34,28) NOT NULL");}
        if(!hasCheck)clauses.Add("ADD CONSTRAINT CK_LN_CenterHalf CHECK (MOD(X,1)=0.5 AND MOD(Y,1)=0.5)");
        if(clauses.Count>0)MigrationExecute(connection,"ALTER TABLE LogicalNodes "+string.Join(", ",clauses));
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=26 WHERE Id=1 AND SchemaVersion=2501");
    }
}
