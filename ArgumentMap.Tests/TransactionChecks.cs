using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using MySqlConnector;

internal static class TransactionChecks
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static SaveLineRequest Shape(LineSnapshot line, params (int X, int Y)[] points) =>
        new(line.Code, line.Owner, line.Version, points.Select((point, i) => new LineNode(line.Code, i, point.X, point.Y)).ToArray());

    private static string Snapshot(ILineRepository repository) => repository.Transaction(state => JsonSerializer.Serialize(new
    {
        LineCount = state.Lines.Count, CrossCount = state.Crosses.Count,
        Lines = state.Lines.Values.OrderBy(line => line.Code).ToArray(),
        Crosses = state.Crosses.Values.OrderBy(cross => cross.Key).ToArray(),
        Pending = state.PendingSegments.OrderBy(item => item.LineCode).ThenBy(item => item.Segment)
            .Select(item => new { item.LineCode, item.Segment }).ToArray(),
        Stale = state.StaleCrosses.Order().ToArray(),
        DirtyLines = state.DirtyLines.Order().ToArray()
    }));

    private static void CheckCallbackRollback(ILineRepository repository, LineSnapshot line)
    {
        var before = Snapshot(repository);
        var failed = repository.Transaction(state =>
        {
            state.Lines[line.Code].Nodes[0] = new(line.Code, 0, 123, 456);


            state.Crosses.Clear();
            state.PendingSegments.Clear();
            state.StaleCrosses.Clear();
            return ServiceResult<bool>.Fail("InjectedFailure", "Injected business rejection");
        });
        Check(!failed.Success && Snapshot(repository) == before, "Business failure rolls back nodes, Crosses, markers and ID counters");
        try
        {
            repository.Transaction<bool>(state =>
            {
                state.Lines[line.Code].Nodes[0] = new(line.Code, 0, 456, 123);

                state.Crosses.Clear();
                throw new InvalidOperationException("Injected callback failure");
            });
            throw new InvalidOperationException("Expected callback failure");
        }
        catch (InvalidOperationException error) when (error.Message == "Injected callback failure") { checks++; }
        Check(Snapshot(repository) == before, "Callback exception cannot leave partial committed state");
    }

    internal static int CheckMemory()
    {
        var before = checks;
        var repository = new InMemoryLineRepository();
        var lines = new LineSaveService(repository);
        var line = lines.Create(new("owner")).Data!;
        line = lines.CheckShapeIsOKThenSave(Shape(line, (6000, 6000), (6010, 6000))).Data!;
        CheckCallbackRollback(repository, line);
        return checks - before;
    }

    private static void WithFailureTrigger(MySqlSettings settings, string table, string timing, Action operation)
    {
        if (!settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("Failure injection requires owned integration database.");
        using var connection = new MySqlConnection(settings.ConnectionString());
        connection.Open();
        using var trigger = connection.CreateCommand();
        trigger.CommandText = $"CREATE TRIGGER TestAtomicFailure BEFORE {timing} ON `{table}` FOR EACH ROW " +
            "SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected transaction failure'";
        trigger.ExecuteNonQuery();
        try
        {
            try { operation(); }
            catch (MySqlException error) when (error.Number == 1644) { checks++; return; }
            throw new InvalidOperationException("Expected injected database failure.");
        }
        finally
        {
            using var drop = connection.CreateCommand();
            drop.CommandText = "DROP TRIGGER TestAtomicFailure";
            drop.ExecuteNonQuery();
        }
    }

    internal static int CheckMySql(MySqlSettings settings)
    {
        var before = checks;
        var repository = new MySqlLineRepository(settings);
        var lines = new LineSaveService(repository);
        var crosses = new CrossService(repository);
        var a = lines.Create(new("事务测试")).Data!;
        a = lines.CheckShapeIsOKThenSave(Shape(a, (6000, 6000), (6010, 6000))).Data!;
        var b = lines.Create(new("事务测试")).Data!;
        b = lines.CheckShapeIsOKThenSave(Shape(b, (6005, 5995), (6005, 6005))).Data!;
        while (crosses.Calculate().Data!.CanContinue) { }
        var cross = crosses.GetAll().Data!.Single(item => item.Key.Contains(a.Code) && item.Key.Contains(b.Code));
        cross = crosses.Edit(new(cross.Key, a.Owner, cross.Version, CrossState.Connected)).Data!;
        CheckCallbackRollback(repository, a);

        var snapshot = Snapshot(repository);
        WithFailureTrigger(settings, "Crosses", "UPDATE", () =>
            lines.CheckShapeIsOKThenSave(Shape(a, (6000, 6000), (6005, 6000), (6010, 6000))));
        Check(Snapshot(new MySqlLineRepository(settings)) == snapshot,
            "Cross marker SQL failure rolls back preceding node deletion, reinsertion and line version update");

        a = lines.CheckShapeIsOKThenSave(Shape(a, (6000, 6000), (6005, 6000), (6010, 6000))).Data!;
        snapshot = Snapshot(repository);
        WithFailureTrigger(settings, "LineNodes", "UPDATE", () => crosses.Calculate());
        Check(Snapshot(new MySqlLineRepository(settings)) == snapshot,
            "Segment marker SQL failure rolls back preceding Cross segment/percentage update and preserves pending work");
        while (crosses.Calculate().Data!.CanContinue) { }
        var recalculated = crosses.GetAll().Data!.Single(item => item.Key == cross.Key);
        Check(recalculated.State == CrossState.Connected && recalculated.LocationOn(a.Code).RoadOrder == 1 && recalculated.LocationOn(a.Code).Percent == 0,
            "Failed calculation can retry with original state and correct node ownership");

        a = lines.CheckShapeIsOKThenSave(Shape(a, (6000, 6001), (6010, 6001))).Data!;
        snapshot = Snapshot(repository);
        WithFailureTrigger(settings, "LineNodes", "UPDATE", () => crosses.Calculate());
        Check(Snapshot(new MySqlLineRepository(settings)) == snapshot,
            "Failed new-Cross insertion leaves no orphan, consumes no ID and retains pending and stale markers");
        Check(crosses.Calculate().Data!.RemainingSegments == 0, "Retry processes the changed segment");

        snapshot = Snapshot(repository);
        WithFailureTrigger(settings, "Lines", "UPDATE", () => crosses.Calculate());
        Check(Snapshot(new MySqlLineRepository(settings)) == snapshot,
            "Final line-marker SQL failure rolls back preceding obsolete Cross deletions in the same cleanup transaction");
        WithFailureTrigger(settings, "Crosses", "DELETE", () => crosses.Calculate());
        Check(Snapshot(new MySqlLineRepository(settings)) == snapshot,
            "Failed final sweep preserves obsolete Cross and its marker for retry");
        Check(!crosses.Calculate().Data!.CanContinue, "Final sweep can retry after SQL failure");
        Check(!crosses.GetAll().Data!.Any(item => item.Key == cross.Key), "Successful sweep removes obsolete Cross");

        snapshot = Snapshot(repository);
        WithFailureTrigger(settings, "Lines", "DELETE", () => lines.Delete(new(b.Code, b.Owner, b.Version)));
        Check(Snapshot(new MySqlLineRepository(settings)) == snapshot,
            "Line deletion SQL failure restores preceding Cross removals in the same transaction");
        Check(lines.Delete(new(b.Code, b.Owner, b.Version)).Success, "Whole-line deletion can retry after SQL failure");
        return checks - before;
    }
}
