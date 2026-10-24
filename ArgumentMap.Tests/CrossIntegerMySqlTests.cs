using System.Globalization;
using System.IO;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql")]
public sealed class CrossIntegerMySqlTests
{
    private sealed record HistoryPayload(MapUndoDelta Delta, UndoStamp[] Stamps);
    private sealed record Fixture(MySqlLineRepository OldRepository, CrossSnapshot Integer, CrossSnapshot Half,
        string GeometryOperation, string IntegerOperation, string RetiredOperation, LineSnapshot BeforeRoad);
    private static T Scalar<T>(MySqlSettings settings, string sql)
    {
        using var connection = new MySqlConnection(settings.ConnectionString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }
    private static void Execute(MySqlSettings settings, string sql, params (string Name, object Value)[] values)
    {
        using var connection = new MySqlConnection(settings.ConnectionString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        foreach (var value in values) command.Parameters.AddWithValue(value.Name, value.Value);
        command.ExecuteNonQuery();
    }
    private static string Rows(MySqlSettings settings, string sql)
    {
        using var connection = new MySqlConnection(settings.ConnectionString()); connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        using var reader = command.ExecuteReader(); var rows = new List<object[]>();
        while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
        return JsonSerializer.Serialize(rows);
    }
    private static string History(MySqlSettings settings) => Rows(settings, "SELECT * FROM MapUndoHistory ORDER BY Sequence");
    private static string Stamps(MySqlSettings settings) => Rows(settings, "SELECT * FROM MapEditStamps ORDER BY ObjectKey");
    private static string IntegerRows(MySqlSettings settings) => Rows(settings,
        "SELECT RoadCode1,RoadCode2,CAST(X AS DECIMAL(7,1)),CAST(Y AS DECIMAL(7,1)),RoadOrder1,RoadOrder2,Percent1,Percent2,RoadVersion1,RoadVersion2,CrossState,Version,IsChanged,UpdatedAt FROM Crosses WHERE X=FLOOR(X) AND Y=FLOOR(Y) ORDER BY RoadCode1,RoadCode2,X,Y");
    private static string Stamp(CrossKey key) => $"C:{key.RoadCode1}:{key.RoadCode2}:{key.Position.X2}:{key.Position.Y2}";
    private static void Calculate(ILineRepository repository)
    {
        var service = new CrossService(repository);
        for (var i = 0; i < 100; i++) { var result = service.Calculate(); Assert.IsTrue(result.Success, result.Message); if (!result.Data!.CanContinue) return; }
        Assert.Fail("K did not complete.");
    }
    private static HistoryPayload Payload(MySqlSettings settings, string operation) => JsonSerializer.Deserialize<HistoryPayload>(
        Scalar<string>(settings, $"SELECT Payload FROM MapUndoHistory WHERE OperationId='{operation}'"))!;
    private static void WritePayload(MySqlSettings settings, string operation, HistoryPayload payload) => Execute(settings,
        "UPDATE MapUndoHistory SET Payload=@payload WHERE OperationId=@id", ("@payload", JsonSerializer.Serialize(payload)), ("@id", operation));

    private static Fixture Version37(MySqlSettings settings)
    {
        var repository = new MySqlLineRepository(settings);
        Add(repository, 90, 110, 130, 110); Add(repository, 110, 90, 110, 130);
        var a = Add(repository, 200, 200, 201, 201); var b = Add(repository, 200, 201, 201, 200);
        Calculate(repository);
        var original = new CrossService(repository).GetAll().Data!.Single();
        var edit = MapUndoTests.Edit(repository, scope => new CrossService(scope).Edit(new(original.Key, "owner", original.Version, CrossState.Road1ToRoad2)));
        Assert.IsTrue(edit.Success, edit.Message);
        var integerOperation = Scalar<string>(settings, "SELECT OperationId FROM MapUndoHistory ORDER BY Sequence DESC LIMIT 1");
        var changed = MapUndoTests.Edit(repository, scope => new LineSaveService(scope).CheckShapeIsOKThenSave(TestMap.Shape(a, 200, 200, 202, 202)));
        Assert.IsTrue(changed.Success, changed.Message); Calculate(repository);
        var geometryOperation = Scalar<string>(settings, "SELECT OperationId FROM MapUndoHistory ORDER BY Sequence DESC LIMIT 1");
        var key = CrossKey.Create(a.Code, b.Code, new(401, 401));
        var halfBefore = new CrossSnapshot(key, 0, 0, key.RoadCode1 == a.Code ? a.Version : b.Version,
            key.RoadCode2 == a.Code ? a.Version : b.Version, CrossState.Unset, 6, .5, .5);
        var halfAfter = halfBefore with { RoadVersion1 = key.RoadCode1 == a.Code ? changed.Data!.Version : b.Version,
            RoadVersion2 = key.RoadCode2 == a.Code ? changed.Data!.Version : b.Version, Version = 7,
            Percent1 = key.RoadCode1 == a.Code ? .25 : .5, Percent2 = key.RoadCode2 == a.Code ? .25 : .5 };
        // Represent authentic old geometric snapshots. Current business rules
        // intentionally cannot create these half-integer Crosses any more.
        var geometry = Payload(settings, geometryOperation);
        WritePayload(settings, geometryOperation, geometry with
        {
            Delta = geometry.Delta with { BeforeCrosses = [halfBefore], AfterCrosses = [halfAfter] },
            Stamps = [.. geometry.Stamps, new(Stamp(key), null)]
        });
        var retired = Guid.NewGuid().ToString("N");
        var halfCurrent = halfAfter with { State = CrossState.Road2ToRoad1, Version = 8 };
        var pure = new HistoryPayload(new([], [], [], [halfAfter], [halfCurrent]), [new(Stamp(key), geometryOperation)]);
        Execute(settings, "INSERT INTO MapUndoHistory (OperationId,Owner,Operation,Payload) VALUES (@id,'owner','旧半整数设置',@payload)",
            ("@id", retired), ("@payload", JsonSerializer.Serialize(pure)));
        Execute(settings, "INSERT INTO MapEditStamps (ObjectKey,OperationId) VALUES (@key,@id)", ("@key", Stamp(key)), ("@id", retired));
        Sql(settings, "ALTER TABLE Crosses DROP CHECK CK_Cross_IntegerCoordinates, MODIFY X DECIMAL(7,1) NOT NULL, MODIFY Y DECIMAL(7,1) NOT NULL; UPDATE MapMetadata SET SchemaVersion=37 WHERE Id=1");
        Execute(settings, "INSERT INTO Crosses (RoadCode1,RoadCode2,X,Y,RoadOrder1,RoadOrder2,Percent1,Percent2,RoadVersion1,RoadVersion2,CrossState,Version,IsChanged,UpdatedAt) VALUES (@a,@b,200.5,200.5,0,0,@p1,@p2,@v1,@v2,3,8,-1,'2001-01-01 00:00:00')",
            ("@a", key.RoadCode1), ("@b", key.RoadCode2), ("@p1", halfCurrent.Percent1), ("@p2", halfCurrent.Percent2),
            ("@v1", halfCurrent.RoadVersion1), ("@v2", halfCurrent.RoadVersion2));
        // Keep full 18-digit storage precision and all lifecycle flags unchanged.
        Sql(settings, "UPDATE Crosses SET Percent1=0.123456789012345678,Percent2=0.987654321098765432,Version=19,IsChanged=-1,UpdatedAt='2000-01-01 00:00:00' WHERE X=110; UPDATE LineNodes SET IsChanged=-1 WHERE NodeOrder=0; UPDATE `Lines` SET IsChanged=-1");
        return new(repository, edit.Data!, halfCurrent, geometryOperation, integerOperation, retired, a);
    }

    private static void AssertCurrent(MySqlSettings settings)
    {
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion, Scalar<int>(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"));
        Assert.AreEqual(2, Scalar<int>(settings, "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='Crosses' AND COLUMN_NAME IN ('X','Y') AND DATA_TYPE='int' AND IS_NULLABLE='NO'"));
        Assert.AreEqual(1, Scalar<int>(settings, "SELECT COUNT(*) FROM information_schema.TABLE_CONSTRAINTS WHERE CONSTRAINT_SCHEMA=DATABASE() AND TABLE_NAME='Crosses' AND CONSTRAINT_NAME='CK_Cross_IntegerCoordinates' AND ENFORCED='YES'"));
        Assert.AreEqual(0, Scalar<int>(settings, "SELECT COUNT(*) FROM Crosses WHERE X<>FLOOR(X) OR Y<>FLOOR(Y)"));
    }

    [TestMethod, DataRow("normal"), DataRow("pending"), DataRow("marker"), DataRow("cross"), DataRow("history"), DataRow("stamps"), DataRow("ddl"), DataRow("publish")]
    public Task Version37RetiresHalfCrossesAndHistory_AtomicallyAndRetryably(string phase) => Isolated(settings =>
    {
        var fixture = Version37(settings); var preserved = IntegerRows(settings);
        var lines = Rows(settings, "SELECT * FROM `Lines` ORDER BY LineCode"); var nodes = Rows(settings, "SELECT * FROM LineNodes ORDER BY LineCode,NodeOrder");
        var initialHistory = History(settings); var initialStamps = Stamps(settings);
        if (phase == "pending") Sql(settings, "UPDATE MapMetadata SET SchemaVersion=3701 WHERE Id=1");
        if (phase == "ddl") Sql(settings, "ALTER TABLE Crosses DROP CHECK AM9_CK_Cross; UPDATE Crosses SET X=500001 WHERE X=110");
        var trigger = phase switch
        {
            "marker" => "CREATE TRIGGER FailIntegerMigration BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=3701 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected integer marker failure'; END IF; END",
            "cross" => "CREATE TRIGGER FailIntegerMigration BEFORE DELETE ON Crosses FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected cross retirement failure'",
            "history" => "CREATE TRIGGER FailIntegerMigration BEFORE UPDATE ON MapUndoHistory FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected history filtering failure'",
            "stamps" => "CREATE TRIGGER FailIntegerMigration BEFORE DELETE ON MapEditStamps FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected stamp filtering failure'",
            "publish" => "CREATE TRIGGER FailIntegerMigration BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=38 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected integer publication failure'; END IF; END",
            _ => null
        };
        if (trigger is not null) Sql(settings, trigger);
        if (phase is not ("normal" or "pending"))
        {
            try
            {
                Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings));
                Assert.AreEqual(phase == "marker" ? 37 : 3701, Scalar<int>(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"));
                Assert.ThrowsExactly<InvalidOperationException>(() => fixture.OldRepository.Transaction(s => true));
                if (phase is "marker" or "cross" or "history" or "stamps")
                {
                    Assert.AreEqual(1, Scalar<int>(settings, "SELECT COUNT(*) FROM Crosses WHERE X=200.5 AND Y=200.5"));
                    Assert.AreEqual(initialHistory, History(settings)); Assert.AreEqual(initialStamps, Stamps(settings));
                }
                else Assert.AreEqual(0, Scalar<int>(settings, "SELECT COUNT(*) FROM Crosses WHERE X=200.5 AND Y=200.5"));
            }
            finally
            {
                if (trigger is not null) Sql(settings, "DROP TRIGGER FailIntegerMigration");
                if (phase == "ddl") Sql(settings, "UPDATE Crosses SET X=110 WHERE X=500001");
            }
        }
        var repository = new MySqlLineRepository(settings); AssertCurrent(settings);
        Assert.AreEqual(preserved, IntegerRows(settings)); Assert.AreEqual(lines, Rows(settings, "SELECT * FROM `Lines` ORDER BY LineCode"));
        Assert.AreEqual(nodes, Rows(settings, "SELECT * FROM LineNodes ORDER BY LineCode,NodeOrder"));
        Assert.AreEqual(1, Scalar<int>(settings, "SELECT COUNT(*) FROM Crosses"), "The retired identity must not be rounded to 200 or 201.");
        Assert.AreEqual(0, Scalar<int>(settings, $"SELECT COUNT(*) FROM MapUndoHistory WHERE OperationId='{fixture.RetiredOperation}'"));
        Assert.AreEqual(0, Scalar<int>(settings, $"SELECT COUNT(*) FROM MapEditStamps WHERE ObjectKey='{Stamp(fixture.Half.Key)}'"));
        var geometry = Payload(settings, fixture.GeometryOperation);
        Assert.AreEqual(0, geometry.Delta.BeforeCrosses.Length); Assert.AreEqual(0, geometry.Delta.AfterCrosses.Length);
        Assert.IsTrue(geometry.Stamps.All(s => !s.Key.StartsWith("C:", StringComparison.Ordinal)));
        Assert.AreEqual(1, geometry.Delta.Lines.Length);
        Assert.IsTrue(File.Exists(repository.CrossIntegerMigrationBackupPath));
        if (phase is "normal" or "pending" or "marker" or "cross" or "history" or "stamps")
        {
            using var backup = JsonDocument.Parse(File.ReadAllText(repository.CrossIntegerMigrationBackupPath!));
            var tables = backup.RootElement.GetProperty("Tables");
            Assert.AreEqual(2, tables.GetProperty("Crosses").GetArrayLength());
            Assert.AreEqual(3, tables.GetProperty("MapUndoHistory").GetArrayLength());
            Assert.IsTrue(tables.TryGetProperty("MapEditStamps", out _));
        }
        var finalRows = IntegerRows(settings); var finalHistory = History(settings); var finalStamps = Stamps(settings);
        var restarted = new MySqlLineRepository(settings);
        Assert.AreEqual(finalRows, IntegerRows(settings)); Assert.AreEqual(finalHistory, History(settings)); Assert.AreEqual(finalStamps, Stamps(settings));
        Assert.IsNull(restarted.CrossIntegerMigrationBackupPath, "A current-schema restart must not rerun the migration.");
        // Clear pending work through K before undo: half crossings must remain
        // absent and the next useful undo entry must remain reachable.
        Calculate(restarted);
        var undo = new MapUndoService(restarted).Undo("owner"); Assert.IsTrue(undo.Success, undo.Message);
        CollectionAssert.AreEqual(fixture.BeforeRoad.Nodes, new LineSaveService(restarted).GetAll().Data!.Single(l => l.Code == fixture.BeforeRoad.Code).Nodes);
        undo = new MapUndoService(restarted).Undo("owner"); Assert.IsTrue(undo.Success, undo.Message);
        Assert.AreEqual(CrossState.Unset, new CrossService(restarted).GetAll().Data!.Single().State);
        Assert.IsTrue(restarted.Transaction(s => s.Crosses.Keys.All(k => k.Position.IsInteger)));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task CompletedHalfCrossUndoKeepsRequestTombstone_AndRetryDoesNotUndoNextOperation() => Isolated(settings =>
    {
        var fixture = Version37(settings); var requestId = Guid.NewGuid().ToString("N");
        // The old server committed the undo, but its response never reached the
        // client. Preserve that request across the integer-coordinate upgrade.
        Execute(settings, "UPDATE MapUndoHistory SET IsUndone=1,UndoRequestId=@request WHERE OperationId=@id",
            ("@request", requestId), ("@id", fixture.RetiredOperation));
        Execute(settings, "UPDATE MapEditStamps SET OperationId=@id WHERE ObjectKey=@key",
            ("@id", fixture.GeometryOperation), ("@key", Stamp(fixture.Half.Key)));
        Sql(settings, "UPDATE Crosses SET CrossState=0,Version=9 WHERE X=200.5 AND Y=200.5");
        var tombstoneMetadata = Rows(settings, $"SELECT Sequence,OperationId,Owner,Operation,IsUndone,UndoRequestId,CreatedAt FROM MapUndoHistory WHERE OperationId='{fixture.RetiredOperation}'");
        var repository = new MySqlLineRepository(settings); AssertCurrent(settings); Calculate(repository);
        var payload = Payload(settings, fixture.RetiredOperation);
        Assert.AreEqual(0, payload.Delta.BeforeCrosses.Length); Assert.AreEqual(0, payload.Delta.AfterCrosses.Length); Assert.AreEqual(0, payload.Stamps.Length);
        Assert.AreEqual(tombstoneMetadata, Rows(settings, $"SELECT Sequence,OperationId,Owner,Operation,IsUndone,UndoRequestId,CreatedAt FROM MapUndoHistory WHERE OperationId='{fixture.RetiredOperation}'"));
        var before = MapUndoTests.Content(repository); var history = History(settings); var stamps = Stamps(settings);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var retry = new MapUndoService(new MySqlLineRepository(settings)).Undo("owner", requestId);
            Assert.IsTrue(retry.Success, retry.Message); StringAssert.Contains(retry.Message, "上次撤销已完成");
            Assert.AreEqual(before, MapUndoTests.Content(repository)); Assert.AreEqual(history, History(settings)); Assert.AreEqual(stamps, Stamps(settings));
        }
        var next = new MapUndoService(repository).Undo("owner"); Assert.IsTrue(next.Success, next.Message);
        CollectionAssert.AreEqual(fixture.BeforeRoad.Nodes, new LineSaveService(repository).GetAll().Data!.Single(l => l.Code == fixture.BeforeRoad.Code).Nodes);
        var integer = new MapUndoService(repository).Undo("owner"); Assert.IsTrue(integer.Success, integer.Message);
        Assert.AreEqual(CrossState.Unset, new CrossService(repository).GetAll().Data!.Single().State);
        return Task.CompletedTask;
    });

    [TestMethod, DataRow(3101), DataRow(3201), DataRow(3301), DataRow(3401)]
    public Task HistoricalPendingBranchesContinueThroughNotAndIntegerUpgrades(int pending) => Isolated(settings =>
    {
        Version37(settings);
        // Model publication interrupted after the old LN10 DDL had completed.
        // Cross storage is still the true v37 DECIMAL shape with half identities.
        Sql(settings, $"UPDATE MapMetadata SET SchemaVersion={pending} WHERE Id=1");
        var repository = new MySqlLineRepository(settings); AssertCurrent(settings);
        Assert.AreEqual(1, repository.Transaction(s => s.Crosses.Count));
        Assert.IsTrue(File.Exists(repository.CrossIntegerMigrationBackupPath));
        using var backup = JsonDocument.Parse(File.ReadAllText(repository.CrossIntegerMigrationBackupPath!));
        Assert.AreEqual(2, backup.RootElement.GetProperty("Tables").GetProperty("Crosses").GetArrayLength());
        return Task.CompletedTask;
    });

    [TestMethod, DataRow("missing"), DataRow("disabled"), DataRow("weak"), DataRow("decimal"), DataRow("decimal-missing-positions")]
    public Task CurrentVersionRejectsBrokenCoordinateContractBeforeRecovery(string damage) => Isolated(settings =>
    {
        new MySqlLineRepository(settings);
        Sql(settings, damage switch
        {
            "missing" => "ALTER TABLE Crosses DROP CHECK CK_Cross_IntegerCoordinates",
            "disabled" => "ALTER TABLE Crosses ALTER CHECK CK_Cross_IntegerCoordinates NOT ENFORCED",
            "weak" => "ALTER TABLE Crosses DROP CHECK CK_Cross_IntegerCoordinates, ADD CONSTRAINT CK_Cross_IntegerCoordinates CHECK (X=X)",
            "decimal-missing-positions" => "ALTER TABLE Crosses MODIFY X DECIMAL(7,1) NOT NULL, MODIFY Y DECIMAL(7,1) NOT NULL; DROP TABLE LogicalNodePositions",
            _ => "ALTER TABLE Crosses MODIFY X DECIMAL(7,1) NOT NULL, MODIFY Y DECIMAL(7,1) NOT NULL"
        });
        Assert.ThrowsExactly<InvalidOperationException>(() => new MySqlLineRepository(settings));
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion, Scalar<int>(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"));
        if (damage == "decimal-missing-positions")
            Assert.AreEqual(0, Scalar<int>(settings, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='LogicalNodePositions'"));
        return Task.CompletedTask;
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task MissingPositionsAndInterruptedPublicationRecoverWithoutHalfResurrection(bool interrupt) => Isolated(settings =>
    {
        var fixture = Version37(settings);
        if (interrupt)
        {
            Sql(settings, "CREATE TRIGGER FailIntegerRecovery BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=38 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected recovery publication failure'; END IF; END");
            try { Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings)); }
            finally { Sql(settings, "DROP TRIGGER FailIntegerRecovery"); }
            Assert.AreEqual(3701, Scalar<int>(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"));
        }
        Sql(settings, "DROP TABLE LogicalNodePositions");
        var repository = new MySqlLineRepository(settings); AssertCurrent(settings);
        Assert.AreEqual(1, Scalar<int>(settings, "SELECT COUNT(*) FROM information_schema.TABLES WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='LogicalNodePositions'"));
        Calculate(repository); Assert.AreEqual(1, repository.Transaction(s => s.Crosses.Count));
        var undo = new MapUndoService(repository).Undo("owner"); Assert.IsTrue(undo.Success, undo.Message);
        Assert.IsFalse(repository.Transaction(s => s.Crosses.ContainsKey(fixture.Half.Key)));
        return Task.CompletedTask;
    });

    [TestMethod, DataRow(false), DataRow(true)]
    public Task Version15DecimalCopyKeepsHalfIdentityUntilDedicatedRetirement(bool interrupt) => Isolated(settings =>
    {
        var fixture = Version37(settings);
        Sql(settings, "ALTER TABLE Crosses MODIFY Percent1 DOUBLE NOT NULL DEFAULT 0, MODIFY Percent2 DOUBLE NOT NULL DEFAULT 0; ALTER TABLE LogicalNodePositions MODIFY RoadPercent DOUBLE NOT NULL; UPDATE MapMetadata SET SchemaVersion=15 WHERE Id=1");
        if (interrupt)
        {
            Sql(settings, "CREATE TRIGGER FailIntegerOldCopy BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=16 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected v16 publication failure'; END IF; END");
            try { Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings)); }
            finally { Sql(settings, "DROP TRIGGER FailIntegerOldCopy"); }
            Assert.AreEqual(1, Scalar<int>(settings, "SELECT COUNT(*) FROM Crosses WHERE X=200.5 AND Y=200.5"));
            Assert.AreEqual(2, Scalar<int>(settings, "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=DATABASE() AND TABLE_NAME='Crosses' AND COLUMN_NAME IN ('X','Y') AND DATA_TYPE='decimal'"));
        }
        var repository = new MySqlLineRepository(settings); AssertCurrent(settings);
        Assert.AreEqual(1, repository.Transaction(s => s.Crosses.Count));
        using var backup = JsonDocument.Parse(File.ReadAllText(repository.CrossIntegerMigrationBackupPath!));
        Assert.AreEqual(2, backup.RootElement.GetProperty("Tables").GetProperty("Crosses").GetArrayLength());
        Assert.IsFalse(repository.Transaction(s => s.Crosses.ContainsKey(fixture.Half.Key)));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task IntegerStorageBoundsAndDalValidationRejectHalfCoordinatesWithoutMutation() => Isolated(settings =>
    {
        var repository = new MySqlLineRepository(settings); Add(repository, 90, 110, 130, 110); Add(repository, 110, 90, 110, 130); Calculate(repository);
        var cross = new CrossService(repository).GetAll().Data!.Single(); var before = LineTopologyMySqlTests.RawSnapshot(settings);
        foreach (var invalid in new[] { new CrossPosition(221, 220), new CrossPosition(220, 221), new CrossPosition(-2, 220), new CrossPosition(1000002, 220) })
            Assert.ThrowsExactly<InvalidCrossCoordinateException>(() => repository.Transaction(s =>
            {
                s.Lines[cross.Key.RoadCode1] = s.Lines[cross.Key.RoadCode1] with { Version = 99 };
                var key = cross.Key with { Position = invalid }; s.Crosses[key] = cross with { Key = key }; return true;
            }));
        Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings));
        foreach (var invalid in new[] { "X=-1", "Y=-1", "X=500001", "Y=500001" })
            Assert.AreEqual(3819, Assert.ThrowsExactly<MySqlException>(() => Sql(settings, "UPDATE Crosses SET " + invalid)).Number);
        Sql(settings, "UPDATE Crosses SET X=0,Y=500000");
        Assert.AreEqual(new CrossPosition(0, 1000000), new CrossService(new MySqlLineRepository(settings)).GetAll().Data!.Single().Key.Position);
        // INT storage coerces raw SQL numeric input before CHECK; API/DAL are
        // responsible for rejecting fractional requests before persistence.
        Sql(settings, "UPDATE Crosses SET X=110,Y=110");
        AssertCurrent(settings); return Task.CompletedTask;
    });
}
