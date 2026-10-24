using System.Text.Json;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeRelationLogicPending=2401;
    public string? LogicalNodeRelationLogicBackupPath {get;private set;}

    private void UpgradeLogicalNodeRelationLogic(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var check=connection.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_RelationLogic' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var hasCheck=Convert.ToInt32(check.ExecuteScalar())==1;
        if(current is 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38)
        {if(!hasCheck)throw new InvalidOperationException("缺少 LN8 值与逻辑语言一致约束，数据库架构不完整。");return;}
        if(current is not (24 or LogicalNodeRelationLogicPending))throw new InvalidOperationException("LN8 逻辑语言架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        // InitializeSchema holds the same schema lease used by every map transaction.
        // Publish a closed marker with the repairs; a failed DDL/publication can retry
        // without incrementing already repaired versions or allowing old writers in.
        using(var transaction=connection.BeginTransaction())
        try
        {
            var repairs=new List<LogicalNodeRelationLogicRepair>();
            using(var read=Command(connection,transaction,"SELECT Code,Version,`Value`,LogicLanguage FROM LogicalNodes WHERE SideLength=8 ORDER BY Code FOR UPDATE"))
            using(var reader=read.ExecuteReader())
                while(reader.Read())
                {
                    var value=reader.GetString(2);var old=reader.GetString(3);
                    var expected=LogicalNodeRelationRules.Expression(value);
                    // A replay during derived-position recovery may encounter either
                    // the legacy NOT rule or v37's placeholder rule. Leave a valid
                    // old NOT for the dedicated v37 migration to update atomically.
                    if(old==expected||(hasCheck&&value==LogicalNodeRelationRules.Negation&&old==value))continue;
                    var oldVersion=reader.GetInt64(1);
                    repairs.Add(new(reader.GetString(0),old,expected,oldVersion,checked(oldVersion+1)));
                }
            Directory.CreateDirectory(ProjectPaths.BackupDirectory);
            LogicalNodeRelationLogicBackupPath=Path.Combine(ProjectPaths.BackupDirectory,$"ln8-matching-logic-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(LogicalNodeRelationLogicBackupPath,JsonSerializer.Serialize(new{Database=DatabaseName,FromVersion=current,TargetVersion=25,FullBackup=LogicalNodeMigrationBackupPath,Repairs=repairs},new JsonSerializerOptions{WriteIndented=true}));
            Execute(connection,transaction,"UPDATE MapMetadata SET SchemaVersion=2401 WHERE Id=1");
            foreach(var repair in repairs)
                Execute(connection,transaction,"UPDATE LogicalNodes SET LogicLanguage=@logic,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                    ("@logic",repair.NewLogic),("@version",repair.NewVersion),("@code",repair.Code));
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        if(!hasCheck)MigrationExecute(connection,"ALTER TABLE LogicalNodes ADD CONSTRAINT "+LogicalNodeRelationExpressionCheck);
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=25 WHERE Id=1 AND SchemaVersion=2401");
    }
    private sealed record LogicalNodeRelationLogicRepair(string Code,string OldLogic,string NewLogic,long OldVersion,long NewVersion);
}
