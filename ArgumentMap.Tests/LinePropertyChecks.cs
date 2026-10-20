using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using MySqlConnector;
using WpfArgumentMap;

internal static class LinePropertyChecks
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
    private static SaveLineRequest Shape(LineSnapshot line, params (int X, int Y)[] points) =>
        new(line.Code, line.Owner, line.Version, points.Select((point, i) => new LineNode(line.Code, i, point.X, point.Y)).ToArray());

    internal static int CheckBusiness()
    {
        var before = checks;
        var repository = new InMemoryLineRepository();
        var lines = new LineSaveService(repository);
        var crosses = new CrossService(repository);
        new LoginService(repository).CreateUser(new("admin", "admin"));
        var line = lines.Create(new("owner")).Data!;
        line = lines.CheckShapeIsOKThenSave(Shape(line, (8000, 8000), (8020, 8000))).Data!;
        Check(!lines.ToggleLock(new(line.Code, "owner", line.Version)).Success, "Old EditAllRoad permission required even for line owner");
        Check(lines.ToggleLock(new(line.Code, "admin", line.Version)).Code == "LineNotCalculated", "Changed line cannot lock before K");
        crosses.Calculate();
        Check(lines.ToggleLock(new(line.Code, "admin", line.Version)).Code == "LineNotCalculated", "Lock waits for final cleanup, even after last segment is calculated");
        crosses.Calculate();
        var properties = new LineProperties { RoadName = "逻辑主线", RoadDetail = "中文说明😀", Speed = 60 };
        var saved = lines.SaveProperties(new(line.Code, "owner", line.Version, properties));
        Check(saved.Success && saved.Data!.Nodes.SequenceEqual(line.Nodes) && saved.Data.Properties == properties,
            "Line name, description and speed update without changing shape");
        Check(repository.Transaction(state => state.PendingSegments.Count == 0 && state.DirtyLines.Count == 0), "Property edits do not schedule geometry recalculation");
        Check(!lines.SaveProperties(new(line.Code, "owner", line.Version, properties)).Success, "Stale property editor rejected");
        line = saved.Data!;
        Check(!lines.SaveProperties(new(line.Code, "other", line.Version, properties)).Success, "Property edit limited to original road controller");
        Check(!lines.SaveProperties(new(line.Code, "owner", line.Version, properties with { Speed = 4 })).Success &&
            !lines.SaveProperties(new(line.Code, "owner", line.Version, properties with { Speed = 501 })).Success,
            "Speed range limited to 5 through 500");
        Check(!lines.SaveProperties(new(line.Code, "owner", line.Version, properties with { Speed = 0 })).Success &&
            !lines.SaveProperties(new(line.Code, "owner", line.Version, properties with { RoadName = new string('名', 16) })).Success,
            "Zero speed and overlong names rejected by server");
        var locked = lines.ToggleLock(new(line.Code, "admin", line.Version));
        Check(locked.Success && locked.Data!.IsLocked, "User with EditAllRoad can lock another owner's calculated line");
        line = locked.Data!;
        Check(!lines.StartDrawing(new(line.Code, "owner")).Success &&
            !lines.SaveProperties(new(line.Code, "owner", line.Version, properties)).Success &&
            !lines.Delete(new(line.Code, "owner", line.Version)).Success, "Locked line rejects drawing, property changes and deletion");
        Check(lines.GetDetail(new(line.Code, "other")).Data!.IsLocked, "Locked properties remain available for read-only inspection");
        var unlocked = lines.ToggleLock(new(line.Code, "admin", line.Version));
        Check(unlocked.Success && !unlocked.Data!.IsLocked, "Lock operation toggles back to unlocked");
        return checks - before;
    }

    internal static async Task<int> CheckMySqlAsync(MySqlSettings settings)
    {
        var before = checks;
        var repository = new MySqlLineRepository(settings);
        var lines = new LineSaveService(repository);
        var crosses = new CrossService(repository);
        new LoginService(repository).CreateUser(new("admin", "admin"));
        var line = lines.Create(new("admin")).Data!;
        line = lines.CheckShapeIsOKThenSave(Shape(line, (8000, 8000), (8020, 8000))).Data!;
        var branch = lines.Create(new("admin")).Data!;
        branch = lines.CheckShapeIsOKThenSave(Shape(branch, (8010, 7990), (8010, 8010))).Data!;
        while (crosses.Calculate().Data!.CanContinue) { }
        var cross = crosses.GetAll().Data!.Single(item => item.Key.Contains(line.Code) && item.Key.Contains(branch.Code));
        cross = crosses.Edit(new(cross.Key, "admin", cross.Version, CrossState.Connected)).Data!;
        var properties = new LineProperties { RoadName = "主线路", RoadDetail = "速度测试😀", Speed = 90 };
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TRIGGER PreventMetadataNodeDeletion BEFORE DELETE ON LineNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Metadata must not replace nodes'";
            create.ExecuteNonQuery();
            try
            {
                var result = lines.SaveProperties(new(line.Code, "admin", line.Version, properties));
                Check(result.Success, "Property update never deletes and reinserts existing nodes");
                line = result.Data!;
                line = lines.ToggleLock(new(line.Code, "admin", line.Version)).Data!;
                Check(line is { IsLocked: true }, "Lock update never replaces geometry");
            }
            finally
            {
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TRIGGER PreventMetadataNodeDeletion";
                drop.ExecuteNonQuery();
            }
        }
        var reopened = new MySqlLineRepository(settings);
        var reloaded = new LineSaveService(reopened).GetDetail(new(line.Code, "admin")).Data!;
        Check(reloaded.IsLocked && reloaded.Properties == properties && reloaded.Nodes.SequenceEqual(line.Nodes),
            "MySQL restart retains lock, Chinese properties and speed");
        Check(new CrossService(reopened).GetAll().Data!.Contains(cross), "Property and lock updates retain Cross ID, percentages, version and setting");
        line = new LineSaveService(reopened).ToggleLock(new(reloaded.Code, "admin", reloaded.Version)).Data!;
        var contenders = await Task.WhenAll(
            Task.Run(() => new LineSaveService(repository).SaveProperties(new(line.Code, "admin", line.Version, properties with { RoadName = "甲修改" }))),
            Task.Run(() => new LineSaveService(reopened).SaveProperties(new(line.Code, "admin", line.Version, properties with { RoadName = "乙修改" }))));
        Check(contenders.Count(result => result.Success) == 1 && contenders.Single(result => !result.Success).Code == "VersionConflict",
            "Concurrent property windows cannot overwrite each other");
        line = contenders.Single(result => result.Success).Data!;

        await using var server = await LocalLineServer.StartAsync(repository: reopened);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        // This account already uses the explicit password chosen above.
        var login = await new ArgumentMap.Client.HttpLoginClient(http).LoginAsync("admin", "admin");
        var client = new ArgumentMap.Client.HttpLineClient(http, login.Data!);
        var editor = new LineEditor(client);
        await editor.RefreshLinesAsync();
        editor.ToggleSelection();
        editor.SelectClosest(new System.Windows.Point(8005, 8000), 1);
        Check((await editor.GetSelectedDetailAsync())?.Properties == line.Properties, "E retrieves fresh complete properties through authenticated HTTP");
        Check(await editor.ToggleSelectedLockAsync() && editor.Selected!.IsLocked, "L stores lock and updates local selected-line color metadata");
        Check(!await editor.EditSelectedAsync(), "F9 cannot edit selected locked line");
        Check(await editor.ToggleSelectedLockAsync() && !editor.Selected!.IsLocked, "L unlock restores ability to edit");
        line = (await client.GetLineDetailAsync(line.Code)).Data!;
        var race = await Task.WhenAll(
            Task.Run(() => new LineSaveService(repository).SaveProperties(new(line.Code, "admin", line.Version, line.Properties with { RoadName = "再次修改" }))),
            Task.Run(() => new LineSaveService(reopened).ToggleLock(new(line.Code, "admin", line.Version))));
        Check(race.Count(result => result.Success) == 1, "Concurrent locking and changed property saving cannot both commit using same old version");
        Check(CoordinateMapView.IsEditorKey(System.Windows.Input.Key.E) && CoordinateMapView.IsEditorKey(System.Windows.Input.Key.L) &&
            CoordinateMapView.IsEditorKey(System.Windows.Input.Key.D1), "Old E, L and 1 keys restored");
        return checks - before;
    }
}
