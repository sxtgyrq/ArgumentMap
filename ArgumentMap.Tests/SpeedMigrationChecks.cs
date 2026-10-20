using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;

namespace ArgumentMap.Tests;

internal static class SpeedMigrationChecks
{
    internal static void Run(MySqlSettings settings)
    {
        if (!settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal)) throw new InvalidOperationException("Owned test database required.");
        var repository = new MySqlLineRepository(settings); var lines = new LineSaveService(repository); var crosses = new CrossService(repository);
        LineSnapshot Save(string owner, params int[] xy)
        {
            var line = lines.Create(new(owner)).Data!;
            var result = lines.CheckShapeIsOKThenSave(TestMap.Shape(line, xy)); Assert.IsTrue(result.Success, result.Message); return result.Data!;
        }
        var a = Save("速度迁移", 400000, 400010, 400020, 400010);
        var b = Save("速度迁移", 400010, 400000, 400010, 400020);
        var c = lines.Create(new("速度迁移")).Data!;
        while (crosses.Calculate().Data!.CanContinue) { }
        var cross = crosses.GetAll().Data!.Single(); cross = crosses.Edit(new(cross.Key, a.Owner, cross.Version, CrossState.Connected)).Data!;
        a = lines.SaveProperties(new(a.Code, a.Owner, a.Version, new LineProperties { RoadName = "保留旧速度", Speed = 60 })).Data!;
        c = lines.SaveProperties(new(c.Code, c.Owner, c.Version, new LineProperties { Speed = 500 })).Data!;
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();
            using var alter = connection.CreateCommand();
            alter.CommandText = "ALTER TABLE `Lines` DROP CHECK AM9_CK_Lines_Speed, CHANGE COLUMN Speed MaxSpeed INT NOT NULL DEFAULT 0," +
                "ADD COLUMN WalkInDirection SMALLINT NOT NULL DEFAULT 1, ADD COLUMN WalkInOpposeDirection SMALLINT NOT NULL DEFAULT 1," +
                "ADD COLUMN EBicycleInDirection SMALLINT NOT NULL DEFAULT 1, ADD COLUMN EBicycleInOpposeDirection SMALLINT NOT NULL DEFAULT 1," +
                "ADD COLUMN CarInDirection SMALLINT NOT NULL DEFAULT 1, ADD COLUMN CarInOpposeDirection SMALLINT NOT NULL DEFAULT 1";
            alter.ExecuteNonQuery();
            using var reset = connection.CreateCommand(); reset.CommandText = "UPDATE `Lines` SET MaxSpeed=0 WHERE LineCode=@id"; reset.Parameters.AddWithValue("@id", b.Code); reset.ExecuteNonQuery();
            reset.CommandText = "UPDATE MapMetadata SET SchemaVersion=6 WHERE Id=1"; reset.ExecuteNonQuery();
        }
        var migrated = new MySqlLineRepository(settings); var migratedLines = new LineSaveService(migrated);
        Assert.AreEqual(60, migratedLines.GetDetail(new(a.Code, a.Owner)).Data!.Properties.Speed);
        Assert.AreEqual(5, migratedLines.GetDetail(new(b.Code, b.Owner)).Data!.Properties.Speed);
        Assert.AreEqual(500, migratedLines.GetDetail(new(c.Code, c.Owner)).Data!.Properties.Speed);
        CollectionAssert.AreEqual(a.Nodes, migratedLines.GetDetail(new(a.Code, a.Owner)).Data!.Nodes);
        Assert.AreEqual(cross, new CrossService(migrated).GetAll().Data!.Single());
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open(); using var check = connection.CreateCommand();
            check.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Lines' AND COLUMN_NAME IN ('MaxSpeed','CarInDirection','CarInOpposeDirection','WalkInDirection','WalkInOpposeDirection','EBicycleInDirection','EBicycleInOpposeDirection')";
            check.Parameters.AddWithValue("@db", settings.Database); Assert.AreEqual(0, Convert.ToInt32(check.ExecuteScalar()));
            check.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"; Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(check.ExecuteScalar()));
            using var transaction = connection.BeginTransaction(); check.Transaction = transaction;
            try
            {
                check.CommandText = "UPDATE `Lines` SET RoadName='should rollback' WHERE LineCode=@id"; check.Parameters.AddWithValue("@id", a.Code); check.ExecuteNonQuery();
                check.CommandText = "UPDATE `Lines` SET Speed=4 WHERE LineCode=@id";
                try { check.ExecuteNonQuery(); Assert.Fail("Database CHECK must reject speed below 5."); }
                catch (MySqlException error) when (error.Number == 3819) { }
            }
            finally { transaction.Rollback(); }
        }
        Assert.AreEqual("保留旧速度", new LineSaveService(new MySqlLineRepository(settings)).GetDetail(new(a.Code, a.Owner)).Data!.Properties.RoadName);
        var beforeRetry = migrated.Transaction(s => s.Lines.Values.OrderBy(l => l.Code).Select(l => (l.Code, l.Version, l.Properties)).ToArray());
        // Simulate a completed atomic ALTER with a failed metadata-version write; reopening must finish safely.
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        { connection.Open(); using var reset = connection.CreateCommand(); reset.CommandText = "UPDATE MapMetadata SET SchemaVersion=6 WHERE Id=1"; reset.ExecuteNonQuery(); }
        var reopened = new MySqlLineRepository(settings);
        CollectionAssert.AreEqual(beforeRetry, reopened.Transaction(s => s.Lines.Values.OrderBy(l => l.Code).Select(l => (l.Code, l.Version, l.Properties)).ToArray()));
        Assert.AreEqual(cross, new CrossService(reopened).GetAll().Data!.Single());
    }
}
