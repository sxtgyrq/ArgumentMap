using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private void UpgradeLogicalNodeStart(MySqlConnection connection)
    {
        // The existing schema lease excludes application writers. One atomic ALTER
        // replaces the shape check and installs the generated unique start slot.
        using var check=connection.CreateCommand();
        check.CommandText="SELECT CONSTRAINT_NAME FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME IN ('CK_LN_V12','CK_LN_V13')";
        check.Parameters.AddWithValue("@db",DatabaseName);
        var names=new List<string>();
        using(var reader=check.ExecuteReader())while(reader.Read())names.Add(reader.GetString(0));
        // A fresh or upgraded v14 table already has the start slot and its geometry check.
        if(ColumnExists(connection,"LogicalNodes","Value") && !ColumnExists(connection,"LogicalNodes","Name"))
        {
            MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=13 WHERE Id=1 AND SchemaVersion=12");
            return;
        }
        if(!names.Contains("CK_LN_V13"))
        {
            BackupLogicalNodeMigration(connection);
            var clauses=new List<string>();
            if(names.Contains("CK_LN_V12"))clauses.Add("DROP CHECK CK_LN_V12");
            if(!ColumnExists(connection,"LogicalNodes","StartSlot"))
                clauses.Add("ADD COLUMN StartSlot TINYINT GENERATED ALWAYS AS (CASE WHEN SideLength=18 THEN 1 ELSE NULL END) STORED, ADD UNIQUE KEY UQ_LN_Start (StartSlot)");
            clauses.Add("ADD CONSTRAINT CK_LN_V13 CHECK (REGEXP_LIKE(Code, '^[A-Z]{10}$', 'c') AND CHAR_LENGTH(TRIM(Owner))>0 AND Version>=0 AND MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (6,12,18) AND X-SideLength/2>=0 AND Y-SideLength/2>=0 AND X+SideLength/2<=500000 AND Y+SideLength/2<=500000 AND Weight BETWEEN 0 AND 99 AND NodeType>=0)");
            MigrationExecute(connection,"ALTER TABLE LogicalNodes "+string.Join(", ",clauses));
        }
        // If publication failed after ALTER, initialization retries this version write.
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=13 WHERE Id=1 AND SchemaVersion=12");
    }
}
