using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeRelationMySqlTests
{
    [TestMethod]
    public Task HttpCreatesEditsMovesAndRestarts_RawSqlAndDalEnforceAllowedValues_AndWriteFailureRollsBack()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,20,100,400,100);
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER TestLN8Write BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='LN8 position failure'");
        try{Assert.AreEqual("DatabaseError",(await client.CreateLogicalNodeAsync(100.5m,100.5m,8,new(){Value="="})).Code);}
        finally{Sql(settings,"DROP TRIGGER TestLN8Write");}Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var created=await client.CreateLogicalNodeAsync(100.5m,100.5m,8,new(){Value="="});Assert.IsTrue(created.Success,created.Message);var node=created.Data!;Assert.AreEqual(1,node.Positions.Length);
        var repeated=await client.CreateLogicalNodeAsync(150.5m,100.5m,8,new(){Value="="});Assert.IsTrue(repeated.Success,repeated.Message);
        var edited=await client.UpdateLogicalNodeAsync(node.Code,node.Version,"→","说明","P → Q");Assert.IsTrue(edited.Success,edited.Message);node=edited.Data!;
        var moved=await client.MoveLogicalNodeAsync(node.Code,node.Version,200.5m,100.5m);Assert.IsTrue(moved.Success,moved.Message);Assert.AreEqual(edited.Data!.Properties,moved.Data!.Properties);
        before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var value in new[]{"等同","蕴含","True","False","->"," = ",""})
        {
            Assert.AreEqual("InvalidRelationValue",(await client.CreateLogicalNodeAsync(250.5m,100.5m,8,new(){Value=value})).Code);
            Assert.AreEqual("InvalidRelationValue",(await client.UpdateLogicalNodeAsync(node.Code,moved.Data.Version,value,"","bad")).Code);
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET `Value`='{value}' WHERE Code='{node.Code}'")).Number);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        Assert.ThrowsExactly<InvalidLogicalNodeRelationException>(()=>repository.Transaction(s=>{var n=s.LogicalNodes[node.Code];s.LogicalNodes[node.Code]=n with{Properties=n.Properties with{Value="invalid"}};return true;}));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        var stale=await client.UpdateLogicalNodeAsync(node.Code,node.Version,"=","","");Assert.AreEqual("VersionConflict",stale.Code);
    });

    [TestMethod,DataRow("none"),DataRow("ddl"),DataRow("publish"),DataRow("existing-check")]
    public Task Schema23UpgradeBacksUpAndPreservesAllRows_RetriesInterruptedDdlAndPublication(string phase)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var service=new LogicalNodeService(repository);Add(repository,20,50,400,50);
        Assert.IsTrue(service.Create(new("owner",100.5m,50.5m,12,new(){Value="S"})).Success);Assert.IsTrue(service.Create(new("owner",150.5m,50.5m,6,new(){Value="判断",LogicLanguage="P"})).Success);Assert.IsTrue(service.Create(new("owner",200.5m,50.5m,4,new(){Value="False",LogicLanguage="¬P"})).Success);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"ALTER TABLE LogicalNodes "+(phase=="existing-check"?"":"DROP CHECK CK_LN_RelationValue, ")+"DROP CHECK CK_LN_V14, ADD CONSTRAINT CK_LN_V14 CHECK (REGEXP_LIKE(Code,'^[A-Z]{10}$','c') AND CHAR_LENGTH(TRIM(Owner))>0 AND Version>=0 AND MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (4,6,12,18) AND X-SideLength/2>=0 AND Y-SideLength/2>=0 AND X+SideLength/2<=500000 AND Y+SideLength/2<=500000); UPDATE MapMetadata SET SchemaVersion=23 WHERE Id=1");
        if(phase=="ddl")Sql(settings,"ALTER TABLE MapMetadata ADD CONSTRAINT CK_LN_RelationValue CHECK (1=1)");
        if(phase=="publish")Sql(settings,"CREATE TRIGGER TestLN8Publish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=24 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='LN8 publication failure'; END IF; END");
        if(phase is "ddl" or "publish")
        {
            try
            {Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>true));}
            finally{Sql(settings,phase=="ddl"?"ALTER TABLE MapMetadata DROP CHECK CK_LN_RelationValue":"DROP TRIGGER TestLN8Publish");}
        }
        repository=new MySqlLineRepository(settings);Assert.IsTrue(System.IO.File.Exists(repository.LogicalNodeMigrationBackupPath));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var relation=LogicalNodeRelationTests.Add(repository,250.5m,50.5m);Assert.AreEqual(8,relation.SideLength);Assert.AreEqual("=",relation.Properties.Value);
        var after=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });

    [TestMethod]
    public Task TwoUsersCanCreateRepeatedSymbolsAtDifferentPoints_ConflictingPositionHasOnlyOneCommit()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        var success=await Task.WhenAll(alice.CreateLogicalNodeAsync(100.5m,100.5m,8,new(){Value="="}),bob.CreateLogicalNodeAsync(150.5m,100.5m,8,new(){Value="="}));Assert.IsTrue(success.All(r=>r.Success));
        var conflict=await Task.WhenAll(alice.CreateLogicalNodeAsync(200.5m,100.5m,8,new(){Value="→"}),bob.CreateLogicalNodeAsync(200.5m,100.5m,8,new(){Value="→"}));Assert.AreEqual(1,conflict.Count(r=>r.Success));Assert.AreEqual("LogicalNodesTouchOrOverlap",conflict.Single(r=>!r.Success).Code);
        Assert.AreEqual(3,(await alice.GetLogicalNodesAsync()).Data!.Length);Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod]
    public Task RebuildMissingDerivedPositionsAfterUpgrade_PreservesLn8AndItsMetadata()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,20,100,400,100);var node=LogicalNodeRelationTests.Add(repository,100.5m,100.5m,"→","P → Q");
        Sql(settings,"DROP TABLE LogicalNodePositions");var reopened=new MySqlLineRepository(settings);var restored=new LogicalNodeService(reopened).GetAll().Data!.Single();
        Assert.AreEqual(node.Code,restored.Code);Assert.AreEqual(node.Properties,restored.Properties);Assert.AreEqual(node.Version,restored.Version);Assert.AreEqual(8,restored.SideLength);CollectionAssert.AreEqual(node.Positions,restored.Positions);return Task.CompletedTask;
    });
}
