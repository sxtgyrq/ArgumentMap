using System.Text.Json;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    private const int LogicalNodeInstanceAttributesPending=3101; // Resume an interrupted v32 upgrade.
    private const int LogicalNodeInstanceAttributeSetsPending=3201;
    private const int LogicalNodeInstanceAliasPending=3301;
    private const int LogicalNodeInstanceConjunctionPending=3401;
    private const string InstanceValueCheck="CK_LN_InstanceValue CHECK (SideLength<>10 OR CONVERT(SUBSTRING_INDEX(`Value`,'(',1) USING binary)=CONVERT(LOWER(SUBSTRING_INDEX(`Value`,'(',1)) USING binary))";
    private const string InstanceLogicCheck="CK_LN_InstanceLogic CHECK (SideLength<>10 OR LOCATE(')',`Value`)<CHAR_LENGTH(`Value`) OR CONVERT(LEFT(LogicLanguage,CHAR_LENGTH(CONCAT(`Value`,'∈',UPPER(SUBSTRING_INDEX(`Value`,'(',1))))) USING binary)=CONVERT(CONCAT(`Value`,'∈',UPPER(SUBSTRING_INDEX(`Value`,'(',1))) USING binary))";
    private const string InstanceAttributesCheck="CK_LN_InstanceAttributes CHECK (SideLength<>10 OR (REGEXP_LIKE(`Value`,'^[^()]+[(]([^()=,]+=[^(),]+(,[^()=,]+=[^(),]+)*)?[)]([[:space:]]+[^()[:space:]][^()]*)?$','c') AND REGEXP_LIKE(SUBSTRING_INDEX(`Value`,'(',1),'[^[:space:]]','c')))";
    private const string InstanceLengthCheck="CK_LN_ValueLength CHECK (CHAR_LENGTH(`Value`)<=CASE WHEN SideLength=10 THEN 220 ELSE 32 END)";
    private const string InstanceLogicLengthCheck="CK_LN_LogicLength CHECK (CHAR_LENGTH(LogicLanguage)<=CASE WHEN SideLength=10 THEN 4096 ELSE 255 END)";
    private static readonly string[] InstanceAttributeTriggers=["TR_LN10_Attributes_Insert","TR_LN10_Attributes_Update"];
    public string? LogicalNodeInstanceAttributeBackupPath {get;private set;}

    // CHECK expressions cannot iterate arbitrary attribute lists in MySQL. These
    // guards validate the complete canonical expression on every SQL write.
    private static string InstanceAttributeTrigger(string name,string action)=>$$"""
        CREATE TRIGGER {{name}} BEFORE {{action}} ON LogicalNodes FOR EACH ROW
        BEGIN
            DECLARE parentValue VARCHAR(220) CHARACTER SET utf8mb4;
            DECLARE remainingValue VARCHAR(220) CHARACTER SET utf8mb4;
            DECLARE attributePair VARCHAR(220) CHARACTER SET utf8mb4;
            DECLARE attributeKey VARCHAR(220) CHARACTER SET utf8mb4;
            DECLARE attributeValue VARCHAR(220) CHARACTER SET utf8mb4;
            DECLARE expectedLogic LONGTEXT CHARACTER SET utf8mb4;
            DECLARE aliasValue VARCHAR(220) CHARACTER SET utf8mb4;
            DECLARE closeAt INT;
            DECLARE separatorAt INT;
            DECLARE equalsAt INT;
            IF NEW.SideLength=10 THEN
                IF NOT REGEXP_LIKE(NEW.`Value`,'^[^()]+[(][^()]*[)]([[:space:]]+[^()[:space:]][^()]*)?$','c') THEN
                    SIGNAL SQLSTATE 'HY000' SET MYSQL_ERRNO=3819, MESSAGE_TEXT='Invalid LN10 parentheses';
                END IF;
                SET parentValue=UPPER(SUBSTRING_INDEX(NEW.`Value`,'(',1));
                SET closeAt=LOCATE(')',NEW.`Value`);
                SET aliasValue=REGEXP_REPLACE(SUBSTRING(NEW.`Value`,closeAt+1),'^[[:space:]]+|[[:space:]]+$','');
                SET remainingValue=SUBSTRING(NEW.`Value`,LOCATE('(',NEW.`Value`)+1,closeAt-LOCATE('(',NEW.`Value`)-1);
                SET expectedLogic=CONCAT(IF(closeAt=CHAR_LENGTH(NEW.`Value`),NEW.`Value`,aliasValue),'∈',parentValue);
                IF LEFT(remainingValue,1)=',' OR RIGHT(remainingValue,1)=',' THEN
                    SIGNAL SQLSTATE 'HY000' SET MYSQL_ERRNO=3819, MESSAGE_TEXT='Empty LN10 attribute';
                END IF;
                WHILE CHAR_LENGTH(remainingValue)>0 DO
                    SET separatorAt=LOCATE(',',remainingValue);
                    SET attributePair=IF(separatorAt=0,remainingValue,LEFT(remainingValue,separatorAt-1));
                    SET equalsAt=LOCATE('=',attributePair);
                    SET attributeKey=LEFT(attributePair,GREATEST(equalsAt-1,0));
                    SET attributeValue=SUBSTRING(attributePair,equalsAt+1);
                    IF equalsAt<2 OR NOT REGEXP_LIKE(attributeKey,'[^[:space:]]','c') OR NOT REGEXP_LIKE(attributeValue,'[^[:space:]]','c') THEN
                        SIGNAL SQLSTATE 'HY000' SET MYSQL_ERRNO=3819, MESSAGE_TEXT='Invalid LN10 attribute assignment';
                    END IF;
                    SET expectedLogic=CONCAT(expectedLogic,'∧',attributeValue,'∈',parentValue,'.',UPPER(attributeKey));
                    SET remainingValue=IF(separatorAt=0,'',SUBSTRING(remainingValue,separatorAt+1));
                END WHILE;
                IF CONVERT(NEW.LogicLanguage USING binary)<>CONVERT(expectedLogic USING binary) THEN
                    SIGNAL SQLSTATE 'HY000' SET MYSQL_ERRNO=3819, MESSAGE_TEXT='LN10 logic must match instance and attribute memberships';
                END IF;
            END IF;
        END
        """;

    private void UpgradeLogicalNodeInstanceAttributes(MySqlConnection connection)
    {
        using var version=connection.CreateCommand();version.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current=Convert.ToInt32(version.ExecuteScalar());
        if(current is not(31 or 32 or 33 or 34 or 35 or LogicalNodeInstanceAttributesPending or LogicalNodeInstanceAttributeSetsPending or LogicalNodeInstanceAliasPending or LogicalNodeInstanceConjunctionPending))throw new InvalidOperationException("LN10 括号属性架构版本不兼容。");
        var definitions=new[]{InstanceValueCheck,InstanceLogicCheck,InstanceAttributesCheck,InstanceLengthCheck,InstanceLogicLengthCheck};
        var names=definitions.Select(d=>d.Split(' ')[0]).ToArray();
        bool Exists(string name)
        {
            using var q=connection.CreateCommand();q.CommandText="SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND CONSTRAINT_NAME=@name AND CONSTRAINT_TYPE='CHECK' AND ENFORCED='YES'";
            q.Parameters.AddWithValue("@db",DatabaseName);q.Parameters.AddWithValue("@name",name);return Convert.ToInt32(q.ExecuteScalar())==1;
        }
        int Width(string column)
        {
            using var q=connection.CreateCommand();q.CommandText="SELECT CHARACTER_MAXIMUM_LENGTH FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND COLUMN_NAME=@column";
            q.Parameters.AddWithValue("@db",DatabaseName);q.Parameters.AddWithValue("@column",column);return Convert.ToInt32(q.ExecuteScalar());
        }
        if(current is 33 or 34 or 35)
        {
            using var q=connection.CreateCommand();q.CommandText="SELECT COUNT(*) FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@db AND EVENT_OBJECT_TABLE='LogicalNodes' AND ACTION_TIMING='BEFORE' AND ((TRIGGER_NAME='TR_LN10_Attributes_Insert' AND EVENT_MANIPULATION='INSERT') OR (TRIGGER_NAME='TR_LN10_Attributes_Update' AND EVENT_MANIPULATION='UPDATE'))";q.Parameters.AddWithValue("@db",DatabaseName);
            if(Convert.ToInt32(q.ExecuteScalar())!=2||!names.All(Exists)||Width("Value")!=LogicalNodeInstanceRules.ValueMaximum||Width("LogicLanguage")!=LogicalNodeInstanceRules.LogicMaximum)
                throw new InvalidOperationException("缺少 LN10 属性隶属表达式、长度约束或数据库触发器，数据库架构不完整。");
            if(current==35)
            {
                q.CommandText="SELECT ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=@db AND TRIGGER_NAME IN ('TR_LN10_Attributes_Insert','TR_LN10_Attributes_Update')";
                using var reader=q.ExecuteReader();
                while(reader.Read())
                    if(!string.Concat(reader.GetString(0).Where(c=>!char.IsWhiteSpace(c))).Contains("CONCAT(expectedLogic,'∧',attributeValue",StringComparison.Ordinal))
                        throw new InvalidOperationException("LN10 数据库触发器尚未使用逻辑合取 ∧，数据库架构不完整。");
                return;
            }
        }
        if(current==32&&(!names.Take(4).All(Exists)||Width("Value")!=220||Width("LogicLanguage")!=255))
            throw new InvalidOperationException("缺少旧 LN10 括号属性约束或字段宽度不正确，数据库架构不完整。");
        var parents=new HashSet<string>(StringComparer.Ordinal);
        using(var q=connection.CreateCommand())
        {q.CommandText="SELECT `Value` FROM LogicalNodes WHERE SideLength=12";using var r=q.ExecuteReader();while(r.Read())parents.Add(r.GetString(0));}
        string UpgradeValue(string value,string code,string logic)
        {
            // v31 values were entire class names, even if a class happened to contain
            // parentheses. Never silently reinterpret one as a v32 attribute instance.
            var oldBareLogic=value+" ∈ "+LogicalNodeValueUniqueness.Normalize(value);
            var legacyBare=logic==oldBareLogic&&oldBareLogic!=value+" ∈ "+LogicalNodeInstanceRules.ParentValue(value);
            if(!legacyBare&&LogicalNodeInstanceRules.ValidFormat(value))return value;
            if(legacyBare||!value.Contains('(')&&!value.Contains(')'))
            {
                var replacement=LogicalNodeInstanceRules.DefaultValue(LogicalNodeValueUniqueness.Normalize(value),code);
                if(LogicalNodeInstanceRules.ValidFormat(replacement))return replacement;
            }
            throw new InvalidOperationException($"旧 LN10 {code} 的括号属性格式无法安全升级，请先修复后重试；原数据保留。");
        }
        var repairs=new List<(string Code,string Value,string Logic,long Version)>();
        using(var q=connection.CreateCommand())
        {
            q.CommandText="SELECT Code,`Value`,LogicLanguage,Version FROM LogicalNodes WHERE SideLength=10 ORDER BY Code";
            using var r=q.ExecuteReader();while(r.Read())
            {
                var value=UpgradeValue(r.GetString(1),r.GetString(0),r.GetString(2));var logic=LogicalNodeInstanceRules.Expression(value);
                if(!parents.Contains(LogicalNodeInstanceRules.ParentValue(value)))throw new InvalidOperationException($"旧 LN10 {r.GetString(0)} 的所属 LN12 不存在，升级未写入。");
                if(value!=r.GetString(1)||logic!=r.GetString(2))repairs.Add((r.GetString(0),value,logic,checked(r.GetInt64(3)+1)));
            }
        }
        // Test actual trigger creation before writing data or publishing a pending marker.
        // A binlogged MySQL server may require additional administrator setup even
        // when this account already has database-scoped TRIGGER permission.
        const string probe="TR_LN10_UpgradePrivilegeProbe";
        var probeCreated=false;
        try
        {
            MigrationExecute(connection,"DROP TRIGGER IF EXISTS "+probe);
            MigrationExecute(connection,"CREATE TRIGGER "+probe+" BEFORE INSERT ON LogicalNodes FOR EACH ROW SET NEW.Code=NEW.Code");
            probeCreated=true;
        }
        catch(MySqlException error) when(error.Number==1419)
        {
            throw new InvalidOperationException("MySQL 开启了二进制日志，当前账号不能创建升级所需的触发器。请 MySQL 管理员临时执行 SET GLOBAL log_bin_trust_function_creators=ON，重启新版 Server 完成升级后再恢复 OFF；不需要给应用账号授予 SUPER。此次未写入升级数据。",error);
        }
        finally{if(probeCreated)MigrationExecute(connection,"DROP TRIGGER "+probe);}
        if(current is 31 or 32 or 33 or 34)BackupLogicalNodeMigration(connection);
        Directory.CreateDirectory(ProjectPaths.BackupDirectory);
        LogicalNodeInstanceAttributeBackupPath=Path.Combine(ProjectPaths.BackupDirectory,$"ln10-attributes-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        File.WriteAllText(LogicalNodeInstanceAttributeBackupPath,JsonSerializer.Serialize(new{Database=DatabaseName,FromVersion=current,TargetVersion=35,FullBackup=LogicalNodeMigrationBackupPath,Repairs=repairs.Select(r=>new{r.Code,r.Value,r.Logic,r.Version})},new JsonSerializerOptions{WriteIndented=true}));
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=3401 WHERE Id=1 AND SchemaVersion IN (31,32,33,34)");
        foreach(var trigger in InstanceAttributeTriggers)MigrationExecute(connection,"DROP TRIGGER IF EXISTS "+trigger);
        var ddl=names.Where(Exists).Select(n=>"DROP CHECK "+n).Concat(new[]{"MODIFY COLUMN `Value` VARCHAR(220) NOT NULL DEFAULT ''","MODIFY COLUMN LogicLanguage VARCHAR(4096) NOT NULL DEFAULT ''"});
        MigrationExecute(connection,"ALTER TABLE LogicalNodes "+string.Join(", ",ddl));
        using(var transaction=connection.BeginTransaction())
        try
        {
            foreach(var repair in repairs)Execute(connection,transaction,"UPDATE LogicalNodes SET `Value`=@value,LogicLanguage=@logic,Version=@version,UpdatedAt=UTC_TIMESTAMP(6) WHERE Code=@code",
                ("@value",repair.Value),("@logic",repair.Logic),("@version",repair.Version),("@code",repair.Code));
            var history=new List<(long Sequence,string Payload)>();
            using(var q=Command(connection,transaction,"SELECT Sequence,Payload FROM MapUndoHistory ORDER BY Sequence FOR UPDATE"))
            using(var r=q.ExecuteReader())while(r.Read())history.Add((r.GetInt64(0),r.GetString(1)));
            LogicalNodeSnapshot? Upgrade(LogicalNodeSnapshot? n)
            {
                if(n?.SideLength!=10)return n;
                var value=UpgradeValue(n.Properties.Value,n.Code,n.Properties.LogicLanguage);var logic=LogicalNodeInstanceRules.Expression(value);
                return n.Properties.Value==value&&n.Properties.LogicLanguage==logic?n:n with{Properties=n.Properties with{Value=value,LogicLanguage=logic}};
            }
            foreach(var row in history)
            {
                var payload=JsonSerializer.Deserialize<UndoPayload>(row.Payload)??throw new InvalidOperationException("撤销记录格式无效。");
                var nodes=payload.Delta.Nodes.Select(c=>new UndoNodeChange(Upgrade(c.Before),Upgrade(c.After))).ToArray();
                if(nodes.SequenceEqual(payload.Delta.Nodes))continue;
                Execute(connection,transaction,"UPDATE MapUndoHistory SET Payload=@payload WHERE Sequence=@sequence",
                    ("@payload",JsonSerializer.Serialize(payload with{Delta=payload.Delta with{Nodes=nodes}})),("@sequence",row.Sequence));
            }
            transaction.Commit();
        }
        catch{transaction.Rollback();throw;}
        MigrationExecute(connection,"ALTER TABLE LogicalNodes "+string.Join(", ",definitions.Select(d=>"ADD CONSTRAINT "+d)));
        MigrationExecute(connection,InstanceAttributeTrigger(InstanceAttributeTriggers[0],"INSERT"));
        MigrationExecute(connection,InstanceAttributeTrigger(InstanceAttributeTriggers[1],"UPDATE"));
        MigrationExecute(connection,"UPDATE MapMetadata SET SchemaVersion=35 WHERE Id=1 AND SchemaVersion IN (3101,3201,3301,3401)");
    }
}
