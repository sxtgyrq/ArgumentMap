using System.IO;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;

namespace ArgumentMap.Tests;

internal static class CodeMigrationChecks
{
    internal static void Run(MySqlSettings settings, int oldVersion, bool failCopy = false)
    {
        Assert.IsTrue(settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal));
        using var connection = new MySqlConnection(settings.ConnectionString()); connection.Open();
        void Sql(string text) { using var cmd = connection.CreateCommand(); cmd.CommandText = text; cmd.ExecuteNonQuery(); }
        foreach (var table in new[] { "LogicalNodePositions", "Crosses", "LineNodes", "Lines", "MapMetadata" }) Sql($"DROP TABLE `{table}`");
        foreach (var statement in File.ReadAllText(Path.Combine(ProjectPaths.Root, "ArgumentMap.Storage", "Schema.Legacy.sql"))
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) Sql(statement);
        Sql("INSERT INTO `Lines` (Id,Owner,Version,RoadName,RoadDetail,Speed,PassInOpposeDirection,IsChanged,IsLocked) VALUES " +
            "(11,'中文负责人',3,'原主线路','保留中文说明',500,1,-1,0),(22,'中文负责人',7,'原交叉线路','',5,0,0,1)," +
            "(33,'中文负责人',2,'半整数甲','',50,0,0,0),(44,'中文负责人',4,'半整数乙','',60,1,0,0)");
        Sql("INSERT INTO LineNodes (LineId,NodeOrder,X,Y,IsChanged) VALUES (11,0,100,110,-1),(11,1,110,110,-1),(11,2,130,110,0)," +
            "(22,0,110,100,0),(22,1,110,140,0),(33,0,200,200,0),(33,1,201,201,0),(44,0,200,201,0),(44,1,201,200,0)");
        Sql("INSERT INTO Crosses (Id,Line1Id,Line2Id,X,Y,Segment1,Segment2,Percent1,Percent2,Line1Version,Line2Version,CrossState,Version,IsChanged) VALUES " +
            "(100,11,22,110,110,1,0,0,0.25,3,7,1,6,-1),(200,33,44,200.5,200.5,0,0,0.5,0.5,2,4,-1,8,0)");
        if (oldVersion == 1)
        {
            Sql("ALTER TABLE Crosses DROP CHECK CK_Cross, DROP CHECK CK_Cross_Position, DROP INDEX UQ_CrossRelation, ADD COLUMN X2 INT, ADD COLUMN Y2 INT");
            Sql("UPDATE Crosses SET X2=X*2,Y2=Y*2");
            Sql("ALTER TABLE Crosses DROP COLUMN X, DROP COLUMN Y, DROP COLUMN Percent1, DROP COLUMN Percent2, ADD UNIQUE KEY UQ_CrossRelation (Line1Id,Line2Id,X2,Y2)");
        }
        if (oldVersion == 3)
        {
            Sql("ALTER TABLE Crosses DROP INDEX UQ_CrossRelation, ADD UNIQUE KEY UQ_CrossRelation (Line1Id,Line2Id,Segment1,Segment2,X,Y)");
            Sql("INSERT INTO Crosses (Id,Line1Id,Line2Id,X,Y,Segment1,Segment2,Line1Version,Line2Version,CrossState,Version) VALUES (101,11,22,110,110,0,0,3,7,0,0)");
        }
        Sql($"UPDATE MapMetadata SET SchemaVersion={oldVersion} WHERE Id=1");
        if (failCopy)
        {
            Sql("ALTER TABLE Crosses DROP CHECK CK_Cross_Position"); Sql("UPDATE Crosses SET Percent1=1.5 WHERE Id=100");
            Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings));
            using var verify = connection.CreateCommand();
            verify.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"; Assert.AreEqual(8, Convert.ToInt32(verify.ExecuteScalar()));
            verify.CommandText = "SELECT COUNT(*) FROM `Lines` WHERE Id IN (11,22,33,44)"; Assert.AreEqual(4, Convert.ToInt32(verify.ExecuteScalar()));
            verify.CommandText = "SELECT CrossState FROM Crosses WHERE Id=100"; Assert.AreEqual(1, Convert.ToInt32(verify.ExecuteScalar()));
            Sql("UPDATE Crosses SET Percent1=0 WHERE Id=100");
        }
        var repository = new MySqlLineRepository(settings);
        Assert.IsNotNull(repository.MigrationBackupPath);
        using var backup = JsonDocument.Parse(File.ReadAllText(repository.MigrationBackupPath));
        Assert.AreEqual(4,backup.RootElement.GetProperty("Tables").GetProperty("Lines").GetArrayLength());
        Assert.AreEqual(4,backup.RootElement.GetProperty("Tables").GetProperty("CodeMap1").GetArrayLength());
        var lines = new LineSaveService(repository); var crosses = new CrossService(repository);
        var snapshots = lines.GetAll().Data!;
        Assert.AreEqual(4, snapshots.Length); Assert.AreEqual(4, snapshots.Select(l => l.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(snapshots.All(l => LineCode.IsValid(l.Code) && l.Nodes.All(n => n.LineCode == l.Code)));
        var a = snapshots.Single(l => l.Properties.RoadName == "原主线路"); var b = snapshots.Single(l => l.Properties.RoadName == "原交叉线路");
        Assert.AreEqual("中文负责人", a.Owner); Assert.AreEqual("保留中文说明", a.Properties.RoadDetail);
        Assert.AreEqual(500, a.Properties.Speed); Assert.AreEqual((short)1, a.Properties.PassInOpposeDirection); Assert.IsTrue(b.IsLocked);
        CollectionAssert.AreEqual(new[] { (100,110), (110,110), (130,110) }, a.Nodes.Select(n => (n.X,n.Y)).ToArray());
        var before = repository.Transaction(s => s.Crosses.Values.ToArray()); Assert.AreEqual(1, before.Length); Assert.IsTrue(before.All(c => c.Key.IsValid));
        var visible = crosses.GetAll().Data!;
        Assert.AreEqual(repository.Transaction(s => s.Crosses.Keys.Count(k => !s.StaleCrosses.Contains(k))), visible.Length);
        var main = before.Single(c => c.Key.Contains(a.Code));
        Assert.AreEqual(CrossState.Connected, main.State); Assert.AreEqual(6L, main.Version);
        if (oldVersion != 1)
        {
            Assert.AreEqual((1,0.0), main.LocationOn(a.Code)); Assert.AreEqual((0,0.25), main.LocationOn(b.Code));
            Assert.AreEqual(a.Code == main.Key.RoadCode1 ? 3L : 7L, main.RoadVersion1);
            Assert.AreEqual(a.Code == main.Key.RoadCode1 ? 7L : 3L, main.RoadVersion2);
        }
        Assert.IsFalse(before.Any(c => c.Key.Position == new CrossPosition(401,401)), "Historical half-integer settings are backed up and retired, never rounded.");
        Assert.IsNotNull(repository.CrossIntegerMigrationBackupPath);
        using var integerBackup = JsonDocument.Parse(File.ReadAllText(repository.CrossIntegerMigrationBackupPath));
        Assert.AreEqual(2, integerBackup.RootElement.GetProperty("Tables").GetProperty("Crosses").GetArrayLength());
        Assert.IsTrue(repository.Transaction(s => s.StaleCrosses.Contains(main.Key) && s.PendingSegments.Contains((a.Code,0))));
        while (crosses.Calculate().Data!.CanContinue) { }
        var after = crosses.GetAll().Data!.Single(c => c.Key == main.Key);
        Assert.AreEqual(CrossState.Connected, after.State); Assert.AreEqual((1,0.0), after.LocationOn(a.Code)); Assert.AreEqual((0,0.25), after.LocationOn(b.Code));
        var restarted = new MySqlLineRepository(settings);
        CollectionAssert.AreEqual(snapshots.Select(l => l.Code).Order(StringComparer.Ordinal).ToArray(), new LineSaveService(restarted).GetAll().Data!.Select(l => l.Code).Order(StringComparer.Ordinal).ToArray());
        Assert.AreEqual(after, new CrossService(restarted).GetAll().Data!.Single(c => c.Key == after.Key));
        VerifySchema(connection, settings.Database);
    }

    internal static void VerifySchema(MySqlConnection connection, string database)
    {
        using var query = connection.CreateCommand(); query.Parameters.AddWithValue("@db", database);
        query.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND ((TABLE_NAME IN ('Lines','LineNodes','Crosses') AND COLUMN_NAME IN ('Id','LineId','Line1Id','Line2Id')) OR COLUMN_NAME IN ('NextLineId','NextCrossId'))";
        Assert.AreEqual(0, Convert.ToInt32(query.ExecuteScalar()));
        query.CommandText = "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=@db AND (TABLE_NAME LIKE '%NumericV8' OR TABLE_NAME LIKE '%CodeV9')";
        Assert.AreEqual(0, Convert.ToInt32(query.ExecuteScalar()));
        query.CommandText = "SELECT GROUP_CONCAT(COLUMN_NAME ORDER BY ORDINAL_POSITION) FROM information_schema.KEY_COLUMN_USAGE WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND CONSTRAINT_NAME='PRIMARY'";
        Assert.AreEqual("RoadCode1,RoadCode2,X,Y", query.ExecuteScalar());
        query.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND COLUMN_NAME IN ('X','Y') AND DATA_TYPE='int'";
        Assert.AreEqual(2, Convert.ToInt32(query.ExecuteScalar()));
        query.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"; Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(query.ExecuteScalar()));
    }
}
