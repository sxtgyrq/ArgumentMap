using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class MapAreaDeleteMySqlTests
{
    private static async Task<(MapAreaSelection Selection,LogicalNodeSnapshot Kept)> Fixture(HttpLineClient client)
    {
        await Add(client,20,14,80,14);await Add(client,30,12,30,80);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        Assert.IsTrue((await client.CreateLogicalNodeAsync(30.5m,50.5m,18,new(){Value="开始",LogicLanguage="S"})).Success);
        var kept=(await client.CreateLogicalNodeAsync(50.5m,12.5m,6,new(){Value="保留",Info="仅清理关联",LogicLanguage="P"})).Data!;
        var selected=await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds);Assert.IsTrue(selected.Success,selected.Message);Assert.AreEqual(1,selected.Data!.LogicalNodes.Length);
        return(selected.Data,kept);
    }

    [TestMethod]
    public Task HttpDeletionPersistsAllCleanup_UpdatesRetainedLnVersion_KJAndRestartAreStable()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var fixture=await Fixture(client);var result=await client.DeleteMapAreaAsync(fixture.Selection);Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(0,result.Data!.Lines.Length);Assert.AreEqual(0,result.Data.Crosses.Length);var kept=result.Data.LogicalNodes.Single();Assert.AreEqual(fixture.Kept.Code,kept.Code);Assert.AreEqual(0,kept.Positions.Length);Assert.AreEqual(fixture.Kept.Version+1,kept.Version);Assert.AreEqual(fixture.Kept.Properties,kept.Properties);
        var reopened=new MySqlLineRepository(settings);reopened.Transaction(s=>{Assert.AreEqual(0,s.Lines.Count+s.Crosses.Count+s.LogicalNodePositions.Count+s.PendingSegments.Count+s.StaleCrosses.Count+s.DirtyLines.Count);Assert.AreEqual(1,s.LogicalNodes.Count);return 0;});
        var before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.IsTrue((await client.CalculateCrossAsync()).Success);Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsFalse((await client.DeleteMapAreaAsync(fixture.Selection)).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await client.CreateLogicalNodeAsync(30.5m,50.5m,18,new(){Value="新开始"})).Success);
    });

    [TestMethod,DataRow("Crosses","DELETE"),DataRow("Lines","DELETE"),DataRow("LogicalNodes","DELETE"),DataRow("LogicalNodes","UPDATE"),DataRow("LogicalNodePositions","DELETE")]
    public Task SqlFailureRollsBackFiveTablesVersionsFlagsAndTimestamps_ThenSameSelectionCanRetry(string table,string action)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");var fixture=await Fixture(client);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        // Fail the second road deletion to verify that an earlier road deletion also rolls back.
        var sql=table=="Lines" ? $"CREATE TRIGGER FailAreaDelete BEFORE DELETE ON `Lines` FOR EACH ROW BEGIN IF OLD.LineCode='{fixture.Selection.Lines[1].Code}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected second road failure'; END IF; END"
            : $"CREATE TRIGGER FailAreaDelete BEFORE {action} ON `{table}` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected area deletion failure'";
        Sql(settings,sql);
        try{var failed=await client.DeleteMapAreaAsync(fixture.Selection);Assert.AreEqual("DatabaseError",failed.Code,failed.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailAreaDelete");}
        var result=await client.DeleteMapAreaAsync(fixture.Selection);Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(0,result.Data!.Lines.Length);Assert.AreEqual(0,result.Data.Crosses.Length);Assert.AreEqual(0,result.Data.LogicalNodes.Single().Positions.Length);
    });

    [TestMethod]
    public Task AuthPermissionsAndCrossVersionAreRechecked_AndOldSessionCannotDelete()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var owner=await TestLogin.ClientAsync(http,server,"owner");var other=await TestLogin.ClientAsync(http,server,"other");var fixture=await Fixture(owner);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual("LoginExpired",(await new HttpLineClient(http,new("owner","invalid")).DeleteMapAreaAsync(fixture.Selection)).Code);
        Assert.AreEqual("NotOwner",(await other.DeleteMapAreaAsync(fixture.Selection)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var cross=fixture.Selection.Crosses.Single();Assert.IsTrue((await owner.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Connected)).Success);before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("SelectionChanged",(await owner.DeleteMapAreaAsync(fixture.Selection)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var refreshed=(await owner.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;Assert.IsTrue((await owner.DeleteMapAreaAsync(refreshed)).Success);
    });

    [TestMethod]
    public Task TwoServersMovingAndDeletingSameSelection_ExactlyOneCommits()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};var owner=await TestLogin.ClientAsync(http1,first,"owner");var peer=new HttpLineClient(http2,owner.Session);var fixture=await Fixture(owner);
        var deleting=owner.DeleteMapAreaAsync(fixture.Selection);var moving=peer.MoveMapAreaAsync(fixture.Selection,100,100);await Task.WhenAll(deleting,moving);
        Assert.AreEqual(1,(deleting.Result.Success?1:0)+(moving.Result.Success?1:0));var reopened=new MySqlLineRepository(settings);
        Assert.AreEqual(deleting.Result.Success?0:2,new LineSaveService(reopened).GetAll().Data!.Length);Assert.IsNull(reopened.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
        Assert.IsTrue(new CrossService(reopened).Recalculate().Success);Assert.IsTrue(new LogicalNodeService(reopened).Calculate().Success);
    });
}
