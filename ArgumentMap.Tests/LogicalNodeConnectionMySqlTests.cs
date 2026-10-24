using System.Net.Http;
using System.Net.Http.Json;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeConnectionMySqlTests
{
    [TestMethod]
    public Task HttpSaveKDisconnectAndUndoPersist_KeepUnrelatedPendingAcrossTwoServers()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=LogicalNodeConnectionTests.Node(repo,50.5m,50.5m);var b=LogicalNodeConnectionTests.Node(repo,150.5m,50.5m);
        Add(repo,100,0,100,100);var pending=Add(repo,300,300,350,300);
        await using var server=await LocalLineServer.StartAsync(repository:repo);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        using var otherHttp=new HttpClient{BaseAddress=second.BaseAddress};var bob=await TestLogin.ClientAsync(otherHttp,second,"bob");
        var before=MapUndoTests.Content(repo);
        var preview=await client.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b));Assert.IsTrue(preview.Success,preview.Message);
        Assert.AreEqual(before,MapUndoTests.Content(repo));var expired=new HttpLineClient(http,new("owner","expired"));Assert.AreEqual("LoginExpired",(await expired.SaveLogicalNodeConnectionAsync(preview.Data!)).Code);
        var saved=await client.SaveLogicalNodeConnectionAsync(preview.Data!);Assert.IsTrue(saved.Success,saved.Message);Assert.IsTrue(saved.Data!.DisconnectedCrosses>0);
        Assert.IsTrue(saved.Data.Map.Crosses.Where(c=>c.Key.Contains(saved.Data.Line.Code)).All(c=>c.State==CrossState.Disconnected));
        Assert.IsTrue(repo.Transaction(s=>s.PendingSegments.Any(p=>p.LineCode==pending.Code)));Assert.IsFalse(repo.Transaction(s=>s.PendingSegments.Any(p=>p.LineCode==saved.Data.Line.Code)));
        Assert.IsTrue((await client.SaveLogicalNodeConnectionAsync(preview.Data!)).Success,"Lost-response retry is read-only.");
        Assert.AreEqual("ConnectionAlreadyChanged",(await bob.SaveLogicalNodeConnectionAsync(preview.Data!)).Code);
        Assert.AreEqual(Snapshot(repo),Snapshot(new MySqlLineRepository(settings)));
        var undo=await client.UndoAsync();Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(before,MapUndoTests.Content(repo));Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);
    });
    [TestMethod,DataRow("cross"),DataRow("journal")]
    public Task SqlFailureAtFinalPersistenceRollsBackSavedLineCrossesPositionsAndHistory(string stage)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=LogicalNodeConnectionTests.Node(repo,50.5m,50.5m);var b=LogicalNodeConnectionTests.Node(repo,150.5m,50.5m);Add(repo,100,0,100,100);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var plan=(await client.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b))).Data!;
        var before=Snapshot(repo);var table=stage=="cross"?"Crosses":"MapUndoHistory";
        Sql(settings,$"CREATE TRIGGER FailConnection BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected connection persistence failure'");
        try{Assert.AreEqual("DatabaseError",(await client.SaveLogicalNodeConnectionAsync(plan)).Code);Assert.AreEqual(before,Snapshot(repo));}
        finally{Sql(settings,"DROP TRIGGER FailConnection");}
        Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);Assert.IsTrue((await client.SaveLogicalNodeConnectionAsync(plan)).Success);
    });
    [TestMethod]
    public Task TwoUsersCompetingForSameRoute_OnlyOneCommits_ThenLateNodeMoveRejectsPreview()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var a=LogicalNodeConnectionTests.Node(repo,50.5m,50.5m);var b=LogicalNodeConnectionTests.Node(repo,150.5m,50.5m);
        await using var one=await LocalLineServer.StartAsync(repository:repo);await using var two=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=one.BaseAddress};using var h2=new HttpClient{BaseAddress=two.BaseAddress};var owner=await TestLogin.ClientAsync(h1,one,"owner");var bob=await TestLogin.ClientAsync(h2,two,"bob");
        var p1=(await owner.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b))).Data!;
        var p2=(await bob.PreviewLogicalNodeConnectionAsync(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b))).Data!;
        var replies=await Task.WhenAll(owner.SaveLogicalNodeConnectionAsync(p1),bob.SaveLogicalNodeConnectionAsync(p2));Assert.AreEqual(1,replies.Count(r=>r.Success));Assert.AreEqual(1,repo.Transaction(s=>s.Lines.Count));
        var current=repo.Transaction(s=>s.LogicalNodes[b.Code]);Assert.IsTrue(new LogicalNodeService(repo).Move(new(b.Code,"owner",current.Version,200.5m,80.5m)).Success);
        var before=Snapshot(repo);Assert.AreEqual("ConnectionNodeChanged",(await owner.SaveLogicalNodeConnectionAsync(p1)).Code);Assert.AreEqual(before,Snapshot(repo));
    });
}
