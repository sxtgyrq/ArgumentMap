using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeConnectionTests;
using static ArgumentMap.Tests.LogicalNodeGoToTests;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class ConnectionExtensionCrossTests
{
    internal static (LogicalNodeConnectionPlan Plan,LineSnapshot JoinRoad,LineSnapshot OtherRoad) Fixture(ILineRepository repo,bool finding,bool bidirectional)
    {
        var a=Node(repo,50.5m,50.5m);var b=Node(repo,120.5m,150.5m);
        var road=Add(repo,120,0,120,50,120,200);
        // The join is an existing road vertex: the new road extends to x=121.
        // This separate road intersects ONLY that one-unit extension at its end.
        var other=Add(repo,121,20,121,80);
        if(finding)
        {
            road=new LineSaveService(repo).Reverse(new(road.Code,"owner",road.Version)).Data!;
            (a,b)=(b,a);
        }
        if(bidirectional){SetBidirectional(repo,road);SetBidirectional(repo,other);}
        var code=LineCode.Generate();
        var nodes=new[]{new LineNode(code,0,42,50),new LineNode(code,1,121,50)};
        if(finding)nodes=nodes.Reverse().Select((n,i)=>n with{Order=i}).ToArray();
        return(new(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),code,nodes,
            finding?LogicalNodeConnectionKind.Find:LogicalNodeConnectionKind.GoTo,new(road.Code,new(240,100))),road,other);
    }

    internal static void CheckCrosses(MapAreaSnapshot map,LogicalNodeConnectionPlan plan,LineSnapshot joinRoad,LineSnapshot other)
    {
        var crosses=map.Crosses.Where(c=>c.Key.Contains(plan.LineCode)).ToArray();Assert.AreEqual(2,crosses.Length);
        var join=crosses.Single(c=>c.Key.Contains(joinRoad.Code));Assert.AreEqual(new CrossPosition(240,100),join.Key.Position);
        Assert.AreEqual(CrossState.Connected,join.State);
        var extension=crosses.Single(c=>c.Key.Contains(other.Code));Assert.AreEqual(new CrossPosition(242,100),extension.Key.Position);
        Assert.AreEqual(CrossState.Disconnected,extension.State);
        Assert.IsFalse(extension.AllowsTransfer(plan.LineCode,other.Code));Assert.IsFalse(extension.AllowsTransfer(other.Code,plan.LineCode));
    }

    [TestMethod,DataRow(false,false),DataRow(false,true),DataRow(true,false),DataRow(true,true)]
    public void ExtensionCrossIsO_ExactJoinIsI_KPreservesBoth_AndUndoRemovesBoth(bool finding,bool bidirectional)
    {
        var repo=new InMemoryLineRepository();var f=Fixture(repo,finding,bidirectional);var before=MapUndoTests.Content(repo);
        var saved=Save(repo,f.Plan);Assert.IsTrue(saved.Success,saved.Message);Assert.AreEqual(1,saved.Data!.DisconnectedCrosses);
        CheckCrosses(saved.Data.Map,f.Plan,f.JoinRoad,f.OtherRoad);
        Assert.IsTrue(new CrossService(repo).Recalculate().Success);
        var map=new MapAreaSnapshot([],[],new CrossService(repo).GetAll().Data!);CheckCrosses(map,f.Plan,f.JoinRoad,f.OtherRoad);
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual(before,MapUndoTests.Content(repo));
        Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class ConnectionExtensionCrossMySqlTests
{
    [TestMethod,DataRow(false,false),DataRow(false,true),DataRow(true,false),DataRow(true,true)]
    public Task HttpSavePersistsExtensionO_AndRestartKAndUndoKeepItCorrect(bool finding,bool bidirectional)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var f=ConnectionExtensionCrossTests.Fixture(repo,finding,bidirectional);var before=MapUndoTests.Content(repo);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var saved=await client.SaveLogicalNodeConnectionAsync(f.Plan);Assert.IsTrue(saved.Success,saved.Message);Assert.AreEqual(1,saved.Data!.DisconnectedCrosses);
        ConnectionExtensionCrossTests.CheckCrosses(saved.Data.Map,f.Plan,f.JoinRoad,f.OtherRoad);
        var reopened=new MySqlLineRepository(settings);
        ConnectionExtensionCrossTests.CheckCrosses(new([],[],new CrossService(reopened).GetAll().Data!),f.Plan,f.JoinRoad,f.OtherRoad);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        ConnectionExtensionCrossTests.CheckCrosses(new([],[],(await client.GetCrossesAsync()).Data!),f.Plan,f.JoinRoad,f.OtherRoad);
        Assert.IsTrue((await client.UndoAsync()).Success);Assert.AreEqual(before,MapUndoTests.Content(repo));
        Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);
    });
}
