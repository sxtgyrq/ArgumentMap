using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private void UpgradeLogicalNodePositions(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        if(current is 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35)
        {
            if(TableExists(connection,"LogicalNodePositions"))
            {
                MigrationExecute(connection,"DROP TABLE IF EXISTS LogicalNodes_OnePositionV11");
                return;
            }
            // Recreate a missing derived table from authoritative LN/road geometry. Publish a
            // recovery marker before DDL so an interrupted rebuild remains retryable and closed to writers.
            MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=11 WHERE Id=1 AND SchemaVersion IN (12,13,14,15,16,17,18,19,20,21,22,23,24,25,26,27,28,29,30,31,32,33,34,35)");
            current=11;
        }
        if(current!=11)throw new InvalidOperationException("LN 位置架构版本不兼容。");
        if(ColumnExists(connection,"LogicalNodes","RoadCode"))
        {
            BackupLogicalNodeMigration(connection);
            MigrationExecute(connection,"DROP TABLE IF EXISTS LogicalNodePositions_MultiV12");
            MigrationExecute(connection,"DROP TABLE IF EXISTS LogicalNodes_MultiV12");
            // Historical values may repeat. Copy them intact before v19 repairs and
            // installs the new constraint under the same schema lease.
            RunSchema(connection,"Schema.sql",sql=>WithoutLogicalNodeValueUniqueness(sql).Replace("CREATE TABLE IF NOT EXISTS LogicalNodes (","CREATE TABLE IF NOT EXISTS LogicalNodes_MultiV12 ("));
            RunSchema(connection,"Schema.LogicalNodePositions.sql",sql=>sql.Replace("LogicalNodePositions (","LogicalNodePositions_MultiV12 (")
                .Replace("REFERENCES LogicalNodes(","REFERENCES LogicalNodes_MultiV12("));
            // Populate isolated tables transactionally. Failure cannot change the live version-11 table.
            using(var transaction=connection.BeginTransaction())
            try
            {
                Execute(connection,transaction,"INSERT INTO LogicalNodes_MultiV12 (Code,Owner,Version,X,Y,SideLength,`Value`,Info,UpdatedAt) SELECT Code,Owner,Version,X,Y,SideLength,Name,Info,UpdatedAt FROM LogicalNodes");
                RebuildMigratedPositions(connection,transaction,"LogicalNodes_MultiV12","LogicalNodePositions_MultiV12");
                transaction.Commit();
            }
            catch{transaction.Rollback();throw;}
            // MySQL DDL commits implicitly: publish both tables together with one atomic RENAME.
            // The schema lease blocks all application writers throughout backup, copy and publication.
            MigrationExecute(connection,"RENAME TABLE LogicalNodes TO LogicalNodes_OnePositionV11, LogicalNodes_MultiV12 TO LogicalNodes, LogicalNodePositions_MultiV12 TO LogicalNodePositions");
        }
        else RunSchema(connection,"Schema.LogicalNodePositions.sql");
        // Supports a restart after atomic publication but before the version write.
        using(var transaction=connection.BeginTransaction())
        try
        {
            Execute(connection,transaction,"DELETE FROM LogicalNodePositions");
            RebuildMigratedPositions(connection,transaction,"LogicalNodes","LogicalNodePositions");
            Execute(connection,transaction,"UPDATE MapMetadata SET SchemaVersion=12 WHERE Id=1 AND SchemaVersion=11");
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        MigrationExecute(connection,"DROP TABLE IF EXISTS LogicalNodes_OnePositionV11");
    }
    private void RebuildMigratedPositions(MySqlConnection connection,MySqlTransaction transaction,string nodesTable,string positionsTable)
    {
        var nodes=new List<LogicalNodeSnapshot>();
        using var valueCheck=Command(connection,transaction,"SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table AND COLUMN_NAME='Value'");
        valueCheck.Parameters.AddWithValue("@db",DatabaseName);valueCheck.Parameters.AddWithValue("@table",nodesTable);
        var valueColumn=Convert.ToInt32(valueCheck.ExecuteScalar())>0?"`Value`":"Name";
        using(var command=Command(connection,transaction,$"SELECT Code,Owner,Version,X,Y,SideLength,{valueColumn},Info FROM `{nodesTable}`"))
        using(var reader=command.ExecuteReader())
            while(reader.Read())nodes.Add(new(reader.GetString(0),reader.GetString(1),reader.GetInt64(2),CompactCoordinate(reader.GetDecimal(3)),CompactCoordinate(reader.GetDecimal(4)),reader.GetInt32(5),
                new(){Value=reader.GetString(6),Info=reader.GetString(7)}));
        var roads=new List<LineSnapshot>();var roadNodes=new Dictionary<string,List<LineNode>>(StringComparer.Ordinal);
        using(var command=Command(connection,transaction,"SELECT LineCode,NodeOrder,X,Y FROM LineNodes ORDER BY LineCode,NodeOrder"))
        using(var reader=command.ExecuteReader())
            while(reader.Read())
            {var code=reader.GetString(0);if(!roadNodes.TryGetValue(code,out var list))roadNodes[code]=list=[];list.Add(new(code,reader.GetInt32(1),reader.GetInt32(2),reader.GetInt32(3)));}
        using(var command=Command(connection,transaction,"SELECT LineCode,Owner,IsLocked,Version FROM `Lines`"))
        using(var reader=command.ExecuteReader())
            while(reader.Read()){var code=reader.GetString(0);roads.Add(new(code,reader.GetString(1),reader.GetBoolean(2),reader.GetInt64(3),roadNodes.TryGetValue(code,out var list)?list.ToArray():[]));}
        foreach(var node in nodes)
        foreach(var position in LogicalNodePositionCalculator.Calculate(node,roads))WriteLogicalNodePosition(connection,transaction,position,positionsTable);
    }
}
