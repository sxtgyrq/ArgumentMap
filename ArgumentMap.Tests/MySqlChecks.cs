using System.Net.Http;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using MySqlConnector;
using WpfArgumentMap;

internal static class MySqlChecks
{
    private static int checks;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks++;
    }

    internal static async Task<int> RunAsync(string? scenario = null)
    {
        var initial = checks;
        var production = MySqlSettings.Load();
        var settings = production with { Database = "ArgumentMap_Integration_" + Guid.NewGuid().ToString("N") };
        using var admin = new MySqlConnection(production.ConnectionString(includeDatabase: false));
        await admin.OpenAsync();
        using (var check = admin.CreateCommand())
        {
            check.CommandText = "SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=@name";
            check.Parameters.AddWithValue("@name", settings.Database);
            Check(Convert.ToInt32(await check.ExecuteScalarAsync()) == 0, "Integration database must be newly owned");
        }
        try
        {
            var repository = new MySqlLineRepository(settings);
            if (scenario is not null)
            {
                switch (scenario)
                {
                    case "CrossDirections": await ArgumentMap.Tests.CrossDirectionMySqlChecks.RunAsync(settings); break;
                    case "Codes": ArgumentMap.Tests.CodeMigrationChecks.Run(settings,8); break;
                    case "CodesRollback": ArgumentMap.Tests.CodeMigrationChecks.Run(settings,8,failCopy:true); break;
                    case "Storage": await CheckStorageAsync(repository, settings); break;
                    case "HttpRestart": await CheckHttpRestartAsync(settings); break;
                    case "IncrementalRestart": CheckIncrementalRestart(settings); break;
                    case "Rollback": checks += TransactionChecks.CheckMySql(settings); break;
                    case "ConcurrentUsers": checks += await ConcurrentMySqlChecks.RunAsync(settings); break;
                    case "Login": checks += await LoginChecks.CheckMySqlAsync(settings); break;
                    case "Properties": checks += await LinePropertyChecks.CheckMySqlAsync(settings); break;
                    case "CrossIdentityMigration": CheckCrossIdentityUpgrade(settings); break;
                    case "SchemaMigration": CheckSchemaUpgrade(settings); break;
                    case "CrossPassage": checks += CrossPassageChecks.CheckMySql(settings); break;
                    case "SpeedMigration": ArgumentMap.Tests.SpeedMigrationChecks.Run(settings); break;
                    case "Direction": await ArgumentMap.Tests.DirectionMySqlChecks.RunAsync(settings); break;
                    case "DirectionCrossLocations": await ArgumentMap.Tests.DirectionMySqlChecks.RunCrossLocationsAsync(settings); break;
                    default: throw new ArgumentException("Unknown MySQL test scenario.", nameof(scenario));
                }
            }
            else
            {
            await CheckStorageAsync(repository, settings);
            await CheckHttpRestartAsync(settings);
            CheckIncrementalRestart(settings);
            checks += TransactionChecks.CheckMySql(settings);
            checks += await ConcurrentMySqlChecks.RunAsync(settings);
            checks += await LoginChecks.CheckMySqlAsync(settings);
            checks += await LinePropertyChecks.CheckMySqlAsync(settings);
            CheckCrossIdentityUpgrade(settings);
            CheckSchemaUpgrade(settings);
            checks += CrossPassageChecks.CheckMySql(settings);
            }
        }
        finally
        {
            // Only this uniquely generated, previously nonexistent test database is removed.
            if (settings.Database == production.Database || !settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal))
                throw new InvalidOperationException("Unsafe integration cleanup target.");
            using var cleanup = admin.CreateCommand();
            cleanup.CommandText = $"DROP DATABASE IF EXISTS `{settings.Database}`";
            await cleanup.ExecuteNonQueryAsync();
        }
        return checks - initial;
    }

    private static SaveLineRequest Shape(LineSnapshot line, params (int X, int Y)[] points) => new(line.Code, line.Owner, line.Version,
        points.Select((point, index) => new LineNode(line.Code, index, point.X, point.Y)).ToArray());

    private static LineSnapshot SaveNew(ILineRepository repository, params (int X, int Y)[] points)
    {
        var service = new LineSaveService(repository);
        var line = service.Create(new("中文用户😀")).Data!;
        var result = service.CheckShapeIsOKThenSave(Shape(line, points));
        Check(result.Success, "MySQL line save succeeds: " + result.Message);
        while (new CrossService(repository).Calculate().Data!.CanContinue) { }
        return result.Data!;
    }

    private static async Task CheckStorageAsync(MySqlLineRepository repository, MySqlSettings settings)
    {
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            await connection.OpenAsync();
            using var charset = connection.CreateCommand();
            charset.CommandText = "SELECT DEFAULT_CHARACTER_SET_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=@db";
            charset.Parameters.AddWithValue("@db", settings.Database);
            Check((string?)await charset.ExecuteScalarAsync() == "utf8mb4", "Database supports full Unicode");
        }
        var service = new LineSaveService(repository);
        var crosses = new CrossService(repository);
        var a = SaveNew(repository, (0, 0), (10, 0));
        var b = SaveNew(repository, (5, 0), (5, 1));
        var cross = crosses.GetAll().Data!.Single();
        Check(crosses.Edit(new(cross.Key, a.Owner, cross.Version, CrossState.Connected)).Success, "Cross setting written to MySQL");
        SaveNew(repository, (100, 100), (101, 101));
        SaveNew(repository, (100, 101), (101, 100));
        Check(!crosses.GetAll().Data!.Any(item => item.Key.Position.X == 100.5), "Half-integer geometric crossing does not create a Cross");
        var reopened = new MySqlLineRepository(settings);
        var reloaded = new LineSaveService(reopened).GetAll().Data!;
        Check(reloaded.Length == 4 && reloaded.All(line => line.Owner == "中文用户😀"), "Reopen retains Chinese and emoji owner text");
        var loadedCrosses = new CrossService(reopened).GetAll().Data!;
        Check(loadedCrosses.Length == 1 && loadedCrosses.Single().State == CrossState.Connected,
            "Reopen retains the integer Cross setting and ignores the half-integer crossing");
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();
            using var column = connection.CreateCommand();
            column.CommandText = "SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='Crosses' AND COLUMN_NAME IN ('X','Y') AND DATA_TYPE='int'";
            column.Parameters.AddWithValue("@db", settings.Database);
            Check(Convert.ToInt32(column.ExecuteScalar()) == 2, "Cross coordinate columns use actual INT storage");
            using var value = connection.CreateCommand();
            value.CommandText = "SELECT X,Y FROM Crosses WHERE RoadCode1=@first AND RoadCode2=@second AND X=@x AND Y=@y";
            value.Parameters.AddWithValue("@first", cross.Key.RoadCode1); value.Parameters.AddWithValue("@second", cross.Key.RoadCode2); value.Parameters.AddWithValue("@x", cross.Key.Position.X); value.Parameters.AddWithValue("@y", cross.Key.Position.Y);
            using var reader = value.ExecuteReader();
            Check(reader.Read() && reader.GetInt32(0) == 5 && reader.GetInt32(1) == 0, "Database saves actual integer coordinates rather than doubled transport values");
        }

        var invalid = service.Create(new(a.Owner)).Data!;
        Check(!service.CheckShapeIsOKThenSave(Shape(invalid, (1, 0), (9, 0))).Success, "Overlapping geometry rejected against persisted lines");
        Check(service.StartDrawing(new(invalid.Code, a.Owner)).Data!.Nodes.Length == 0, "Rejected save inserts no nodes");
        Check(!service.CheckShapeIsOKThenSave(Shape(invalid, (0, 0), (0, 10))).Success, "Global occupied coordinates rejected");
        Check(!service.CheckShapeIsOKThenSave(Shape(invalid, (20, 20), (30, 20), (20, 20))).Success, "Duplicate nodes rejected");
        Check(!service.CheckShapeIsOKThenSave(Shape(a, (0, 0), (10, 0), (5, 0))).Success, "Invalid edit leaves database unchanged");

        var savedCross = crosses.GetAll().Data!.Single(item => item.Key.Contains(a.Code) && item.Key.Contains(b.Code));
        var acuteEdit = service.CheckShapeIsOKThenSave(Shape(a, (0, 0), (10, 0), (5, 5)));
        Check(!acuteEdit.Success && acuteEdit.Code == "AcuteCorner", "MySQL transaction rejects a 45-degree interior corner");
        var unchangedAfterAcute = new LineSaveService(new MySqlLineRepository(settings)).StartDrawing(new(a.Code, a.Owner)).Data!;
        Check(unchangedAfterAcute.Version == a.Version && unchangedAfterAcute.Nodes.SequenceEqual(a.Nodes),
            "Reopening MySQL after an acute edit retains the saved nodes and version");
        Check(new CrossService(new MySqlLineRepository(settings)).GetAll().Data!.Contains(savedCross) &&
            repository.Transaction(state => state.PendingSegments.Count == 0 && state.DirtyLines.Count == 0 && state.StaleCrosses.Count == 0),
            "MySQL acute rejection retains Cross settings and leaves no pending or stale markers");
        try
        {
            repository.Transaction<bool>(state =>
            {
                state.Lines[a.Code] = state.Lines[a.Code] with { Version = 99, Nodes = [new(a.Code, 0, 100, 100), new(a.Code, 1, 110, 100)] };
                return true;
            });
            throw new InvalidOperationException("Expected unique coordinate database constraint failure.");
        }
        catch (MySqlException exception) when (exception.Number == 1062) { checks++; }
        Check(service.StartDrawing(new(a.Code, a.Owner)).Data!.Nodes.SequenceEqual(a.Nodes), "SQL failure rolls back node replacement");
        Check(crosses.GetAll().Data!.Contains(savedCross), "SQL failure preserves Cross state and version");

        a = service.CheckShapeIsOKThenSave(Shape(a, (0, 0), (10, 0), (20, 0))).Data!;
        while (crosses.Calculate().Data!.CanContinue) { }
        Check(a.Nodes.Length == 3 && crosses.GetAll().Data!.Any(item => item.Key == savedCross.Key && item.State == CrossState.Connected), "Changed line atomically preserves valid Cross setting");
        Check(service.CheckShapeIsOKThenSave(Shape(a, (0, 0), (10, 0), (20, 0))).Success, "Unchanged saves succeed without node replacement");
        Check(service.Delete(new(b.Code, b.Owner, b.Version)).Success && crosses.GetAll().Data!.All(item => item.Key.RoadCode2 != b.Code && item.Key.RoadCode1 != b.Code), "Delete cleans up nodes and Cross records");
        var nextBefore = repository.Transaction(state => state.Lines.Count);
        var afterReopen = new MySqlLineRepository(settings);
        Check(afterReopen.Transaction(state => state.Lines.Count) == nextBefore, "Identifier counters survive restart");

        var other = new LineSaveService(afterReopen);
        var c = service.Create(new(a.Owner)).Data!;
        var d = other.Create(new(a.Owner)).Data!;
        var concurrent = await Task.WhenAll(
            Task.Run(() => service.CheckShapeIsOKThenSave(Shape(c, (1000, 1000), (1020, 1000)))),
            Task.Run(() => other.CheckShapeIsOKThenSave(Shape(d, (1002, 1000), (1018, 1000)))));
        Check(concurrent.Count(result => result.Success) == 1, "Separate repository instances cannot commit overlapping saves concurrently");
    }

    private static async Task CheckHttpRestartAsync(MySqlSettings settings)
    {
        LineSnapshot first;
        LineSnapshot second;
        CrossKey crossId;
        await using (var host = await LocalLineServer.StartAsync(repository: new MySqlLineRepository(settings)))
        {
            using var http = new HttpClient { BaseAddress = host.BaseAddress };
            var client = await TestLogin.ClientAsync(http, host, "数据库测试用户");
            var editor = new LineEditor(client);
            await editor.NewLineAsync();
            await editor.BeginDrawingAsync();
            editor.AddPoint(new Point(2000, 2000));
            editor.AddPoint(new Point(2010, 2000));
            Check(await editor.CompleteAsync(), "WPF F6 path saves into MySQL");
            first = (await client.GetAllAsync()).Data!.Single(line => line.Owner == "数据库测试用户");
            second = (await client.CreateAsync()).Data!;
            var secondSave = await client.SaveAsync(second.Code, second.Version, Shape(second, (2005, 2000), (2005, 2001)).Nodes);
            Check(secondSave.Success, "Perpendicular T-junction saves through HTTP"); second = secondSave.Data!;
            Check(!(await client.GetCrossesAsync()).Data!.Any(cross => cross.Key.Contains(first.Code) && cross.Key.Contains(second.Code)),
                "F6 persists geometry and defers Cross generation until K");
            while ((await client.CalculateCrossAsync()).Data!.CanContinue) { }
            var cross = (await client.GetCrossesAsync()).Data!.Single(item => item.Key.Contains(first.Code) && item.Key.Contains(second.Code));
            crossId = cross.Key;
            Check((await client.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Disconnected)).Success, "HTTP Cross O state saved to database");
        }
        await using (var restarted = await LocalLineServer.StartAsync(repository: new MySqlLineRepository(settings)))
        {
            using var http = new HttpClient { BaseAddress = restarted.BaseAddress };
            var client = await TestLogin.ClientAsync(http, restarted, "数据库测试用户");
            Check((await client.GetAllAsync()).Data!.Any(line => line.Code == first.Code && line.Nodes.SequenceEqual(first.Nodes)), "HTTP host restart restores line geometry");
            Check((await client.GetCrossesAsync()).Data!.Any(cross => cross.Key == crossId && cross.State == CrossState.Disconnected), "HTTP host restart restores Cross state");
            Check((await client.DeleteAsync(second.Code, second.Version)).Success, "HTTP deletion persisted");
        }
        Check(new LineSaveService(new MySqlLineRepository(settings)).GetAll().Data!.All(line => line.Code != second.Code), "Deletion survives repository restart");
    }

    private static void CheckSchemaUpgrade(MySqlSettings settings) => ArgumentMap.Tests.CodeMigrationChecks.Run(settings, 1);

    private static void CheckIncrementalRestart(MySqlSettings settings)
    {
        var repository = new MySqlLineRepository(settings);
        var lines = new LineSaveService(repository);
        var a = lines.Create(new("断点测试")).Data!;
        a = lines.CheckShapeIsOKThenSave(Shape(a, (4000, 4000), (4010, 4000))).Data!;
        var b = lines.Create(new("断点测试")).Data!;
        b = lines.CheckShapeIsOKThenSave(Shape(b, (4005, 3995), (4005, 4005))).Data!;
        repository = new MySqlLineRepository(settings);
        Check(repository.Transaction(state => state.PendingSegments.Count(segment => segment.LineCode == a.Code || segment.LineCode == b.Code)) == 2,
            "Changed segment flags survive repository restart");
        var service = new CrossService(repository);
        service.Calculate();
        repository = new MySqlLineRepository(settings);
        Check(repository.Transaction(state => state.PendingSegments.Count(segment => segment.LineCode == a.Code || segment.LineCode == b.Code)) == 1,
            "Processed segment stays complete after interrupted calculation");
        service = new CrossService(repository);
        while (service.Calculate().Data!.CanContinue) { }
        var cross = service.GetAll().Data!.Single(cross => cross.Key.Contains(a.Code) && cross.Key.Contains(b.Code));
        cross = service.Edit(new(cross.Key, a.Owner, cross.Version, CrossState.Connected)).Data!;
        lines = new LineSaveService(repository);
        a = lines.CheckShapeIsOKThenSave(Shape(a, (4000, 4000), (4005, 4000), (4010, 4000))).Data!;
        repository = new MySqlLineRepository(settings);
        Check(repository.Transaction(state => state.StaleCrosses.Contains(cross.Key) &&
            state.PendingSegments.Count(segment => segment.LineCode == a.Code) == 2), "Both obsolete Cross and pending segment markers persist");
        service = new CrossService(repository);
        var pendingEdit = service.Edit(new(cross.Key, a.Owner, cross.Version, CrossState.Disconnected));
        Check(pendingEdit.Success && repository.Transaction(state => state.StaleCrosses.Contains(cross.Key)),
            "Restart retains pending marker while allowing existing Cross setting to be edited");
        service.Calculate();
        var resumed = new CrossService(new MySqlLineRepository(settings));
        while (resumed.Calculate().Data!.CanContinue) { }
        var canonical = resumed.GetAll().Data!.Single(item => item.Key == cross.Key);
        Check(canonical.State == CrossState.Disconnected && canonical.LocationOn(a.Code).RoadOrder == 1 && canonical.LocationOn(a.Code).Percent == 0,
            "Resumed calculation retains setting and persists larger node index at percent zero");
        lines = new LineSaveService(new MySqlLineRepository(settings));
        a = lines.CheckShapeIsOKThenSave(Shape(a, (4000, 4000), (4010, 4000))).Data!;
        resumed = new CrossService(new MySqlLineRepository(settings));
        while (resumed.Calculate().Data!.CanContinue) { }
        var removedNode = new CrossService(new MySqlLineRepository(settings)).GetAll().Data!.Single(item => item.Key == cross.Key);
        Check(removedNode.State == CrossState.Disconnected && removedNode.LocationOn(a.Code).RoadOrder == 0 && removedNode.LocationOn(a.Code).Percent == 0.5,
            "Deleting internal node retains Cross ID and state across MySQL restart");
        Check(new LineSaveService(new MySqlLineRepository(settings)).Delete(new(a.Code, a.Owner, a.Version)).Success,
            "Whole-line deletion remains available after interrupted calculation");
    }

    private static void CheckCrossIdentityUpgrade(MySqlSettings settings) => ArgumentMap.Tests.CodeMigrationChecks.Run(settings, 3);
}


