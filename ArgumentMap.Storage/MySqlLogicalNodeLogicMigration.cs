using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private void UpgradeLogicalNodeLogicLanguage(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        if(current is not (16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31))throw new InvalidOperationException("逻辑语言架构版本不兼容。");
        var exists=ColumnExists(connection,"LogicalNodes","LogicLanguage");
        if(current>=17)
        {
            if(!exists)throw new InvalidOperationException("缺少 LN 逻辑语言字段。");
            return;
        }
        if(!exists)
        {
            BackupLogicalNodeMigration(connection);
            // MySQL's atomic ALTER commits separately. Keep version 16 closed to v17
            // writers until publication; a failed publication can safely retry this step.
            MigrationExecute(connection,"ALTER TABLE LogicalNodes ADD COLUMN LogicLanguage VARCHAR(255) NOT NULL DEFAULT '' COMMENT '逻辑语言／命题表达式' AFTER Info");
        }
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=17 WHERE Id=1 AND SchemaVersion=16");
    }
}
