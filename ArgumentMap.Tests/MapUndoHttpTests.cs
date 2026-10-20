using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Http")]
public sealed class MapUndoHttpTests
{
    [TestMethod]
    public async Task AuthorizedHistoryCoversCreateEditMoveDelete_AndRejectedRequestsNeverConsumeHistory()
    {
        var repo = new InMemoryLineRepository(); await using var server = await LocalLineServer.StartAsync(repository: repo);
        using var http = new HttpClient { BaseAddress = server.BaseAddress }; var owner = await TestLogin.ClientAsync(http, server, "owner");
        Assert.AreEqual("NothingToUndo", (await owner.UndoAsync()).Code);
        var node = (await owner.CreateLogicalNodeAsync(50.5m, 50.5m, 6, new() { LogicLanguage = "P" })).Data!;
        node = (await owner.UpdateLogicalNodeAsync(node.Code, node.Version, "命题", "说明", "Q")).Data!;
        node = (await owner.MoveLogicalNodeAsync(node.Code, node.Version, 90.5m, 90.5m)).Data!;
        Assert.IsTrue((await owner.DeleteLogicalNodeAsync(node.Code, node.Version)).Success);
        var expired = new HttpLineClient(http, new("owner", "invalid")); Assert.AreEqual("LoginExpired", (await expired.UndoAsync()).Code);
        var undo = await owner.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual("删除 LN", undo.Data!.Operation);
        node = undo.Data.Map.LogicalNodes.Single(); Assert.AreEqual(90.5m, node.X); Assert.AreEqual("Q", node.Properties.LogicLanguage);
        undo = await owner.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual(50.5m, undo.Data!.Map.LogicalNodes.Single().X);
        undo = await owner.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual("P", undo.Data!.Map.LogicalNodes.Single().Properties.LogicLanguage);
        undo = await owner.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual(0, undo.Data!.Map.LogicalNodes.Length);
        Assert.AreEqual("NothingToUndo", (await owner.UndoAsync()).Code);
    }

    [TestMethod, DataRow("move"), DataRow("delete"), DataRow("paste"), DataRow("import")]
    public async Task AreaAndImportRoutesRecordExactlyOneUndo_AndBackgroundKJAreNotRecorded(string action)
    {
        var repo = new InMemoryLineRepository(); var selection = MapAreaPasteTests.Fixture(repo);
        if (action == "delete")
        {
            var child = selection.LogicalNodes.Single(n => n.SideLength == 10); new LogicalNodeService(repo).Delete(new(child.Code, "owner", child.Version));
            selection = MapAreaPasteTests.Select(repo, MapAreaPasteTests.Bounds);
        }
        var before = MapUndoTests.Content(repo);
        await using var server = await LocalLineServer.StartAsync(repository: repo); using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = await TestLogin.ClientAsync(http, server, "owner");
        if (action == "move") Assert.IsTrue((await client.MoveMapAreaAsync(selection, 300, 300)).Success);
        else if (action == "delete") Assert.IsTrue((await client.DeleteMapAreaAsync(selection)).Success);
        else if (action == "paste") Assert.IsTrue((await client.PasteMapAreaAsync(selection, 300, 300)).Success);
        else { var result = await client.ImportFlowDiagramAsync(FlowDiagramTests.Small().ToJsonString(), 1000.5m, 1000.5m); Assert.IsTrue(result.Success, result.Message); }
        while ((await client.CalculateCrossAsync()).Data!.CanContinue) { }
        Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);
        var undo = await client.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual(before, MapUndoTests.Content(repo));
        Assert.AreEqual("NothingToUndo", (await client.UndoAsync()).Code);
    }

    [TestMethod, DataRow("delete"), DataRow("save"), DataRow("reverse"), DataRow("property"), DataRow("split"), DataRow("lock"), DataRow("cross")]
    public async Task LineAndCrossOperationsUndoOriginalShapeAttributesAndDirection(string action)
    {
        var repo = new InMemoryLineRepository(); var road = LogicalNodeMySqlTests.Add(repo, 20, 50, 80, 50, 80, 100, 140, 100);
        LogicalNodeMySqlTests.Add(repo, 110, 80, 110, 120); var cross = new CrossService(repo); while (cross.Calculate().Data!.CanContinue) { }
        var before = MapUndoTests.Content(repo); await using var server = await LocalLineServer.StartAsync(repository: repo);
        using var http = new HttpClient { BaseAddress = server.BaseAddress }; var client = await TestLogin.ClientAsync(http, server, "owner");
        if (action == "lock") repo.Transaction(s => { s.Accounts["owner"] = s.Accounts["owner"] with { EditAllRoad = true }; return true; });
        switch (action)
        {
            case "delete": Assert.IsTrue((await client.DeleteAsync(road.Code, road.Version)).Success); break;
            case "save": Assert.IsTrue((await client.SaveAsync(road.Code, road.Version, TestMap.Shape(road, 20, 40, 80, 40, 80, 100, 140, 100).Nodes)).Success); break;
            case "reverse": Assert.IsTrue((await client.ReverseLineAsync(road.Code, road.Version)).Success); break;
            case "property": Assert.IsTrue((await client.SaveLinePropertiesAsync(road.Code, road.Version, new() { Speed = 299, PassInOpposeDirection = 1, RoadName = "属性" })).Success); break;
            case "split": { var r = await client.SplitLineAsync(road.Code, road.Version, 2); Assert.IsTrue(r.Success, r.Message); break; }
            case "lock": Assert.IsTrue((await client.ToggleLineLockAsync(road.Code, road.Version)).Success); break;
            case "cross": { var c = cross.GetAll().Data!.Single(); Assert.IsTrue((await client.SetCrossStateAsync(c.Key, c.Version, CrossState.Road1ToRoad2)).Success); break; }
        }
        var undo = await client.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual(before, MapUndoTests.Content(repo));
        Assert.AreEqual("NothingToUndo", (await client.UndoAsync()).Code);
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public async Task PastedLineIncludingLockedCopyCanUndoWithoutChangingSource(bool locked)
    {
        var repo = new InMemoryLineRepository(); var source = LogicalNodeMySqlTests.Add(repo,20,50,80,50,80,100,140,100);
        if(locked)repo.Transaction(s=>{s.Lines[source.Code]=source=source with{IsLocked=true};return true;});
        var before=MapUndoTests.Content(repo);await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var pasted=await client.PasteLineAsync(LinePasteTests.Copy(source),300,300);Assert.IsTrue(pasted.Success,pasted.Message);
        var undo=await client.UndoAsync();Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(before,MapUndoTests.Content(repo));
    }

    [TestMethod]
    public async Task MergeUndoRestoresBothOriginalRoadCodesAndTheirCrossSettings()
    {
        var repo=new InMemoryLineRepository();var a=LogicalNodeMySqlTests.Add(repo,20,50,80,50);var b=LogicalNodeMySqlTests.Add(repo,100,70,100,130);
        LogicalNodeMySqlTests.Add(repo,80,100,120,100);
        var c=new CrossService(repo);while(c.Calculate().Data!.CanContinue){}
        var crossing=c.GetAll().Data!.Single();Assert.IsTrue(c.Edit(new(crossing.Key,"owner",crossing.Version,CrossState.Road2ToRoad1)).Success);
        var before=MapUndoTests.Content(repo);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var preview=await client.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version);Assert.IsTrue(preview.Success,preview.Message);Assert.IsTrue(preview.Data!.Plans.Length>0);
        var merged=await client.ApplyMergePlanAsync(preview.Data,preview.Data.Plans[0]);Assert.IsTrue(merged.Success,merged.Message);
        var undo=await client.UndoAsync();Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(before,MapUndoTests.Content(repo));
    }
}
