using System.Text.Json;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeBranchPending=2101;
    public string? LogicalNodeBranchBackupPath {get;private set;}
    private void UpgradeLogicalNodeBranches(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var check=connection.CreateCommand();check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_BranchValue' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var hasCheck=Convert.ToInt32(check.ExecuteScalar())==1;
        if(current is 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35)
        {if(!hasCheck)throw new InvalidOperationException("缺少 LN4 True／False 值约束，数据库架构不完整。");return;}
        if(current is not (21 or LogicalNodeBranchPending))throw new InvalidOperationException("LN4 分支逻辑架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        using(var transaction=connection.BeginTransaction())
        try
        {
            var nodes=new List<LogicalNodeSnapshot>();
            using(var read=Command(connection,transaction,"SELECT Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage FROM LogicalNodes ORDER BY Code FOR UPDATE"))
            using(var reader=read.ExecuteReader())while(reader.Read())nodes.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt64(2),reader.GetDecimal(3),reader.GetDecimal(4),reader.GetInt32(5),new(){Value=reader.GetString(6),Info=reader.GetString(7),LogicLanguage=reader.GetString(8)}));
            var repairs=new List<BranchRepair>();
            var sources=LogicalNodeBranchRules.SourceCounts(nodes);
            foreach(var old in nodes.Where(n=>n.SideLength==4))
            {
                var value=old.Properties.Value.Trim() is "是" or "真"?LogicalNodeBranchRules.True:
                    string.Equals(old.Properties.Value.Trim(),"True",StringComparison.OrdinalIgnoreCase)?LogicalNodeBranchRules.True:LogicalNodeBranchRules.False;
                var logic=string.IsNullOrWhiteSpace(old.Properties.LogicLanguage)?"":old.Properties.LogicLanguage;
                var candidate=old with{Properties=old.Properties with{Value=value,LogicLanguage=logic}};
                if(LogicalNodeBranchRules.Validate(candidate,sources) is not null)logic="";
                if(value!=old.Properties.Value||logic!=old.Properties.LogicLanguage)repairs.Add(new(old.Code,old.Properties.Value,old.Properties.LogicLanguage,value,logic,old.Version,checked(old.Version+1)));
            }
            Directory.CreateDirectory(ProjectPaths.BackupDirectory);
            LogicalNodeBranchBackupPath=Path.Combine(ProjectPaths.BackupDirectory,$"ln4-branches-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(LogicalNodeBranchBackupPath,JsonSerializer.Serialize(new{Database=DatabaseName,FromVersion=current,TargetVersion=22,FullBackup=LogicalNodeMigrationBackupPath,Repairs=repairs},new JsonSerializerOptions{WriteIndented=true}));
            Execute(connection,transaction,"UPDATE MapMetadata SET SchemaVersion=2101 WHERE Id=1");
            foreach(var repair in repairs)Execute(connection,transaction,"UPDATE LogicalNodes SET `Value`=@value,LogicLanguage=@logic,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                ("@value",repair.NewValue),("@logic",repair.NewLogic),("@version",repair.NewVersion),("@code",repair.Code));
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        if(!hasCheck)MigrationExecute(connection,"ALTER TABLE LogicalNodes ADD CONSTRAINT CK_LN_BranchValue CHECK (SideLength<>4 OR CONVERT(`Value` USING binary) IN (_binary'True',_binary'False'))");
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=22 WHERE Id=1 AND SchemaVersion=2101");
    }
    private sealed record BranchRepair(string Code,string OldValue,string OldLogic,string NewValue,string NewLogic,long OldVersion,long NewVersion);
}
