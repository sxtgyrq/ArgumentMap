using System.IO;
using System.Net.Http;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql")]
public sealed class LogicalNodeUniqueValueMySqlTests
{
    private static object Scalar(MySqlSettings settings, string sql)
    {
        using var c = new MySqlConnection(settings.ConnectionString()); c.Open();
        using var q = c.CreateCommand(); q.CommandText = sql; return q.ExecuteScalar()!;
    }
    private static int Version(MySqlSettings settings) => Convert.ToInt32(Scalar(settings, "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"));
    private static void Downgrade(MySqlSettings settings) => Sql(settings,
        "ALTER TABLE LogicalNodes DROP CHECK CK_LN_Value12Upper, DROP CHECK CK_LN_Logic12Required, DROP INDEX UQ_LN_Value12, DROP COLUMN Value12Key; UPDATE MapMetadata SET SchemaVersion=18 WHERE Id=1");
    private static void Seed(MySqlSettings settings) => Sql(settings, """
        INSERT INTO LogicalNodes (Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage) VALUES
        ('AAAAAAAAAA','alice',3,50.5,50.5,12,'S','原说明','S'),
        ('BBBBBBBBBB','bob',4,100.5,50.5,12,'S','原说明','T'),
        ('CCCCCCCCCC','alice',5,150.5,50.5,12,'S1','原说明','U'),
        ('DDDDDDDDDD','bob',6,200.5,50.5,6,'S','原说明','V'),
        ('EEEEEEEEEE','alice',7,250.5,50.5,4,'False','原说明','');
        """);

    [TestMethod]
    public Task DatabaseConstraint_ExactValuesOtherSizesAndSideLengthUpdatesCannotBypass() => Isolated(settings =>
    {
        var repository = new MySqlLineRepository(settings);
        LogicalNodeUniqueValueTests.Add(repository, "S");
        foreach (var (value, i) in new[] { "s", "S ", " S", "é", "e\u0301", "😀", "" }.Select((v, i) => (v, i)))
            LogicalNodeUniqueValueTests.Add(repository, value, 100.5m + 30 * i);
        var other = LogicalNodeUniqueValueTests.Add(repository, "S", 400.5m, 6);
        var b = LogicalNodeUniqueValueTests.Add(repository, "T", 450.5m);
        var before = LineTopologyMySqlTests.RawSnapshot(settings);
        foreach (var sql in new[]
        {
            $"UPDATE LogicalNodes SET `Value`='S' WHERE Code='{b.Code}'",
            $"UPDATE LogicalNodes SET SideLength=12 WHERE Code='{other.Code}'",
            "INSERT INTO LogicalNodes (Code,Owner,X,Y,SideLength,`Value`,LogicLanguage) VALUES ('ZZZZZZZZZZ','owner',500.5,50.5,12,'S','S')"
        })
        {
            var e = Assert.ThrowsExactly<MySqlException>(() => Sql(settings, sql)); Assert.AreEqual(1062, e.Number);
            Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings));
        }
        LogicalNodeUniqueValueTests.Add(repository, "S", 500.5m, 4);
        LogicalNodeUniqueValueTests.Add(repository, "S", 550.5m, 18);
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion, Version(settings));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task DalBypassAndInjectedUpdateFailure_RollBackAllData_ThenRetrySucceeds() => Isolated(async settings =>
    {
        var repository = new MySqlLineRepository(settings); Add(repository, 10, 50, 220, 50);
        var a = LogicalNodeUniqueValueTests.Add(repository, "S"); var b = LogicalNodeUniqueValueTests.Add(repository, "T", 100.5m);
        var before = LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.ThrowsExactly<DuplicateLogicalNodeValueException>(() => repository.Transaction(s =>
        {
            s.LogicalNodes[b.Code] = b with { Properties = b.Properties with { Value = "S" } };
            s.Lines.Clear(); s.LogicalNodePositions.Clear(); return true;
        }));
        Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings));
        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress }; var client = await TestLogin.ClientAsync(http, server, "owner");
        Sql(settings,"CREATE TRIGGER TestUniqueCreate BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected numbered creation failure'");
        try{Assert.AreEqual("DatabaseError",(await client.CreateLogicalNodeAsync(150.5m,50.5m,12,new(){Value="S"})).Code);}
        finally{Sql(settings,"DROP TRIGGER TestUniqueCreate");}
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var retry=await client.CreateLogicalNodeAsync(150.5m,50.5m,12,new(){Value="S"});
        Assert.IsTrue(retry.Success,retry.Message);Assert.AreEqual("S1",retry.Data!.Properties.Value);
        Assert.AreEqual(1,retry.Data.Positions.Length);before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings, "CREATE TRIGGER TestUniqueValueUpdate BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected value update failure'");
        try { Assert.AreEqual("DatabaseError", (await client.UpdateLogicalNodeAsync(a.Code, a.Version, "U", "新说明", "P")).Code); }
        finally { Sql(settings, "DROP TRIGGER TestUniqueValueUpdate"); }
        Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await client.UpdateLogicalNodeAsync(a.Code, a.Version, "U", "新说明", "P")).Success);
        var same = await client.UpdateLogicalNodeAsync(b.Code, b.Version, "U", "不可写", "Q"); Assert.AreEqual("DuplicateLogicalNodeValue", same.Code);
        StringAssert.Contains(same.Message,a.Code);StringAssert.Contains(same.Message,"中心坐标 X=50.5, Y=50.5");
        var nodes = (await client.GetLogicalNodesAsync()).Data!;
        Assert.AreEqual("T", nodes.Single(n => n.Code == b.Code).Properties.Value);
        Assert.AreEqual("P", nodes.Single(n => n.Code == a.Code).Properties.LogicLanguage);
    });

    [TestMethod]
    public Task TwoServers_ConcurrentCreatesBothSucceedWithUniqueValues_UpdatesHaveOneWinner() => Isolated(async settings =>
    {
        var repository = new MySqlLineRepository(settings);
        await using var first = await LocalLineServer.StartAsync(repository: repository);
        await using var second = await LocalLineServer.StartAsync(repository: new MySqlLineRepository(settings));
        using var h1 = new HttpClient { BaseAddress = first.BaseAddress }; using var h2 = new HttpClient { BaseAddress = second.BaseAddress };
        var alice = await TestLogin.ClientAsync(h1, first, "alice"); var bob = await TestLogin.ClientAsync(h2, second, "bob");
        for (var i = 0; i < 3; i++)
        {
            var outcomes = await Task.WhenAll(alice.CreateLogicalNodeAsync(50.5m + i * 100, 50.5m, 12, new() { Value = "S" + i }),
                bob.CreateLogicalNodeAsync(100.5m + i * 100, 50.5m, 12, new() { Value = "S" + i }));
            Assert.IsTrue(outcomes.All(r=>r.Success));
            CollectionAssert.AreEquivalent(new[]{"S"+i,"S"+i+"1"},outcomes.Select(r=>r.Data!.Properties.Value).ToArray());
        }
        var a = (await alice.CreateLogicalNodeAsync(50.5m, 150.5m, 12, new() { Value = "A" })).Data!;
        var b = (await bob.CreateLogicalNodeAsync(100.5m, 150.5m, 12, new() { Value = "B" })).Data!;
        var updates = await Task.WhenAll(alice.UpdateLogicalNodeAsync(a.Code, a.Version, "共同值", "说明"),
            bob.UpdateLogicalNodeAsync(b.Code, b.Version, "共同值", "说明"));
        Assert.AreEqual(1, updates.Count(r => r.Success)); Assert.AreEqual("DuplicateLogicalNodeValue", updates.Single(r => !r.Success).Code);
        var nodes = (await alice.GetLogicalNodesAsync()).Data!;
        Assert.AreEqual(1, nodes.Count(n => n.Properties.Value == "共同值"));
        Assert.AreEqual(8, nodes.Length); Assert.IsNull(LogicalNodeValueUniqueness.FindConflict(nodes));
    });

    [TestMethod]
    public Task Migration_RenamesAllDuplicatesSkipsOccupiedSuffixes_PreservesAssociationsAndIsIdempotent() => Isolated(settings =>
    {
        var repository = new MySqlLineRepository(settings); var road = Add(repository, 10, 50, 300, 50);
        Downgrade(settings); Seed(settings);
        Sql(settings, $"INSERT INTO LogicalNodePositions (LogicalNodeCode,RoadCode,RoadOrder,RoadPercent,X,Y,RoadVersion,IsChanged) VALUES ('AAAAAAAAAA','{road.Code}',0,0.123456789012345678,50.5,50,{road.Version},-1)");
        var positions = Scalar(settings, "SELECT CONCAT(RoadPercent,':',RoadVersion,':',IsChanged,':',UpdatedAt) FROM LogicalNodePositions");
        repository = new MySqlLineRepository(settings);
        var nodes = repository.Transaction(s => s.LogicalNodes);
        Assert.AreEqual("S2", nodes["AAAAAAAAAA"].Properties.Value); Assert.AreEqual("S3", nodes["BBBBBBBBBB"].Properties.Value);
        Assert.AreEqual("S1", nodes["CCCCCCCCCC"].Properties.Value); Assert.AreEqual("S", nodes["DDDDDDDDDD"].Properties.Value);
        Assert.AreEqual("False", nodes["EEEEEEEEEE"].Properties.Value);
        Assert.AreEqual(4L, nodes["AAAAAAAAAA"].Version); Assert.AreEqual(5L, nodes["BBBBBBBBBB"].Version); Assert.AreEqual(5L, nodes["CCCCCCCCCC"].Version);
        Assert.AreEqual("alice", nodes["AAAAAAAAAA"].Owner); Assert.AreEqual("原说明", nodes["AAAAAAAAAA"].Properties.Info); Assert.AreEqual("S", nodes["AAAAAAAAAA"].Properties.LogicLanguage);
        Assert.AreEqual(50.5m, nodes["AAAAAAAAAA"].X);
        Assert.AreEqual(positions, Scalar(settings, "SELECT CONCAT(RoadPercent,':',RoadVersion,':',IsChanged,':',UpdatedAt) FROM LogicalNodePositions"));
        Assert.IsTrue(File.Exists(repository.LogicalNodeMigrationBackupPath));
        using var plan = JsonDocument.Parse(File.ReadAllText(repository.LogicalNodeUniqueValueBackupPath!));
        Assert.AreEqual(2, plan.RootElement.GetProperty("Repairs").GetArrayLength());
        var before = LineTopologyMySqlTests.RawSnapshot(settings); new MySqlLineRepository(settings);
        Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings)); Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion, Version(settings));
        return Task.CompletedTask;
    });

    [TestMethod, DataRow("dml"), DataRow("ddl"), DataRow("publish")]
    public Task MigrationFailures_RollBackOrFailClosed_AndRestartRepairsWithoutDoubleSuffix(string phase) => Isolated(settings =>
    {
        var old = new MySqlLineRepository(settings); Downgrade(settings); Seed(settings);
        var before = LineTopologyMySqlTests.RawSnapshot(settings);
        if (phase == "dml") Sql(settings, "CREATE TRIGGER TestUniqueMigration BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='BBBBBBBBBB' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected repair failure'; END IF; END");
        if (phase == "ddl") Sql(settings, "CREATE INDEX UQ_LN_Value12 ON LogicalNodes (`Value`)");
        if (phase == "publish") Sql(settings, "CREATE TRIGGER TestUniqueMigration BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=19 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected publication failure'; END IF; END");
        string pending;
        try
        {
            Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings));
            Assert.AreEqual(phase == "dml" ? 18 : 1801, Version(settings));
            if (phase == "dml") Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings));
            Assert.ThrowsExactly<InvalidOperationException>(() => old.Transaction(s => s.LogicalNodes.Count));
            pending = (string)Scalar(settings, "SELECT GROUP_CONCAT(`Value` ORDER BY Code SEPARATOR '|') FROM LogicalNodes");
        }
        finally { Sql(settings, phase == "ddl" ? "DROP INDEX UQ_LN_Value12 ON LogicalNodes" : "DROP TRIGGER TestUniqueMigration"); }
        var repository = new MySqlLineRepository(settings); Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion, Version(settings));
        if (phase != "dml") Assert.AreEqual(pending, Scalar(settings, "SELECT GROUP_CONCAT(`Value` ORDER BY Code SEPARATOR '|') FROM LogicalNodes"));
        Assert.IsNull(repository.Transaction(s => LogicalNodeValueUniqueness.FindConflict(s.LogicalNodes.Values)));
        Assert.AreEqual("S2", repository.Transaction(s => s.LogicalNodes["AAAAAAAAAA"].Properties.Value));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Migration_LongUnicodeAndEmptyValues_CollisionsRemainWithin32Utf16Units() => Isolated(settings =>
    {
        new MySqlLineRepository(settings); Downgrade(settings);
        using (var c = new MySqlConnection(settings.ConnectionString()))
        {
            c.Open();
            var values = new[] { new string('中', 32), new string('中', 32), string.Concat(Enumerable.Repeat("😀", 16)), string.Concat(Enumerable.Repeat("😀", 16)), "", "", "1" };
            for (var i = 0; i < values.Length; i++)
            {
                using var q = c.CreateCommand(); q.CommandText = "INSERT INTO LogicalNodes (Code,Owner,X,Y,SideLength,`Value`) VALUES (@code,'owner',@x,50.5,12,@value)";
                q.Parameters.AddWithValue("@code", new string((char)('A' + i), 10)); q.Parameters.AddWithValue("@x", 50.5m + 30 * i); q.Parameters.AddWithValue("@value", values[i]); q.ExecuteNonQuery();
            }
        }
        var repo = new MySqlLineRepository(settings); var nodes = repo.Transaction(s => s.LogicalNodes.Values.ToArray());
        Assert.IsNull(LogicalNodeValueUniqueness.FindConflict(nodes));
        Assert.IsTrue(nodes.All(n => n.Properties.Value.Length <= 32 && !n.Properties.Value.Contains('\uFFFD')));
        Assert.AreEqual("2", nodes.Single(n => n.Code == "EEEEEEEEEE").Properties.Value);
        Assert.AreEqual("3", nodes.Single(n => n.Code == "FFFFFFFFFF").Properties.Value);
        return Task.CompletedTask;
    });
}
