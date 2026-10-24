using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeConnectionTests;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;
using static ArgumentMap.Tests.LogicalNodeGoToTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeGoToMySqlTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public Task HttpDiagonalIntegerJoinFindsNeighboringApproach_RestartsAndUndoes(bool find)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var offRoad=Node(repo,50.5m,101.5m);var onRoad=Node(repo,200.5m,300.5m);
        var old=Add(repo,0,0,200,200,200,350);SetBidirectional(repo,old);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};
        var client=await TestLogin.ClientAsync(http,server,"owner");var before=MapUndoTests.Content(repo);
        var preview=await client.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(find?onRoad:offRoad),
            LogicalNodeConnectionAnchor.From(find?offRoad:onRoad),find?LogicalNodeConnectionKind.Find:LogicalNodeConnectionKind.GoTo);
        Assert.IsTrue(preview.Success,preview.Message);var plan=preview.Data!;
        Assert.IsTrue(plan.Join!.Position.IsInteger);Assert.AreEqual(old.Code,plan.Join.RoadCode);
        Assert.AreEqual(13+22*Math.Sqrt(2),NewLength(plan),1e-8);
        var saved=await client.SaveLogicalNodeConnectionAsync(plan);Assert.IsTrue(saved.Success,saved.Message);
        var cross=saved.Data!.Map.Crosses.Single(c=>c.Key.Contains(plan.LineCode));
        Assert.AreEqual(plan.Join.Position,cross.Key.Position);Assert.AreEqual(CrossState.Connected,cross.State);
        var restarted=new MySqlLineRepository(settings);Assert.AreEqual(MapUndoTests.Content(repo),MapUndoTests.Content(restarted));
        Assert.IsTrue(new MapUndoService(restarted).Undo("owner").Success);Assert.AreEqual(before,MapUndoTests.Content(repo));
    });

    [TestMethod]
    public Task HttpGoToPreservesOtherCrossesAndPending_RealKReuseRestartRetryAndUndo()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=Node(repo,50.5m,40.5m);var b=Node(repo,200.5m,200.5m);
        var main=Add(repo,200,0,200,250);var feeder=Add(repo,10,50,240,50);Transfer(repo,feeder,main);
        var oldCross=new CrossService(repo).GetAll().Data!.Single();var pending=Add(repo,300,300,350,300);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var before=Snapshot(repo);var content=MapUndoTests.Content(repo);
        var plan=await client.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),LogicalNodeConnectionKind.GoTo);
        Assert.IsTrue(plan.Success,plan.Message);Assert.AreEqual(before,Snapshot(repo));Assert.AreEqual(feeder.Code,plan.Data!.Join!.RoadCode);
        var saved=await client.SaveLogicalNodeConnectionAsync(plan.Data);Assert.IsTrue(saved.Success,saved.Message);
        var crossing=saved.Data!.Map.Crosses.Single(c=>c.Key.Contains(saved.Data.Line.Code));Assert.AreEqual(CrossState.Connected,crossing.State);
        Assert.IsTrue(crossing.AllowsTransfer(saved.Data.Line.Code,feeder.Code));Assert.IsTrue(crossing.AllowsTransfer(feeder.Code,saved.Data.Line.Code));
        Assert.AreEqual(oldCross,saved.Data.Map.Crosses.Single(c=>c.Key==oldCross.Key));
        Assert.IsTrue(repo.Transaction(s=>s.PendingSegments.Any(p=>p.LineCode==pending.Code)));
        Assert.IsFalse(repo.Transaction(s=>s.PendingSegments.Any(p=>p.LineCode==saved.Data.Line.Code)));
        var after=Snapshot(repo);Assert.IsTrue((await client.SaveLogicalNodeConnectionAsync(plan.Data)).Success);Assert.AreEqual(after,Snapshot(repo));
        Assert.AreEqual(after,Snapshot(new MySqlLineRepository(settings)));
        // Later ordinary background K must retain the ordinary I connection.
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var refreshed=(await client.GetCrossesAsync()).Data!.Single(c=>c.Key==crossing.Key);Assert.AreEqual(crossing.State,refreshed.State);
        Assert.IsTrue((await client.UndoAsync()).Success);Assert.AreEqual(content,MapUndoTests.Content(repo));Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);
    });
    [TestMethod,DataRow("cross"),DataRow("journal")]
    public Task GoToPersistenceFailureRollsBackEveryStage(string stage)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=Node(repo,50.5m,50.5m);var b=Node(repo,100.5m,150.5m);Add(repo,100,0,100,200);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var plan=(await client.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),LogicalNodeConnectionKind.GoTo)).Data!;
        var before=Snapshot(repo);var table=stage=="cross"?"Crosses":"MapUndoHistory";
        Sql(settings,$"CREATE TRIGGER FailGoTo BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected goto failure'");
        try{Assert.AreEqual("DatabaseError",(await client.SaveLogicalNodeConnectionAsync(plan)).Code);Assert.AreEqual(before,Snapshot(repo));}
        finally{Sql(settings,"DROP TRIGGER FailGoTo");}
        Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);Assert.IsTrue((await client.SaveLogicalNodeConnectionAsync(plan)).Success);
    });
    [TestMethod]
    public Task AnotherServerChangingContinuationRejectsPreviewWithoutTouchingItsEdit()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=Node(repo,50.5m,50.5m);var b=Node(repo,100.5m,150.5m);
        var service=new LineSaveService(repo);var old=service.Create(new("bob")).Data!;
        old=service.CheckShapeIsOKThenSave(new(old.Code,"bob",old.Version,[new(old.Code,0,100,0),new(old.Code,1,100,200)])).Data!;
        await using var one=await LocalLineServer.StartAsync(repository:repo);await using var two=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=one.BaseAddress};using var h2=new HttpClient{BaseAddress=two.BaseAddress};
        var owner=await TestLogin.ClientAsync(h1,one,"owner");var bob=await TestLogin.ClientAsync(h2,two,"bob");
        var plan=(await owner.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),LogicalNodeConnectionKind.GoTo)).Data!;
        Assert.IsTrue((await bob.ReverseLineAsync(old.Code,old.Version)).Success);var before=Snapshot(repo);
        Assert.AreEqual("ConnectionPathChanged",(await owner.SaveLogicalNodeConnectionAsync(plan)).Code);Assert.AreEqual(before,Snapshot(repo));
        Assert.AreEqual("NothingToUndo",(await owner.UndoAsync()).Code);
    });
}
