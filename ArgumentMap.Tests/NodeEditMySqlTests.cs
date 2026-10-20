using System.Net.Http;
using System.Windows;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class NodeEditMySqlTests
{
    [TestMethod,DataRow("LineNodes"),DataRow("LogicalNodePositions"),DataRow("Crosses")]
    public Task EFindIsReadOnly_F6RollsBackAllDependentsOnFailure_AndRetryKeepsCrossState(string table)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,100,100,120,100);await Add(client,115,90,115,110);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var old=(await client.GetCrossesAsync()).Data!.Single();old=(await client.SetCrossStateAsync(old.Key,old.Version,CrossState.Road1ToRoad2)).Data!;
        var ln=(await client.CreateLogicalNodeAsync(110.5m,100.5m,6,new(){Value="节点编辑",Info="保留资料"})).Data!;
        var editor=new LineEditor(client);await FrontAdditionTests.Edit(editor,new(119.8,100));var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.IsTrue(await editor.MoveNodeAsync(new(119.8,100)));Assert.AreEqual(1,editor.MovingNodeOrder);
        Assert.IsTrue(await editor.MoveNodeAsync(new(140.2,100.1)),editor.Message);Assert.AreEqual(new GridPoint(140,100),editor.DraftNodes[1]);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Sql(settings,$"CREATE TRIGGER TestNodeMoveAtomic BEFORE {(table=="Crosses"?"UPDATE":"INSERT")} ON `{table}` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected node move failure'");
        try{Assert.IsFalse(await editor.CompleteAsync());}finally{Sql(settings,"DROP TRIGGER TestNodeMoveAtomic");}
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(LineEditorMode.Drawing,editor.Mode);Assert.AreEqual(new GridPoint(140,100),editor.DraftNodes[1]);Assert.IsFalse(editor.IsBusy);
        Assert.IsTrue(await editor.CompleteAsync(),editor.Message);var saved=(await client.GetLineDetailAsync(road.Code)).Data!;Assert.AreEqual(road.Version+1,saved.Version);Assert.AreEqual(140,saved.Nodes[1].X);LineSubdivisionTests.AssertSegments(saved);
        var current=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(ln.Version+1,current.Version);Assert.AreEqual(ln.Properties,current.Properties);
        var position=current.Positions.Single();Assert.AreEqual((0,.2625d),(position.RoadOrder,position.RoadPercent));Assert.AreEqual(saved.Version,position.RoadVersion);Assert.AreEqual((110.5m,100m),(position.X,position.Y));
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.AreEqual(old.Key,cross.Key);Assert.AreEqual(old.State,cross.State);Assert.AreEqual((0,.375d),cross.LocationOn(road.Code));
        before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod,DataRow("ln"),DataRow("cross")]
    public Task OtherUserChangesMapAfterEProposal_F6RejectsAndNewEFindsSafeAlternative(string kind)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        var road=await Add(alice,100,100,110,100);var proposal=await alice.FindNodePositionAsync(road.Code,road.Version,road.Nodes,1,140,100);Assert.IsTrue(proposal.Success);Assert.AreEqual(140,proposal.Data!.X);
        if(kind=="ln")Assert.IsTrue((await bob.CreateLogicalNodeAsync(140.5m,100.5m,6,new())).Success);
        else{await Add(bob,125,95,135,105);await Add(bob,125,105,135,95);while((await bob.CalculateCrossAsync()).Data!.CanContinue){}}
        var nodes=road.Nodes.ToArray();nodes[1]=nodes[1] with{X=proposal.Data.X,Y=proposal.Data.Y};var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual(kind=="ln"?"LineNodeInsideLogicalNode":"LinePassesCross",(await alice.SaveAsync(road.Code,road.Version,nodes)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var retry=await alice.FindNodePositionAsync(road.Code,road.Version,road.Nodes,1,140,100);Assert.IsTrue(retry.Success,retry.Message);Assert.AreEqual(kind=="ln"?137:129,retry.Data!.X);Assert.AreEqual(100,retry.Data.Y);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        nodes[1]=nodes[1] with{X=retry.Data.X,Y=retry.Data.Y};var saved=await alice.SaveAsync(road.Code,road.Version,nodes);Assert.IsTrue(saved.Success,saved.Message);LineSubdivisionTests.AssertSegments(saved.Data!);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod]
    public Task NodeSearchRequiresLiveSessionOwnerUnlockedAndCurrentVersion_WithoutAnyWrites()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};
        var owner=await TestLogin.ClientAsync(http,server,"owner");var outsider=await TestLogin.ClientAsync(http,server,"other");var admin=await TestLogin.ClientAsync(http,server,"admin");var road=await Add(owner,100,100,110,100);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var client in new[]{outsider,admin})Assert.AreEqual("NotOwner",(await client.FindNodePositionAsync(road.Code,road.Version,road.Nodes,1,120,100)).Code);
        var invalid=new HttpLineClient(http,new("owner","invalid"));Assert.AreEqual("LoginExpired",(await invalid.FindNodePositionAsync(road.Code,road.Version,road.Nodes,1,120,100)).Code);
        Assert.AreEqual("VersionConflict",(await owner.FindNodePositionAsync(road.Code,road.Version-1,road.Nodes,1,120,100)).Code);
        Assert.AreEqual("OutOfBounds",(await owner.FindNodePositionAsync(road.Code,road.Version,road.Nodes,1,500001,100)).Code);
        Assert.AreEqual("InvalidNodeOrder",(await owner.FindNodePositionAsync(road.Code,road.Version,road.Nodes,9,120,100)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        repository.Transaction(s=>{s.Lines[road.Code]=road with{IsLocked=true};return true;});before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LineIsLocked",(await owner.FindNodePositionAsync(road.Code,road.Version,road.Nodes,1,120,100)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });
}
