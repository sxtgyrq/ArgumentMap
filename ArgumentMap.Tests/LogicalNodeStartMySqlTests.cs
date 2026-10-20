using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeStartMySqlTests
{
    [TestMethod]
    public Task TwoIndependentServersRaceToCreateStart_OnlyOneCommits_DeletionAllowsReplacement()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);
        await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        var results=await Task.WhenAll(alice.CreateLogicalNodeAsync(100.5m,100.5m,18,new()),bob.CreateLogicalNodeAsync(300.5m,300.5m,18,new()));
        Assert.AreEqual(1,results.Count(r=>r.Success));Assert.AreEqual("StartLogicalNodeExists",results.Single(r=>!r.Success).Code);
        var start=(await alice.GetLogicalNodesAsync()).Data!.Single();Assert.IsTrue(start.IsStart);
        var winner=start.Owner=="alice"?alice:bob;Assert.IsTrue((await winner.DeleteLogicalNodeAsync(start.Code,start.Version)).Success);
        Assert.IsTrue((await bob.CreateLogicalNodeAsync(400.5m,400.5m,18,new())).Success);
        var restarted=new LogicalNodeService(new MySqlLineRepository(settings));Assert.AreEqual(1,restarted.GetAll().Data!.Count(n=>n.IsStart));
    });
    [TestMethod]
    public Task FailedPositionInsertionRollsBackStartSlot_AndDatabaseRejectsDuplicateEvenWithoutBusinessValidation()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,90,110,130,110);var service=new LogicalNodeService(repository);var before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestStartFailure BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected start-position failure'");
        try{Assert.ThrowsExactly<MySqlException>(()=>service.Create(new("owner",110.5m,110.5m,18,new())));Assert.AreEqual(before,Snapshot(repository));}
        finally{Sql(settings,"DROP TRIGGER TestStartFailure");}
        var start=service.Create(new("owner",110.5m,110.5m,18,new())).Data!;Assert.AreEqual(1,start.Positions.Length);
        before=Snapshot(repository);
        Assert.ThrowsExactly<MySqlException>(()=>repository.Transaction(s=>{s.LogicalNodes.Add("ABCDEFGHIJ",new("ABCDEFGHIJ","other",0,300.5m,300.5m,18,new()));return true;}));
        Assert.AreEqual(before,Snapshot(repository),"A unique-index violation must never update the original start via upsert.");
        Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"INSERT INTO LogicalNodes (Code,Owner,X,Y,SideLength) VALUES ('ZYXWVUTSRQ','other',400.5,400.5,18)"));
        Assert.AreEqual(before,Snapshot(repository));
        Assert.IsTrue(service.Delete(new(start.Code,"owner",start.Version)).Success);Assert.IsTrue(service.Create(new("other",300.5m,300.5m,18,new())).Success);
        return Task.CompletedTask;
    });
    [TestMethod,DataRow(false),DataRow(true)]
    public Task Schema12UpgradeRetainsAllLnPositions_AndRetriesPublicationFailure(bool interrupt)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,90,110,130,110);var service=new LogicalNodeService(repository);
        Assert.IsTrue(service.Create(new("owner",110.5m,110.5m,6,new(){Value="中文节点"})).Success);var before=Snapshot(repository);
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_InstanceValue, DROP CHECK CK_LN_InstanceLogic, DROP CHECK CK_LN_BranchValue, DROP CHECK CK_LN_RelationValue, DROP CHECK CK_LN_RelationLogic, DROP CHECK CK_LN_Value12Upper, DROP INDEX UQ_LN_Value12, DROP COLUMN Value12Key, DROP CHECK CK_LN_V14, DROP INDEX UQ_LN_Start, DROP COLUMN StartSlot, CHANGE COLUMN `Value` Name VARCHAR(32) NOT NULL, ADD COLUMN NodeType INT NOT NULL DEFAULT 99, ADD COLUMN Weight INT NOT NULL DEFAULT 10, ADD CONSTRAINT CK_LN_V12 CHECK (MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (6,12))");
        Sql(settings,"UPDATE MapMetadata SET SchemaVersion=12 WHERE Id=1");
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER TestStartPublication BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=13 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected start-schema publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));}
            finally{Sql(settings,"DROP TRIGGER TestStartPublication");}
        }
        repository=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(repository));
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var query=connection.CreateCommand();query.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(query.ExecuteScalar()));
        Assert.IsTrue(new LogicalNodeService(repository).Create(new("owner",300.5m,300.5m,18,new())).Success);
        return Task.CompletedTask;
    });
}
