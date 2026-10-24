using System.Net.Http;
using System.Windows.Input;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using WpfArgumentMap;

internal static class IncrementalCrossChecks
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
        var repository = new InMemoryLineRepository();
        var lines = new LineSaveService(repository);
        var service = new CrossService(repository);
        LineSnapshot Save(params (int X, int Y)[] points)
        {
            var line = lines.Create(new("owner")).Data!;
            var saved = lines.CheckShapeIsOKThenSave(Shape(line, points));
            Check(saved.Success, "Incremental test line saves");
            return saved.Data!;
        }
        var a = Save((10, 10), (30, 10));
        var b = Save((20, 0), (20, 20));
        Check(service.GetAll().Data!.Length == 0, "Save only marks changed geometry, as in old workflow");
        Check(repository.Transaction(state => state.PendingSegments.Count) == 2, "Both segment starts marked for calculation");
        var step = service.Calculate().Data!;
        Check(step.CanContinue && step.RemainingSegments == 1, "One request processes exactly one segment");
        var cross = service.GetAll().Data!.Single();
        var pendingEdit = service.Edit(new(cross.Key, "owner", cross.Version, CrossState.Connected));
        Check(pendingEdit.Success, "Existing Cross can be configured while participants await calculation, as in old workflow");
        cross = pendingEdit.Data!;
        Check(service.Calculate().Data!.CanContinue, "Final segment request still schedules cleanup request");
        Check(!service.Calculate().Data!.CanContinue, "Cleanup signals completion");
        cross = service.Edit(new(cross.Key, "owner", cross.Version, CrossState.Connected)).Data!;
        a = lines.CheckShapeIsOKThenSave(Shape(a, (10, 10), (20, 10), (30, 10))).Data!;
        Check(repository.Transaction(state => state.PendingSegments.Count == 2 && state.StaleCrosses.Contains(cross.Key)), "Save marks both new segments and affected old Cross");
        Check(repository.Transaction(s=>s.Crosses[cross.Key]) == cross && service.GetAll().Data!.Length == 0, "Old Cross retained in storage but hidden until revalidation");
        pendingEdit = service.Edit(new(cross.Key, "owner", cross.Version, CrossState.Connected));
        Check(pendingEdit.Success && repository.Transaction(state => state.StaleCrosses.Contains(cross.Key)), "Pending Cross edit retains recalculation marker");
        cross = pendingEdit.Data!;
        service.Calculate();
        var canonical = service.GetAll().Data!.Single();
        Check(canonical.Key == cross.Key && canonical.State == CrossState.Connected && canonical.LocationOn(a.Code).RoadOrder == 1 && canonical.LocationOn(a.Code).Percent == 0,
            "Internal node prefers larger sequence as next segment start and retains state");
        var second = service.Calculate();
        Check(second.Data!.RemainingSegments == 0 && service.GetAll().Data!.Single() == canonical,
            "Adjacent segment calculation neither duplicates nor increments unchanged Cross");
        service.Calculate();
        Check(!service.Calculate().Data!.CanContinue && service.GetAll().Data!.Single() == canonical, "Repeated K with no changes is idempotent");
        a = lines.CheckShapeIsOKThenSave(Shape(a, (10, 11), (30, 11))).Data!;
        Check(repository.Transaction(state => state.StaleCrosses.Contains(canonical.Key)), "Moved geometry marks old Cross obsolete");
        service.Calculate();
        Check(repository.Transaction(s=>s.Crosses.Count) == 2 && service.GetAll().Data!.Length == 1, "Old Cross remains marked and hidden until final sweep; new Cross generated");
        service.Calculate();
        var moved = service.GetAll().Data!.Single();
        Check(moved.Key != canonical.Key && moved.Key.Position.Y == 11 && moved.State == CrossState.Unset, "Cleanup deletes obsolete Cross and new coordinate starts unset");
        a = lines.CheckShapeIsOKThenSave(Shape(a, (10, 12), (30, 12))).Data!;
        Check(lines.Delete(new(b.Code, b.Owner, b.Version)).Success && service.GetAll().Data!.Length == 0,
            "Whole-line deletion removes even stale Cross immediately");
        while (service.Calculate().Data!.CanContinue) { }
        Check(service.GetAll().Data!.Length == 0, "Calculation cannot revive Cross belonging to deleted line");

        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = await TestLogin.ClientAsync(http, server, "owner");
        var c = (await client.CreateAsync()).Data!;
        Check((await client.SaveAsync(c.Code, c.Version, Shape(c, (20, 1), (20, 21)).Nodes)).Success, "HTTP saves pending geometry");
        var editor = new CrossEditor();
        editor.ConfigureClient(client);
        var updates = 0;
        Check(await editor.CalculateAsync(() => updates++), "WPF calculation loops HTTP until cleanup completes");
        Check(updates == 2 && editor.Crosses.Count == 1 && !editor.IsBusy, "One segment plus final sweep updates WPF progress");
        Check(CoordinateMapView.IsEditorKey(Key.K), "K routed to Cross calculation");
        return checks;
    }
}

