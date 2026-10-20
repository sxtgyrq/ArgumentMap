using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private void UpgradeLogicalNodeMetadata(MySqlConnection connection)
    {
        var changes=new List<string>();
        if(ColumnExists(connection,"LogicalNodes","Name"))changes.Add("CHANGE COLUMN Name `Value` VARCHAR(32) NOT NULL DEFAULT ''");
        foreach(var column in new[]{"NodeType","Weight"})
            if(ColumnExists(connection,"LogicalNodes",column))changes.Add($"DROP COLUMN `{column}`");
        if(changes.Count>0)
        {
            BackupLogicalNodeMigration(connection);
            using var check=connection.CreateCommand();
            check.CommandText="SELECT CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_TYPE='CHECK'";
            check.Parameters.AddWithValue("@db",DatabaseName);
            var checks=new List<string>();using(var reader=check.ExecuteReader())while(reader.Read())checks.Add(reader.GetString(0));
            changes.InsertRange(0,checks.Select(name=>$"DROP CHECK `{name.Replace("`","``")}`"));
            changes.Add("ADD CONSTRAINT CK_LN_V14 CHECK (REGEXP_LIKE(Code, '^[A-Z]{10}$', 'c') AND CHAR_LENGTH(TRIM(Owner))>0 AND Version>=0 AND MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (6,12,18) AND X-SideLength/2>=0 AND Y-SideLength/2>=0 AND X+SideLength/2<=500000 AND Y+SideLength/2<=500000)");
            // MySQL DDL commits implicitly. One atomic ALTER preserves identities and positions;
            // the shared schema lease excludes all application transactions during migration.
            MigrationExecute(connection,"ALTER TABLE LogicalNodes "+string.Join(", ",changes));
        }
        // A failure here leaves v13 closed to new writers, and is retryable after the atomic ALTER.
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=14 WHERE Id=1 AND SchemaVersion=13");
    }
}
