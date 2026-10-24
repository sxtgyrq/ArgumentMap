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

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeFindMySqlTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public Task HttpFindUsesForwardContinuation_OrdinaryJoin_OtherCrossesO_KRetryRestartAndUndo(bool bidirectional)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=Node(repo,200.5m,200.5m);var b=Node(repo,50.5m,40.5m);
        var main=Add(repo,200,250,200,0);var branch=Add(repo,240,50,10,50);
        if(bidirectional){SetBidirectional(repo,main);SetBidirectional(repo,branch);}
        Transfer(repo,main,branch);var oldCross=new CrossService(repo).GetAll().Data!.Single();
        var pending=Add(repo,0,35,100,35);
        var marks=repo.Transaction(s=>(s.PendingSegments.ToHashSet(),s.DirtyLines.ToHashSet(),s.StaleCrosses.ToHashSet()));
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var content=MapUndoTests.Content(repo);
        var preview=await client.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),LogicalNodeConnectionKind.Find);
        Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var plan=preview.Data!;Assert.AreEqual(LogicalNodeConnectionKind.Find,plan.Kind);Assert.AreEqual(branch.Code,plan.Join!.RoadCode);Assert.AreEqual(a.Y,plan.Continuation[0].Y);
        var saved=await client.SaveLogicalNodeConnectionAsync(plan with{Continuation=[new("FAKE",0,0)]});Assert.IsTrue(saved.Success,saved.Message);
        var added=saved.Data!.Map.Crosses.Where(c=>c.Key.Contains(plan.LineCode)).ToArray();Assert.AreEqual(2,added.Length);
        var join=added.Single(c=>c.Key.Contains(branch.Code));Assert.AreEqual(CrossState.Connected,join.State);
        Assert.IsTrue(join.AllowsTransfer(branch.Code,plan.LineCode));Assert.IsTrue(join.AllowsTransfer(plan.LineCode,branch.Code));
        Assert.AreEqual(CrossState.Disconnected,added.Single(c=>c.Key.Contains(pending.Code)).State);
        Assert.AreEqual(oldCross,saved.Data.Map.Crosses.Single(c=>c.Key==oldCross.Key));
        repo.Transaction(s=>{Assert.IsTrue(marks.Item1.SetEquals(s.PendingSegments));Assert.IsTrue(marks.Item2.SetEquals(s.DirtyLines));Assert.IsTrue(marks.Item3.SetEquals(s.StaleCrosses));return true;});
        var after=LineTopologyMySqlTests.RawSnapshot(settings);Assert.IsTrue((await client.SaveLogicalNodeConnectionAsync(plan)).Success);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.AreEqual(MapUndoTests.Content(repo),MapUndoTests.Content(new MySqlLineRepository(settings)));
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        Assert.AreEqual(CrossState.Connected,(await client.GetCrossesAsync()).Data!.Single(c=>c.Key==join.Key).State);
        Assert.IsTrue((await client.UndoAsync()).Success);Assert.AreEqual(content,MapUndoTests.Content(repo));
        Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);
    });

    [TestMethod,DataRow("cross"),DataRow("journal"),DataRow("nodes"),DataRow("positions")]
    public Task FailureRollsBackEveryStageAndCanRetryWithoutDuplicateHistory(string phase)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=Node(repo,100.5m,50.5m);var b=Node(repo,50.5m,150.5m);Add(repo,100,0,100,200);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var preview=await client.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),LogicalNodeConnectionKind.Find);
        Assert.IsTrue(preview.Success,preview.Message);var plan=preview.Data!;
        var table=phase switch{"cross"=>"Crosses","journal"=>"MapUndoHistory","nodes"=>"LineNodes",_=>"LogicalNodePositions"};
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,$"CREATE TRIGGER FailFind BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected find failure'");
        try{Assert.AreEqual("DatabaseError",(await client.SaveLogicalNodeConnectionAsync(plan)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailFind");}
        Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);Assert.IsTrue((await client.SaveLogicalNodeConnectionAsync(plan)).Success);
        Assert.IsTrue((await client.UndoAsync()).Success);Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);
    });

    [TestMethod]
    public Task OtherServerReversingSourceRoadInvalidatesPreviewAndPreservesItsEdit()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=Node(repo,100.5m,50.5m);var b=Node(repo,50.5m,150.5m);
        var roads=new LineSaveService(repo);var old=roads.Create(new("bob")).Data!;
        old=roads.CheckShapeIsOKThenSave(new(old.Code,"bob",old.Version,[new(old.Code,0,100,0),new(old.Code,1,100,200)])).Data!;
        await using var one=await LocalLineServer.StartAsync(repository:repo);await using var two=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=one.BaseAddress};using var h2=new HttpClient{BaseAddress=two.BaseAddress};
        var owner=await TestLogin.ClientAsync(h1,one,"owner");var bob=await TestLogin.ClientAsync(h2,two,"bob");
        var preview=await owner.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),LogicalNodeConnectionKind.Find);Assert.IsTrue(preview.Success,preview.Message);
        Assert.IsTrue((await bob.ReverseLineAsync(old.Code,old.Version)).Success);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("ConnectionPathChanged",(await owner.SaveLogicalNodeConnectionAsync(preview.Data!)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.AreEqual("NothingToUndo",(await owner.UndoAsync()).Code);
    });
}
