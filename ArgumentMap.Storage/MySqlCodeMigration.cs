using System.Data;
using System.Text.Json;
using ArgumentMap.Contracts;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    public string? MigrationBackupPath { get; private set; }
    private IDisposable AcquireSchemaLease(MySqlConnection connection)
    {
        var name = DatabaseName + ":Schema";
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT GET_LOCK(@name,15)"; command.Parameters.AddWithValue("@name", name);
        if (Convert.ToInt32(command.ExecuteScalar()) != 1) throw new InvalidOperationException("数据库正在更新，请稍后重试。");
        return new SchemaLease(connection, name);
    }

    private sealed class SchemaLease(MySqlConnection connection, string name) : IDisposable
    {
        public void Dispose()
        {
            if (connection.State != ConnectionState.Open) return;
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT RELEASE_LOCK(@name)"; command.Parameters.AddWithValue("@name", name);
            command.ExecuteScalar();
        }
    }

    private static void RunSchema(MySqlConnection connection, string resource, Func<string, string>? transform = null)
    {
        using var stream = typeof(MySqlLineRepository).Assembly.GetManifestResourceStream("ArgumentMap.Storage." + resource)
            ?? throw new InvalidOperationException("缺少数据库建表脚本。");
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        if (transform is not null) sql = transform(sql);
        foreach (var statement in sql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        { using var command = connection.CreateCommand(); command.CommandText = statement; command.ExecuteNonQuery(); }
    }

    private bool TableExists(MySqlConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db AND TABLE_NAME=@table";
        command.Parameters.AddWithValue("@db", DatabaseName); command.Parameters.AddWithValue("@table", table);
        return Convert.ToInt32(command.ExecuteScalar()) != 0;
    }

    private static void DropMigrationTables(MySqlConnection connection, string suffix)
    {
        foreach (var table in new[] { "Crosses", "LineNodes", "Lines", "MapMetadata" })
        { using var command = connection.CreateCommand(); command.CommandText = $"DROP TABLE IF EXISTS `{table}{suffix}`"; command.ExecuteNonQuery(); }
    }

    private void UpgradeCodeSchema(MySqlConnection connection)
    {
        if (ColumnExists(connection, "Lines", "LineCode"))
        {
            // Supports retry after publication, and older property migrations on already coded tables.
            using var version = connection.CreateCommand(); version.CommandText = "UPDATE MapMetadata SET SchemaVersion=9 WHERE Id=1"; version.ExecuteNonQuery();
            DropMigrationTables(connection, "_NumericV8");
            return;
        }
        DropMigrationTables(connection, "_CodeV9");
        RunSchema(connection, "Schema.CodeV14.sql", sql => sql
            .Replace("CREATE TABLE IF NOT EXISTS MapMetadata", "CREATE TABLE IF NOT EXISTS MapMetadata_CodeV9")
            .Replace("CREATE TABLE IF NOT EXISTS `Lines`", "CREATE TABLE IF NOT EXISTS `Lines_CodeV9`")
            .Replace("CREATE TABLE IF NOT EXISTS LineNodes", "CREATE TABLE IF NOT EXISTS LineNodes_CodeV9")
            .Replace("CREATE TABLE IF NOT EXISTS Crosses", "CREATE TABLE IF NOT EXISTS Crosses_CodeV9")
            .Replace("REFERENCES `Lines`", "REFERENCES `Lines_CodeV9`")
            .Replace("INSERT IGNORE INTO MapMetadata", "INSERT IGNORE INTO MapMetadata_CodeV9"));

        // Separate temporary tables avoid MySQL's restriction on re-opening one temp table twice.
        foreach (var name in new[] { "CodeMap1", "CodeMap2" })
        {
            using var create = connection.CreateCommand();
            create.CommandText = $"CREATE TEMPORARY TABLE {name} (NumericId INT PRIMARY KEY, LineCode VARCHAR(10) CHARACTER SET ascii COLLATE ascii_bin NOT NULL UNIQUE) ENGINE=InnoDB";
            create.ExecuteNonQuery();
        }
        // Keep native table locks through DML commit and atomic DDL publication. These also
        // block schema-8 clients which do not yet acquire the new schema advisory lease.
        MigrationExecute(connection, "SET autocommit=0");
        var locked = false;
        try
        {
            MigrationExecute(connection, "LOCK TABLES `Lines` WRITE, `Lines` AS l WRITE, LineNodes WRITE, LineNodes AS n WRITE, " +
                "Crosses WRITE, Crosses AS c WRITE, MapMetadata WRITE, Lines_CodeV9 WRITE, LineNodes_CodeV9 WRITE, Crosses_CodeV9 WRITE, MapMetadata_CodeV9 WRITE");
            locked = true;
                using (var gate = MigrationCommand(connection, "SELECT Id FROM MapMetadata WHERE Id=1 FOR UPDATE")) gate.ExecuteScalar();
                var ids = new List<int>();
                using (var select = MigrationCommand(connection, "SELECT Id FROM `Lines` ORDER BY Id"))
                using (var reader = select.ExecuteReader()) while (reader.Read()) ids.Add(reader.GetInt32(0));
                var codes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var id in ids)
                {
                    var code = LineCode.Generate(); while (!codes.Add(code)) code = LineCode.Generate();
                    foreach (var name in new[] { "CodeMap1", "CodeMap2" })
                        MigrationExecute(connection, $"INSERT INTO {name} VALUES (@id,@code)", ("@id", id), ("@code", code));
                }
                BackupNumericMap(connection);
                MigrationExecute(connection,
                    "INSERT INTO Lines_CodeV9 (LineCode,Owner,IsLocked,IsChanged,Version,RoadName,RoadDetail,Speed,PassInDirection,PassInOpposeDirection,UpdatedAt) " +
                    "SELECT m.LineCode,l.Owner,l.IsLocked,l.IsChanged,l.Version,l.RoadName,l.RoadDetail,l.Speed,l.PassInDirection,l.PassInOpposeDirection,l.UpdatedAt FROM `Lines` l JOIN CodeMap1 m ON m.NumericId=l.Id");
                MigrationExecute(connection,
                    "INSERT INTO LineNodes_CodeV9 (LineCode,NodeOrder,X,Y,IsChanged) SELECT m.LineCode,n.NodeOrder,n.X,n.Y,n.IsChanged FROM LineNodes n JOIN CodeMap1 m ON m.NumericId=n.LineId");
                MigrationExecute(connection,
                    "INSERT INTO Crosses_CodeV9 (Line1Code,Line2Code,X,Y,Segment1,Segment2,Percent1,Percent2,Line1Version,Line2Version,CrossState,Version,IsChanged,UpdatedAt) " +
                    "SELECT GREATEST(a.LineCode,b.LineCode),LEAST(a.LineCode,b.LineCode),c.X,c.Y," +
                    "IF(a.LineCode>b.LineCode,c.Segment1,c.Segment2),IF(a.LineCode>b.LineCode,c.Segment2,c.Segment1)," +
                    "IF(a.LineCode>b.LineCode,c.Percent1,c.Percent2),IF(a.LineCode>b.LineCode,c.Percent2,c.Percent1)," +
                    "IF(a.LineCode>b.LineCode,c.Line1Version,c.Line2Version),IF(a.LineCode>b.LineCode,c.Line2Version,c.Line1Version),c.CrossState,c.Version,c.IsChanged,c.UpdatedAt " +
                    "FROM Crosses c JOIN CodeMap1 a ON a.NumericId=c.Line1Id JOIN CodeMap2 b ON b.NumericId=c.Line2Id");
                foreach (var table in new[] { "Lines", "LineNodes", "Crosses" })
                {
                    using var verify = MigrationCommand(connection, $"SELECT (SELECT COUNT(*) FROM `{table}`)=(SELECT COUNT(*) FROM `{table}_CodeV9`)");
                    if (Convert.ToInt32(verify.ExecuteScalar()) != 1) throw new InvalidOperationException("字母编码迁移校验失败，原表未修改。");
                }
            MigrationExecute(connection, "COMMIT");
        // MySQL DDL implicitly commits. Publish all four validated tables with ONE atomic RENAME;
        // never pretend DDL can be rolled back as normal DML. Schema leases exclude other writers.
        using (var publish = connection.CreateCommand())
        {
            publish.CommandText = "RENAME TABLE `Lines` TO Lines_NumericV8, Lines_CodeV9 TO `Lines`, " +
                "LineNodes TO LineNodes_NumericV8, LineNodes_CodeV9 TO LineNodes, Crosses TO Crosses_NumericV8, Crosses_CodeV9 TO Crosses, " +
                "MapMetadata TO MapMetadata_NumericV8, MapMetadata_CodeV9 TO MapMetadata";
            publish.ExecuteNonQuery();
        }
        // If interrupted here, the coded schema is already published; the next startup cleans old tables.
        DropMigrationTables(connection, "_NumericV8");
        }
        catch
        {
            try { MigrationExecute(connection, "ROLLBACK"); } catch (MySqlException) { }
            throw;
        }
        finally
        {
            if (connection.State == ConnectionState.Open)
            {
                if (locked) MigrationExecute(connection, "UNLOCK TABLES");
                MigrationExecute(connection, "SET autocommit=1");
            }
        }
    }

    private static MySqlCommand MigrationCommand(MySqlConnection connection, string sql)
    {
        var command = connection.CreateCommand(); command.CommandText = sql; return command;
    }

    private void BackupNumericMap(MySqlConnection connection)
    {
        var tables = new Dictionary<string, List<Dictionary<string, object?>>>();
        foreach (var table in new[] { "Lines", "LineNodes", "Crosses", "MapMetadata", "CodeMap1" })
        {
            var rows = new List<Dictionary<string, object?>>();
            using var command = MigrationCommand(connection, $"SELECT * FROM `{table}`");
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var row = new Dictionary<string, object?>();
                for (var i = 0; i < reader.FieldCount; i++) row.Add(reader.GetName(i),reader.IsDBNull(i) ? null : reader.GetValue(i));
                rows.Add(row);
            }
            tables.Add(table,rows);
        }
        var directory = ProjectPaths.BackupDirectory; Directory.CreateDirectory(directory);
        MigrationBackupPath = Path.Combine(directory,$"before-letter-codes-{DatabaseName}-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json");
        // No credentials or login tokens are exported. This snapshot and the old-ID/code mapping
        // are captured under native locks before changing the live table names.
        File.WriteAllText(MigrationBackupPath,JsonSerializer.Serialize(new { Database = DatabaseName, Tables = tables }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void MigrationExecute(MySqlConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = MigrationCommand(connection, sql);
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name,value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
}
