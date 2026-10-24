using System.Text.Json;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeNegationLogicPending=3601;
    private const string LogicalNodeRelationExpressionCheck="CK_LN_RelationLogic CHECK (SideLength<>8 OR CONVERT(LogicLanguage USING binary)=CASE WHEN CONVERT(`Value` USING binary)=X'C2AC' THEN X'C2AC283F29' ELSE CONVERT(`Value` USING binary) END)";
    private const string LogicalNodeRelationTransitionCheck="CK_LN_RelationLogic CHECK (SideLength<>8 OR (CONVERT(`Value` USING binary)=X'C2AC' AND CONVERT(LogicLanguage USING binary) IN (X'C2AC',X'C2AC283F29')) OR (CONVERT(`Value` USING binary)<>X'C2AC' AND CONVERT(LogicLanguage USING binary)=CONVERT(`Value` USING binary)))";
    public string? LogicalNodeNegationLogicBackupPath {get;private set;}

    private void UpgradeLogicalNodeNegationLogic(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        if(current is not (36 or 37 or 38 or LogicalNodeNegationLogicPending))throw new InvalidOperationException("LN8 NOT 占位表达式架构版本不兼容。");
        using var check=connection.CreateCommand();
        check.CommandText="SELECT c.CHECK_CLAUSE FROM information_schema.CHECK_CONSTRAINTS c JOIN information_schema.TABLE_CONSTRAINTS t ON t.CONSTRAINT_SCHEMA=c.CONSTRAINT_SCHEMA AND t.CONSTRAINT_NAME=c.CONSTRAINT_NAME WHERE t.CONSTRAINT_SCHEMA=@db AND t.TABLE_NAME='LogicalNodes' AND t.CONSTRAINT_NAME=@name AND t.CONSTRAINT_TYPE='CHECK' AND t.ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var name=check.Parameters.AddWithValue("@name","CK_LN_RelationValue");
        var values=check.ExecuteScalar() as string;
        if(values is null||new[]{"3D","E28692","E28694","E28A87","E28A8B","E28A86","E28A8A","C2AC"}.Any(hex=>!values.Contains(hex,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("缺少完整的 LN8 关系符号约束，数据库架构不完整。");
        name.Value="CK_LN_RelationLogic";var logic=check.ExecuteScalar() as string;
        if(logic is null)throw new InvalidOperationException("缺少 LN8 固定逻辑语言约束，数据库架构不完整。");
        if(current is 37 or 38)
        {
            if(!logic.Contains("C2AC283F29",StringComparison.OrdinalIgnoreCase)||!logic.Contains("case",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("LN8 NOT 占位表达式约束尚未完整升级，数据库架构不完整。");
            return;
        }
        BackupLogicalNodeMigration(connection);
        // Keep all application writers closed while DDL commits implicitly. The
        // temporary contract admits only the old and new NOT expressions; other
        // relations retain their exact value/logic equality throughout the repair.
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=3601 WHERE Id=1 AND SchemaVersion=36");
        MigrationExecute(connection,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic, ADD CONSTRAINT "+LogicalNodeRelationTransitionCheck);
        using(var transaction=connection.BeginTransaction())
        try
        {
            var repairs=new List<LogicalNodeRelationLogicRepair>();
            using(var read=Command(connection,transaction,"SELECT Code,Version,LogicLanguage FROM LogicalNodes WHERE SideLength=8 AND CONVERT(`Value` USING binary)=X'C2AC' ORDER BY Code FOR UPDATE"))
            using(var reader=read.ExecuteReader())
                while(reader.Read())
                {
                    var old=reader.GetString(2);var expected=LogicalNodeRelationRules.Expression(LogicalNodeRelationRules.Negation);
                    if(old==expected)continue;
                    var oldVersion=reader.GetInt64(1);
                    repairs.Add(new(reader.GetString(0),old,expected,oldVersion,checked(oldVersion+1)));
                }
            Directory.CreateDirectory(ProjectPaths.BackupDirectory);
            LogicalNodeNegationLogicBackupPath=Path.Combine(ProjectPaths.BackupDirectory,$"ln8-not-placeholder-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(LogicalNodeNegationLogicBackupPath,JsonSerializer.Serialize(new{Database=DatabaseName,FromVersion=current,TargetVersion=37,FullBackup=LogicalNodeMigrationBackupPath,Repairs=repairs},new JsonSerializerOptions{WriteIndented=true}));
            foreach(var repair in repairs)
                Execute(connection,transaction,"UPDATE LogicalNodes SET LogicLanguage=@logic,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                    ("@logic",repair.NewLogic),("@version",repair.NewVersion),("@code",repair.Code));
            var history=new List<(long Sequence,string Payload)>();
            using(var read=Command(connection,transaction,"SELECT Sequence,Payload FROM MapUndoHistory ORDER BY Sequence FOR UPDATE"))
            using(var reader=read.ExecuteReader())while(reader.Read())history.Add((reader.GetInt64(0),reader.GetString(1)));
            LogicalNodeSnapshot? Upgrade(LogicalNodeSnapshot? node)=>node is {SideLength:8}&&node.Properties.Value==LogicalNodeRelationRules.Negation&&node.Properties.LogicLanguage!=LogicalNodeRelationRules.Expression(node.Properties.Value)
                ?node with{Properties=node.Properties with{LogicLanguage=LogicalNodeRelationRules.Expression(node.Properties.Value)}}:node;
            foreach(var row in history)
            {
                var payload=JsonSerializer.Deserialize<UndoPayload>(row.Payload)??throw new InvalidOperationException("撤销记录格式无效。");
                var nodes=payload.Delta.Nodes.Select(change=>new UndoNodeChange(Upgrade(change.Before),Upgrade(change.After))).ToArray();
                if(nodes.SequenceEqual(payload.Delta.Nodes))continue;
                Execute(connection,transaction,"UPDATE MapUndoHistory SET Payload=@payload WHERE Sequence=@sequence",
                    ("@payload",JsonSerializer.Serialize(payload with{Delta=payload.Delta with{Nodes=nodes}})),("@sequence",row.Sequence));
            }
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        MigrationExecute(connection,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic, ADD CONSTRAINT "+LogicalNodeRelationExpressionCheck);
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=37 WHERE Id=1 AND SchemaVersion=3601");
    }
}
