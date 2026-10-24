using System.Net.Http;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class BackgroundMapOperationsMySqlTests
{
    private static async Task Finish(HttpLineClient client)
    {for(var i=0;i<100;i++){var step=await client.CalculateCrossAsync();Assert.IsTrue(step.Success,step.Message);if(!step.Data!.CanContinue)return;}Assert.Fail("K did not finish.");}

    [TestMethod,DataRow(false),DataRow(true)]
    public Task KMayRestoreSelectedCrossBeforeGroupDrop_MoveIsAtomicAndPreservesUnrelatedPendingWork(bool injectFailure)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var client=await TestLogin.ClientAsync(h1,first,"owner");var other=new HttpLineClient(h2,client.Session);
        var road=await Add(client,20,50,80,50);await Add(client,50,20,50,80);await Finish(client);var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        var node=(await client.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){LogicLanguage="P"})).Data!;
        Assert.IsTrue((await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,20,50,40,50,80,50).Nodes)).Success);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var selected=await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds);Assert.IsTrue(selected.Success,selected.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await other.CalculateCrossAsync()).Success);Assert.IsTrue((await other.CalculateCrossAsync()).Success);
        Assert.AreEqual(selected.Data!.Crosses.Single().Version,(await client.GetCrossesAsync()).Data!.Single().Version);
        var unrelated=await Add(other,300,300,330,300);before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(injectFailure)
        {
            Sql(settings,"CREATE TRIGGER FailPendingAreaMove BEFORE INSERT ON Crosses FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected group move failure'");
            try{Assert.AreEqual("DatabaseError",(await client.MoveMapAreaAsync(selected.Data,100,100)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
            finally{Sql(settings,"DROP TRIGGER FailPendingAreaMove");}
        }
        var moved=await client.MoveMapAreaAsync(selected.Data,100,100);Assert.IsTrue(moved.Success,moved.Message);var current=moved.Data!.Crosses.Single();Assert.AreEqual(CrossState.Disconnected,current.State);Assert.AreEqual(new CrossPosition(300,300),current.Key.Position);
        var ln=moved.Data.LogicalNodes.Single();Assert.AreEqual(node.Code,ln.Code);Assert.AreEqual(150.5m,ln.X);Assert.AreEqual(2,ln.Positions.Length);Assert.IsTrue(ln.Positions.All(p=>!p.IsChanged));
        Assert.IsTrue(repository.Transaction(s=>s.PendingSegments.SetEquals(new[]{(unrelated.Code,0)})&&s.DirtyLines.SetEquals(new[]{unrelated.Code})));
        await Finish(other);Assert.AreEqual(current,(await client.GetCrossesAsync()).Data!.Single());Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod,DataRow(false),DataRow(true)]
    public Task ImportBetweenKSegmentsPreservesOldSettingsAndPendingWork_AndRollsBackWholeDiagramOnFailure(bool injectFailure)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var client=await TestLogin.ClientAsync(h1,first,"owner");var other=new HttpLineClient(h2,client.Session);
        var road=await Add(client,20,50,80,50);await Add(client,50,20,50,80);await Finish(client);var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Connected)).Success);
        Assert.IsTrue((await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,20,50,40,50,80,50).Nodes)).Success);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var preview=await client.PreviewFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var step=await other.CalculateCrossAsync();Assert.IsTrue(step.Success,step.Message);Assert.IsTrue(step.Data!.CanContinue);before=LineTopologyMySqlTests.RawSnapshot(settings);var pending=repository.Transaction(s=>s.PendingSegments.ToHashSet());var dirty=repository.Transaction(s=>s.DirtyLines.ToHashSet());
        if(injectFailure)
        {
            Sql(settings,"CREATE TRIGGER FailImportDuringK BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected import failure'");
            try{Assert.AreEqual("DatabaseError",(await client.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
            finally{Sql(settings,"DROP TRIGGER FailImportDuringK");}
        }
        var imported=await client.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m);Assert.IsTrue(imported.Success,imported.Message);Assert.IsTrue(repository.Transaction(s=>s.PendingSegments.SetEquals(pending)&&s.DirtyLines.SetEquals(dirty)));
        Assert.AreEqual(CrossState.Connected,repository.Transaction(s=>s.Crosses[cross.Key].State));
        Assert.AreEqual(repository.Transaction(s=>!s.StaleCrosses.Contains(cross.Key)),imported.Data!.Map!.Crosses.Any(c=>c.Key==cross.Key));
        Assert.IsTrue(imported.Data.Added.Crosses.All(c=>c.State==CrossState.Connected));
        await Finish(other);var all=(await client.GetCrossesAsync()).Data!;Assert.AreEqual(3,all.Length);Assert.IsTrue(all.All(c=>c.State==CrossState.Connected));Assert.AreEqual(10,(await client.GetLogicalNodesAsync()).Data!.Length);Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });
}
