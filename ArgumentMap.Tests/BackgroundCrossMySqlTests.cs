using System.Net.Http;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class BackgroundCrossMySqlTests
{
    private static async Task Finish(HttpLineClient client)
    {
        for(var i=0;i<100;i++){var step=await client.CalculateCrossAsync();Assert.IsTrue(step.Success,step.Message);if(!step.Data!.CanContinue)return;}
        Assert.Fail("K did not complete.");
    }

    [TestMethod]
    public Task EditingBetweenKSegmentsRequeuesLatestGeometry_RollbackKeepsWork_AndCleanupReusesSettings()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,20,50,180,50,220,50);await Add(client,80,20,80,80);await Add(client,160,20,160,80);
        var ln=(await client.CreateLogicalNodeAsync(30.5m,50.5m,6,new(){LogicLanguage="P"})).Data!;await Finish(client);
        foreach(var c in (await client.GetCrossesAsync()).Data!)Assert.IsTrue((await client.SetCrossStateAsync(c.Key,c.Version,c.Key.Position.X==80?CrossState.Connected:CrossState.Disconnected)).Success);
        var saved=await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,20,50,60,50,180,50,220,50).Nodes);Assert.IsTrue(saved.Success,saved.Message);road=saved.Data!;
        var first=await client.CalculateCrossAsync();Assert.IsTrue(first.Success,first.Message);Assert.IsTrue(first.Data!.CanContinue);Assert.AreEqual(2,repository.Transaction(s=>s.PendingSegments.Count));
        // Save newer geometry before K has finished the prior revision. The old far
        // intersection vanishes; the surviving intersection must retain its setting.
        saved=await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,20,50,40,50,100,50,120,50).Nodes);Assert.IsTrue(saved.Success,saved.Message);road=saved.Data!;
        Assert.AreEqual(3,repository.Transaction(s=>s.PendingSegments.Count(p=>p.LineCode==road.Code)));Assert.AreEqual(2,repository.Transaction(s=>s.StaleCrosses.Count));
        ln=(await client.GetLogicalNodesAsync()).Data!.Single();var moved=await client.MoveLogicalNodeAsync(ln.Code,ln.Version,50.5m,50.5m);Assert.IsTrue(moved.Success,moved.Message);
        await Add(client,110,20,110,80);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailBackgroundSegment BEFORE UPDATE ON LineNodes FOR EACH ROW BEGIN IF OLD.IsChanged<0 AND NEW.IsChanged=0 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected segment failure'; END IF; END");
        try{Assert.AreEqual("DatabaseError",(await client.CalculateCrossAsync()).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailBackgroundSegment");}
        await Finish(client);var crosses=(await client.GetCrossesAsync()).Data!;Assert.AreEqual(2,crosses.Length);
        Assert.AreEqual(CrossState.Connected,crosses.Single(c=>c.Key.Position.X==80).State);Assert.AreEqual(CrossState.Unset,crosses.Single(c=>c.Key.Position.X==110).State);Assert.IsFalse(crosses.Any(c=>c.Key.Position.X==160));
        Assert.IsTrue(repository.Transaction(s=>s.PendingSegments.Count==0&&s.StaleCrosses.Count==0&&s.DirtyLines.Count==0));
        ln=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(50.5m,ln.X);Assert.AreEqual("P",ln.Properties.LogicLanguage);Assert.AreEqual(road.Code,ln.Positions.Single().RoadCode);Assert.AreEqual(road.Version,ln.Positions.Single().RoadVersion);Assert.AreEqual(1,ln.Positions.Single().RoadOrder);Assert.AreEqual(.175,ln.Positions.Single().RoadPercent,1e-15);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod]
    public Task TwoServersInterleaveKWithLineSaveAndLnMove_ThenFinishWithoutLosingCommittedEdits()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var client=await TestLogin.ClientAsync(h1,first,"owner");var editor=new HttpLineClient(h2,client.Session);
        var road=await Add(client,20,50,100,50,180,50);await Add(client,80,20,80,80);await Finish(client);var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Road1ToRoad2)).Success);
        var ln=(await client.CreateLogicalNodeAsync(300.5m,300.5m,6,new(){LogicLanguage="P"})).Data!;
        var shape=TestMap.Shape(road,20,50,40,50,100,50,180,50).Nodes;
        // Put existing work in the queue, then race a fresh replacement and an
        // unrelated LN move against a segment transaction on another server.
        var changed=await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,20,50,60,50,100,50,180,50).Nodes);Assert.IsTrue(changed.Success,changed.Message);road=changed.Data!;
        var k=client.CalculateCrossAsync();var save=editor.SaveAsync(road.Code,road.Version,shape);var move=editor.MoveLogicalNodeAsync(ln.Code,ln.Version,320.5m,300.5m);
        await Task.WhenAll(k,save,move);Assert.IsTrue(k.Result.Success,k.Result.Message);Assert.IsTrue(save.Result.Success,save.Result.Message);Assert.IsTrue(move.Result.Success,move.Result.Message);
        await Finish(client);var current=(await client.GetAllAsync()).Data!.Single(r=>r.Code==road.Code);CollectionAssert.AreEqual(shape,current.Nodes);Assert.AreEqual(save.Result.Data!.Version,current.Version);
        Assert.AreEqual(320.5m,(await client.GetLogicalNodesAsync()).Data!.Single().X);Assert.AreEqual(CrossState.Road1ToRoad2,(await client.GetCrossesAsync()).Data!.Single().State);
        Assert.IsTrue(repository.Transaction(s=>s.PendingSegments.Count==0&&s.StaleCrosses.Count==0));Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });
}
