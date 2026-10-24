using ArgumentMap.Rules;
using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using WpfArgumentMap;

internal static class CrossEditChecks
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
    private static SaveLineRequest Shape(LineSnapshot line, params (int X, int Y)[] points) =>
        new(line.Code, line.Owner, line.Version, points.Select((point, i) => new LineNode(line.Code, i, point.X, point.Y)).ToArray());

    internal static async Task<int> RunAsync()
    {
        CheckRenumbering();
        await CheckEditorAsync();
        return checks;
    }

    private static void CheckRenumbering()
    {
        var repository = new InMemoryLineRepository();
        var lines = new LineSaveService(repository);
        var crosses = new CrossService(repository);
        LineSnapshot Save(params (int X, int Y)[] points)
        {
            var line = lines.Create(new("owner")).Data!;
            var result = lines.CheckShapeIsOKThenSave(Shape(line, points));
            Check(result.Success, "Edit scenario geometry saves");
            return result.Data!;
        }
        void Calculate() { while (crosses.Calculate().Data!.CanContinue) { } }
        var a = Save((100, 100), (120, 100), (140, 100));
        var b = Save((110, 90), (110, 110));
        Save((130, 90), (130, 110));
        Calculate();
        var first = crosses.GetAll().Data!.Single(cross => cross.Key.Position.X == 110);
        var second = crosses.GetAll().Data!.Single(cross => cross.Key.Position.X == 130);
        first = crosses.Edit(new(first.Key, a.Owner, first.Version, CrossState.Connected)).Data!;
        second = crosses.Edit(new(second.Key, a.Owner, second.Version, CrossState.Disconnected)).Data!;
        var nextId = repository.Transaction(state => state.Crosses.Count);
        a = lines.CheckShapeIsOKThenSave(Shape(a, (100, 100), (105, 100), (120, 100), (140, 100))).Data!;
        Check(repository.Transaction(state => state.PendingSegments.Count) == 3, "Inserting one node schedules every segment of edited line");
        Calculate();
        var current = crosses.GetAll().Data!;
        Check(current.Single(cross => cross.Key == first.Key) is { State: CrossState.Connected } && current.Single(cross => cross.Key == first.Key).LocationOn(a.Code).RoadOrder == 1 &&
            current.Single(cross => cross.Key == second.Key) is { State: CrossState.Disconnected } && current.Single(cross => cross.Key == second.Key).LocationOn(a.Code).RoadOrder == 2,
            "Several Crosses retain settings when insertion shifts segment orders");
        Check(repository.Transaction(state => state.Crosses.Count) == nextId, "Renumbering existing intersections allocates no new IDs");
        a = lines.CheckShapeIsOKThenSave(Shape(a, (100, 100), (110, 100), (120, 100), (140, 100))).Data!;
        b = lines.CheckShapeIsOKThenSave(Shape(b, (110, 90), (110, 95), (110, 110))).Data!;
        Calculate();
        var nodeCross = crosses.GetAll().Data!.Single(cross => cross.Key == first.Key);
        Check(nodeCross.LocationOn(a.Code).RoadOrder == 1 && nodeCross.LocationOn(a.Code).Percent == 0 && nodeCross.LocationOn(b.Code).RoadOrder == 1 &&
            Math.Abs(nodeCross.LocationOn(b.Code).Percent - 1.0 / 3) < 1e-12 && nodeCross.State == CrossState.Connected,
            "Editing both lines uses current geometry, outgoing-node zero and precise ratios");
        a = lines.CheckShapeIsOKThenSave(Shape(a, (100, 100), (140, 100))).Data!;
        Calculate();
        current = crosses.GetAll().Data!;
        Check(current.Single(cross => cross.Key == first.Key).LocationOn(a.Code).Percent == 0.25 && current.Single(cross => cross.Key == second.Key).LocationOn(a.Code).Percent == 0.75,
            "Deleting internal nodes merges segments and refreshes both percentages");
        Check(current.Single(cross => cross.Key == first.Key).State == CrossState.Connected &&
            current.Single(cross => cross.Key == second.Key).State == CrossState.Disconnected, "Node deletion retains both existing Cross states");
        a = lines.CheckShapeIsOKThenSave(Shape(a, (140, 100), (100, 100))).Data!;
        Calculate();
        Check(crosses.GetAll().Data!.Single(cross => cross.Key == first.Key).LocationOn(a.Code).Percent == 0.75, "Reversed node order changes percentage while preserving Cross identity");
        var version = a.Version;
        Check(lines.CheckShapeIsOKThenSave(Shape(a, (140, 100), (100, 100))).Data!.Version == version &&
            repository.Transaction(state => state.PendingSegments.Count) == 0, "Unchanged shape does not schedule recalculation");

        var half = Save((200, 200), (210, 210));
        Save((200, 209), (209, 200));
        Calculate();
        Check(!crosses.GetAll().Data!.Any(cross => cross.Key.Contains(half.Code)), "Half-integer geometric contact creates no Cross");
        half = lines.CheckShapeIsOKThenSave(Shape(half, (200, 200), (202, 202), (210, 210))).Data!;
        Calculate();
        Check(!crosses.GetAll().Data!.Any(cross => cross.Key.Contains(half.Code)), "Inserting an integer node does not materialize or round a half-integer contact");
        var nodeRoute = new[] { new LineNode("AAAAAAAAAA", 0, 10, 10), new LineNode("AAAAAAAAAA", 1, 20, 10), new LineNode("AAAAAAAAAA", 2, 20, 20),
            new LineNode("AAAAAAAAAA", 3, 10, 20), new LineNode("AAAAAAAAAA", 4, 30, 0) };
        Check(LineGeometry.LocateCross(nodeRoute, 3, new(40, 20)) == (1, 0.0),
            "Node outgoing segment takes priority over another segment passing through same coordinate");
    }

    private static async Task CheckEditorAsync()
    {
        await using var server = await LocalLineServer.StartAsync(repository: new InMemoryLineRepository());
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = await TestLogin.ClientAsync(http, server, "owner");
        async Task<LineSnapshot> Save(params (int X, int Y)[] points)
        {
            var line = (await client.CreateAsync()).Data!;
            var result = await client.SaveAsync(line.Code, line.Version, Shape(line, points).Nodes);
            Check(result.Success, "HTTP edit scenario saves");
            return result.Data!;
        }
        async Task Calculate() { while ((await client.CalculateCrossAsync()).Data!.CanContinue) { } }
        var line = await Save((1000, 1000), (1020, 1000), (1040, 1000));
        await Save((1030, 990), (1030, 1010));
        await Calculate();
        var cross = (await client.GetCrossesAsync()).Data!.Single();
        cross = (await client.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Connected)).Data!;
        var editor = new LineEditor(client);
        await editor.RefreshLinesAsync();
        editor.ToggleSelection();
        editor.SelectClosest(new Point(1010, 1000), 1);
        Check(await editor.EditSelectedAsync(), "F8/F9 load saved polyline for editing");
        Check(editor.InsertPoint(new Point(1010.1, 1000.2)) && editor.DraftNodes[1] == new GridPoint(1010, 1000),
            "T inserts integer node into best position following old minimum-length insertion");
        Check(!editor.InsertPoint(new Point(1010, 1000)), "T rejects an already occupied draft node");
        Check(!editor.InsertPoint(new Point(1033, 1002)), "T cannot break eight-direction connections");
        Check((await client.GetCrossesAsync()).Data!.Single() == cross, "Draft edits do not alter saved Cross");
        Check(await editor.CompleteAsync(), "F6 saves entire edited node list");
        var saved = (await client.GetAllAsync()).Data!.Single(item => item.Code == line.Code);
        Check(saved.Nodes.Select(node => node.Order).SequenceEqual(new[] { 0, 1, 2, 3 }), "Inserted nodes are renumbered before submission");
        await Calculate();
        cross = (await client.GetCrossesAsync()).Data!.Single();
        Check(cross.LocationOn(line.Code).RoadOrder == 2 && cross.State == CrossState.Connected && cross.LocationOn(line.Code).Percent == 0.5, "K reattaches Cross after inserted node shifts its segment");
        editor.ToggleSelection();
        editor.SelectClosest(new Point(1010, 1000), 1);
        await editor.EditSelectedAsync();
        Check(await editor.RemoveAsync(new Point(1020, 1000)), "Delete removes intermediate draft node with valid merged direction");
        Check(await editor.CompleteAsync(), "F6 saves node deletion");
        await Calculate();
        var deletedNode = (await client.GetCrossesAsync()).Data!.Single();
        Check(deletedNode.Key == cross.Key && deletedNode.State == CrossState.Connected && deletedNode.LocationOn(line.Code).RoadOrder == 1 &&
            Math.Abs(deletedNode.LocationOn(line.Code).Percent - 2.0 / 3) < 1e-12, "K updates segment and ratio without discarding Cross state after deletion");
        editor.ToggleSelection();
        editor.SelectClosest(new Point(1010, 1000), 1);
        await editor.EditSelectedAsync();
        await editor.RemoveAsync(new Point(1040, 1000));
        Check(await editor.CompleteAsync(), "Deleting terminal node saves shortened polyline");
        Check((await client.GetCrossesAsync()).Data!.Length == 0, "Cross is hidden immediately while marked for K's final sweep");
        await Calculate();
        Check((await client.GetCrossesAsync()).Data!.Length == 0, "K removes Cross lost after endpoint deletion");
        Check(CoordinateMapView.IsEditorKey(Key.T), "T routed to insertion as in old editor");
    }
}

