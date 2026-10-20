using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql"), TestCategory("Http")]
public sealed class LogicalNodeMoveMySqlTests
{
    [TestMethod]
    public Task MoveRebuildsAllProjections_ChineseMetadataAndCrossStatesPersistAfterRestart()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var oldRoad=await Add(client,90,110,130,110);
        var a=await Add(client,190,210,230,210,230,212,190,212);var b=await Add(client,210,190,210,230);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        foreach(var cross in (await client.GetCrossesAsync()).Data!)Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new(){Value="中文值",Info="保留说明"})).Data!;
        Assert.AreEqual(oldRoad.Code,node.Positions.Single().RoadCode);
        var crosses=System.Text.Json.JsonSerializer.Serialize((await client.GetCrossesAsync()).Data);
        var move=await client.MoveLogicalNodeAsync(node.Code,node.Version,210.5m,210.5m);Assert.IsTrue(move.Success,move.Message);var moved=move.Data!;
        Assert.AreEqual(node.Version+1,moved.Version);Assert.AreEqual(node.Properties,moved.Properties);Assert.AreEqual("owner",moved.Owner);Assert.AreEqual(3,moved.Positions.Length);
        Assert.AreEqual(2,moved.Positions.Count(p=>p.RoadCode==a.Code));Assert.IsTrue(moved.Positions.All(p=>!p.IsChanged&&p.RoadVersion==(p.RoadCode==a.Code?a.Version:b.Version)));
        Assert.AreEqual(crosses,System.Text.Json.JsonSerializer.Serialize((await client.GetCrossesAsync()).Data));
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.IsTrue((await client.MoveLogicalNodeAsync(moved.Code,moved.Version,moved.X,moved.Y)).Success);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings),"No-op must preserve timestamps and raw decimal values.");
        var restarted=new LogicalNodeService(new MySqlLineRepository(settings)).GetAll("owner").Data!.Single();
        // DECIMAL(7,1) reads integer feet as 210.0, while a fresh projection may be 210.
        // Compare numeric records rather than JSON's decimal formatting.
        CollectionAssert.AreEqual(moved.Positions,restarted.Positions);
        Assert.AreEqual(moved with {Positions=restarted.Positions},restarted);
        Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await client.DeleteAsync(a.Code,a.Version)).Success);
        node=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(b.Code,node.Positions.Single().RoadCode);
        Assert.IsTrue((await client.DeleteLogicalNodeAsync(node.Code,node.Version)).Success);
        Assert.AreEqual(0,repository.Transaction(s=>s.LogicalNodePositions.Count));
    });

    [TestMethod,DataRow("parent"),DataRow("delete"),DataRow("insert"),DataRow("update")]
    public Task WriteFailureRollsBackParentPositionsFlagsVersionsAndTimestamps_RetrySucceeds(string failure)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        await Add(client,90,110,130,110);await Add(client,190,210,230,210);
        var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Data!;
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        var table=failure=="parent"?"LogicalNodes":"LogicalNodePositions";
        var action=failure switch{"delete"=>"DELETE","insert"=>"INSERT",_=>"UPDATE"};
        Sql(settings,$"CREATE TRIGGER TestLNMove BEFORE {action} ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected move failure'");
        var x=failure=="update"?111.5m:210.5m;var y=failure=="update"?110.5m:210.5m;
        try
        {
            Assert.AreEqual("DatabaseError",(await client.MoveLogicalNodeAsync(node.Code,node.Version,x,y)).Code);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        finally{Sql(settings,"DROP TRIGGER TestLNMove");}
        Assert.IsTrue((await client.MoveLogicalNodeAsync(node.Code,node.Version,x,y)).Success);
        var stored=new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single();
        Assert.AreEqual(x,stored.X);Assert.AreEqual(y,stored.Y);Assert.AreEqual(node.Version+1,stored.Version);Assert.AreEqual(1,stored.Positions.Length);
    });

    [TestMethod]
    public Task HttpChecksSessionOwnerVersionAndFreshGeometry_AdminRetainsOriginalOwner()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var owner=await TestLogin.ClientAsync(http,server,"owner");var other=await TestLogin.ClientAsync(http,server,"other");var admin=await TestLogin.ClientAsync(http,server,"admin");
        var node=(await owner.CreateLogicalNodeAsync(50.5m,50.5m,18,new(){Value="开始"})).Data!;
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LoginExpired",(await new HttpLineClient(http,new("owner","invalid")).MoveLogicalNodeAsync(node.Code,node.Version,110.5m,110.5m)).Code);
        Assert.AreEqual("NotOwner",(await other.MoveLogicalNodeAsync(node.Code,node.Version,110.5m,110.5m)).Code);
        Assert.AreEqual("VersionConflict",(await owner.MoveLogicalNodeAsync(node.Code,node.Version+1,110.5m,110.5m)).Code);
        Assert.AreEqual("InvalidLogicalNodeCoordinate",(await owner.MoveLogicalNodeAsync(node.Code,node.Version,110,110.5m)).Code);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        // A different user's road was committed after the movement source snapshot was read.
        var road=await Add(other,110,110,110,130);before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LineNodeInsideLogicalNode",(await owner.MoveLogicalNodeAsync(node.Code,node.Version,110.5m,110.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await other.DeleteAsync(road.Code,road.Version)).Success);
        var moved=(await admin.MoveLogicalNodeAsync(node.Code,node.Version,110.5m,110.5m)).Data!;
        Assert.AreEqual("owner",moved.Owner);Assert.IsTrue(moved.IsStart);
        Assert.AreEqual("VersionConflict",(await owner.MoveLogicalNodeAsync(node.Code,node.Version,210.5m,210.5m)).Code);
        Assert.IsTrue((await owner.DeleteLogicalNodeAsync(moved.Code,moved.Version)).Success);
        Assert.AreEqual("LogicalNodeNotFound",(await owner.MoveLogicalNodeAsync(moved.Code,moved.Version,210.5m,210.5m)).Code);
    });

    [TestMethod,DataRow("road"),DataRow("ln"),DataRow("cross")]
    public Task TwoServersSerializeMoveAgainstConflictingRoadSquareOrCrossState(string scenario)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(http1,first,"alice");var bob=await TestLogin.ClientAsync(http2,second,"bob");
        for(var i=0;i<3;i++)
        {
            var y=110+i*100;var node=(await alice.CreateLogicalNodeAsync(50.5m,y+0.5m,6,new())).Data!;
            Task<bool> conflict;
            CrossSnapshot? cross=null;LineSnapshot? road=null;
            if(scenario=="road")road=(await bob.CreateAsync()).Data!;
            if(scenario=="cross")
            {
                var a=await Add(alice,90,y,130,y);var b=await Add(bob,110,y-20,110,y+20);while((await alice.CalculateCrossAsync()).Data!.CanContinue){}
                cross=(await alice.GetCrossesAsync()).Data!.Single(c=>c.Key.Contains(a.Code)&&c.Key.Contains(b.Code));
                cross=(await alice.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Data!;
            }
            var move=alice.MoveLogicalNodeAsync(node.Code,node.Version,110.5m,y+0.5m);
            conflict=scenario switch
            {
                "road"=>Succeeded(bob.SaveAsync(road!.Code,road.Version,TestMap.Shape(road,110,y+1,110,y+20).Nodes)),
                "ln"=>Succeeded(bob.CreateLogicalNodeAsync(110.5m,y+0.5m,6,new())),
                _=>Succeeded(bob.SetCrossStateAsync(cross!.Key,cross.Version,CrossState.Connected))
            };
            await Task.WhenAll(move,conflict);
            Assert.AreEqual(1,(move.Result.Success?1:0)+(conflict.Result?1:0),"Only one conflicting operation may commit under the shared map transaction lock.");
            Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
            var stored=(await alice.GetLogicalNodesAsync()).Data!.Single(n=>n.Code==node.Code);
            Assert.AreEqual(move.Result.Success?110.5m:50.5m,stored.X);Assert.AreEqual(move.Result.Success?node.Version+1:node.Version,stored.Version);
        }
    });

    [TestMethod]
    public Task TwoServersSameNodeMovementVersusMetadataEdit_ExactlyOneVersionCommits()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var owner=await TestLogin.ClientAsync(http1,first,"owner");var sameSession=new HttpLineClient(http2,owner.Session);
        var node=(await owner.CreateLogicalNodeAsync(50.5m,50.5m,6,new())).Data!;
        var move=owner.MoveLogicalNodeAsync(node.Code,node.Version,100.5m,100.5m);
        var update=sameSession.UpdateLogicalNodeAsync(node.Code,node.Version,"新值","新说明");await Task.WhenAll(move,update);
        Assert.AreEqual(1,(move.Result.Success?1:0)+(update.Result.Success?1:0));Assert.AreEqual("VersionConflict",move.Result.Success?update.Result.Code:move.Result.Code);
        var stored=(await owner.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(node.Version+1,stored.Version);
        Assert.AreEqual(move.Result.Success?100.5m:50.5m,stored.X);Assert.AreEqual(update.Result.Success?"新值":node.Properties.Value,stored.Properties.Value);
    });

    private static async Task<bool> Succeeded<T>(Task<ServiceResult<T>> result)=>(await result).Success;
}
