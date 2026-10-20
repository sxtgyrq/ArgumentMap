using ArgumentMap.Rules;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using WpfArgumentMap;

internal static class CrossPassageChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++;
    }
    private static SaveLineRequest Shape(LineSnapshot line, params (int X, int Y)[] points) =>
        new(line.Code, line.Owner, line.Version, points.Select((p, i) => new LineNode(line.Code, i, p.X, p.Y)).ToArray());
    private static string Snapshot(ILineRepository repository) => repository.Transaction(state => JsonSerializer.Serialize(new
    {
        LineCount = state.Lines.Count, CrossCount = state.Crosses.Count,
        Lines = state.Lines.Values.OrderBy(line => line.Code).ToArray(),
        Crosses = state.Crosses.Values.OrderBy(cross => cross.Key).ToArray(),
        Pending = state.PendingSegments.OrderBy(p => p.LineCode).ThenBy(p => p.Segment).Select(p => new { p.LineCode, p.Segment }).ToArray(),
        Stale = state.StaleCrosses.Order().ToArray(), Dirty = state.DirtyLines.Order().ToArray()
    }));
    private static LineSnapshot Save(LineSaveService service, params (int X, int Y)[] points)
    {
        var line = service.Create(new("cross-check")).Data!;
        var saved = service.CheckShapeIsOKThenSave(Shape(line, points));
        Check(saved.Success, "Passage fixture geometry saves: " + saved.Message);
        return saved.Data!;
    }
    private static void Calculate(CrossService service)
    {
        while (true)
        {
            var result = service.Calculate();
            Check(result.Success && result.Data is not null, "Valid pair-only Cross calculation succeeds: " + result.Message);
            if (!result.Data!.CanContinue) return;
        }
    }

    internal static async Task<int> RunAsync()
    {
        var initial = checks;
        CheckGeometry();
        CheckSaveAndLegacy(new InMemoryLineRepository(), 10000);
        CheckStaleCross();
        await CheckHttpAsync();
        await CheckConcurrencyAsync();
        return checks - initial;
    }

    private static void CheckGeometry()
    {
        var line = new[] { new LineNode("AAAAAAAAAA", 0, 0, 0), new LineNode("AAAAAAAAAA", 1, 500000, 500000) };
        Check(LineGeometry.FindPointOnLine(line, new(1, 1)) is { NodeOrder: null, Segment: 0 }, "Half-integer point inside diagonal is exact");
        Check(LineGeometry.FindPointOnLine(line, new(1000000, 1000000)) is { NodeOrder: 1 }, "Final endpoint counts as passage");
        Check(LineGeometry.FindPointOnLine(line, new(1000001, 1000001)) is null, "Collinear extension outside segment does not count");
        Check(LineGeometry.FindPointOnLine(line, new(500001, 500000)) is null, "Half-unit near miss is not passage");
        var bent = new[] { new LineNode("AAAAAAAAAA", 0, 0, 0), new LineNode("AAAAAAAAAA", 1, 10, 0), new LineNode("AAAAAAAAAA", 2, 10, 10) };
        Check(LineGeometry.FindPointOnLine(bent, new(20, 0)) is { NodeOrder: 1, Segment: 1 }, "Internal node is reported as a node");
        Check(LineGeometry.FindPointOnLine(bent, new(20, 7)) is { NodeOrder: null, Segment: 1 }, "Later segment includes half-integer position");
    }

    private static void CheckSaveAndLegacy(ILineRepository repository, int origin)
    {
        var service = new LineSaveService(repository);
        var crosses = new CrossService(repository);
        var a = Save(service, (origin, origin + 10), (origin + 20, origin + 10));
        var b = Save(service, (origin + 10, origin), (origin + 10, origin + 20));
        var c = service.Create(new(a.Owner)).Data!;
        var before = Snapshot(repository);
        var invalidShape = Shape(c, (origin + 5, origin + 5), (origin + 15, origin + 15));
        var rejected = service.CheckShapeIsOKThenSave(invalidShape);
        Check(!rejected.Success && rejected.Code == "LinePassesCross" && rejected.Message.Contains("待计算"),
            "F6 rejects a third line even before the pair's first K calculation");
        Check(Snapshot(repository) == before, "Save rejection changes no geometry, versions, markers, or counters");
        Calculate(crosses);
        var cross = crosses.GetAll().Data!.Single(item => item.Key.Contains(a.Code) && item.Key.Contains(b.Code));
        foreach (var state in new[] { CrossState.Connected, CrossState.Disconnected, CrossState.Unset })
        {
            cross = crosses.Edit(new(cross.Key, a.Owner, cross.Version, state)).Data!;
            before = Snapshot(repository);
            foreach (var nodes in new[]
            {
                Shape(c, (origin + 5, origin + 5), (origin + 15, origin + 15)),
                Shape(c, (origin + 5, origin + 5), (origin + 10, origin + 10), (origin + 15, origin + 15)),
                Shape(c, (origin + 10, origin + 10), (origin + 15, origin + 15)),
                Shape(c, (origin + 5, origin + 5), (origin + 10, origin + 10))
            })
            {
                var result = service.CheckShapeIsOKThenSave(nodes);
                Check(!result.Success && result.Code == "LinePassesCross" && result.Message.Contains($"Cross {cross.Key}"),
                    "Segment interior, internal node, first and final node cannot occupy a third-party Cross in any state");
                Check(Snapshot(repository) == before, "Each rejected third-line save rolls back completely");
            }
        }
        a = service.CheckShapeIsOKThenSave(Shape(a, (origin, origin + 10), (origin + 10, origin + 10), (origin + 20, origin + 10))).Data!;
        Check(a is not null, "A participating line may insert its own node at the Cross");
        Calculate(crosses);
        var updatedCross = crosses.GetAll().Data!.Single(item => item.Key == cross.Key);
        Check(updatedCross.LocationOn(a!.Code).RoadOrder == 1 && updatedCross.LocationOn(a.Code).Percent == 0, "Own Cross node still uses outgoing segment zero");

        // Existing databases may contain shapes saved before this restriction. Seed that legacy state deliberately.
        repository.Transaction(state =>
        {
            state.Lines[c.Code] = c with { Version = 1, Nodes = invalidShape.Nodes };
            state.PendingSegments.Add((c.Code, 0));
            state.DirtyLines.Add(c.Code);
            return true;
        });
        before = Snapshot(repository);
        var blocked = crosses.Calculate();
        Check(!blocked.Success && blocked.Code == "LinePassesCross" && blocked.Message.Contains("计算前"), "K pre-check refuses legacy triple crossing");
        Check(Snapshot(repository) == before, "Rejected K preserves pending markers, Cross settings, and ID counters");
        var storedViolation = repository.Transaction(CrossPassageValidator.FindThroughCalculatedCrosses);
        Check(storedViolation is { } && storedViolation.LineCode == c.Code && storedViolation.Cross == updatedCross.Key,
            "Post-calculation validator finds the extra segment through an actual stored Cross");
        Check(!crosses.Recalculate().Success && Snapshot(repository) == before, "Full rebuild also rejects without marking or partially recalculating");
        repository.Transaction(state => { state.PendingSegments.Remove((c.Code, 0)); state.DirtyLines.Remove(c.Code); return true; });
        before = Snapshot(repository);
        Check(!crosses.Calculate().Success && Snapshot(repository) == before, "K checks legacy geometry even with no pending segments");
        var corrected = service.CheckShapeIsOKThenSave(Shape(c with { Version = 1 }, (origin + 25, origin + 26), (origin + 35, origin + 36)));
        Check(corrected.Success, "Existing violating line can be edited away rather than being permanently blocked");
        Calculate(crosses);
        Check(repository.Transaction(CrossPassageValidator.FindThroughCalculatedCrosses) is null,
            "Completed calculation has no third-party passage after correction");
        Check(crosses.GetAll().Data!.Any(item => item.Key == updatedCross.Key && item.State == updatedCross.State),
            "Fixing legacy geometry preserves unrelated existing Cross settings");
        Check(service.Delete(new(c.Code, c.Owner, corrected.Data!.Version)).Success, "Correction fixture deletes whole third line");
        Check(service.Delete(new(b.Code, b.Owner, b.Version)).Success && service.Delete(new(a!.Code, a.Owner, a.Version)).Success,
            "Fixture cleanup preserves other maps in the integration database");
    }

    private static void CheckStaleCross()
    {
        var repository = new InMemoryLineRepository();
        var lines = new LineSaveService(repository);
        var crosses = new CrossService(repository);
        var a = Save(lines, (0, 10), (20, 10));
        var b = Save(lines, (10, 0), (10, 20));
        Calculate(crosses);
        var old = crosses.GetAll().Data!.Single();
        a = lines.CheckShapeIsOKThenSave(Shape(a, (0, 12), (20, 12))).Data!;
        Check(lines.CheckShapeIsOKThenSave(Shape(b, (10, 12), (10, 20))).Success, "Both roads clear the obsolete coordinate before checking stale Cross passage");
        Check(repository.Transaction(state => state.StaleCrosses.Contains(old.Key)), "Moved Cross remains marked until cleanup");
        Save(lines, (9, 9), (11, 11));
        Calculate(crosses);
        Check(crosses.GetAll().Data!.All(cross => cross.Key != old.Key) && repository.Transaction(CrossPassageValidator.FindThroughCalculatedCrosses) is null,
            "An obsolete Cross does not falsely reject passage through its old coordinate");
    }

    private static async Task CheckHttpAsync()
    {
        var repository = new InMemoryLineRepository();
        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = await TestLogin.ClientAsync(http, server, "owner");
        var lines = new LineSaveService(repository);
        var a = Save(lines, (20000, 20010), (20020, 20010));
        Save(lines, (20010, 20000), (20010, 20020));
        Calculate(new CrossService(repository));
        var editor = new LineEditor(client);
        Check(await editor.NewLineAsync() && await editor.BeginDrawingAsync(), "HTTP F6 restriction fixture starts drawing");
        editor.AddPoint(new Point(20005, 20005)); editor.AddPoint(new Point(20015, 20015));
        var draft = editor.DraftNodes.ToArray();
        Check(!await editor.CompleteAsync() && editor.Message.Contains("第三条线路") && editor.DraftNodes.SequenceEqual(draft) &&
            editor.Mode == LineEditorMode.Drawing && !editor.IsBusy, "F6 HTTP failure retains the editable draft and names the Cross restriction");
        var id = editor.DraftCode!;
        repository.Transaction(state => { state.Lines[id] = state.Lines[id] with { Nodes = Shape(state.Lines[id], (20005, 20005), (20015, 20015)).Nodes, Version = 1 }; state.PendingSegments.Add((id, 0)); return true; });
        var before = Snapshot(repository);
        var crossEditor = new CrossEditor(); crossEditor.ConfigureClient(client);
        await crossEditor.RefreshAsync();
        var previousCrosses = crossEditor.Crosses.ToArray();
        Check(!await crossEditor.CalculateAsync(() => { }) && crossEditor.CalculationMessage.Contains("计算前") && !crossEditor.IsBusy && !crossEditor.IsCalculating &&
            crossEditor.Crosses.SequenceEqual(previousCrosses), "K HTTP rejection stops the loop, reports the violation, and preserves displayed Crosses");
        Check(Snapshot(repository) == before, "Authenticated K rejection makes no partial changes");
    }

    private static async Task CheckConcurrencyAsync()
    {
        var repository = new InMemoryLineRepository();
        var service = new LineSaveService(repository);
        Save(service, (30000, 30010), (30020, 30010));
        var vertical = service.Create(new("cross-check")).Data!;
        var diagonal = service.Create(new("cross-check")).Data!;
        var results = await Task.WhenAll(
            Task.Run(() => service.CheckShapeIsOKThenSave(Shape(vertical, (30010, 30000), (30010, 30020)))),
            Task.Run(() => service.CheckShapeIsOKThenSave(Shape(diagonal, (30001, 30001), (30019, 30019)))));
        Check(results[0].Success && !results[1].Success && results[1].Code is "LinePassesCross" or "NonOrthogonalIntersection",
            "Only the perpendicular road commits; the oblique road is rejected by the angle or third-party rule depending on transaction order");
        Calculate(new CrossService(repository));
        Check(new CrossService(repository).GetAll().Data!.Length == 1, "Concurrent winner retains a valid pair-only Cross");
    }

    internal static int CheckMySql(MySqlSettings settings)
    {
        if (!settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal)) throw new InvalidOperationException("Owned test database required.");
        var initial = checks;
        CheckSaveAndLegacy(new MySqlLineRepository(settings), 380000);
        return checks - initial;
    }
}
