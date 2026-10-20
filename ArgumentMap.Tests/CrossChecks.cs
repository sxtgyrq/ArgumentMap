using ArgumentMap.Rules;
using System.Net.Http;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using WpfArgumentMap;
using LineGeometry = ArgumentMap.Rules.LineGeometry;

internal static class CrossChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
        checks++;
    }

    internal static async Task<int> RunAsync()
    {
        CheckGeometry();
        CheckLifecycle();
        await CheckHttpAsync();
        return checks;
    }

    private static LineNode N(int x, int y) => new("ZAAAAAAAAB", 0, x, y);
    private static void CheckGeometry()
    {
        Check(LineGeometry.TryIntersection(N(0, 0), N(10, 0), N(5, 0), N(6, 1), out var t) && t == new CrossPosition(10, 0), "User T-junction example");
        Check(LineGeometry.TryIntersection(N(0, 0), N(1, 1), N(0, 1), N(1, 0), out var half) && half == new CrossPosition(1, 1), "Half-integer intersection exact");
        Check(LineGeometry.TryIntersection(N(0, 0), N(500000, 500000), N(0, 500000), N(500000, 0), out var large) && large == new CrossPosition(500000, 500000), "Large coordinates remain exact");
        Check(!LineGeometry.TryIntersection(N(0, 0), N(10, 0), N(1, 1), N(9, 1), out _), "Parallel segments do not create cross");
        Check(!LineGeometry.TryIntersection(N(0, 0), N(10, 0), N(1, 0), N(9, 0), out _), "Overlap is not represented as Cross");
        Check(!LineGeometry.TryIntersection(N(0, 0), N(1, 1), N(10, 0), N(10, 20), out _), "Infinite-line crossing outside segment excluded");
        foreach (var (dx, dy) in new (int, int)[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) })
        {
            var a = N(250000 - dx * 100, 250000 - dy * 100);
            var b = N(250000 + dx * 100, 250000 + dy * 100);
            var c = N(250000 - dy * 100, 250000 + dx * 100);
            var d = N(250000 + dy * 100, 250000 - dx * 100);
            Check(LineGeometry.TryIntersection(a, b, c, d, out var cross) && cross == new CrossPosition(500000, 500000), "Every direction intersects its perpendicular");
            Check(LineGeometry.TryIntersection(d, c, b, a, out var reversed) && reversed == cross, "Intersection invariant to endpoint order");
        }
    }

    private static SaveLineRequest Shape(LineSnapshot line, params (int X, int Y)[] points) => new(line.Code, line.Owner, line.Version,
        points.Select((point, index) => new LineNode(line.Code, index, point.X, point.Y)).ToArray());

    private static LineSnapshot SaveNew(ILineRepository repository, params (int X, int Y)[] points)
    {
        var service = new LineSaveService(repository);
        var line = service.Create(new("owner")).Data!;
        var saved = service.CheckShapeIsOKThenSave(Shape(line, points));
        Check(saved.Success, "Cross test geometry must save");
        while (new CrossService(repository).Calculate().Data!.CanContinue) { }
        return saved.Data!;
    }

    private static void CheckLifecycle()
    {
        var repository = new InMemoryLineRepository();
        var lines = new LineSaveService(repository);
        var service = new CrossService(repository);
        var a = SaveNew(repository, (0, 0), (10, 0));
        var b = SaveNew(repository, (5, 0), (5, 1));
        var cross = service.GetAll().Data!.Single();
        Check(cross.Key.Position == new CrossPosition(10, 0) && cross.State == CrossState.Unset && !cross.CanTransfer, "Saving generates unset crossing");
        var connected = service.Edit(new(cross.Key, "owner", cross.Version, CrossState.Connected));
        Check(connected.Success && connected.Data!.CanTransfer, "I state allows transfer");
        Check(lines.GetAll().Data!.All(line => line.Nodes.Length == 2), "Cross state does not insert fractional or duplicate line nodes");
        Check(!service.Edit(new(cross.Key, "owner", cross.Version, CrossState.Disconnected)).Success, "Stale Cross update rejected");
        cross = connected.Data!;
        Check(!service.Edit(new(cross.Key, "other", cross.Version, CrossState.Disconnected)).Success, "Cross ownership enforced");
        Check(!service.Edit(new(cross.Key, "owner", cross.Version, (CrossState)4)).Success, "Unsupported Cross states rejected");
        SaveNew(repository, (20, 20), (30, 20));
        Check(service.GetAll().Data!.Single() == cross, "Unrelated save preserves Cross ID and settings");
        var failure = lines.CheckShapeIsOKThenSave(Shape(a, (0, 0), (10, 0), (5, 0)));
        Check(!failure.Success && service.GetAll().Data!.Single() == cross, "Invalid line save leaves Cross topology unchanged");
        var updated = lines.CheckShapeIsOKThenSave(Shape(a, (0, 0), (10, 0), (20, 0))).Data!;
        while (service.Calculate().Data!.CanContinue) { }
        cross = service.GetAll().Data!.Single();
        Check(cross.Key == connected.Data!.Key && cross.State == CrossState.Connected && cross.Version > connected.Data.Version, "Geometry edit at same crossing preserves setting and updates version");
        var disconnected = service.Edit(new(cross.Key, "owner", cross.Version, CrossState.Disconnected));
        Check(disconnected.Success && !disconnected.Data!.CanTransfer, "O state forbids transfer without removing geometry");
        var reset = service.Edit(new(cross.Key, "owner", disconnected.Data!.Version, CrossState.Unset));
        Check(reset.Success && reset.Data!.State == CrossState.Unset, "P resets state");
        repository.Transaction(state => { state.Lines[a.Code] = state.Lines[a.Code] with { IsLocked = true }; return true; });
        var singleLockEdit = service.Edit(new(cross.Key, "owner", reset.Data!.Version, CrossState.Connected));
        Check(singleLockEdit.Success, "One locked participant still permits Cross edit as in old project");
        repository.Transaction(state => { state.Lines[b.Code] = state.Lines[b.Code] with { IsLocked = true }; return true; });
        Check(!service.Edit(new(cross.Key, "owner", singleLockEdit.Data!.Version, CrossState.Disconnected)).Success, "Both locked participants block Cross edit");
        repository.Transaction(state =>
        {
            state.Lines[a.Code] = state.Lines[a.Code] with { IsLocked = false };
            state.Lines[b.Code] = state.Lines[b.Code] with { IsLocked = false };
            return true;
        });
        updated = lines.CheckShapeIsOKThenSave(Shape(updated, (0, 2), (10, 2))).Data!;
        while (service.Calculate().Data!.CanContinue) { }
        Check(service.GetAll().Data!.Length == 0, "Moving geometry removes obsolete Cross");
        Check(!service.Edit(new(cross.Key, "owner", reset.Data!.Version, CrossState.Connected)).Success, "Deleted crossing cannot be edited");
        lines.CheckShapeIsOKThenSave(Shape(updated, (0, 0), (10, 0)));
        while (service.Calculate().Data!.CanContinue) { }
        var recreated = service.GetAll().Data!.Single();
        Check(recreated.Key == cross.Key && recreated.State == CrossState.Unset && recreated.Version == 0, "Reintroduced coordinate identity starts unset after the old record was deleted");
        Check(lines.Delete(new(b.Code, b.Owner, b.Version)).Success && service.GetAll().Data!.Length == 0, "Line deletion removes associated Cross atomically");

        var bentRepo = new InMemoryLineRepository();
        var bentLines = new LineSaveService(bentRepo);
        var bent = SaveNew(bentRepo, (10, 10), (15, 10), (20, 10));
        var other = SaveNew(bentRepo, (15, 9), (15, 11));
        Check(new CrossService(bentRepo).GetAll().Data!.Length == 1, "Internal-node intersection deduplicates adjacent perpendicular segments");
        var bend = new CrossService(bentRepo).GetAll().Data!.Single();
        Check(bend.LocationOn(bent.Code).RoadOrder == 1 && bend.LocationOn(bent.Code).Percent == 0 && bend.LocationOn(other.Code).RoadOrder == 0 && bend.LocationOn(other.Code).Percent == 0.5,
            "Internal bend belongs to following segment at zero, other segment at midpoint");
        var nodes = new[] { N(10, 10), N(20, 10), N(20, 20) };
        Check(LineGeometry.LocateCross(nodes, 0, new(20, 20)) == (0, 0.0), "First endpoint uses zero");
        Check(LineGeometry.LocateCross(nodes, 0, new(40, 20)) == (1, 0.0), "Internal endpoint uses following segment at zero");
        Check(LineGeometry.LocateCross(nodes, 1, new(40, 40)) == (1, 1.0), "Final endpoint uses final segment at one");
        Check(LineGeometry.LocateCross([N(20, 20), N(10, 10)], 0, new(25, 25)) == (0, 0.75), "Reversed diagonal position follows node order");
        var halfRepo = new InMemoryLineRepository();
        var halfLines = new LineSaveService(halfRepo);
        SaveNew(halfRepo, (0, 0), (1, 1));
        SaveNew(halfRepo, (0, 1), (1, 0));
        Check(new CrossService(halfRepo).GetAll().Data!.Single().Key.Position == new CrossPosition(1, 1), "Half-integer crossing stored with integer doubled coordinates");
        Check(new CrossService(halfRepo).GetAll().Data!.Single().Percent1 == 0.5 && new CrossService(halfRepo).GetAll().Data!.Single().Percent2 == 0.5,
            "Half-integer intersection positions are both one half");
        var selfRepo = new InMemoryLineRepository();
        SaveNew(selfRepo, (100, 110), (120, 110), (120, 120), (110, 120), (110, 100));
        Check(new CrossService(selfRepo).GetAll().Data!.Length == 0, "Cross records join distinct lines, as in source workflow");
        var branchLines = new LineSaveService(selfRepo);
        var branchCrosses = new CrossService(selfRepo);
        var legacyBranch = branchLines.Create(new("owner")).Data!;
        Check(branchLines.CheckShapeIsOKThenSave(Shape(legacyBranch, (105, 105), (115, 115))).Code == "NonOrthogonalIntersection",
            "New road cannot obliquely cross either segment of a self-crossing road");
        // Preserve topology coverage for historical data predating the new save restriction.
        selfRepo.Transaction(state =>
        {
            state.Lines[legacyBranch.Code] = legacyBranch with { Version = 1, Nodes = Shape(legacyBranch, (105, 105), (115, 115)).Nodes };
            state.PendingSegments.Add((legacyBranch.Code, 0)); state.DirtyLines.Add(legacyBranch.Code); return true;
        });
        while (branchCrosses.Calculate().Data!.CanContinue) { }
        Check(branchCrosses.GetAll().Data!.Count(cross => cross.Key.Position == new CrossPosition(220, 220)) == 1,
            "Same road pair and coordinate has one Cross, independent of segment orders as in old project");
        var branch = branchLines.GetAll().Data!.Single(line => line.Nodes.Length == 5);
        Check(branchLines.Delete(new(branch.Code, branch.Owner, branch.Version)).Success && branchCrosses.GetAll().Data!.Length == 0,
            "Deleting entire multi-segment line removes every associated Cross");
    }

    private static async Task CheckHttpAsync()
    {
        var repository = new InMemoryLineRepository();
        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = await TestLogin.ClientAsync(http, server, "owner");
        async Task<LineSnapshot> Save(params (int, int)[] points)
        {
            var line = (await client.CreateAsync()).Data!;
            var result = await client.SaveAsync(line.Code, line.Version, Shape(line, points).Nodes);
            Check(result.Success, "HTTP cross setup saves");
            return result.Data!;
        }
        var first = await Save((0, 5), (10, 5));
        var second = await Save((5, 0), (5, 10));
        while ((await client.CalculateCrossAsync()).Data!.CanContinue) { }
        // Keep navigation coverage for legacy records, although new F6 saves reject triple crossings.
        var third = (await client.CreateAsync()).Data!;
        Check(!(await client.SaveAsync(third.Code, third.Version, Shape(third, (1, 1), (9, 9)).Nodes)).Success,
            "New third-line passage is refused while legacy relation navigation remains supported");
        repository.Transaction(state =>
        {
            state.Lines[third.Code] = third with { Version = 1, Nodes = Shape(third, (1, 1), (9, 9)).Nodes };
            foreach (var participant in new[] { first, second })
            {
                var key = CrossKey.Create(participant.Code, third.Code, new(10, 10));
                state.Crosses[key] = new(key, 0, 0, participant.Version, 1, CrossState.Unset, 0, 0.5, 0.5);
            }
            return true;
        });
        var editor = new CrossEditor();
        editor.ConfigureClient(client);
        Check(await editor.RefreshAsync() && editor.Crosses.Count == 3, "Three intersecting lines have three independent pair relations");
        editor.ToggleSelection();
        editor.SelectClosest(new Point(5, 5), 1);
        var selectedIds = new HashSet<CrossKey>();
        for (var i = 0; i < 3; i++)
        {
            selectedIds.Add(editor.SelectedKey!.Value);
            editor.CycleSelection(new Point(5, 5), 1);
            editor.SelectClosest(new Point(5, 5), 1);
        }
        Check(selectedIds.Count == 3, "Tab can reach all relations at same coordinate");
        var beforeMenuCycle = editor.SelectedKey;
        editor.CycleSelection(null, 1);
        Check(editor.SelectedKey.HasValue && editor.SelectedKey != beforeMenuCycle,
            "Cross menu cycles selected relation after the mouse has left the map");
        Check(await editor.SetStateAsync(CrossState.Connected) && editor.Selected!.CanTransfer, "UI Cross I operation uses HTTP");
        Check(editor.Crosses.Count(cross => cross.CanTransfer) == 1, "Editing one pair does not change other pairs");
        Check(await editor.SetStateAsync(CrossState.Disconnected) && !editor.Selected!.CanTransfer, "UI Cross O operation uses HTTP");
        Check(await editor.SetStateAsync(CrossState.Unset) && editor.Selected!.State == CrossState.Unset, "UI Cross P operation uses HTTP");
        editor.SelectClosest(new Point(100, 100), 1);
        Check(editor.SelectedKey is null && !await editor.SetStateAsync(CrossState.Connected), "Cannot edit a distant or unselected Cross");
        editor.SelectClosest(new Point(5, 5), 1);
        var selected = editor.Selected!;
        var invalidOwner = await (await TestLogin.ClientAsync(http, server, "other")).SetCrossStateAsync(selected.Key, selected.Version, CrossState.Connected);
        Check(!invalidOwner.Success, "HTTP Cross permissions enforced");
        var all = (await client.GetAllAsync()).Data!;
        await client.DeleteAsync(all[0].Code, all[0].Version);
        Check(await editor.RefreshAsync() && editor.Crosses.Count == 1, "HTTP line deletion refreshes remaining Cross pairs");
    }
}


