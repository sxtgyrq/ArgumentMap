using ArgumentMap.Rules;
using System.Net.Http;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using WpfArgumentMap;

internal static class CornerChecks
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }

    private static LineNode[] Nodes(LineSnapshot line, params (int X, int Y)[] points) =>
        points.Select((p, i) => new LineNode(line.Code, i, p.X, p.Y)).ToArray();

    internal static async Task<int> RunAsync()
    {
        var directions = new (int X, int Y)[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) };
        var blank = new LineSnapshot("ZAAAAAAAAB", "owner", false, 0, []);
        for (var a = 0; a < 8; a++)
            for (var b = 0; b < 8; b++)
            {
                var nodes = Nodes(blank, (250000 + directions[a].X * 100, 250000 + directions[a].Y * 100),
                    (250000, 250000), (250000 + directions[b].X * 300, 250000 + directions[b].Y * 300));
                var delta = Math.Abs(a - b);
                var expectedAcute = Math.Min(delta, 8 - delta) == 1;
                Check((LineGeometry.FindAcuteCorner(nodes) == 1) == expectedAcute,
                    "All compass pairs distinguish 45-degree interior angles from 90/135/180, with unequal lengths");
                Check((LineGeometry.FindAcuteCorner(nodes.Reverse().ToArray()) == 1) == expectedAcute,
                    "Reversing node order preserves corner classification");
            }
        Check(LineGeometry.FindAcuteCorner(Nodes(blank, (0, 0), (500000, 0), (0, 500000))) == 1,
            "Large-coordinate acute corner uses exact arithmetic without overflow");
        Check(LineGeometry.FindAcuteCorner(Nodes(blank, (0, 0), (500000, 500000))) is null,
            "Two-node lines have no interior corner");
        Check(LineGeometry.FindAcuteCorner(Nodes(blank, (0, 0), (10, 0), (10, 10), (5, 5))) == 2,
            "Validation locates an acute corner beyond the first intermediate node");

        var repository = new InMemoryLineRepository();
        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = await TestLogin.ClientAsync(http, server, "owner");
        var line = (await client.CreateAsync()).Data!;
        line = (await client.SaveAsync(line.Code, line.Version, Nodes(line, (100, 100), (120, 100)))).Data!;
        var other = (await client.CreateAsync()).Data!;
        Check((await client.SaveAsync(other.Code, other.Version, Nodes(other, (110, 90), (110, 110)))).Success,
            "Corner scenario has a saved intersecting line");
        while ((await client.CalculateCrossAsync()).Data!.CanContinue) { }
        var cross = (await client.GetCrossesAsync()).Data!.Single();
        cross = (await client.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Connected)).Data!;
        var rejected = await client.SaveAsync(line.Code, line.Version, Nodes(line, (100, 100), (120, 100), (115, 95)));
        Check(!rejected.Success && rejected.Code == "AcuteCorner" && rejected.Message.Contains("序号 1") && rejected.Message.Contains("120, 100"),
            "Direct authenticated HTTP cannot bypass acute-corner validation and reports the offending node");
        var unchanged = (await client.GetLineDetailAsync(line.Code)).Data!;
        Check(unchanged.Version == line.Version && unchanged.Nodes.SequenceEqual(line.Nodes),
            "Rejected acute edit preserves stored geometry and version");
        Check((await client.GetCrossesAsync()).Data!.Single() == cross && repository.Transaction(s =>
            s.PendingSegments.Count == 0 && s.DirtyLines.Count == 0 && s.StaleCrosses.Count == 0),
            "Rejected acute edit preserves Cross setting and leaves no recalculation markers");

        var editor = new LineEditor(client);
        await editor.RefreshLinesAsync();
        editor.ToggleSelection();
        editor.SelectClosest(new Point(115, 100), 1);
        Check(await editor.EditSelectedAsync(), "Acute-corner scenario loads an existing line for editing");
        Check(editor.AddPoint(new Point(115, 95)), "Drawing still permits placing a candidate for subsequent validation");
        var draft = editor.DraftNodes.ToArray();
        Check(!await editor.CompleteAsync() && editor.Message.Contains("45° 锐角") &&
            editor.Mode == LineEditorMode.Drawing && editor.DraftNodes.SequenceEqual(draft) && !editor.IsBusy,
            "F6 rejects an acute corner while retaining the editable draft");
        Check((await client.GetLineDetailAsync(line.Code)).Data!.Version == line.Version,
            "Client rejection does not save an invalid draft");
        Check(await editor.RemoveAsync(new Point(115, 95)), "Offending node can be removed manually");
        Check(editor.AddPoint(new Point(125, 105)) && await editor.CompleteAsync(),
            "135-degree obtuse corner saves after correcting the acute draft");

        var right = (await client.CreateAsync()).Data!;
        Check((await client.SaveAsync(right.Code, right.Version, Nodes(right, (1000, 1000), (1010, 1000), (1010, 1010)))).Success,
            "90-degree right corner remains valid");
        var straight = (await client.CreateAsync()).Data!;
        Check((await client.SaveAsync(straight.Code, straight.Version, Nodes(straight, (2000, 2000), (2010, 2000), (2020, 2000)))).Success,
            "180-degree straight continuation remains valid");
        var diagonal = (await client.CreateAsync()).Data!;
        Check((await client.SaveAsync(diagonal.Code, diagonal.Version, Nodes(diagonal, (3000, 3000), (3010, 3010)))).Success,
            "Standalone diagonal line saves");
        var horizontal = (await client.CreateAsync()).Data!;
        Check((await client.SaveAsync(horizontal.Code, horizontal.Version, Nodes(horizontal, (3000, 3005), (3010, 3005)))).Code == "NonOrthogonalIntersection",
            "Different lines cannot intersect at 45 degrees even without an interior acute corner");
        return checks;
    }
}
