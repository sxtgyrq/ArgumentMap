using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private void UpgradeLogicalNodeRelations(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var check=connection.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_RelationValue' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var hasCheck=Convert.ToInt32(check.ExecuteScalar())==1;
        if(current is 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35)
        {if(!hasCheck)throw new InvalidOperationException("缺少 LN8 等同／蕴含值约束，数据库架构不完整。");return;}
        if(current!=23)throw new InvalidOperationException("LN8 架构版本不兼容。");
        BackupLogicalNodeMigration(connection);
        // Broaden geometry even when a restored/partially upgraded database already
        // has the value check. Retrying this atomic ALTER does not change any row.
        MigrationExecute(connection,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_V14, ADD CONSTRAINT CK_LN_V14 CHECK (REGEXP_LIKE(Code, '^[A-Z]{10}$', 'c') AND CHAR_LENGTH(TRIM(Owner))>0 AND Version>=0 AND MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (4,6,8,10,12,18) AND X-SideLength/2>=0 AND Y-SideLength/2>=0 AND X+SideLength/2<=500000 AND Y+SideLength/2<=500000)"+
            (hasCheck?"":", ADD CONSTRAINT CK_LN_RelationValue CHECK (SideLength<>8 OR CONVERT(`Value` USING binary) IN (X'3D',X'E28692',X'E28694',X'E28A87',X'E28A8B',X'E28A86',X'E28A8A'))"));
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=24 WHERE Id=1 AND SchemaVersion=23");
    }
}
