using System.IO;
using System.Globalization;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql")]
public sealed class PercentDecimalMySqlTests
{
    private static MySqlLineRepository Fixture(MySqlSettings settings)
    {
        var repository = new MySqlLineRepository(settings);
        Add(repository, 90, 110, 100, 110, 130, 110); Add(repository, 110, 90, 110, 140);
        var crosses = new CrossService(repository); while (crosses.Calculate().Data!.CanContinue) { }
        var cross = crosses.GetAll().Data!.Single(); Assert.IsTrue(crosses.Edit(new(cross.Key, "owner", cross.Version, CrossState.Road1ToRoad2)).Success);
        Assert.IsTrue(new LogicalNodeService(repository).Create(new("owner", 120.5m, 110.5m, 6, new() { Value = "中文位置" })).Success);
        return repository;
    }

    private static void DowngradeStorage(MySqlSettings settings) => Sql(settings,
        "ALTER TABLE Crosses MODIFY Percent1 DOUBLE NOT NULL DEFAULT 0, MODIFY Percent2 DOUBLE NOT NULL DEFAULT 0; " +
        "ALTER TABLE LogicalNodePositions MODIFY RoadPercent DOUBLE NOT NULL; UPDATE MapMetadata SET SchemaVersion=15 WHERE Id=1");

    private static object? Scalar(MySqlSettings settings, string sql)
    {
        using var connection = new MySqlConnection(settings.ConnectionString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar();
    }

    private static void AssertSchema(MySqlSettings settings)
    {
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion, Convert.ToInt32(Scalar(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1")));
        Assert.AreEqual(3, Convert.ToInt32(Scalar(settings,
            "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() " +
            "AND ((TABLE_NAME='Crosses' AND COLUMN_NAME IN ('Percent1','Percent2')) OR (TABLE_NAME='LogicalNodePositions' AND COLUMN_NAME='RoadPercent')) " +
            "AND DATA_TYPE='decimal' AND NUMERIC_PRECISION=20 AND NUMERIC_SCALE=18 AND IS_NULLABLE='NO'")));
    }

    [TestMethod]
    public Task CalculatedRepeatingAndSmallPercents_WriteDecimalParametersAndRespect18DigitScale() => Isolated(settings =>
    {
        var repository = Fixture(settings);
        var returned = new LogicalNodeService(repository).GetAll().Data!.Single().Positions.Single();
        Assert.AreEqual(0.6833333333333333m, Scalar(settings, "SELECT RoadPercent FROM LogicalNodePositions"));
        Assert.AreEqual(0.6833333333333333, returned.RoadPercent);
        var road = TestMap.HistoricalRoad(repository, 0, 2000, 499999, 2000); Add(repository, 10, 1990, 10, 2010);
        var crosses = new CrossService(repository); while (crosses.Calculate().Data!.CanContinue) { }
        Assert.IsTrue(new LogicalNodeService(repository).Create(new("owner", 3.5m, 2000.5m, 6, new())).Success);
        var position = new LogicalNodeService(repository).GetAll().Data!.Single(n => n.Y == 2000.5m).Positions.Single();
        Assert.AreEqual(decimal.Round(3.5m / 499999m, 18), Scalar(settings, "SELECT RoadPercent FROM LogicalNodePositions WHERE Y=2000"));
        Assert.AreEqual(3.5, position.RoadPercent * 499999, 3e-13, "18-decimal scale keeps the projected coordinate accurate even on long segments.");
        var cross = crosses.GetAll().Data!.Single(c => c.Key.Contains(road.Code));
        Assert.AreEqual(decimal.Round(10m / 499999m, 18), Scalar(settings, "SELECT " + (cross.Key.RoadCode1 == road.Code ? "Percent1" : "Percent2") + " FROM Crosses WHERE Y=2000"));
        Assert.AreEqual(10.0, cross.LocationOn(road.Code).Percent * 499999, 3e-13);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task FreshSchema_Stores18FractionalDigitsReadsThroughDoubleAndKeepsBounds() => Isolated(settings =>
    {
        var repository = Fixture(settings); AssertSchema(settings);
        Sql(settings, "UPDATE Crosses SET Percent1=0.123456789012345678,Percent2=0.999999999999999999; " +
            "UPDATE LogicalNodePositions SET RoadPercent=0.333333333333333333");
        Assert.AreEqual(0.123456789012345678m, Scalar(settings, "SELECT Percent1 FROM Crosses"));
        Assert.AreEqual(0.999999999999999999m, Scalar(settings, "SELECT Percent2 FROM Crosses"));
        Assert.AreEqual(0.333333333333333333m, Scalar(settings, "SELECT RoadPercent FROM LogicalNodePositions"));
        var reopened = new MySqlLineRepository(settings);
        var cross = new CrossService(reopened).GetAll().Data!.Single(); Assert.AreEqual(double.Parse("0.123456789012345678",CultureInfo.InvariantCulture), cross.Percent1);
        Assert.AreEqual(double.Parse("0.333333333333333333",CultureInfo.InvariantCulture), new LogicalNodeService(reopened).GetAll().Data!.Single().Positions.Single().RoadPercent);
        foreach (var column in new[] { "Percent1", "Percent2", "RoadPercent" })
        {
            var table = column == "RoadPercent" ? "LogicalNodePositions" : "Crosses";
            foreach (var invalid in new[] { "-0.000000000000000001", "1.000000000000000001" })
                Assert.ThrowsExactly<MySqlException>(() => Sql(settings, $"UPDATE {table} SET {column}={invalid}"));
            Sql(settings, $"UPDATE {table} SET {column}=0"); Assert.AreEqual(0m, Scalar(settings, $"SELECT {column} FROM {table}"));
            Sql(settings, $"UPDATE {table} SET {column}=1"); Assert.AreEqual(1m, Scalar(settings, $"SELECT {column} FROM {table}"));
        }
        return Task.CompletedTask;
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task Schema15Upgrade_PreservesMapAndMarkers_RetriesFailedPublication(bool interrupt) => Isolated(settings =>
    {
        var repository = Fixture(settings);
        repository.Transaction(s => { foreach (var road in s.Lines.Keys) { s.DirtyLines.Add(road); s.PendingSegments.Add((road, 0)); } foreach (var cross in s.Crosses.Keys) s.StaleCrosses.Add(cross); return true; });
        var before = Snapshot(repository); DowngradeStorage(settings);
        if (interrupt)
        {
            Sql(settings, "CREATE TRIGGER TestPercentPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=16 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected percent publication failure'; END IF; END");
            try { Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings)); }
            finally { Sql(settings, "DROP TRIGGER TestPercentPublish"); }
            Assert.AreEqual(15, Convert.ToInt32(Scalar(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1")));
            Assert.ThrowsExactly<InvalidOperationException>(() => repository.Transaction(s => true));
        }
        var migrated = new MySqlLineRepository(settings); AssertSchema(settings); Assert.AreEqual(before, Snapshot(migrated));
        if (!interrupt) { Assert.IsNotNull(migrated.PercentDecimalBackupPath); Assert.IsTrue(File.Exists(migrated.PercentDecimalBackupPath)); }
        Assert.AreEqual(0, Convert.ToInt32(Scalar(settings, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME IN ('Crosses_DoubleV15','LogicalNodePositions_DoubleV15','Crosses_DecimalV16','LogicalNodePositions_DecimalV16')")));
        Assert.AreEqual(before, Snapshot(new MySqlLineRepository(settings)), "Restart is idempotent and must not recalculate positions or Cross states.");
        var roadToDelete = migrated.Transaction(s => s.Lines.Values.Single(r => r.Nodes.Length == 3));
        Assert.IsTrue(new LineSaveService(migrated).Delete(new(roadToDelete.Code, roadToDelete.Owner, roadToDelete.Version)).Success);
        Assert.AreEqual(0, new CrossService(migrated).GetAll().Data!.Length);
        Assert.AreEqual(0, new LogicalNodeService(migrated).GetAll().Data!.Single().Positions.Length);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task InvalidSecondTable_RollsBackBothCopiesAndRetainsOriginalsUntilCorrectedRetry() => Isolated(settings =>
    {
        var repository = Fixture(settings); var before = Snapshot(repository); DowngradeStorage(settings);
        Sql(settings, "ALTER TABLE LogicalNodePositions DROP CHECK CK_LNP_V12; UPDATE LogicalNodePositions SET RoadPercent=1.5");
        Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings));
        Assert.AreEqual(0, Convert.ToInt32(Scalar(settings, "SELECT COUNT(*) FROM Crosses_DecimalV16")), "The first table's inserts must also roll back.");
        Assert.AreEqual(0, Convert.ToInt32(Scalar(settings, "SELECT COUNT(*) FROM LogicalNodePositions_DecimalV16")));
        Assert.AreEqual(1.5, Convert.ToDouble(Scalar(settings, "SELECT RoadPercent FROM LogicalNodePositions")));
        Assert.AreEqual(15, Convert.ToInt32(Scalar(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1")));
        Sql(settings, "UPDATE LogicalNodePositions SET RoadPercent=0.6833333333333333");
        var migrated = new MySqlLineRepository(settings); AssertSchema(settings); Assert.AreEqual(before, Snapshot(migrated));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task TwoInitializers_SerializeMigrationAndObserveSameCommittedData() => Isolated(async settings =>
    {
        var before = Snapshot(Fixture(settings)); DowngradeStorage(settings);
        var repositories = await Task.WhenAll(Task.Run(() => new MySqlLineRepository(settings)), Task.Run(() => new MySqlLineRepository(settings)));
        AssertSchema(settings); Assert.AreEqual(1, repositories.Count(r => r.PercentDecimalBackupPath is not null));
        foreach (var repository in repositories) Assert.AreEqual(before, Snapshot(repository));
    });
}
