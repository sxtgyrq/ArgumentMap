using System.Net.Http;
using System.Net.Http.Json;
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
public sealed class LogicalNodeReviewMySqlTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public Task MissingPositionsTable_RebuildsEveryRowAndRetriesInterruptedRecovery(bool interrupt)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var service=new LogicalNodeService(repository);var node=service.Create(new("owner",110.5m,110.5m,6,new())).Data!;
        service.Create(new("owner",120.5m,110.5m,6,new()));Add(repository,90,108,130,108,130,112,90,112);
        var before=Snapshot(repository);Sql(settings,"DROP TABLE LogicalNodePositions");
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER TestRecoveryPublication BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=12 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected recovery publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));}
            finally{Sql(settings,"DROP TRIGGER TestRecoveryPublication");}
            using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var check=connection.CreateCommand();check.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
            Assert.AreEqual(11,Convert.ToInt32(check.ExecuteScalar()));check.CommandText="SELECT COUNT(*) FROM LogicalNodePositions";Assert.AreEqual(0,Convert.ToInt32(check.ExecuteScalar()),"Failed rebuild must roll back every position insertion.");
        }
        repository=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(repository));Assert.AreEqual(4,repository.Transaction(s=>s.LogicalNodePositions.Count));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task FailureAfterPositionWrites_RollsBackEverythingIncludingPendingMarkersAndAllowsRetry()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Data!;var road=await Add(client,90,110,130,110);var before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestLateLnSave BEFORE UPDATE ON LineNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected failure after LN positions persisted'");
        try{Assert.AreEqual("DatabaseError",(await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,90,110,100,110,130,110).Nodes)).Code);Assert.AreEqual(before,Snapshot(repository));}
        finally{Sql(settings,"DROP TRIGGER TestLateLnSave");}
        var saved=await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,90,110,100,110,130,110).Nodes);Assert.IsTrue(saved.Success);
        var position=(await client.GetLogicalNodesAsync()).Data!.Single().Positions.Single();Assert.AreEqual(1,position.RoadOrder);Assert.AreEqual(0.35,position.RoadPercent,1e-12);Assert.AreEqual(saved.Data!.Version,position.RoadVersion);
    });

    [TestMethod]
    public Task PropertyChangesRefreshRoadVersions_AndFailedWeightEditsKeepAllPositionRows()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};
        var client=await TestLogin.ClientAsync(http,server,"owner");var road=await Add(client,90,110,130,110);var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new(){Value=new string('中',32),Info=new string('文',255)})).Data!;
        road=(await client.SaveLinePropertiesAsync(road.Code,road.Version,road.Properties with{Speed=500,PassInOpposeDirection=1})).Data!;
        var current=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(road.Version,current.Positions.Single().RoadVersion);Assert.AreEqual(node.Version+1,current.Version);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var admin=await TestLogin.ClientAsync(http,server,"admin");var beforeLock=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestLnLockPositionFailure BEFORE UPDATE ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN position failure during lock'");
        try{Assert.AreEqual("DatabaseError",(await admin.ToggleLineLockAsync(road.Code,road.Version)).Code);Assert.AreEqual(beforeLock,Snapshot(repository));}
        finally{Sql(settings,"DROP TRIGGER TestLnLockPositionFailure");}
        road=(await admin.ToggleLineLockAsync(road.Code,road.Version)).Data!;Assert.IsTrue(road.IsLocked);Assert.AreEqual(road.Version,(await client.GetLogicalNodesAsync()).Data!.Single().Positions.Single().RoadVersion);
        var locked=Snapshot(repository);Assert.AreEqual("LineIsLocked",(await client.ReverseLineAsync(road.Code,road.Version)).Code);Assert.AreEqual(locked,Snapshot(repository));
        road=(await admin.ToggleLineLockAsync(road.Code,road.Version)).Data!;Assert.IsFalse(road.IsLocked);current=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(road.Version,current.Positions.Single().RoadVersion);
        var before=Snapshot(repository);Sql(settings,"CREATE TRIGGER TestLnWeightFailure BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN weight failure'");
        try{Assert.AreEqual("DatabaseError",(await client.UpdateLogicalNodeAsync(current.Code,current.Version,current.Properties.Value,current.Properties.Info)).Code);Assert.AreEqual(before,Snapshot(repository));}
        finally{Sql(settings,"DROP TRIGGER TestLnWeightFailure");}
        var updated=await client.UpdateLogicalNodeAsync(current.Code,current.Version,current.Properties.Value,current.Properties.Info);Assert.IsTrue(updated.Success);CollectionAssert.AreEqual(current.Positions,updated.Data!.Positions);
        Assert.AreEqual(new string('中',32),updated.Data.Properties.Value);Assert.AreEqual(new string('文',255),updated.Data.Properties.Info);
    });

    [TestMethod,TestCategory("Http")]
    public Task EveryLnEndpointRejectsExpiredSession_AndManagerPermissionPersistsOverHttp()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};
        var owner=await TestLogin.ClientAsync(http,server,"owner");var manager=await TestLogin.ClientAsync(http,server,"manager");var admin=await TestLogin.ClientAsync(http,server,"admin");
        var node=(await owner.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Data!;
        repository.Transaction(s=>{s.Accounts["manager"]=s.Accounts["manager"] with{EditAllLogicalNodes=true};s.Accounts["admin"]=s.Accounts["admin"] with{EditAllLogicalNodes=false};return true;});
        Assert.AreEqual(true,(await manager.GetLogicalNodesAsync()).Data!.Single().CanEdit);Assert.AreEqual(false,(await admin.GetLogicalNodesAsync()).Data!.Single().CanEdit);
        var currentOwner=await TestLogin.ClientAsync(http,server,"owner");var before=Snapshot(repository);
        Assert.AreEqual("LoginExpired",(await owner.GetLogicalNodesAsync()).Code);Assert.AreEqual("LoginExpired",(await owner.CreateLogicalNodeAsync(120.5m,120.5m,6,new())).Code);
        Assert.AreEqual("LoginExpired",(await owner.UpdateLogicalNodeAsync(node.Code,node.Version,"99","")).Code);Assert.AreEqual("LoginExpired",(await owner.DeleteLogicalNodeAsync(node.Code,node.Version)).Code);Assert.AreEqual("LoginExpired",(await owner.CalculateLogicalNodeAsync()).Code);Assert.AreEqual(before,Snapshot(repository));
        var changed=await manager.UpdateLogicalNodeAsync(node.Code,node.Version,"99","");Assert.IsTrue(changed.Success);Assert.AreEqual("manager",changed.Data!.Owner);
        Assert.AreEqual(false,(await currentOwner.GetLogicalNodesAsync()).Data!.Single().CanEdit);
        using(var response=await http.PostAsJsonAsync("UpdateLogicalNode",new UpdateLogicalNodeRequest(node.Code,"owner",changed.Data.Version,"0","",manager.Session.LoginCode)))
        {Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized,response.StatusCode);}
        Assert.AreEqual("99",(await manager.GetLogicalNodesAsync()).Data!.Single().Properties.Value);
        Assert.IsTrue((await manager.DeleteLogicalNodeAsync(node.Code,changed.Data.Version)).Success);
    });

    [TestMethod]
    public Task ConcurrentSaveCreateReversalAndJ_ReadersNeverObserveHalfUpdatedPositionSets()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(http1,first,"alice");var bob=await TestLogin.ClientAsync(http2,second,"bob");
        var vertical=await Add(bob,110,90,110,130);var horizontal=(await alice.CreateAsync()).Data!;
        var create=alice.CreateLogicalNodeAsync(110.5m,110.5m,6,new());var save=alice.SaveAsync(horizontal.Code,horizontal.Version,TestMap.Shape(horizontal,90,110,130,110).Nodes);
        // Either serialization order is valid; if both roads commit first, creation must reject the unset crossing.
        await Task.WhenAll(create,save);
        if(!create.Result.Success)
        {
            Assert.AreEqual("LogicalNodeCrossNotDisconnected",create.Result.Code);
            while((await alice.CalculateCrossAsync()).Data!.CanContinue){}
            var cross=(await alice.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await alice.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
            Assert.IsTrue((await alice.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Success);
        }
        Assert.IsTrue(save.Result.Success);horizontal=save.Result.Data!;
        async Task ReverseHorizontal(){for(var i=0;i<6;i++){var result=await alice.ReverseLineAsync(horizontal.Code,horizontal.Version);Assert.IsTrue(result.Success,result.Message);horizontal=result.Data!;}}
        async Task ReverseVertical(){for(var i=0;i<6;i++){var result=await bob.ReverseLineAsync(vertical.Code,vertical.Version);Assert.IsTrue(result.Success,result.Message);vertical=result.Data!;}}
        async Task Recalculate(){for(var i=0;i<6;i++)Assert.IsTrue((await bob.CalculateLogicalNodeAsync()).Success);}
        var reads=0;
        var observer=Task.Run(()=>
        {
            for(var i=0;i<12;i++)repository.Transaction(state=>
            {
                foreach(var n in state.LogicalNodes.Values)
                {
                    var expected=LogicalNodePositionCalculator.Calculate(n,state.Lines.Values);var actual=state.LogicalNodePositions.Values.Where(p=>p.LogicalNodeCode==n.Code).OrderBy(p=>p.RoadCode).ThenBy(p=>p.RoadOrder).ToArray();
                    CollectionAssert.AreEqual(expected,actual,"Every reader must see positions from the same committed road geometry and versions.");
                }
                reads++;return true;
            });
        });
        await Task.WhenAll(ReverseHorizontal(),ReverseVertical(),Recalculate(),observer);Assert.AreEqual(12,reads);
        var ln=(await alice.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(2,ln.Positions.Length);Assert.AreEqual(0.5125,ln.Positions.Single(p=>p.RoadCode==horizontal.Code).RoadPercent,1e-12);
    });
}
