using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class CrossPendingVisibilityTests
{
    internal static void Calculate(ILineRepository repository)
    {
        var result = new CrossService(repository).Recalculate();
        Assert.IsTrue(result.Success, result.Message);
    }

    internal static (LineSnapshot Road, CrossSnapshot Pending, CrossSnapshot Unrelated) Setup(ILineRepository repository)
    {
        LogicalNodeMySqlTests.Add(repository, 10, 50, 90, 50);
        var road = LogicalNodeMySqlTests.Add(repository, 50, 10, 50, 90);
        LogicalNodeMySqlTests.Add(repository, 150, 50, 250, 50);
        LogicalNodeMySqlTests.Add(repository, 200, 10, 200, 90);
        Calculate(repository);
        var crosses = new CrossService(repository);
        var pending = crosses.GetAll().Data!.Single(c => c.Key.Contains(road.Code));
        pending = crosses.Edit(new(pending.Key, "owner", pending.Version, CrossState.Connected)).Data!;
        return (road, pending, crosses.GetAll().Data!.Single(c => !c.Key.Contains(road.Code)));
    }

    internal static void CheckPending(ILineRepository repository, CrossSnapshot pending, CrossSnapshot unrelated)
    {
        CollectionAssert.AreEqual(new[] { unrelated }, new CrossService(repository).GetAll().Data!);
        repository.Transaction(state =>
        {
            Assert.AreEqual(pending, state.Crosses[pending.Key], "Do not destroy the original passage setting before K.");
            Assert.IsTrue(state.StaleCrosses.Contains(pending.Key));
            Assert.IsTrue(state.PendingSegments.Count > 0);
            return true;
        });
    }

    [TestMethod, DataRow(false), DataRow(true)]
    public void ShortenedOrStillIntersectingRoadHidesPendingCross_ThenKCleansOrReusesIt(bool stillIntersects)
        => Exercise(new InMemoryLineRepository(), stillIntersects);

    internal static void Exercise(ILineRepository repository, bool stillIntersects)
    {
        var f = Setup(repository);
        var result = MapUndoTests.Edit(repository, r => new LineSaveService(r).CheckShapeIsOKThenSave(
            TestMap.Shape(f.Road, 50, 10, 50, stillIntersects ? 80 : 40)));
        Assert.IsTrue(result.Success, result.Message);
        CheckPending(repository, f.Pending, f.Unrelated);
        var crossService = new CrossService(repository);
        Assert.IsTrue(crossService.Calculate().Success);
        if (stillIntersects)
        {
            var restored = crossService.GetAll().Data!.Single(c => c.Key == f.Pending.Key);
            Assert.AreEqual(CrossState.Connected, restored.State);
            Assert.IsFalse(repository.Transaction(s => s.StaleCrosses.Contains(f.Pending.Key)));
        }
        else CheckPendingAfterSegment();
        Assert.IsFalse(crossService.Calculate().Data!.CanContinue);
        Assert.AreEqual(stillIntersects ? 2 : 1, crossService.GetAll().Data!.Length);
        Assert.AreEqual(stillIntersects, repository.Transaction(s => s.Crosses.ContainsKey(f.Pending.Key)));
        Assert.IsTrue(new MapUndoService(repository).Undo("owner").Success);
        Assert.AreEqual(CrossState.Connected, crossService.GetAll().Data!.Single(c => c.Key == f.Pending.Key).State);
        Assert.AreEqual(f.Unrelated, crossService.GetAll().Data!.Single(c => c.Key == f.Unrelated.Key));

        void CheckPendingAfterSegment()
        {
            Assert.IsTrue(repository.Transaction(s => s.StaleCrosses.Contains(f.Pending.Key)));
            CollectionAssert.AreEqual(new[] { f.Unrelated }, crossService.GetAll().Data!);
        }
    }

    [TestMethod, DataRow("linePaste"), DataRow("areaPaste"), DataRow("areaMove"), DataRow("areaDelete"), DataRow("import"), DataRow("undo")]
    public void MapResponsesNeverRepublishAnotherRoadsPendingCross(string operation)
    {
        var repo = new InMemoryLineRepository();
        var f = Setup(repo);
        var changed = new LineSaveService(repo).CheckShapeIsOKThenSave(TestMap.Shape(f.Road, 50, 10, 50, 40));
        Assert.IsTrue(changed.Success, changed.Message);
        var source = LogicalNodeMySqlTests.Add(repo, 300, 300, 350, 300);
        var area = new MapAreaMoveService(repo);
        var selection = area.Select(new("owner", new(290, 290, 360, 310))).Data!;
        MapAreaSnapshot map;
        if (operation == "linePaste") map = new LinePasteService(repo).Paste(new("owner", new(source.Code, source.Version, source.Nodes, source.Properties, source.IsLocked), 400, 300)).Data!.Map;
        else if (operation == "areaPaste") map = new MapAreaPasteService(repo).Paste(new("owner", selection, 100, 0)).Data!.Map;
        else if (operation == "areaMove") map = area.Move(new("owner", selection, 100, 0)).Data!;
        else if (operation == "areaDelete") map = area.Delete(new("owner", selection)).Data!;
        else if (operation == "import") map = new FlowDiagramService(repo).Import(new("owner", FlowDiagramTests.Small().ToJsonString(), 500.5m, 500.5m)).Data!.Map!;
        else
        {
            Assert.IsTrue(MapUndoTests.Edit(repo, r => new LineSaveService(r).Delete(new(source.Code, "owner", source.Version))).Success);
            map = new MapUndoService(repo).Undo("owner").Data!.Map;
        }
        Assert.IsFalse(map.Crosses.Any(c => c.Key == f.Pending.Key));
        CollectionAssert.Contains(map.Crosses, f.Unrelated);
        CheckPending(repo, f.Pending, f.Unrelated);
    }

    [STATestMethod]
    public void WpfDeletingWholeRoadRefreshesCrossCacheImmediately()
    {
        using var handler = new DirectLogicalNodeHttpStub();
        var f = Setup(handler.Repository);
        using var http = new HttpClient(handler) { BaseAddress = new("http://localhost/") };
        var view = new CoordinateMapView();
        view.ConfigureClient(new ArgumentMap.Client.HttpLineClient(http, new("owner", "test")));
        Assert.IsTrue(view.RefreshLinesAsync().GetAwaiter().GetResult());
        Assert.AreEqual(2, view.CrossCount);
        var editor = (LineEditor)typeof(CoordinateMapView).GetField("editor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view)!;
        editor.ToggleSelection(); editor.SelectClosest(new(50, 30), 1);
        Assert.AreEqual(f.Road.Code, editor.SelectedCode);
        Assert.IsTrue(view.HandleEditorKeyAsync(System.Windows.Input.Key.R).GetAwaiter().GetResult());
        Assert.AreEqual(1, view.CrossCount);
        Assert.IsFalse(handler.Repository.Transaction(s => s.Crosses.ContainsKey(f.Pending.Key)));
    }
}

[TestClass, TestCategory("MySql")]
public sealed class CrossPendingVisibilityMySqlTests
{
    [TestMethod, DataRow(false), DataRow(true)]
    public Task PendingFlagsSettingsCleanupAndUndoPersist(bool stillIntersects) => LogicalNodeMySqlTests.Isolated(settings =>
    {
        CrossPendingVisibilityTests.Exercise(new MySqlLineRepository(settings), stillIntersects);
        var reopened = new MySqlLineRepository(settings);
        Assert.AreEqual(2, new CrossService(reopened).GetAll().Data!.Length);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task TwoServersReadPendingCrossAsHidden_WithoutChangingDatabaseRecords() => LogicalNodeMySqlTests.Isolated(async settings =>
    {
        var repo = new MySqlLineRepository(settings); var f = CrossPendingVisibilityTests.Setup(repo);
        await using var first = await LocalLineServer.StartAsync(repository: repo);
        await using var second = await LocalLineServer.StartAsync(repository: new MySqlLineRepository(settings));
        using var http1 = new HttpClient { BaseAddress = first.BaseAddress };
        using var http2 = new HttpClient { BaseAddress = second.BaseAddress };
        var owner = await TestLogin.ClientAsync(http1, first, "owner");
        var other = await TestLogin.ClientAsync(http2, second, "other");
        Assert.IsTrue((await owner.SaveAsync(f.Road.Code, f.Road.Version, TestMap.Shape(f.Road, 50, 10, 50, 40).Nodes)).Success);
        var before = LineTopologyMySqlTests.RawSnapshot(settings);
        CollectionAssert.AreEqual(new[] { f.Unrelated }, (await other.GetCrossesAsync()).Data!);
        Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings), "Reading must not consume K flags or alter history.");
        CrossPendingVisibilityTests.CheckPending(new MySqlLineRepository(settings), f.Pending, f.Unrelated);
        Assert.IsTrue((await owner.UndoAsync()).Success);
        Assert.AreEqual(CrossState.Connected, (await other.GetCrossesAsync()).Data!.Single(c => c.Key == f.Pending.Key).State);
    });
}
