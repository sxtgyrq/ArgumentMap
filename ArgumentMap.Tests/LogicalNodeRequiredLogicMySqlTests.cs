using System.Net.Http;
using System.IO;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeRequiredLogicMySqlTests
{
    private static string WithoutGeneratedLogicKeyAndLn12Rows(string snapshot)
    {
        var tables=JsonSerializer.Deserialize<SortedDictionary<string,string[]>>(snapshot)!;
        tables["LogicalNodes"]=tables["LogicalNodes"].Where(row=>JsonSerializer.Deserialize<SortedDictionary<string,JsonElement>>(row)!["SideLength"].GetInt32()!=12).Select(row=>
        {var fields=JsonSerializer.Deserialize<SortedDictionary<string,JsonElement>>(row)!;fields.Remove("Logic6Key");return JsonSerializer.Serialize(fields);}).Order(StringComparer.Ordinal).ToArray();
        return JsonSerializer.Serialize(tables);
    }
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}
    private static void Legacy20(MySqlSettings settings)
    {
        new MySqlLineRepository(settings);
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_Logic6Required, DROP CHECK CK_LN_Logic12Required, DROP INDEX UQ_LN_Logic6, DROP COLUMN Logic6Key, MODIFY COLUMN LogicLanguage VARCHAR(255) NOT NULL DEFAULT ''; UPDATE MapMetadata SET SchemaVersion=20 WHERE Id=1");
        Sql(settings,"""
            INSERT INTO LogicalNodes (Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage) VALUES
            ('AAAAAAAAAA','owner',1,50.5,50.5,6,'repeat','small info',''),
            ('BBBBBBBBBB','other',2,100.5,50.5,6,'repeat','small info','  '),
            ('CCCCCCCCCC','owner',3,150.5,50.5,6,'repeat','small info','P'),
            ('DDDDDDDDDD','owner',4,200.5,50.5,6,'repeat','small info','p'),
            ('EEEEEEEEEE','owner',5,250.5,50.5,4,'False','small info',''),
            ('FFFFFFFFFF','owner',6,300.5,50.5,12,'SINGLE','small info',''),
            ('GGGGGGGGGG','owner',7,350.5,50.5,6,'repeat','small info','LN_AAAAAAAAAA');
            """);
    }
    [TestMethod]
    public Task DatabaseAndDalEnforceRequiredCaseSensitiveLogic_OnlyOnSize6()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);
        var a=LogicalNodeRequiredLogicTests.Add(repository,"P");var b=LogicalNodeRequiredLogicTests.Add(repository,"p",100.5m);
        Assert.AreEqual(a.Properties.Value,b.Properties.Value);
        foreach(var (logic,i) in new[]{"é","e\u0301","P ","  P"}.Select((v,i)=>(v,i)))
            Assert.AreEqual(logic,LogicalNodeRequiredLogicTests.Add(repository,logic,150.5m+i*30).Properties.LogicLanguage);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var logic in new[]{""," \t\r\n","\u00a0\u2003"})
        {
            using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();
            q.CommandText="UPDATE LogicalNodes SET LogicLanguage=@logic WHERE Code=@code";q.Parameters.AddWithValue("@logic",logic);q.Parameters.AddWithValue("@code",b.Code);
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>q.ExecuteNonQuery()).Number);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        Assert.AreEqual(1062,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET LogicLanguage='P' WHERE Code='{b.Code}'")).Number);
        Assert.ThrowsExactly<LogicalNodeLogicRequiredException>(()=>repository.Transaction(s=>{s.LogicalNodes[b.Code]=b with{Properties=b.Properties with{LogicLanguage=""}};return true;}));
        Assert.ThrowsExactly<DuplicateLogicalNodeLogicException>(()=>repository.Transaction(s=>{s.LogicalNodes[b.Code]=b with{Properties=b.Properties with{LogicLanguage="P"}};return true;}));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        foreach(var (size,x) in new[]{(4,350.5m),(12,400.5m),(18,450.5m)})
        {
            var node=LogicalNodeRequiredLogicTests.Add(repository,"",x,size);
            Assert.AreEqual(size==12?node.Properties.Value:"",node.Properties.LogicLanguage);
        }
        return Task.CompletedTask;
    });
    [TestMethod]
    public Task TwoServers_ExplicitDuplicatesAllocateUniqueSuffixes_BlankPlaceholdersBothSucceed_ConcurrentUpdatesAreAtomic()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        var duplicate=await Task.WhenAll(alice.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){LogicLanguage="P"}),bob.CreateLogicalNodeAsync(100.5m,50.5m,6,new(){LogicLanguage="P"}));
        Assert.IsTrue(duplicate.All(r=>r.Success));CollectionAssert.AreEquivalent(new[]{"P","P1"},duplicate.Select(r=>r.Data!.Properties.LogicLanguage).ToArray());
        var placeholders=await Task.WhenAll(alice.CreateLogicalNodeAsync(150.5m,50.5m,6,new()),bob.CreateLogicalNodeAsync(200.5m,50.5m,6,new()));
        Assert.IsTrue(placeholders.All(r=>r.Success));Assert.AreEqual(2,placeholders.Select(r=>r.Data!.Properties.LogicLanguage).Distinct().Count());
        var a=placeholders[0].Data!;var b=placeholders[1].Data!;
        var edited=await Task.WhenAll(alice.UpdateLogicalNodeAsync(a.Code,a.Version,"值可重复","","Q"),bob.UpdateLogicalNodeAsync(b.Code,b.Version,"值可重复","","Q"));
        Assert.AreEqual(1,edited.Count(r=>r.Success));Assert.AreEqual("DuplicateLogicalNodeLogic",edited.Single(r=>!r.Success).Code);
        Assert.AreEqual("LogicalNodeLogicRequired",(await alice.UpdateLogicalNodeAsync(a.Code,(await alice.GetLogicalNodesAsync()).Data!.Single(n=>n.Code==a.Code).Version,"任意值",""," \t")).Code);
        Assert.IsNull(repository.Transaction(s=>LogicalNodeLogicRules.FindConflict(s.LogicalNodes.Values)));
    });
    [TestMethod]
    public Task LateSqlFailure_RollsBackAllocatedLogicNodeAndPositions_AndRetryReusesSuffix()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,10,50,400,50);
        LogicalNodeRequiredLogicTests.Add(repository,"P",100.5m);
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER TestRequiredLogicInsert BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected generated-logic position failure'");
        try{Assert.AreEqual("DatabaseError",(await client.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){LogicLanguage="P"})).Code);}
        finally{Sql(settings,"DROP TRIGGER TestRequiredLogicInsert");}
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var retry=await client.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){LogicLanguage="P"});Assert.IsTrue(retry.Success,retry.Message);
        Assert.AreEqual("P1",retry.Data!.Properties.LogicLanguage);Assert.AreEqual(1,retry.Data.Positions.Length);
    });
    [TestMethod]
    public Task Upgrade20_RepairsOnlyEmptySize6_SkipsOccupiedPlaceholder_PreservesPositionsAndVersionsOnRetry()=>Isolated(settings=>
    {
        Legacy20(settings);
        Sql(settings,"""
            INSERT INTO `Lines` (LineCode,Owner,Version) VALUES ('ZZZZZZZZZZ','owner',1);
            INSERT INTO LineNodes (LineCode,NodeOrder,X,Y) VALUES ('ZZZZZZZZZZ',0,10,50),('ZZZZZZZZZZ',1,400,50);
            INSERT INTO LogicalNodePositions (LogicalNodeCode,RoadCode,RoadOrder,RoadPercent,X,Y,RoadVersion,IsChanged)
            VALUES ('AAAAAAAAAA','ZZZZZZZZZZ',0,0.123456789012345678,50.5,50,1,-1);
            """);
        var repository=new MySqlLineRepository(settings);var nodes=repository.Transaction(s=>s.LogicalNodes);
        Assert.AreEqual("LN_AAAAAAAAAA1",nodes["AAAAAAAAAA"].Properties.LogicLanguage);Assert.AreEqual(2L,nodes["AAAAAAAAAA"].Version);
        Assert.AreEqual("LN_BBBBBBBBBB",nodes["BBBBBBBBBB"].Properties.LogicLanguage);Assert.AreEqual(3L,nodes["BBBBBBBBBB"].Version);
        Assert.AreEqual("P",nodes["CCCCCCCCCC"].Properties.LogicLanguage);Assert.AreEqual(3L,nodes["CCCCCCCCCC"].Version);
        Assert.AreEqual("p",nodes["DDDDDDDDDD"].Properties.LogicLanguage);Assert.AreEqual(4L,nodes["DDDDDDDDDD"].Version);
        Assert.AreEqual("",nodes["EEEEEEEEEE"].Properties.LogicLanguage);Assert.AreEqual("SINGLE",nodes["FFFFFFFFFF"].Properties.LogicLanguage);Assert.AreEqual(7L,nodes["FFFFFFFFFF"].Version);
        Assert.AreEqual("repeat",nodes["AAAAAAAAAA"].Properties.Value);Assert.AreEqual("small info",nodes["AAAAAAAAAA"].Properties.Info);
        Assert.AreEqual(50.5m,nodes["AAAAAAAAAA"].X);Assert.AreEqual("owner",nodes["AAAAAAAAAA"].Owner);
        using(var c=new MySqlConnection(settings.ConnectionString()))
        {c.Open();using var q=c.CreateCommand();q.CommandText="SELECT RoadPercent,IsChanged,RoadVersion FROM LogicalNodePositions";using var r=q.ExecuteReader();Assert.IsTrue(r.Read());Assert.AreEqual(.123456789012345678m,r.GetDecimal(0));Assert.AreEqual(-1,r.GetInt32(1));Assert.AreEqual(1L,r.GetInt64(2));}
        using var plan=JsonDocument.Parse(File.ReadAllText(repository.LogicalNodeRequiredLogicBackupPath!));Assert.AreEqual(2,plan.RootElement.GetProperty("Repairs").GetArrayLength());
        Assert.IsTrue(File.Exists(repository.LogicalNodeMigrationBackupPath));var before=LineTopologyMySqlTests.RawSnapshot(settings);
        new MySqlLineRepository(settings);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));return Task.CompletedTask;
    });
    [TestMethod,DataRow("dml"),DataRow("ddl"),DataRow("publish")]
    public Task UpgradeFailures_RollBackOrFailClosed_AndRetryNeverChangesRepairedExpressionsTwice(string phase)=>Isolated(settings=>
    {
        Legacy20(settings);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(phase=="dml")Sql(settings,"CREATE TRIGGER TestRequiredLogicFailure BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='BBBBBBBBBB' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN6 DML failure'; END IF; END");
        if(phase=="ddl")Sql(settings,"CREATE INDEX UQ_LN_Logic6 ON LogicalNodes (LogicLanguage)");
        if(phase=="publish")Sql(settings,"CREATE TRIGGER TestRequiredLogicFailure BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=21 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN6 publication failure'; END IF; END");
        string pending;
        try
        {
            Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(phase=="dml"?20:2001,Version(settings));
            pending=LineTopologyMySqlTests.RawSnapshot(settings);if(phase=="dml")Assert.AreEqual(before,pending);
        }
        finally{Sql(settings,phase=="ddl"?"DROP INDEX UQ_LN_Logic6 ON LogicalNodes":"DROP TRIGGER TestRequiredLogicFailure");}
        var repository=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));
        if(phase!="dml")Assert.AreEqual(WithoutGeneratedLogicKeyAndLn12Rows(pending),WithoutGeneratedLogicKeyAndLn12Rows(LineTopologyMySqlTests.RawSnapshot(settings)));
        var repaired12=repository.Transaction(s=>s.LogicalNodes["FFFFFFFFFF"]);Assert.AreEqual("SINGLE",repaired12.Properties.LogicLanguage);Assert.AreEqual(7L,repaired12.Version);
        return Task.CompletedTask;
    });
    [TestMethod]
    public Task ExistingNonemptyDuplicateNeverGetsRewritten_UpgradeStopsWithoutAnyPartialRepair()=>Isolated(settings=>
    {
        Legacy20(settings);Sql(settings,"UPDATE LogicalNodes SET LogicLanguage='P' WHERE Code='DDDDDDDDDD'");var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.ThrowsExactly<DuplicateLogicalNodeLogicException>(()=>new MySqlLineRepository(settings));
        Assert.AreEqual(20,Version(settings));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Sql(settings,"UPDATE LogicalNodes SET LogicLanguage='Q' WHERE Code='DDDDDDDDDD'");
        var repository=new MySqlLineRepository(settings);Assert.AreEqual("Q",repository.Transaction(s=>s.LogicalNodes["DDDDDDDDDD"].Properties.LogicLanguage));
        Assert.AreEqual("LN_AAAAAAAAAA1",repository.Transaction(s=>s.LogicalNodes["AAAAAAAAAA"].Properties.LogicLanguage));return Task.CompletedTask;
    });
}
