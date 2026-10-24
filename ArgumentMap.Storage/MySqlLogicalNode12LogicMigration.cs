using System.Text.Json;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNode12LogicPending=2201;
    public string? LogicalNode12LogicBackupPath {get;private set;}

    private void UpgradeLogicalNode12Logic(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var check=connection.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_Logic12Required' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var hasCheck=Convert.ToInt32(check.ExecuteScalar())==1;
        if(current is 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38)
        {if(!hasCheck)throw new InvalidOperationException("缺少边长 12 的 LN 逻辑语言非空约束，数据库架构不完整。");return;}
        if(current is not (22 or LogicalNode12LogicPending))throw new InvalidOperationException("LN12 逻辑语言架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        using(var transaction=connection.BeginTransaction())
        try
        {
            var repairs=new List<LogicalNode12LogicRepair>();
            using(var read=Command(connection,transaction,"SELECT Code,Version,`Value`,LogicLanguage FROM LogicalNodes WHERE SideLength=12 ORDER BY Code FOR UPDATE"))
            using(var reader=read.ExecuteReader())
                while(reader.Read())
                {
                    var old=reader.GetString(3);if(!string.IsNullOrWhiteSpace(old))continue;
                    var code=reader.GetString(0);var value=reader.GetString(2);var oldVersion=reader.GetInt64(1);
                    // Legacy nodes can have a blank display value. Preserve that value while giving
                    // their required logic an identifiable fallback; new blank/blank nodes are rejected.
                    repairs.Add(new(code,old,string.IsNullOrWhiteSpace(value)?"LN_"+code:value,oldVersion,checked(oldVersion+1)));
                }
            Directory.CreateDirectory(ProjectPaths.BackupDirectory);
            LogicalNode12LogicBackupPath=Path.Combine(ProjectPaths.BackupDirectory,$"ln12-required-logic-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(LogicalNode12LogicBackupPath,JsonSerializer.Serialize(new{Database=DatabaseName,FromVersion=current,TargetVersion=23,FullBackup=LogicalNodeMigrationBackupPath,Repairs=repairs},new JsonSerializerOptions{WriteIndented=true}));
            Execute(connection,transaction,"UPDATE MapMetadata SET SchemaVersion=2201 WHERE Id=1");
            foreach(var repair in repairs)
                Execute(connection,transaction,"UPDATE LogicalNodes SET LogicLanguage=@logic,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                    ("@logic",repair.NewLogic),("@version",repair.NewVersion),("@code",repair.Code));
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        if(!hasCheck)MigrationExecute(connection,"ALTER TABLE LogicalNodes ADD CONSTRAINT CK_LN_Logic12Required CHECK (SideLength<>12 OR REGEXP_LIKE(LogicLanguage,'[^[:space:]]','c'))");
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=23 WHERE Id=1 AND SchemaVersion=2201");
    }
    private sealed record LogicalNode12LogicRepair(string Code,string OldLogic,string NewLogic,long OldVersion,long NewVersion);
}
