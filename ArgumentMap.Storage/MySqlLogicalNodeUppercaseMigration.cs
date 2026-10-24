using System.Globalization;
using System.Text.Json;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeUppercaseMigrationPending=1901;
    public string? LogicalNodeUppercaseBackupPath {get;private set;}

    private void UpgradeLogicalNodeUppercaseValues(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        using var check=connection.CreateCommand();
        check.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME='CK_LN_Value12Upper' AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
        check.Parameters.AddWithValue("@db",DatabaseName);var hasCheck=Convert.ToInt32(check.ExecuteScalar())==1;
        if(current is 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35 or 36 or 37 or 38)
        {
            if(!hasCheck)throw new InvalidOperationException("缺少边长 12 的 LN 值大写约束，数据库架构不完整。");
            return;
        }
        if(current is not (19 or LogicalNodeUppercaseMigrationPending))throw new InvalidOperationException("LN 值大写架构版本不兼容。");
        // The shared schema lease excludes every app writer. DML and its recovery marker
        // commit together; MySQL's atomic DDL is installed before publishing version 20.
        BackupLogicalNodeMigration(connection);
        using(var transaction=connection.BeginTransaction())
        try
        {
            var rows=new List<(string Code,string Value,string Upper,long Version)>();
            using(var read=Command(connection,transaction,"SELECT Code,`Value`,UPPER(`Value`),Version FROM LogicalNodes WHERE SideLength=12 ORDER BY Code FOR UPDATE"))
            using(var reader=read.ExecuteReader())
                while(reader.Read())
                {
                    var value=reader.GetString(1);var upper=LogicalNodeValueUniqueness.Normalize(value);
                    if(upper!=reader.GetString(2))throw new InvalidOperationException($"LN {reader.GetString(0)} 的字符大写映射与数据库不一致，升级未提交。");
                    rows.Add((reader.GetString(0),value,upper,reader.GetInt64(3)));
                }
            // Reserve every uppercase original before assigning suffixes, so final
            // values cannot collide with an existing row during sequential SQL updates.
            var occupied=rows.Select(n=>n.Upper).ToHashSet(StringComparer.Ordinal);
            var repairs=new List<LogicalNodeUppercaseRepair>();
            foreach(var group in rows.GroupBy(n=>n.Upper,StringComparer.Ordinal))
            {
                var duplicate=group.Count()>1;long suffix=0;
                foreach(var row in group)
                {
                    var value=row.Upper;
                    if(duplicate)
                        do
                        {
                            var number=(++suffix).ToString(CultureInfo.InvariantCulture);
                            value=LogicalNodeValuePrefix(row.Upper,32-number.Length)+number;
                        }while(!occupied.Add(value));
                    if(value!=row.Value)repairs.Add(new(row.Code,row.Value,value,row.Version,checked(row.Version+1)));
                }
            }
            Directory.CreateDirectory(ProjectPaths.BackupDirectory);
            LogicalNodeUppercaseBackupPath=Path.Combine(ProjectPaths.BackupDirectory,$"ln12-uppercase-values-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
            File.WriteAllText(LogicalNodeUppercaseBackupPath,JsonSerializer.Serialize(new
            {Database=DatabaseName,FromVersion=current,TargetVersion=20,Kind="RepairPlanBeforeCommit",FullBackup=LogicalNodeMigrationBackupPath,Repairs=repairs},new JsonSerializerOptions{WriteIndented=true}));
            Execute(connection,transaction,"UPDATE MapMetadata SET SchemaVersion=1901 WHERE Id=1");
            foreach(var repair in repairs)
                Execute(connection,transaction,"UPDATE LogicalNodes SET `Value`=@value,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                    ("@value",repair.NewValue),("@version",repair.NewVersion),("@code",repair.Code));
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        if(!hasCheck)MigrationExecute(connection,"ALTER TABLE LogicalNodes ADD CONSTRAINT CK_LN_Value12Upper CHECK (SideLength<>12 OR CONVERT(`Value` USING binary)=CONVERT(UPPER(`Value`) USING binary))");
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=20 WHERE Id=1 AND SchemaVersion=1901");
    }
    private sealed record LogicalNodeUppercaseRepair(string Code,string OldValue,string NewValue,long OldVersion,long NewVersion);
}
