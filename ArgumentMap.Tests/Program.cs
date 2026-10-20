using ArgumentMap.Rules;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using WpfArgumentMap;
using LineGeometry = ArgumentMap.Rules.LineGeometry;

internal static class Program
{
    private static int checks;

    [STAThread]
    private static void Main(string[] args)
    {
        CheckGeometry();
        CheckBusiness();
        checks += CornerChecks.RunAsync().GetAwaiter().GetResult();
        checks += CrossPassageChecks.RunAsync().GetAwaiter().GetResult();
        CheckConcurrencyAsync().GetAwaiter().GetResult();
        CheckHttpAndEditorAsync().GetAwaiter().GetResult();
        checks += CrossChecks.RunAsync().GetAwaiter().GetResult();
        checks += IncrementalCrossChecks.RunAsync().GetAwaiter().GetResult();
        checks += CrossEditChecks.RunAsync().GetAwaiter().GetResult();
        checks += TransactionChecks.CheckMemory();
        checks += LinePropertyChecks.CheckBusiness();
        checks += LoginChecks.RunAsync().GetAwaiter().GetResult();
        checks += MySqlChecks.RunAsync().GetAwaiter().GetResult();
        CheckRender(args.FirstOrDefault() ?? Path.Combine(ProjectPaths.Root, ".artifacts", "renders"));
        Console.WriteLine($"PASS: {checks} assertions covering geometry, save validation, transactions, HTTP, editor and rendering.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static LineNode N(int x, int y) => new("ZAAAAAAAAB", 0, x, y);

    internal static void CheckGeometry()
    {
        foreach (var (dx, dy) in new (int X, int Y)[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) })
        {
            var origin = N(250000, 250000);
            var end = N(origin.X + dx * 100, origin.Y + dy * 100);
            Check(LineGeometry.IsEightDirection(origin, end), "All eight directions accepted");
            var innerA = N(origin.X + dx * 10, origin.Y + dy * 10);
            var innerB = N(origin.X + dx * 90, origin.Y + dy * 90);
            Check(LineGeometry.Overlaps(origin, end, innerA, innerB), "Contained segment overlaps");
            Check(LineGeometry.Overlaps(end, origin, innerB, innerA), "Reversed segment overlaps");
            Check(LineGeometry.Overlaps(innerA, innerB, origin, end), "Overlap symmetry");
            Check(!LineGeometry.Overlaps(origin, end, end, N(end.X + dx * 20, end.Y + dy * 20)), "Touching endpoint is not positive-length overlap");
            Check(!LineGeometry.Overlaps(origin, end, N(end.X + dx, end.Y + dy), N(end.X + dx * 20, end.Y + dy * 20)), "Disjoint same-axis segments allowed");
        }
        Check(LineGeometry.Overlaps(N(0, 0), N(10, 0), N(1, 0), N(9, 0)), "User containment example");
        Check(LineGeometry.Overlaps(N(0, 0), N(10, 0), N(8, 0), N(20, 0)), "Partial overlap");
        Check(LineGeometry.Overlaps(N(0, 0), N(10, 0), N(0, 0), N(10, 0)), "Identical segments overlap");
        Check(!LineGeometry.Overlaps(N(0, 0), N(10, 0), N(1, 1), N(9, 1)), "Parallel distinct segments allowed");
        Check(!LineGeometry.Overlaps(N(0, 0), N(10, 10), N(0, 10), N(10, 0)), "Single crossing allowed");
        Check(!LineGeometry.IsEightDirection(N(0, 0), N(10, 9)), "Other slopes rejected");
        Check(!LineGeometry.IsEightDirection(N(0, 0), N(0, 0)), "Zero-length segment rejected");
        Check(!LineGeometry.Overlaps(N(0, 0), N(500000, 500000), N(0, 500000), N(500000, 0)), "Large-coordinate arithmetic avoids overflow");
        var random = new Random(1234);
        for (var i = 0; i < 500; i++)
        {
            var start = random.Next(400000);
            var length = random.Next(2, 100000);
            Check(LineGeometry.Overlaps(N(start, start), N(start + length, start + length),
                N(start + 1, start + 1), N(start + length - 1, start + length - 1)) == (length > 2), "Random diagonal containment");
        }
    }

    private static LineSnapshot Create(LineSaveService service, string owner = "owner") => service.Create(new(owner)).Data!;
    private static SaveLineRequest Shape(LineSnapshot line, params (int X, int Y)[] points) => new(line.Code, line.Owner, line.Version,
        points.Select((point, index) => new LineNode(line.Code, index, point.X, point.Y)).ToArray());

    internal static void CheckBusiness()
    {
        var repository = new InMemoryLineRepository();
        var service = new LineSaveService(repository);
        var a = Create(service);
        void Reject(SaveLineRequest request, string code)
        {
            var result = service.CheckShapeIsOKThenSave(request);
            Check(!result.Success && result.Code == code, $"Expected rejection {code}, got {result.Code}");
        }
        Reject(Shape(a), "TooFewNodes");
        Reject(Shape(a, (0, 0)), "TooFewNodes");
        Reject(Shape(a, (0, 0), (10, 0), (0, 0)), "DuplicateNode");
        Reject(Shape(a, (0, 0), (10, 0), (10, 10), (10, 0), (20, 0)), "DuplicateNode");
        Reject(Shape(a, (-1, 0), (10, 0)), "OutOfBounds");
        Reject(Shape(a, (500001, 0), (10, 0)), "OutOfBounds");
        Reject(Shape(a, (0, 0), (10, 9)), "InvalidDirection");
        var badOrder = Shape(a, (0, 0), (10, 0));
        badOrder.Nodes[1] = badOrder.Nodes[1] with { Order = 7 };
        Reject(badOrder, "InvalidOrder");
        badOrder = Shape(a, (0, 0), (10, 0));
        badOrder.Nodes[0] = badOrder.Nodes[0] with { Order = 1 };
        Reject(badOrder, "InvalidOrder");
        var badId = Shape(a, (0, 0), (10, 0));
        badId.Nodes[1] = badId.Nodes[1] with { LineCode = "ZZZZZZZZZZ" };
        Reject(badId, "LineCodeMismatch");
        Reject(Shape(a, (0, 0), (10, 0), (5, 0)), "SegmentOverlap");
        Reject(Shape(a, (0, 0), (10, 0), (10, 10), (5, 10), (5, 0), (8, 0)), "SegmentOverlap");
        Check(service.GetAll().Data!.Length == 0, "Failures do not publish partial lines");
        Check(service.StartDrawing(new(a.Code, a.Owner)).Data!.Nodes.Length == 0, "Failed saves preserve registered line");
        var savedA = service.CheckShapeIsOKThenSave(Shape(a, (0, 0), (10, 0))).Data!;
        Check(savedA.Version == 1 && savedA.Nodes.Length == 2, "First save commits full shape");
        var b = Create(service);
        Reject(Shape(b, (1, 0), (9, 0)), "SegmentOverlap");
        Reject(Shape(b, (8, 0), (20, 0)), "SegmentOverlap");
        Reject(Shape(b, (9, 0), (1, 0)), "SegmentOverlap");
        Reject(Shape(b, (10, 0), (20, 0)), "PointIsOccupied");
        Reject(Shape(b, (0, 0), (0, 10)), "PointIsOccupied");
        Check(service.CheckShapeIsOKThenSave(Shape(b, (1, 1), (9, 1))).Success, "Parallel non-overlapping line saves");
        Reject(Shape(savedA, (0, 1), (10, 1)), "SegmentOverlap");
        Check(service.StartDrawing(new(a.Code, a.Owner)).Data!.Nodes.SequenceEqual(savedA.Nodes), "Failed edit leaves original intact");
        Check(service.CheckShapeIsOKThenSave(Shape(savedA, (0, 0), (10, 0))).Success, "Unchanged edit excludes own stored version");
        Check(service.StartDrawing(new(a.Code, a.Owner)).Data!.Version == 1, "Unchanged shape retains version");
        var updated = service.CheckShapeIsOKThenSave(Shape(savedA, (0, 2), (10, 2))).Data!;
        Check(updated.Version == 2, "Changed geometry increments version");
        Reject(Shape(savedA, (0, 3), (10, 3)), "VersionConflict");
        Reject(Shape(updated, (0, 3), (10, 3)) with { Owner = "other" }, "NotOwner");
        Check(!service.StartDrawing(new(a.Code, "other")).Success, "Cannot edit another owner's line");
        Check(!service.Delete(new(a.Code, "other", updated.Version)).Success, "Cannot delete another owner's line");
        repository.Transaction(state => { state.Lines[a.Code] = state.Lines[a.Code] with { IsLocked = true }; return true; });
        Reject(Shape(updated, (0, 3), (10, 3)), "LineIsLocked");
        Check(!service.StartDrawing(new(a.Code, a.Owner)).Success, "Locked line cannot be signed out");
        Check(!service.Delete(new(a.Code, a.Owner, updated.Version)).Success, "Locked line cannot be deleted");
        repository.Transaction(state => { state.Lines[a.Code] = state.Lines[a.Code] with { IsLocked = false }; return true; });
        Check(service.Delete(new(a.Code, a.Owner, updated.Version)).Success, "Delete unlocks occupied coordinates and geometry");
        Reject(Shape(updated, (0, 3), (10, 3)), "LineNotFound");
        var longLine = Create(service);
        var divided=service.CheckShapeIsOKThenSave(Shape(longLine, (0, 500000), (500000, 500000))).Data!.Nodes;
        Check(divided.Length>2&&divided.Zip(divided.Skip(1)).All(p=>p.Second.X-p.First.X<=500),
            "F6 subdivides long logical segments without changing their path");
        var singleUnit = Create(service);
        Check(service.CheckShapeIsOKThenSave(Shape(singleUnit, (50, 50), (51, 50))).Success, "One-unit segments are valid");
        var crossing = Create(service);
        Check(service.CheckShapeIsOKThenSave(Shape(crossing, (100, 100), (140, 100), (140, 140), (120, 140), (120, 80))).Success,
            "Crossing without shared nodes or overlap allowed");
        var contiguous = Create(service);
        Check(service.CheckShapeIsOKThenSave(Shape(contiguous, (200, 200), (210, 200), (220, 200))).Success,
            "Consecutive segments of same line can meet at their one shared node");
        var snapshot = service.GetAll().Data!.First();
        var firstNode = snapshot.Nodes[0];
        snapshot.Nodes[0] = firstNode with { X = 499999 };
        Check(service.GetAll().Data!.First().Nodes[0] == firstNode, "Returned arrays cannot mutate store");
    }

    internal static async Task CheckConcurrencyAsync()
    {
        var service = new LineSaveService(new InMemoryLineRepository());
        var a = Create(service);
        var b = Create(service);
        var results = await Task.WhenAll(
            Task.Run(() => service.CheckShapeIsOKThenSave(Shape(a, (100, 100), (120, 100)))),
            Task.Run(() => service.CheckShapeIsOKThenSave(Shape(b, (102, 100), (118, 100)))));
        Check(results.Count(result => result.Success) == 1, "Concurrent overlapping saves cannot both commit");
        Check(service.GetAll().Data!.Length == 1, "Atomic store contains only valid winner");
    }

    internal static async Task CheckHttpAndEditorAsync()
    {
        await using var server = await LocalLineServer.StartAsync(repository: new InMemoryLineRepository());
        using var http = new HttpClient { BaseAddress = server.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var client = await TestLogin.ClientAsync(http, server, "owner");
        var editor = new LineEditor(client);
        Check(!await editor.BeginDrawingAsync(), "Drawing requires registered line");
        Check(await editor.NewLineAsync() && await editor.BeginDrawingAsync(), "HTTP create then sign out");
        editor.AddPoint(new Point(0.3, 0.4));
        editor.AddPoint(new Point(10.2, 0.4));
        Check(editor.DraftNodes[0] == new GridPoint(0, 0), "UI stores integer coordinates");
        editor.AddPoint(new Point(0, 0));
        var draft = editor.DraftNodes.ToArray();
        Check(!await editor.CompleteAsync() && editor.Message.Contains("重复"), "F6 validates duplicates on service");
        Check(editor.Mode == LineEditorMode.Drawing && editor.DraftNodes.SequenceEqual(draft), "Invalid save retains draft");
        await editor.RemoveAsync(new Point(0, 0));
        Check(await editor.CompleteAsync() && editor.Mode == LineEditorMode.Idle && editor.Lines.Count == 1, "Successful save refreshes and exits drawing");
        var saved = (await client.GetAllAsync()).Data![0];
        Check(saved.Nodes.Select(node => node.Order).SequenceEqual(new[] { 0, 1 }), "Client renumbers before HTTP save");
        Check(saved.Nodes.All(node => node.LineCode == saved.Code), "Submission carries consistent line ID");

        await editor.NewLineAsync();
        await editor.BeginDrawingAsync();
        editor.AddPoint(new Point(1, 0));
        editor.AddPoint(new Point(9, 0));
        Check(!await editor.CompleteAsync() && editor.Message.Contains("重叠"), "User example rejected through full WPF save path");
        Check(editor.DraftNodes.Count == 2 && editor.Mode == LineEditorMode.Drawing && editor.Lines.Count == 1, "Overlap rejection preserves draft and saved lines");
        editor.Cancel();
        editor.ToggleSelection();
        editor.SelectClosest(new Point(5, 0), 1);
        Check(await editor.EditSelectedAsync(), "F8/F9 retrieve committed shape through HTTP");
        editor.AddPoint(new Point(0, 10));
        Check(await editor.CompleteAsync() && editor.Lines.Count == 1, "Edit replaces same line");
        editor.ToggleSelection();
        editor.SelectClosest(new Point(5, 0), 1);
        Check(await editor.RemoveAsync(null) && editor.Lines.Count == 0, "Delete uses server repository");

        var registered = (await client.CreateAsync()).Data!;
        using var nonInteger = new StringContent($"{{\"lineCode\":{registered.Code},\"owner\":\"owner\",\"version\":0,\"nodes\":[{{\"lineCode\":{registered.Code},\"order\":0,\"x\":1.5,\"y\":0}},{{\"lineCode\":{registered.Code},\"order\":1,\"x\":10,\"y\":0}}]}}", Encoding.UTF8, "application/json");
        using var rejected = await http.PostAsync("CheckShapeIsOKThenSave", nonInteger);
        Check(rejected.StatusCode == HttpStatusCode.BadRequest, "Fractional JSON coordinates rejected at HTTP boundary");
        Check((await client.StartDrawingAsync(registered.Code)).Data!.Nodes.Length == 0, "Malformed JSON cannot change store");
        Check(!(await (await TestLogin.ClientAsync(http, server, "other")).StartDrawingAsync(registered.Code)).Success, "Owner denial through HTTP");

        var delayed = new DelayedSaveClient(client);
        var saving = new LineEditor(delayed);
        await saving.NewLineAsync();
        await saving.BeginDrawingAsync();
        saving.AddPoint(new Point(1000, 1000));
        saving.AddPoint(new Point(1010, 1000));
        var pending = saving.CompleteAsync();
        Check(saving.IsBusy, "Save is asynchronous and marks busy");
        saving.Cancel();
        Check(!saving.AddPoint(new Point(1020, 1000)) && !await saving.NewLineAsync(), "Busy commands cannot corrupt in-flight draft");
        delayed.Release();
        Check(await pending && !saving.IsBusy && saving.Mode == LineEditorMode.Idle, "Busy clears after successful save");

        await editor.NewLineAsync();
        await editor.BeginDrawingAsync();
        editor.AddPoint(new Point(2000, 2000));
        editor.AddPoint(new Point(2010, 2000));
        var disconnectedServer = await LocalLineServer.StartAsync(repository: new InMemoryLineRepository());
        using var disconnectedHttp = new HttpClient { BaseAddress = disconnectedServer.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        var failed = new LineEditor((await TestLogin.ClientAsync(disconnectedHttp, disconnectedServer, "owner")));
        await failed.NewLineAsync();
        await failed.BeginDrawingAsync();
        failed.AddPoint(new Point(3000, 3000));
        failed.AddPoint(new Point(3010, 3000));
        await disconnectedServer.DisposeAsync();
        Check(!await failed.CompleteAsync() && !failed.IsBusy && failed.Mode == LineEditorMode.Drawing && failed.DraftNodes.Count == 2,
            "Connection failure leaves draft editable");
    }

    internal static void CheckRender(string directory)
    {
        Directory.CreateDirectory(directory);
        var server = LocalLineServer.StartAsync(repository: new InMemoryLineRepository()).GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = TestLogin.ClientAsync(http, server, "owner").GetAwaiter().GetResult();
        var first = client.CreateAsync().GetAwaiter().GetResult().Data!;
        var points = new (int, int)[] { (249750, 249850), (249920, 250020), (249920, 250200), (250180, 250200), (250360, 250020), (250360, 249850) };
        Check(client.SaveAsync(first.Code, first.Version, Shape(first, points).Nodes).GetAwaiter().GetResult().Success, "Render data saved through HTTP");
        var intersecting = client.CreateAsync().GetAwaiter().GetResult().Data!;
        Check(client.SaveAsync(intersecting.Code, intersecting.Version, Shape(intersecting, (250000, 249900), (250000, 250250)).Nodes)
            .GetAwaiter().GetResult().Success, "Render crossing saved through HTTP");
        while (client.CalculateCrossAsync().GetAwaiter().GetResult().Data!.CanContinue) { }
        first = client.GetLineDetailAsync(first.Code).GetAwaiter().GetResult().Data!;
        first = client.SaveLinePropertiesAsync(first.Code, first.Version, new LineProperties
        { RoadName = "主线路", RoadDetail = "地图箭头沿节点序号显示", Speed = 60 }).GetAwaiter().GetResult().Data!;
        intersecting = client.GetLineDetailAsync(intersecting.Code).GetAwaiter().GetResult().Data!;
        intersecting = client.SaveLinePropertiesAsync(intersecting.Code, intersecting.Version, new LineProperties
        { RoadName = "交叉线路", Speed = 90 }).GetAwaiter().GetResult().Data!;
        var initialCross = client.GetCrossesAsync().GetAwaiter().GetResult().Data!.Single();
        var crossSnapshot = client.SetCrossStateAsync(initialCross.Key, initialCross.Version, CrossState.Connected).GetAwaiter().GetResult().Data!;
        var application = new Application();
        var window = new MainWindow(client, new HttpLoginClient(http));
        var root = (FrameworkElement)window.Content;
        var map = (CoordinateMapView)window.FindName("MapView");
        // Wait outside the WPF dispatcher to avoid blocking an awaited UI continuation.
        var snapshots = client.GetAllAsync().GetAwaiter().GetResult().Data!;
        var model = (LineEditor)typeof(CoordinateMapView).GetField("editor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(map)!;
        foreach (var snapshot in snapshots)
            typeof(LineEditor).GetMethod("ApplySnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(model, [snapshot]);
        var crossModel = (CrossEditor)typeof(CoordinateMapView).GetField("crossEditor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(map)!;
        ((List<CrossSnapshot>)typeof(CrossEditor).GetField("crosses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(crossModel)!).Add(crossSnapshot);
        root.Measure(new Size(1200, 800));
        root.Arrange(new Rect(0, 0, 1200, 800));
        root.UpdateLayout();
        Check(map.Center == new Point(250000, 250000) && map.LineCount == 2 && map.CrossCount == 1, "WPF startup coordinates, lines and Cross");
        Render(root, Path.Combine(directory, "saved-lines.png"));
        var expandedMapHeight = map.ActualHeight;
        var toolbar = (System.Windows.Controls.ScrollViewer)window.FindName("ToolPanel");
        var panelToggle = (System.Windows.Controls.Button)window.FindName("PanelToggleButton");
        panelToggle.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        root.UpdateLayout();
        Check(!window.IsToolPanelExpanded && map.ActualHeight > expandedMapHeight + 80,
            "Unified toolbar collapse gives space back to the map");
        Check(map.Center == new Point(250000, 250000) && map.LineCount == 2 && map.CrossCount == 1,
            "Collapsing controls preserves map center and loaded geometry");
        Check(((System.Windows.Controls.Menu)window.FindName("MainMenu")) is { Visibility: Visibility.Visible, ActualHeight: > 0 },
            "Menus remain available with the toolbar collapsed");
        Render(root, Path.Combine(directory, "collapsed-toolbar.png"));
        var arrowMenu = (System.Windows.Controls.MenuItem)window.FindName("ArrowMenu");
        arrowMenu.IsChecked = true;
        arrowMenu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        Check(map.ShowArrows && ((System.Windows.Controls.CheckBox)window.FindName("ArrowToggle")).IsChecked == true,
            "Collapsed view menu controls arrows and keeps toolbar state synchronized");
        map.SetShowArrows(false);
        var toolbarMenu = (System.Windows.Controls.MenuItem)window.FindName("ToolbarMenu");
        toolbarMenu.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.MenuItem.ClickEvent));
        root.UpdateLayout();
        Check(window.IsToolPanelExpanded && toolbarMenu.IsChecked && map.ActualHeight == expandedMapHeight,
            "View menu restores all grouped controls and original map space");
        var arrowOff = MapBitmap(map);
        map.SetShowArrows(true);
        root.UpdateLayout();
        var arrowOn = MapBitmap(map);
        Check(!arrowOff.SequenceEqual(arrowOn), "Visible direction arrows change map pixels");
        Render(root, Path.Combine(directory, "direction-lines.png"));
        server.Accounts.CreateUser(new("admin", "admin"));
        var adminSession = new HttpLoginClient(http).LoginAsync("admin", "admin").GetAwaiter().GetResult().Data!;
        var adminClient = new HttpLineClient(http, adminSession);
        var lockedResult = adminClient.ToggleLineLockAsync(intersecting.Code, intersecting.Version).GetAwaiter().GetResult();
        Check(lockedResult.Success, "Green locked-line render uses authenticated persisted lock state");
        var lockedSnapshot = lockedResult.Data!;
        typeof(LineEditor).GetMethod("ApplySnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(model, [lockedSnapshot]);
        map.ResetView();
        root.UpdateLayout();
        Render(root, Path.Combine(directory, "locked-direction-lines.png"));
        var bothResult = client.SaveLinePropertiesAsync(first.Code, first.Version,
            first.Properties with { RoadName = "高速线路", Speed = 500, PassInOpposeDirection = 1 }).GetAwaiter().GetResult();
        Check(bothResult.Success, "Maximum allowed speed saved through property service");
        first = bothResult.Data!;
        typeof(LineEditor).GetMethod("ApplySnapshot", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(model, [first]);
        map.ResetView();
        root.UpdateLayout();
        Render(root, Path.Combine(directory, "speed-500-lines.png"));
        Render(root, Path.Combine(directory, "bidirectional-lines.png"));
        var bidirectionalCrossPixels = MapBitmap(map);
        var firstOneWay = client.SetCrossStateAsync(crossSnapshot.Key, crossSnapshot.Version, CrossState.Road1ToRoad2).GetAwaiter().GetResult();
        Check(firstOneWay.Success, "Road1 to Road2 Cross state saved through HTTP for rendering");
        var renderedCrosses = (List<CrossSnapshot>)typeof(CrossEditor).GetField("crosses", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(crossModel)!;
        renderedCrosses.Clear(); renderedCrosses.Add(firstOneWay.Data!);
        crossModel.RememberExitLine(firstOneWay.Data!.Key.RoadCode1);
        crossModel.ToggleSelection(); crossModel.SelectClosest(new Point(firstOneWay.Data.Key.Position.X,firstOneWay.Data.Key.Position.Y),1);
        map.ResetView(); root.UpdateLayout();
        var firstOneWayPixels = MapBitmap(map);
        Check(!firstOneWayPixels.SequenceEqual(bidirectionalCrossPixels), "One-way Cross changes its marker and selected relation display");
        Render(root, Path.Combine(directory, "cross-road1-to-road2.png"));
        var secondOneWay = client.SetCrossStateAsync(firstOneWay.Data.Key,firstOneWay.Data.Version,CrossState.Road2ToRoad1).GetAwaiter().GetResult();
        Check(secondOneWay.Success, "Road2 to Road1 Cross state saved through HTTP for rendering");
        renderedCrosses.Clear(); renderedCrosses.Add(secondOneWay.Data!);
        crossModel.RememberExitLine(secondOneWay.Data!.Key.RoadCode2);
        map.ResetView(); root.UpdateLayout();
        Check(!firstOneWayPixels.SequenceEqual(MapBitmap(map)), "Opposite Cross directions render different marker colors, arrows and relation text");
        Render(root, Path.Combine(directory, "cross-road2-to-road1.png"));
        crossModel.LeaveSelection(); map.ResetView(); root.UpdateLayout();
        var logicalOneResult = client.CreateLogicalNodeAsync(250000.5m,250000.5m,6,new(){Value="小逻辑节点",Info="允许线路穿过，节点不能位于方形内"}).GetAwaiter().GetResult();
        Check(logicalOneResult.Success, "Logical square created through authenticated HTTP");
        while(client.CalculateLogicalNodeAsync().GetAwaiter().GetResult().Data!.CanContinue) { }
        var logicalTwoResult = client.CreateLogicalNodeAsync(250060.5m,250000.5m,12,new(){Value="大逻辑节点",Info="位置按所有穿过线段自动计算"}).GetAwaiter().GetResult();
        Check(logicalTwoResult.Success, "Second square uses side length 12");
        var passing=client.CreateAsync().GetAwaiter().GetResult().Data!;
        passing=client.SaveAsync(passing.Code,passing.Version,Shape(passing,(249980,250000),(250100,250000)).Nodes).GetAwaiter().GetResult().Data!;
        Check(passing is not null,"Extra axis-aligned road saved with LN positions atomically");
        while(client.CalculateCrossAsync().GetAwaiter().GetResult().Data!.CanContinue) { }
        typeof(LineEditor).GetMethod("ApplySnapshot",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.Invoke(model,[passing]);
        renderedCrosses.Clear();renderedCrosses.AddRange(client.GetCrossesAsync().GetAwaiter().GetResult().Data!);
        var logicalSnapshots=client.GetLogicalNodesAsync().GetAwaiter().GetResult().Data!;
        var logicalModel=(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(map)!;
        ((List<LogicalNodeSnapshot>)typeof(LogicalNodeEditor).GetField("nodes",System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(logicalModel)!).AddRange(logicalSnapshots);
        map.ToggleLogicalNodeMode();map.ToggleLogicalNodeSelection();logicalModel.SelectClosest(new Point(250000.5,250000.5),0.1);
        map.ZoomAtCenter(5);root.UpdateLayout();
        Check(map.LogicalNodeCount==2 && logicalModel.Selected?.Code==logicalOneResult.Data!.Code,"Both sizes display and square selection chooses correct node");
        Render(root,Path.Combine(directory,"logical-nodes.png"));
        var logicalWindow=new LogicalNodeWindow(client,"owner",new Point(250000.5,250000.5));
        var logicalRoot=(FrameworkElement)logicalWindow.Content;((System.Windows.Controls.Grid)logicalRoot).Background=logicalWindow.Background;logicalRoot.Measure(new Size(584,751));logicalRoot.Arrange(new Rect(0,0,584,751));logicalRoot.UpdateLayout();
        RenderDialog(logicalRoot,Path.Combine(directory,"logical-node-create.png"));logicalWindow.Close();
        logicalWindow=new LogicalNodeWindow(client,"owner",new Point(250000.5,250000.5),logicalSnapshots.Single(n=>n.Code==logicalOneResult.Data!.Code));
        logicalRoot=(FrameworkElement)logicalWindow.Content;((System.Windows.Controls.Grid)logicalRoot).Background=logicalWindow.Background;logicalRoot.Measure(new Size(584,751));logicalRoot.Arrange(new Rect(0,0,584,751));logicalRoot.UpdateLayout();
        ((System.Windows.Controls.ScrollViewer)logicalWindow.FindName("EditorScroll")).ScrollToBottom();logicalRoot.UpdateLayout();
        Check(((System.Windows.Controls.DataGrid)logicalWindow.FindName("PositionsGrid")).Items.Count==2,"LN properties list all passing segment positions");
        RenderDialog(logicalRoot,Path.Combine(directory,"logical-node-properties.png"));logicalWindow.Close();map.ToggleLogicalNodeMode();map.ResetView();root.UpdateLayout();
        map.FitView();
        root.UpdateLayout();
        Render(root, Path.Combine(directory, "whole-map.png"));
        map.ResetView();
        root.Measure(new Size(640, 400));
        root.Arrange(new Rect(0, 0, 640, 400));
        root.UpdateLayout();
        Render(root, Path.Combine(directory, "small-window.png"));
        Check(map.ActualHeight > 150 && toolbar.ScrollableHeight > 0,
            "Small windows retain map space and scroll to every wrapped tool group");
        toolbar.ScrollToBottom();
        root.UpdateLayout();
        Render(root, Path.Combine(directory, "small-window-cross-tools.png"));
        var shortcutsWindow = new ShortcutWindow();
        var shortcutsRoot = (FrameworkElement)shortcutsWindow.Content;
        shortcutsRoot.Measure(new Size(744, 681));
        shortcutsRoot.Arrange(new Rect(0, 0, 744, 681));
        shortcutsRoot.UpdateLayout();
        Render(shortcutsRoot, Path.Combine(directory, "shortcuts.png"));
        var shortcutSearch = (System.Windows.Controls.TextBox)shortcutsWindow.FindName("SearchBox");
        shortcutSearch.Text = "交点";
        shortcutsRoot.UpdateLayout();
        var searchGroups=((System.Windows.Controls.ItemsControl)shortcutsWindow.FindName("ShortcutGroups")).ItemsSource.Cast<object>();
        var searchEntries=searchGroups.SelectMany(group=>((Array)group.GetType().GetProperty("Entries")!.GetValue(group)!).Cast<object>()).ToArray();
        var searchKeys=searchEntries.Select(entry=>(string)entry.GetType().GetProperty("Keys")!.GetValue(entry)!).ToArray();
        Check(searchKeys.Contains("K")&&searchKeys.Contains("F7")&&!searchKeys.Contains("3")&&
            ((System.Windows.Controls.TextBlock)shortcutsWindow.FindName("ResultCount")).Text.StartsWith($"{searchEntries.Length} 项"),
            "Shortcut search finds cross actions and matching operation descriptions");
        Render(shortcutsRoot, Path.Combine(directory, "shortcuts-search.png"));
        shortcutSearch.Text = "no-such-shortcut";
        Check(((FrameworkElement)shortcutsWindow.FindName("EmptyResults")).Visibility == Visibility.Visible,
            "Shortcut search explains an empty result");
        shortcutsWindow.Close();
        var loginWindow = new LoginWindow(new HttpLoginClient(http), "admin");
        var loginRoot = (FrameworkElement)loginWindow.Content;
        loginRoot.Measure(new Size(424, 301));
        loginRoot.Arrange(new Rect(0, 0, 424, 301));
        loginRoot.UpdateLayout();
        Render(loginRoot, Path.Combine(directory, "login-window.png"));
        loginWindow.Close();
        var propertiesWindow = new LinePropertiesWindow(client, first, "owner");
        var propertiesRoot = (FrameworkElement)propertiesWindow.Content;
        propertiesRoot.Measure(new Size(544, 721));
        propertiesRoot.Arrange(new Rect(0, 0, 544, 721));
        propertiesRoot.UpdateLayout();
        Render(propertiesRoot, Path.Combine(directory, "line-properties.png"));
        propertiesWindow.Close();
        window.Close();
        application.Shutdown();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void Render(FrameworkElement visual, string path)
    {
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static void RenderDialog(FrameworkElement visual,string path)
    {
        var bitmap=new RenderTargetBitmap((int)visual.ActualWidth,(int)visual.ActualHeight,96,96,PixelFormats.Pbgra32);
        var drawing=new DrawingVisual();
        using(var context=drawing.RenderOpen()) context.DrawRectangle(new VisualBrush(visual){AutoLayoutContent=false},null,new Rect(0,0,visual.ActualWidth,visual.ActualHeight));
        bitmap.Render(drawing);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var stream=File.Create(path);encoder.Save(stream);
    }

    private static byte[] MapBitmap(FrameworkElement visual)
    {
        var bitmap = new RenderTargetBitmap((int)visual.ActualWidth, (int)visual.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return pixels;
    }

    private sealed class DelayedSaveClient(ILineClient inner) : ILineClient
    {
        private readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Release() => gate.SetResult();
        public Task<ServiceResult<LineSnapshot>> CreateAsync() => inner.CreateAsync();
        public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string id) => inner.StartDrawingAsync(id);
        public Task<ServiceResult<LineSnapshot[]>> GetAllAsync() => inner.GetAllAsync();
        public Task<ServiceResult<bool>> DeleteAsync(string id, long version) => inner.DeleteAsync(id, version);
        public async Task<ServiceResult<LineSnapshot>> SaveAsync(string id, long version, LineNode[] nodes)
        {
            await gate.Task;
            return await inner.SaveAsync(id, version, nodes);
        }
    }

}


