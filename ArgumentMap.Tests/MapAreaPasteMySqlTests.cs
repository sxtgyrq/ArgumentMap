using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class MapAreaPasteMySqlTests
{
    private static int Scalar(MySqlSettings settings,string sql)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText=sql;return Convert.ToInt32(q.ExecuteScalar());}

    [TestMethod]
    public Task HttpWholeGroupRollsBackEverySqlFailure_AndPreservesExistingIsChangedUntilKJRun()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);MapAreaPasteTests.Fixture(repository,CrossState.Road1ToRoad2);
        // Model a K/J unit not yet restored. Canonical source selection is read-only.
        Sql(settings,"UPDATE Crosses SET IsChanged=-1; UPDATE LogicalNodePositions SET IsChanged=-1");
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var selection=await client.SelectMapAreaAsync(MapAreaPasteTests.Bounds);Assert.IsTrue(selection.Success,selection.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var expired=new HttpLineClient(http,new("owner","invalid"));Assert.AreEqual("LoginExpired",(await expired.PasteMapAreaAsync(selection.Data!,300,300)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        foreach(var table in new[]{"LogicalNodes","LineNodes","Crosses","LogicalNodePositions"})
        {
            Sql(settings,$"CREATE TRIGGER TestAreaPasteFailure BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected group paste failure'");
            try{var failed=await client.PasteMapAreaAsync(selection.Data!,300,300);Assert.AreEqual("DatabaseError",failed.Code,failed.Message);}
            finally{Sql(settings,"DROP TRIGGER TestAreaPasteFailure");}
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings),table);
        }
        var oldCrossCount=Scalar(settings,"SELECT COUNT(*) FROM Crosses WHERE IsChanged=-1");var oldPositionCount=Scalar(settings,"SELECT COUNT(*) FROM LogicalNodePositions WHERE IsChanged=-1");
        var result=await client.PasteMapAreaAsync(selection.Data!,300,300);Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual("PEOPLE1",result.Data!.Added.LogicalNodes.Single(n=>n.SideLength==12).Properties.Value);Assert.AreEqual("P1",result.Data.Added.LogicalNodes.Single(n=>n.SideLength==6).Properties.LogicLanguage);
        Assert.AreEqual(oldCrossCount,Scalar(settings,"SELECT COUNT(*) FROM Crosses WHERE IsChanged=-1"));Assert.AreEqual(oldPositionCount,Scalar(settings,"SELECT COUNT(*) FROM LogicalNodePositions WHERE IsChanged=-1"));
        Assert.AreEqual(1,Scalar(settings,"SELECT COUNT(*) FROM Crosses WHERE IsChanged=0"));Assert.AreEqual(6,Scalar(settings,"SELECT COUNT(*) FROM LogicalNodePositions WHERE IsChanged=1"));
        var persisted=Snapshot(repository);Assert.AreEqual(persisted,Snapshot(new MySqlLineRepository(settings)));
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);
        Assert.AreEqual(0,Scalar(settings,"SELECT COUNT(*) FROM Crosses WHERE IsChanged=-1"));Assert.AreEqual(0,Scalar(settings,"SELECT COUNT(*) FROM LogicalNodePositions WHERE IsChanged=-1"));
        Assert.AreEqual(12,(await client.GetLogicalNodesAsync()).Data!.Length);
    });

    [TestMethod]
    public Task TwoServersAllocateNewNamesAtomically_OnlyOneCanPasteSamePlace_WhileOtherUserRunsKJ()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);MapAreaPasteTests.Fixture(repository);
        await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};using var h3=new HttpClient{BaseAddress=second.BaseAddress};
        var owner=await TestLogin.ClientAsync(h1,first,"owner");var bob=await TestLogin.ClientAsync(h2,second,"bob");var worker=await TestLogin.ClientAsync(h3,second,"worker");
        repository.Transaction(s=>{s.Accounts["bob"]=s.Accounts["bob"] with{EditAllLogicalNodes=true};return true;});
        // A node-only group can be administered by either authenticated account.
        var selected=await owner.SelectMapAreaAsync(new(40,40,220,60));Assert.IsTrue(selected.Success,selected.Message);Assert.AreEqual(0,selected.Data!.Lines.Length);
        await Add(worker,10000,10000,14000,10000);
        var background=Task.Run(async ()=>
        {
            for(var i=0;i<8;i++)Assert.IsTrue((await worker.CalculateCrossAsync()).Success);
            string? cursor=null;
            for(var i=0;i<100;i++){var step=await worker.CalculateLogicalNodeStepAsync(cursor);Assert.IsTrue(step.Success,step.Message);if(!step.Data!.CanContinue)break;cursor=step.Data.NextCode;}
        });
        var copies=await Task.WhenAll(owner.PasteMapAreaAsync(selected.Data,300,300),bob.PasteMapAreaAsync(selected.Data,600,600));
        foreach(var copy in copies)Assert.IsTrue(copy.Success,copy.Message);
        CollectionAssert.AreEquivalent(new[]{"PEOPLE1","PEOPLE2"},copies.Select(c=>c.Data!.Added.LogicalNodes.Single(n=>n.SideLength==12).Properties.Value).ToArray());
        CollectionAssert.AreEquivalent(new[]{"P1","P2"},copies.Select(c=>c.Data!.Added.LogicalNodes.Single(n=>n.SideLength==6).Properties.LogicLanguage).ToArray());
        Assert.IsTrue(copies[1].Data!.Added.LogicalNodes.All(n=>n.Owner=="bob"));
        await background;
        var collision=await Task.WhenAll(owner.PasteMapAreaAsync(selected.Data,1000,1000),bob.PasteMapAreaAsync(selected.Data,1000,1000));Assert.AreEqual(1,collision.Count(r=>r.Success));
        Assert.AreEqual(24,(await owner.GetLogicalNodesAsync()).Data!.Length);Assert.AreEqual(3,(await owner.GetAllAsync()).Data!.Length);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });
}
