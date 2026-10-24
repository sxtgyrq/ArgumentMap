using MySqlConnector;
using ArgumentMap.Contracts;

namespace ArgumentMap.Storage;

public sealed partial class MySqlLineRepository
{
    public string? PercentDecimalBackupPath { get; private set; }

    // Retain the round-trip text of the old double calculation. A direct C# double -> decimal
    // cast retains only about 15 digits; a floating SQL parameter can introduce another conversion.
    private static decimal PercentDecimal(double value) => PercentPrecision.ToStorage(value);
    private static double ReadPercent(MySqlDataReader reader, int ordinal) => PercentPrecision.FromStorage(reader.GetDecimal(ordinal));

    private void UpgradePercentDecimals(MySqlConnection connection)
    {
        using var version = connection.CreateCommand();
        version.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
        var current = Convert.ToInt32(version.ExecuteScalar());
        if (current is not (15 or 16 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 or 25 or 26 or 27 or 28 or 29 or 30 or 31 or 32 or 33 or 34 or 35)) throw new InvalidOperationException("位置比例架构版本不兼容。");
        using var check = connection.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db " +
            "AND ((TABLE_NAME='Crosses' AND COLUMN_NAME IN ('Percent1','Percent2')) OR (TABLE_NAME='LogicalNodePositions' AND COLUMN_NAME='RoadPercent')) " +
            "AND DATA_TYPE='decimal' AND NUMERIC_PRECISION=20 AND NUMERIC_SCALE=18 AND IS_NULLABLE='NO'";
        check.Parameters.AddWithValue("@db", DatabaseName);
        var ready = Convert.ToInt32(check.ExecuteScalar()) == 3;
        if (current >= 16 && !ready) throw new InvalidOperationException("位置比例字段必须为 DECIMAL(20,18)。");
        if (!ready)
        {
            // InitializeSchema holds the same advisory lease as every business transaction.
            // MySQL DDL implicitly commits, so copy both tables in one transaction and publish
            // both with one atomic RENAME, rather than claiming two ALTERs can roll back together.
            BackupLogicalNodeMigration(connection);
            PercentDecimalBackupPath = LogicalNodeMigrationBackupPath;
            MigrationExecute(connection, "DROP TABLE IF EXISTS LogicalNodePositions_DecimalV16, Crosses_DecimalV16");
            RunSchema(connection, "Schema.sql", sql => sql
                .Replace("CREATE TABLE IF NOT EXISTS Crosses (", "CREATE TABLE IF NOT EXISTS Crosses_DecimalV16 (")
                .Replace("AM9_", "AM16_"));
            RunSchema(connection, "Schema.LogicalNodePositions.sql", sql => sql
                .Replace("LogicalNodePositions (", "LogicalNodePositions_DecimalV16 (")
                .Replace("_V12", "_V16"));
            using (var transaction = connection.BeginTransaction())
            try
            {
                Execute(connection, transaction, "INSERT INTO Crosses_DecimalV16 (RoadCode1,RoadCode2,X,Y,RoadOrder1,RoadOrder2,Percent1,Percent2,RoadVersion1,RoadVersion2,CrossState,Version,IsChanged,UpdatedAt) " +
                    "SELECT RoadCode1,RoadCode2,X,Y,RoadOrder1,RoadOrder2,CAST(Percent1 AS CHAR),CAST(Percent2 AS CHAR),RoadVersion1,RoadVersion2,CrossState,Version,IsChanged,UpdatedAt FROM Crosses");
                Execute(connection, transaction, "INSERT INTO LogicalNodePositions_DecimalV16 (LogicalNodeCode,RoadCode,RoadOrder,RoadPercent,X,Y,RoadVersion,IsChanged,UpdatedAt) " +
                    "SELECT LogicalNodeCode,RoadCode,RoadOrder,CAST(RoadPercent AS CHAR),X,Y,RoadVersion,IsChanged,UpdatedAt FROM LogicalNodePositions");
                transaction.Commit();
            }
            catch { transaction.Rollback(); throw; }
            MigrationExecute(connection, "RENAME TABLE Crosses TO Crosses_DoubleV15, Crosses_DecimalV16 TO Crosses, " +
                "LogicalNodePositions TO LogicalNodePositions_DoubleV15, LogicalNodePositions_DecimalV16 TO LogicalNodePositions");
        }
        // A failed version publication leaves new application writers closed until this retry.
        MigrationExecute(connection, "UPDATE MapMetadata SET SchemaVersion=16 WHERE Id=1 AND SchemaVersion=15");
        MigrationExecute(connection, "DROP TABLE IF EXISTS LogicalNodePositions_DoubleV15, Crosses_DoubleV15");
    }
}
