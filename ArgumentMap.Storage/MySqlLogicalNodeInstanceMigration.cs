using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeInstancesPending=2601;
    private void UpgradeLogicalNodeInstances(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var check=connection.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME IN ('CK_LN_InstanceValue','CK_LN_InstanceLogic') AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var ready=Convert.ToInt32(check.ExecuteScalar())==2;
        if(current is 27 or 28 or 29 or 30 or 31){if(!ready)throw new InvalidOperationException("缺少 LN10 小写值或隶属表达式约束，数据库架构不完整。");return;}
        if(current is not(26 or LogicalNodeInstancesPending))throw new InvalidOperationException("LN10 架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=2601 WHERE Id=1 AND SchemaVersion=26");
        // Reapply the broadened geometry check even when restoring a database whose
        // instance CHECKs already exist but whose historical size check was rebuilt.
        {
            var clauses=new List<string>{"DROP CHECK CK_LN_V14","ADD CONSTRAINT CK_LN_V14 CHECK (REGEXP_LIKE(Code, '^[A-Z]{10}$', 'c') AND CHAR_LENGTH(TRIM(Owner))>0 AND Version>=0 AND MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (4,6,8,10,12,18) AND X-SideLength/2>=0 AND Y-SideLength/2>=0 AND X+SideLength/2<=500000 AND Y+SideLength/2<=500000)"};
            foreach(var definition in new[]{"CK_LN_InstanceValue CHECK (SideLength<>10 OR (REGEXP_LIKE(`Value`,'[^[:space:]]','c') AND CONVERT(`Value` USING binary)=CONVERT(LOWER(`Value`) USING binary)))",
                "CK_LN_InstanceLogic CHECK (SideLength<>10 OR CONVERT(LogicLanguage USING binary)=CONVERT(CONCAT(`Value`,' ∈ ',UPPER(`Value`)) USING binary))"})
            {
                using var exists=connection.CreateCommand();exists.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME=@name";
                exists.Parameters.AddWithValue("@db",DatabaseName);exists.Parameters.AddWithValue("@name",definition.Split(' ')[0]);
                if(Convert.ToInt32(exists.ExecuteScalar())==0)clauses.Add("ADD CONSTRAINT "+definition);
            }
            MigrationExecute(connection,"ALTER TABLE LogicalNodes "+string.Join(", ",clauses));
        }
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=27 WHERE Id=1 AND SchemaVersion=2601");
    }
}
