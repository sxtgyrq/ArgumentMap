using System.Net.Http;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeSetRelationMySqlTests
{
    private const string OldCheck="ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationValue, ADD CONSTRAINT CK_LN_RelationValue CHECK (SideLength<>8 OR CONVERT(`Value` USING binary) IN (X'3D',X'E28692'))";
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}

    [TestMethod,DataRow("normal"),DataRow("pending-ddl"),DataRow("publish")]
    public Task Schema27UpgradeUsesAtomicCheckReplacement_ClosedMarkerAndRetry_PreservesMap(string phase)=>Isolated(settings=>
    {
        var old=new MySqlLineRepository(settings);
        LogicalNodeRelationTests.Add(old);LogicalNodeRelationTests.Add(old,150.5m,value:"→");
        LogicalNodeInstanceTests.Parent(old,x:200.5m);LogicalNodeInstanceTests.Child(old,x:250.5m);
        Sql(settings,OldCheck+"; UPDATE MapMetadata SET SchemaVersion=27 WHERE Id=1");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(phase=="pending-ddl")
        {
            // Simulate interruption after the closed marker, before the atomic DDL.
            Sql(settings,"UPDATE MapMetadata SET SchemaVersion=2701 WHERE Id=1");
            Assert.ThrowsExactly<InvalidOperationException>(()=>old.Transaction(s=>true));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        if(phase=="publish")Sql(settings,"CREATE TRIGGER FailSetRelationPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=28 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected relation publication failure'; END IF; END");
        if(phase=="publish")
        {
            try
            {
                Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(2701,Version(settings));
                Assert.ThrowsExactly<InvalidOperationException>(()=>old.Transaction(s=>true));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
            }
            finally{Sql(settings,"DROP TRIGGER FailSetRelationPublish");}
        }
        var upgraded=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));Assert.IsTrue(System.IO.File.Exists(upgraded.LogicalNodeMigrationBackupPath));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        foreach(var value in new[]{"⊇","⊋","⊆","⊊"})LogicalNodeRelationTests.Add(upgraded,300.5m+Array.IndexOf(new[]{"⊇","⊋","⊆","⊊"},value)*30,value:value);
        var after=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });

    [TestMethod,DataRow("⊊"),DataRow("↔"),DataRow("¬")]
    public Task FreshSchemaAndHttpSupportAllSymbols_SqlRejectsMismatchAndOtherValues_ImportRollsBack(string importedValue)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var symbols=new[]{"=","→","↔","¬","⊇","⊋","⊆","⊊"};
        foreach(var value in symbols)
        {
            var result=await client.CreateLogicalNodeAsync(50.5m+Array.IndexOf(symbols,value)*30,50.5m,8,new(){Value=value,LogicLanguage="ignored"});Assert.IsTrue(result.Success,result.Message);
            var node=result.Data!;Assert.AreEqual(LogicalNodeRelationRules.Expression(value),node.Properties.LogicLanguage);
            var changed=await client.UpdateLogicalNodeAsync(node.Code,node.Version,value,"preserved","also ignored");Assert.IsTrue(changed.Success,changed.Message);Assert.AreEqual(LogicalNodeRelationRules.Expression(value),changed.Data!.Properties.LogicLanguage);
            // Both checks apply to raw SQL, not only HTTP and the DAL.
            Sql(settings,$"UPDATE LogicalNodes SET `Value`='{value}', LogicLanguage='{LogicalNodeRelationRules.Expression(value)}' WHERE Code='{node.Code}'");
            var before=LineTopologyMySqlTests.RawSnapshot(settings);
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET LogicLanguage='P' WHERE Code='{node.Code}'")).Number);
            foreach(var invalid in new[]{"⊂","⊃","∈","包含","⊆ "})
                Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET `Value`='{invalid}',LogicLanguage='{invalid}' WHERE Code='{node.Code}'")).Number);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        var doc=new JsonObject{["format"]="argumentmap.flow/1",["origin"]="rel",["ln"]=new JsonArray(new JsonObject{["id"]="rel",["size"]=8,["at"]=new JsonArray(.5m,.5m),["value"]=importedValue,["logic"]=importedValue}),["roads"]=new JsonArray(),["crosses"]=new JsonArray()};
        var snapshot=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailSetImport BEFORE INSERT ON LogicalNodes FOR EACH ROW BEGIN IF NEW.SideLength=8 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected relation write failure'; END IF; END");
        try{Assert.AreEqual("DatabaseError",(await client.ImportFlowDiagramAsync(doc.ToJsonString(),500.5m,500.5m)).Code);Assert.AreEqual(snapshot,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailSetImport");}
        Assert.IsTrue((await client.ImportFlowDiagramAsync(doc.ToJsonString(),500.5m,500.5m)).Success);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod,DataRow("⊇"),DataRow("↔")]
    public Task TwoServersTwoUsersCanCreateSameRelationSymbol_ExpiredLoginCannotWrite(string symbol)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        var results=await Task.WhenAll(alice.CreateLogicalNodeAsync(50.5m,50.5m,8,new(){Value=symbol}),bob.CreateLogicalNodeAsync(100.5m,50.5m,8,new(){Value=symbol}));Assert.IsTrue(results.All(r=>r.Success));
        var conflict=await Task.WhenAll(alice.CreateLogicalNodeAsync(150.5m,50.5m,8,new(){Value="⊊"}),bob.CreateLogicalNodeAsync(150.5m,50.5m,8,new(){Value="⊊"}));Assert.AreEqual(1,conflict.Count(r=>r.Success));
        await TestLogin.ClientAsync(h1,first,"alice");var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LoginExpired",(await alice.CreateLogicalNodeAsync(200.5m,50.5m,8,new(){Value="⊆"})).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    public Task CurrentVersionWithNarrowOrMissingCheckFailsClosed()=>Isolated(settings=>
    {
        new MySqlLineRepository(settings);Sql(settings,OldCheck);Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationValue");Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));return Task.CompletedTask;
    });
}
