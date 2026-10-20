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

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeUppercaseMySqlTests
{
    private static int Version(MySqlSettings settings)
    {
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());
    }
    private static void Legacy19(MySqlSettings settings)
    {
        new MySqlLineRepository(settings);
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_Value12Upper; UPDATE MapMetadata SET SchemaVersion=19 WHERE Id=1");
        Sql(settings,"""
            INSERT INTO LogicalNodes (Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage) VALUES
            ('AAAAAAAAAA','alice',3,50.5,50.5,12,'S','small info','s ∧ p'),
            ('BBBBBBBBBB','bob',4,100.5,50.5,12,'s','small info','s'),
            ('CCCCCCCCCC','alice',5,150.5,50.5,12,'S1','small info','p'),
            ('DDDDDDDDDD','bob',6,200.5,50.5,12,'s1','small info','q'),
            ('EEEEEEEEEE','alice',7,250.5,50.5,6,'mixed','small info','r'),
            ('FFFFFFFFFF','alice',8,300.5,50.5,12,'free','small info','a'),
            ('GGGGGGGGGG','alice',9,350.5,50.5,12,'KEEP','small info','b');
            """);
    }

    [TestMethod]
    public Task SqlAndDalEnforceUppercaseOnlyOnSize12_UnicodeAndRestartAreConsistent()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);
        var a=LogicalNodeUniqueValueTests.Add(repository,"s");Assert.AreEqual("S",a.Properties.Value);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        var error=Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET `Value`='s' WHERE Code='{a.Code}'"));
        Assert.AreEqual(3819,error.Number);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>
        {s.LogicalNodes[a.Code]=a with{Properties=a.Properties with{Value="lower"}};return true;}));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        foreach(var (value,i) in new[]{"iıéσя","ß","e\u0301","ﬀ"}.Select((s,i)=>(s,i)))
            Assert.AreEqual(LogicalNodeValueUniqueness.Normalize(value),LogicalNodeUniqueValueTests.Add(repository,value,100.5m+i*30).Properties.Value);
        foreach(var (size,x) in new[]{(4,300.5m),(6,350.5m),(18,400.5m)})
            Assert.AreEqual(size==4?"False":"mixed",LogicalNodeUniqueValueTests.Add(repository,"mixed",x,size).Properties.Value);
        var snapshot=Snapshot(repository);Assert.AreEqual(snapshot,Snapshot(new MySqlLineRepository(settings)));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task Migration19_UppercasesAndRepairsCaseCollisions_PreservesCoordinatesPositionsAndOtherSizes()=>Isolated(settings=>
    {
        Legacy19(settings);
        // Seed one derived position with full precision to detect accidental J/geometry updates.
        Sql(settings,"""
            INSERT INTO `Lines` (LineCode,Owner,Version) VALUES ('ZZZZZZZZZZ','owner',1);
            INSERT INTO LineNodes (LineCode,NodeOrder,X,Y) VALUES ('ZZZZZZZZZZ',0,10,50),('ZZZZZZZZZZ',1,400,50);
            INSERT INTO LogicalNodePositions (LogicalNodeCode,RoadCode,RoadOrder,RoadPercent,X,Y,RoadVersion,IsChanged)
            VALUES ('AAAAAAAAAA','ZZZZZZZZZZ',0,0.123456789012345678,50.5,50,1,-1);
            """);
        var repository=new MySqlLineRepository(settings);var nodes=repository.Transaction(s=>s.LogicalNodes);
        Assert.AreEqual("S2",nodes["AAAAAAAAAA"].Properties.Value);Assert.AreEqual("S3",nodes["BBBBBBBBBB"].Properties.Value);
        Assert.AreEqual("S11",nodes["CCCCCCCCCC"].Properties.Value);Assert.AreEqual("S12",nodes["DDDDDDDDDD"].Properties.Value);
        Assert.AreEqual("mixed",nodes["EEEEEEEEEE"].Properties.Value);Assert.AreEqual(7L,nodes["EEEEEEEEEE"].Version);
        Assert.AreEqual("FREE",nodes["FFFFFFFFFF"].Properties.Value);Assert.AreEqual(9L,nodes["FFFFFFFFFF"].Version);
        Assert.AreEqual("KEEP",nodes["GGGGGGGGGG"].Properties.Value);Assert.AreEqual(9L,nodes["GGGGGGGGGG"].Version);
        Assert.AreEqual(4L,nodes["AAAAAAAAAA"].Version);Assert.AreEqual("alice",nodes["AAAAAAAAAA"].Owner);
        Assert.AreEqual(50.5m,nodes["AAAAAAAAAA"].X);Assert.AreEqual("small info",nodes["AAAAAAAAAA"].Properties.Info);
        Assert.AreEqual("s ∧ p",nodes["AAAAAAAAAA"].Properties.LogicLanguage);
        using(var c=new MySqlConnection(settings.ConnectionString()))
        {c.Open();using var q=c.CreateCommand();q.CommandText="SELECT RoadPercent,IsChanged,RoadVersion FROM LogicalNodePositions";using var r=q.ExecuteReader();Assert.IsTrue(r.Read());Assert.AreEqual(.123456789012345678m,r.GetDecimal(0));Assert.AreEqual(-1,r.GetInt32(1));Assert.AreEqual(1L,r.GetInt64(2));}
        using var backup=JsonDocument.Parse(File.ReadAllText(repository.LogicalNodeUppercaseBackupPath!));Assert.AreEqual(5,backup.RootElement.GetProperty("Repairs").GetArrayLength());
        Assert.IsTrue(File.Exists(repository.LogicalNodeMigrationBackupPath));
        var snapshot=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(snapshot,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));return Task.CompletedTask;
    });

    [TestMethod,DataRow("dml"),DataRow("ddl"),DataRow("publish")]
    public Task MigrationFailure_RollsBackOrFailsClosed_AndRetryKeepsNamesAndVersions(string phase)=>Isolated(settings=>
    {
        Legacy19(settings);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(phase=="dml")Sql(settings,"CREATE TRIGGER TestUppercaseFailure BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='BBBBBBBBBB' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected uppercase DML failure'; END IF; END");
        if(phase=="ddl")Sql(settings,"ALTER TABLE LogicalNodes ADD CONSTRAINT CK_LN_Value12Upper CHECK (SideLength<>12 OR CONVERT(`Value` USING binary)=CONVERT(UPPER(`Value`) USING binary)) NOT ENFORCED");
        if(phase=="publish")Sql(settings,"CREATE TRIGGER TestUppercaseFailure BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=20 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected uppercase publication failure'; END IF; END");
        string pending;
        try
        {
            Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));
            Assert.AreEqual(phase=="dml"?19:1901,Version(settings));
            pending=LineTopologyMySqlTests.RawSnapshot(settings);if(phase=="dml")Assert.AreEqual(before,pending);
        }
        finally{Sql(settings,phase=="ddl"?"ALTER TABLE LogicalNodes DROP CHECK CK_LN_Value12Upper":"DROP TRIGGER TestUppercaseFailure");}
        var repository=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));
        if(phase!="dml")Assert.AreEqual(pending,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsNull(repository.Transaction(s=>LogicalNodeValueUniqueness.FindConflict(s.LogicalNodes.Values)));
        Assert.AreEqual("S2",repository.Transaction(s=>s.LogicalNodes["AAAAAAAAAA"].Properties.Value));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task TwoServers_LowerAndUpperCreatesGetUniqueSuffixes_ConcurrentEditsRejectCaseCollision()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var a=await LocalLineServer.StartAsync(repository:repository);
        await using var b=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=a.BaseAddress};using var h2=new HttpClient{BaseAddress=b.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,a,"alice");var bob=await TestLogin.ClientAsync(h2,b,"bob");
        var results=await Task.WhenAll(alice.CreateLogicalNodeAsync(50.5m,50.5m,12,new(){Value="s"}),bob.CreateLogicalNodeAsync(100.5m,50.5m,12,new(){Value="S"}));
        Assert.IsTrue(results.All(r=>r.Success));CollectionAssert.AreEquivalent(new[]{"S","S1"},results.Select(r=>r.Data!.Properties.Value).ToArray());
        var first=results[0].Data!;var second=results[1].Data!;
        var edits=await Task.WhenAll(alice.UpdateLogicalNodeAsync(first.Code,first.Version,"shared",""),bob.UpdateLogicalNodeAsync(second.Code,second.Version,"ShArEd",""));
        Assert.AreEqual(1,edits.Count(r=>r.Success));Assert.AreEqual("DuplicateLogicalNodeValue",edits.Single(r=>!r.Success).Code);
        Assert.AreEqual("SHARED",edits.Single(r=>r.Success).Data!.Properties.Value);
        Assert.IsTrue(repository.Transaction(s=>s.LogicalNodes.Values.All(n=>n.Properties.Value==LogicalNodeValueUniqueness.Normalize(n.Properties.Value))));
    });
}
