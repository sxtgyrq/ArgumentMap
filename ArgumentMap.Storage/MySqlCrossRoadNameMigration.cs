using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    public string? CrossRoadNamingBackupPath { get; private set; }
    private static readonly (string Old,string New)[] CrossRoadColumnNames =
    [
        ("Line1Code","RoadCode1"),("Line2Code","RoadCode2"),
        ("Segment1","RoadOrder1"),("Segment2","RoadOrder2"),
        ("Line1Version","RoadVersion1"),("Line2Version","RoadVersion2")
    ];
    private void UpgradeCrossRoadNames(MySqlConnection connection)
    {
        var requiresMigration=false;
        foreach(var (oldName,newName) in CrossRoadColumnNames)
        {
            var oldExists=ColumnExists(connection,"Crosses",oldName);var newExists=ColumnExists(connection,"Crosses",newName);
            if(oldExists==newExists)throw new InvalidOperationException($"Cross 字段 {oldName}/{newName} 的架构不兼容。");
            requiresMigration|=oldExists;
        }
        if(requiresMigration)
        {
            // Same schema lease as all business transactions. CHECK recreation and foreign-key
            // column renames cannot share MySQL's required algorithms, so use an isolated copy.
            BackupLogicalNodeMigration(connection);
            CrossRoadNamingBackupPath=LogicalNodeMigrationBackupPath;
            MigrationExecute(connection,"DROP TABLE IF EXISTS Crosses_RoadNamesV15");
            RunSchema(connection,"Schema.CodeV15.sql",sql=>sql.Replace("CREATE TABLE IF NOT EXISTS Crosses (","CREATE TABLE IF NOT EXISTS Crosses_RoadNamesV15 (").Replace("AM9_","AM15_"));
            var newColumns=new[]{"RoadCode1","RoadCode2","X","Y","RoadOrder1","RoadOrder2","Percent1","Percent2","RoadVersion1","RoadVersion2","CrossState","Version","IsChanged","UpdatedAt"};
            var sources=newColumns.Select(n=>CrossRoadColumnNames.FirstOrDefault(c=>c.New==n) is var pair && pair.Old is not null && ColumnExists(connection,"Crosses",pair.Old)?pair.Old:n).ToArray();
            using(var transaction=connection.BeginTransaction())
            try
            {
                Execute(connection,transaction,"INSERT INTO Crosses_RoadNamesV15 ("+string.Join(',',newColumns.Select(n=>$"`{n}`"))+") SELECT "+string.Join(',',sources.Select(n=>$"`{n}`"))+" FROM Crosses");
                transaction.Commit();
            }
            catch{transaction.Rollback();throw;}
            // Both table names change in one atomic DDL publication. The lease still excludes
            // old v14 clients until the version write succeeds or a later startup resumes it.
            MigrationExecute(connection,"RENAME TABLE Crosses TO Crosses_RoadFieldsV14, Crosses_RoadNamesV15 TO Crosses");
        }
        // Retry after table publication succeeded but version publication failed.
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=15 WHERE Id=1 AND SchemaVersion=14");
        MigrationExecute(connection,"DROP TABLE IF EXISTS Crosses_RoadFieldsV14");
    }
}
