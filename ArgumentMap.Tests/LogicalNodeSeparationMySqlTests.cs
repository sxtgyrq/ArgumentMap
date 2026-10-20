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
public sealed class LogicalNodeSeparationMySqlTests
{
    [TestMethod,DataRow(5,0),DataRow(6,0),DataRow(6,6)]
    public Task ConcurrentCreatesOfOverlappingOrTouchingSquares_OnlyOneCommitsAcrossTwoServers(int dx,int dy)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        var results=await Task.WhenAll(alice.CreateLogicalNodeAsync(110.5m,110.5m,6,new()),bob.CreateLogicalNodeAsync(110.5m+dx,110.5m+dy,6,new()));
        Assert.AreEqual(1,results.Count(r=>r.Success));Assert.AreEqual("LogicalNodesTouchOrOverlap",results.Single(r=>!r.Success).Code);
        var winner=results.Single(r=>r.Success).Data!;var reader=new LogicalNodeService(new MySqlLineRepository(settings));
        Assert.AreEqual(winner.Code,reader.GetAll().Data!.Single().Code);Assert.AreEqual(0,repository.Transaction(s=>s.LogicalNodePositions.Count));
        var before=Snapshot(repository);Assert.AreEqual("LogicalNodesTouchOrOverlap",(await alice.CreateLogicalNodeAsync(winner.X,winner.Y,12,new())).Code);Assert.AreEqual(before,Snapshot(repository));
        var client=winner.Owner=="alice"?alice:bob;Assert.IsTrue((await client.DeleteLogicalNodeAsync(winner.Code,winner.Version)).Success);
        Assert.IsTrue((await bob.CreateLogicalNodeAsync(110.5m,110.5m,12,new())).Success);
    });
    [TestMethod]
    public Task ValidationAndLateSqlFailureRollBackPositions_ValidSeparatedRetryCommits()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,90,110,140,110);var service=new LogicalNodeService(repository);
        var first=service.Create(new("owner",110.5m,110.5m,6,new())).Data!;var before=Snapshot(repository);
        Assert.AreEqual("LogicalNodesTouchOrOverlap",service.Create(new("other",116.5m,110.5m,6,new())).Code);Assert.AreEqual(before,Snapshot(repository));
        Sql(settings,"CREATE TRIGGER TestLnSeparationFailure BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected separated-LN position failure'");
        try{Assert.ThrowsExactly<MySqlException>(()=>service.Create(new("other",117.5m,110.5m,6,new())));Assert.AreEqual(before,Snapshot(repository));}
        finally{Sql(settings,"DROP TRIGGER TestLnSeparationFailure");}
        var second=service.Create(new("other",117.5m,110.5m,6,new()));Assert.IsTrue(second.Success,second.Message);Assert.AreEqual(1,second.Data!.Positions.Length);
        Assert.AreEqual(2,new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Length);
        Assert.IsTrue(service.Delete(new(first.Code,first.Owner,first.Version)).Success);
        Assert.IsTrue(service.Create(new("owner",110.5m,110.5m,6,new())).Success);
        return Task.CompletedTask;
    });
}
