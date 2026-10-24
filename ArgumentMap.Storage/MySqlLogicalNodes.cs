using ArgumentMap.Contracts;
using System.Text.Json;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    public string? LogicalNodeMigrationBackupPath { get; private set; }
    private void UpgradeLogicalNodeSchema(MySqlConnection connection)
    {
        var hadPermission = ColumnExists(connection, "DictLogin", "EditAllLogicalNodes");
        AddColumnIfMissing(connection, "DictLogin", "EditAllLogicalNodes", "BOOLEAN NOT NULL DEFAULT FALSE");
        using var version = connection.CreateCommand(); version.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if ((current is 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35) && hadPermission) return;
        if (current is not (10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35)) throw new InvalidOperationException("数据库架构版本不兼容。");
        if (current == 10) BackupLogicalNodeMigration(connection);
        // CREATE TABLE was already run from Schema.sql. After DDL, publish permissions and version atomically.
        using var transaction = connection.BeginTransaction();
        try
        {
            Execute(connection, transaction, "UPDATE DictLogin SET EditAllLogicalNodes=1 WHERE UserName='admin'");
            Execute(connection, transaction, "UPDATE MapMetadata SET SchemaVersion=11 WHERE Id=1 AND SchemaVersion=10");
            transaction.Commit();
        }
        catch { transaction.Rollback(); throw; }
    }

    private void BackupLogicalNodeMigration(MySqlConnection connection)
    {
        // InitializeSchema holds the same schema lease used by every map transaction.
        // Capture geometry and permissions here so no other user can commit between backup and publication.
        var tables=new Dictionary<string,List<Dictionary<string,object?>>>();
        foreach(var table in new[]{"Lines","LineNodes","Crosses","LogicalNodes","LogicalNodePositions","MapMetadata","DictLogin","MapUndoHistory","MapEditStamps"})
        {
            if(!TableExists(connection,table))continue;
            using var command=connection.CreateCommand();
            command.CommandText=table=="DictLogin"?"SELECT UserName,EditAllRoad,EditAllLogicalNodes FROM DictLogin":$"SELECT * FROM `{table}`";
            using var reader=command.ExecuteReader();var rows=new List<Dictionary<string,object?>>();
            while(reader.Read())
            {
                var row=new Dictionary<string,object?>();
                for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
                rows.Add(row);
            }
            tables[table]=rows;
        }
        var directory=ProjectPaths.BackupDirectory;Directory.CreateDirectory(directory);
        LogicalNodeMigrationBackupPath=Path.Combine(directory,$"before-logical-nodes-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        // Password hashes, login tokens and connection configuration are deliberately excluded.
        File.WriteAllText(LogicalNodeMigrationBackupPath,JsonSerializer.Serialize(new{Database=DatabaseName,Tables=tables},new JsonSerializerOptions{WriteIndented=true}));
    }

    private static void ReadLogicalNodes(MySqlConnection connection, MySqlTransaction transaction, LineStoreState state)
    {
        using (var command=Command(connection,transaction,"SELECT Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage,OperandKind FROM LogicalNodes ORDER BY Code"))
        using (var reader=command.ExecuteReader())
            while(reader.Read())state.LogicalNodes.Add(reader.GetString(0),new(reader.GetString(0),reader.GetString(1),reader.GetInt64(2),CompactCoordinate(reader.GetDecimal(3)),CompactCoordinate(reader.GetDecimal(4)),reader.GetInt32(5),
                new(){Value=reader.GetString(6),Info=reader.GetString(7),LogicLanguage=reader.GetString(8),OperandKind=reader.GetString(9)}));
        using(var command=Command(connection,transaction,"SELECT LogicalNodeCode,RoadCode,RoadOrder,RoadPercent,X,Y,RoadVersion,IsChanged FROM LogicalNodePositions ORDER BY LogicalNodeCode,RoadCode,RoadOrder"))
        using(var reader=command.ExecuteReader())
            while(reader.Read())
            {
                var p=new LogicalNodePosition(reader.GetString(0),reader.GetString(1),reader.GetInt32(2),ReadPercent(reader,3),reader.GetDecimal(4),reader.GetDecimal(5),reader.GetInt64(6),reader.GetInt32(7)<0);
                state.LogicalNodePositions.Add(p.Key,p);
            }
    }
    // Widened SQL columns retain input precision for CHECK; omit storage padding in
    // returned coordinates without rounding or changing the actual numeric value.
    private static decimal CompactCoordinate(decimal value)=>decimal.Parse(value.ToString("G29",System.Globalization.CultureInfo.InvariantCulture),System.Globalization.CultureInfo.InvariantCulture);
    private static void PersistLogicalNodes(MySqlConnection connection,MySqlTransaction transaction,LineStoreState state,Dictionary<string,LogicalNodeSnapshot> previous)
    {
        foreach(var code in previous.Keys.Where(c=>!state.LogicalNodes.ContainsKey(c)))Execute(connection,transaction,"DELETE FROM LogicalNodes WHERE Code=@code",("@code",code));
        foreach(var n in state.LogicalNodes.Values)
        {
            if(previous.TryGetValue(n.Code,out var old)&&old==n)continue;
            // A unique start-slot conflict must fail, never upsert another node's identity.
            var sql=previous.ContainsKey(n.Code)
                ? "UPDATE LogicalNodes SET Owner=@owner,Version=@version,X=@x,Y=@y,SideLength=@size,`Value`=@value,Info=@info,LogicLanguage=@logic,OperandKind=@kind,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code"
                : "INSERT INTO LogicalNodes (Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage,OperandKind,UpdatedAt) VALUES (@code,@owner,@version,@x,@y,@size,@value,@info,@logic,@kind,UTC_TIMESTAMP(6))";
            Execute(connection,transaction,sql,
                ("@code",n.Code),("@owner",n.Owner),("@version",n.Version),("@x",n.X),("@y",n.Y),("@size",n.SideLength),("@value",n.Properties.Value),("@info",n.Properties.Info),("@logic",n.Properties.LogicLanguage),("@kind",n.Properties.OperandKind));
        }
    }
    private static bool ShapeChanged(string code,LineStoreState state,Dictionary<string,LineSnapshot> before)=>!state.Lines.TryGetValue(code,out var now)||!before.TryGetValue(code,out var old)||!old.Nodes.SequenceEqual(now.Nodes);
    private static void DeleteLogicalNodePositions(MySqlConnection connection,MySqlTransaction transaction,LineStoreState state,Dictionary<LogicalNodePositionKey,LogicalNodePosition> before,Dictionary<string,LineSnapshot> lines)
    {
        foreach(var p in before.Values)
            if(!state.LogicalNodePositions.ContainsKey(p.Key)||ShapeChanged(p.RoadCode,state,lines))
                Execute(connection,transaction,"DELETE FROM LogicalNodePositions WHERE LogicalNodeCode=@ln AND RoadCode=@road AND RoadOrder=@order",("@ln",p.LogicalNodeCode),("@road",p.RoadCode),("@order",p.RoadOrder));
    }
    private static void PersistLogicalNodePositions(MySqlConnection connection,MySqlTransaction transaction,LineStoreState state,Dictionary<LogicalNodePositionKey,LogicalNodePosition> before,Dictionary<string,LineSnapshot> lines,Dictionary<LogicalNodePositionKey,decimal> translatedPercents)
    {
        foreach(var p in state.LogicalNodePositions.Values)
            if(!before.TryGetValue(p.Key,out var old)||old!=p||ShapeChanged(p.RoadCode,state,lines)||state.LogicalNodePositionRecalculationMarks.Contains(p.Key))
                WriteLogicalNodePosition(connection,transaction,p,preservePercent:old is not null && old.RoadPercent==p.RoadPercent,rawPercent:translatedPercents.TryGetValue(p.Key,out var raw)?raw:null);
    }
    private static void WriteLogicalNodePosition(MySqlConnection connection,MySqlTransaction transaction,LogicalNodePosition p,string table="LogicalNodePositions",bool preservePercent=false,decimal? rawPercent=null) =>
        Execute(connection,transaction,$"INSERT INTO `{table}` (LogicalNodeCode,RoadCode,RoadOrder,RoadPercent,X,Y,RoadVersion,IsChanged,UpdatedAt) VALUES (@ln,@road,@order,@percent,@x,@y,@version,@changed,UTC_TIMESTAMP(6)) " +
            "ON DUPLICATE KEY UPDATE RoadPercent=IF(@preservePercent,RoadPercent,@percent),X=@x,Y=@y,RoadVersion=@version,IsChanged=@changed,UpdatedAt=UTC_TIMESTAMP(6)",
            ("@ln",p.LogicalNodeCode),("@road",p.RoadCode),("@order",p.RoadOrder),("@percent",rawPercent??PercentDecimal(p.RoadPercent)),("@x",p.X),("@y",p.Y),("@version",p.RoadVersion),("@changed",p.IsChanged?-1:1),("@preservePercent",preservePercent));
}
