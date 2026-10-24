using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private void UpgradeSmallLogicalNodes(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        if(current is 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38)return;
        if(current!=17)throw new InvalidOperationException("LN 边长架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        // One atomic ALTER broadens the existing geometry check. Version publication is
        // separate because MySQL DDL commits implicitly; interrupted publication is retryable.
        MigrationExecute(connection,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_V14, ADD CONSTRAINT CK_LN_V14 CHECK (REGEXP_LIKE(Code, '^[A-Z]{10}$', 'c') AND CHAR_LENGTH(TRIM(Owner))>0 AND Version>=0 AND MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (4,6,8,10,12,18) AND X-SideLength/2>=0 AND Y-SideLength/2>=0 AND X+SideLength/2<=500000 AND Y+SideLength/2<=500000)");
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=18 WHERE Id=1 AND SchemaVersion=17");
    }
}
