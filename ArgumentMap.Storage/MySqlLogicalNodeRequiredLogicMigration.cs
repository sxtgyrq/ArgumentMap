using System.Text.Json;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeRequiredLogicPending=2001;
    public string? LogicalNodeRequiredLogicBackupPath {get;private set;}

    private void UpgradeLogicalNodeRequiredLogic(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        var hasColumn=ColumnExists(connection,"LogicalNodes","Logic6Key");
        using var index=connection.CreateCommand();
        index.CommandText="SELECT COUNT(*)=1 AND COALESCE(SUM(NON_UNIQUE=0 AND COLUMN_NAME='Logic6Key' AND SUB_PART IS NULL),0)=1 FROM information_schema.STATISTICS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND INDEX_NAME='UQ_LN_Logic6'";
        index.Parameters.AddWithValue("@db",DatabaseName);var hasIndex=Convert.ToInt32(index.ExecuteScalar())==1;
        using var check=connection.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_Logic6Required' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var hasCheck=Convert.ToInt32(check.ExecuteScalar())==1;
        if(current is 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38)
        {
            if(!hasColumn||!hasIndex||!hasCheck)throw new InvalidOperationException("缺少边长 6 的 LN 逻辑语言非空或唯一约束，数据库架构不完整。");
            return;
        }
        if(current is not (20 or LogicalNodeRequiredLogicPending))throw new InvalidOperationException("LN6 逻辑语言架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        using(var transaction=connection.BeginTransaction())
        try
        {
            var nodes=new List<LogicalNodeSnapshot>();
            using(var read=Command(connection,transaction,"SELECT Code,Owner,Version,X,Y,LogicLanguage FROM LogicalNodes WHERE SideLength=6 ORDER BY Code FOR UPDATE"))
            using(var reader=read.ExecuteReader())while(reader.Read())nodes.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt64(2),reader.GetDecimal(3),reader.GetDecimal(4),6,new(){LogicLanguage=reader.GetString(5)}));
            // Never rewrite an existing nonempty expression to hide a duplicate: it can
            // change the meaning of a flow. Fail without publishing any empty repairs.
            if(LogicalNodeLogicRules.FindConflict(nodes) is {} duplicate)throw new DuplicateLogicalNodeLogicException(duplicate);
            var used=nodes.Where(n=>!string.IsNullOrWhiteSpace(n.Properties.LogicLanguage)).Select(n=>n.Properties.LogicLanguage).ToHashSet(StringComparer.Ordinal);
            var repairs=nodes.Where(n=>string.IsNullOrWhiteSpace(n.Properties.LogicLanguage)).Select(n=>new LogicalNodeRequiredLogicRepair(
                n.Code,n.Properties.LogicLanguage,LogicalNodeLogicRules.ReservePlaceholder(n.Code,used),n.Version,checked(n.Version+1))).ToArray();
            Directory.CreateDirectory(ProjectPaths.BackupDirectory);
            LogicalNodeRequiredLogicBackupPath=Path.Combine(ProjectPaths.BackupDirectory,$"ln6-required-logic-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(LogicalNodeRequiredLogicBackupPath,JsonSerializer.Serialize(new
            {Database=DatabaseName,FromVersion=current,TargetVersion=21,Kind="RepairPlanBeforeCommit",FullBackup=LogicalNodeMigrationBackupPath,Repairs=repairs},new JsonSerializerOptions{WriteIndented=true}));
            Execute(connection,transaction,"UPDATE MapMetadata SET SchemaVersion=2001 WHERE Id=1");
            foreach(var repair in repairs)
                Execute(connection,transaction,"UPDATE LogicalNodes SET LogicLanguage=@logic,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                    ("@logic",repair.NewLogic),("@version",repair.NewVersion),("@code",repair.Code));
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        var clauses=new List<string>();
        if(!hasColumn)clauses.Add("ADD COLUMN Logic6Key VARBINARY(1020) GENERATED ALWAYS AS (CASE WHEN SideLength=6 THEN CONVERT(LogicLanguage USING binary) ELSE NULL END) STORED");
        if(!hasIndex)clauses.Add("ADD UNIQUE KEY UQ_LN_Logic6 (Logic6Key)");
        if(!hasCheck)clauses.Add("ADD CONSTRAINT CK_LN_Logic6Required CHECK (SideLength<>6 OR REGEXP_LIKE(LogicLanguage,'[^[:space:]]','c'))");
        if(clauses.Count>0)MigrationExecute(connection,"ALTER TABLE LogicalNodes "+string.Join(", ",clauses));
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=21 WHERE Id=1 AND SchemaVersion=2001");
    }
    private sealed record LogicalNodeRequiredLogicRepair(string Code,string OldLogic,string NewLogic,long OldVersion,long NewVersion);
}
