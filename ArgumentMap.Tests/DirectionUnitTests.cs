using System.Net.Http;
using System.Net;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Contracts;
using ArgumentMap.Client;
using ArgumentMap.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class DirectionUnitTests
{
    [TestMethod]
    [DataRow((short)0), DataRow((short)1)]
    public void Direction_DefaultForwardIsFixedAndOppositeControlsSingleOrBoth(short opposite)
    {
        var map = new TestMap(); var line = map.Add(100, 100, 120, 100); map.Calculate();
        Assert.AreEqual((short)1, line.Properties.PassInDirection); Assert.AreEqual((short)0, line.Properties.PassInOpposeDirection);
        var result = map.Lines.SaveProperties(new(line.Code, line.Owner, line.Version, line.Properties with { PassInOpposeDirection = opposite }));
        Assert.IsTrue(result.Success); Assert.AreEqual((short)1, result.Data!.Properties.PassInDirection);
        Assert.AreEqual(opposite, result.Data.Properties.PassInOpposeDirection); CollectionAssert.AreEqual(line.Nodes, result.Data.Nodes);
        Assert.IsTrue(map.Repository.Transaction(s => s.PendingSegments.Count == 0 && s.DirtyLines.Count == 0));
    }

    [TestMethod]
    [DataRow((short)0, (short)0), DataRow((short)2, (short)0), DataRow((short)1, (short)-1), DataRow((short)1, (short)2)]
    public void Direction_RejectForwardChangesAndInvalidOppositeWithoutCommit(short forward, short opposite)
    {
        var map = new TestMap(); var line = map.Add(100, 100, 120, 100); var before = map.Snapshot();
        var result = map.Lines.SaveProperties(new(line.Code, line.Owner, line.Version, line.Properties with { PassInDirection = forward, PassInOpposeDirection = opposite }));
        Assert.AreEqual("InvalidPassDirection", result.Code); Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void Reverse_RenumbersNodesAndKRetainsCrossIdStateAndOutgoingZero()
    {
        var map = new TestMap(); var a = map.Add(100, 110, 110, 110, 130, 110); var b = map.Add(110, 100, 110, 120); map.Calculate();
        a = map.Lines.SaveProperties(new(a.Code, a.Owner, a.Version, a.Properties with { RoadName = "反转线路", Speed = 500, PassInOpposeDirection = 1 })).Data!;
        var cross = map.Between(a, b); cross = map.Crosses.Edit(new(cross.Key, a.Owner, cross.Version, CrossState.Connected)).Data!;
        var reversed = map.Lines.Reverse(new(a.Code, a.Owner, a.Version)); Assert.IsTrue(reversed.Success, reversed.Message);
        var changed = reversed.Data!; Assert.AreEqual(a.Version + 1, changed.Version); Assert.AreEqual(a.Properties, changed.Properties);
        CollectionAssert.AreEqual(new[] { 130, 110, 100 }, changed.Nodes.Select(n => n.X).ToArray());
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, changed.Nodes.Select(n => n.Order).ToArray());
        Assert.AreEqual(cross, map.Between(a, b));
        Assert.IsTrue(map.Repository.Transaction(s => s.PendingSegments.Count(p => p.LineCode == a.Code) == 2 && s.StaleCrosses.Contains(cross.Key)));
        map.Calculate(); var newCross = map.Between(changed, b);
        Assert.AreEqual(cross.Key, newCross.Key); Assert.AreEqual(CrossState.Connected, newCross.State);
        Assert.AreEqual(1, newCross.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.0, newCross.LocationOn(a.Code).Percent);
    }

    [TestMethod]
    public void Reverse_RepositionsMultipleCrossesAndRoundTripRestoresGeometry()
    {
        var map = new TestMap(); var a = map.Add(100, 110, 110, 110, 120, 110, 140, 110);
        var b = map.Add(110, 100, 110, 120); var c = map.Add(125, 100, 125, 120); map.Calculate();
        var nodeCross = map.Between(a, b); var interiorCross = map.Between(a, c);
        nodeCross = map.Crosses.Edit(new(nodeCross.Key, a.Owner, nodeCross.Version, CrossState.Connected)).Data!;
        interiorCross = map.Crosses.Edit(new(interiorCross.Key, a.Owner, interiorCross.Version, CrossState.Disconnected)).Data!;
        Assert.AreEqual(1, nodeCross.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.0, nodeCross.LocationOn(a.Code).Percent);
        Assert.AreEqual(2, interiorCross.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.25, interiorCross.LocationOn(a.Code).Percent);
        var nextCrossId = map.Repository.Transaction(s => s.Crosses.Count);
        var reverse = map.Lines.Reverse(new(a.Code, a.Owner, a.Version)); Assert.IsTrue(reverse.Success, reverse.Message); map.Calculate();
        var reversed = reverse.Data!; var shiftedNode = map.Between(reversed, b); var shiftedInterior = map.Between(reversed, c);
        Assert.AreEqual(nodeCross.Key, shiftedNode.Key); Assert.AreEqual(CrossState.Connected, shiftedNode.State);
        Assert.AreEqual(2, shiftedNode.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.0, shiftedNode.LocationOn(a.Code).Percent);
        Assert.AreEqual(interiorCross.Key, shiftedInterior.Key); Assert.AreEqual(CrossState.Disconnected, shiftedInterior.State);
        Assert.AreEqual(0, shiftedInterior.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.75, shiftedInterior.LocationOn(a.Code).Percent);
        Assert.AreEqual(nextCrossId, map.Repository.Transaction(s => s.Crosses.Count));
        var second = map.Lines.Reverse(new(reversed.Code, reversed.Owner, reversed.Version)); Assert.IsTrue(second.Success); map.Calculate();
        CollectionAssert.AreEqual(a.Nodes, second.Data!.Nodes); Assert.AreEqual(a.Properties, second.Data.Properties);
        var restoredNode = map.Between(second.Data, b); var restoredInterior = map.Between(second.Data, c);
        Assert.AreEqual(nodeCross.Key, restoredNode.Key); Assert.AreEqual(nodeCross.State, restoredNode.State);
        Assert.AreEqual(1, restoredNode.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.0, restoredNode.LocationOn(a.Code).Percent);
        Assert.AreEqual(interiorCross.Key, restoredInterior.Key); Assert.AreEqual(interiorCross.State, restoredInterior.State);
        Assert.AreEqual(2, restoredInterior.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.25, restoredInterior.LocationOn(a.Code).Percent);
    }

    [TestMethod]
    [DataRow("AcuteCorner", new int[] { 100, 100, 120, 100, 110, 110 })]
    [DataRow("InvalidDirection", new int[] { 100, 100, 110, 109 })]
    [DataRow("DuplicateNode", new int[] { 100, 100, 110, 100, 100, 100 })]
    [DataRow("SegmentOverlap", new int[] { 100, 100, 120, 100, 110, 100 })]
    public void Reverse_DoesNotBypassGeometryRulesForLegacyInvalidLines(string expectedCode, int[] xy)
    {
        var map = new TestMap(); var line = map.Create();
        map.Repository.Transaction(s => { s.Lines[line.Code] = line with { Version = 1, Nodes = TestMap.Shape(line, xy).Nodes }; return true; });
        var before = map.Snapshot(); var rejected = map.Lines.Reverse(new(line.Code, line.Owner, 1));
        Assert.IsFalse(rejected.Success); Assert.AreEqual(expectedCode, rejected.Code); Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod, TestCategory("Http")]
    public async Task Reverse_HttpRejectsMissingExpiredAndUnrelatedSessionsWithoutChangingLine()
    {
        var map = new TestMap(); var line = map.Add(100, 100, 120, 100); var before = map.Snapshot();
        await using var server = await LocalLineServer.StartAsync(repository: map.Repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        using (var response = await http.PostAsJsonAsync("ReverseLine", new ReverseLineRequest(line.Code, line.Owner, line.Version)))
        {
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.AreEqual("LoginExpired", (await response.Content.ReadFromJsonAsync<ServiceResult<LineSnapshot>>())!.Code);
        }
        Assert.AreEqual(before, map.Snapshot());
        var oldClient = await TestLogin.ClientAsync(http, server, line.Owner);
        var currentClient = await TestLogin.ClientAsync(http, server, line.Owner);
        Assert.AreEqual("LoginExpired", (await oldClient.ReverseLineAsync(line.Code, line.Version)).Code);
        var outsider = await TestLogin.ClientAsync(http, server, "intruder");
        Assert.AreEqual("NotOwner", (await outsider.ReverseLineAsync(line.Code, line.Version)).Code);
        Assert.AreEqual("InvalidPassDirection", (await currentClient.SaveLinePropertiesAsync(line.Code, line.Version,
            line.Properties with { PassInDirection = 0 })).Code);
        Assert.AreEqual(before, map.Snapshot());
        Assert.IsTrue((await currentClient.ReverseLineAsync(line.Code, line.Version)).Success);
    }

    [TestMethod]
    public void Reverse_RejectsStaleVersionOtherOwnerLockedAndEmptyLines()
    {
        var map = new TestMap(); var line = map.Add(100, 100, 120, 100); var before = map.Snapshot();
        Assert.AreEqual("VersionConflict", map.Lines.Reverse(new(line.Code, line.Owner, line.Version - 1)).Code);
        Assert.AreEqual("NotOwner", map.Lines.Reverse(new(line.Code, "other", line.Version)).Code);
        Assert.AreEqual(before, map.Snapshot());
        map.Repository.Transaction(s => { s.Lines[line.Code] = line with { IsLocked = true }; return true; });
        Assert.AreEqual("LineIsLocked", map.Lines.Reverse(new(line.Code, line.Owner, line.Version)).Code);
        var empty = map.Create(); before = map.Snapshot();
        Assert.AreEqual("TooFewNodes", map.Lines.Reverse(new(empty.Code, empty.Owner, empty.Version)).Code);
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void Reverse_StillRejectsAnExistingThirdLineThroughCross()
    {
        var map = new TestMap(); map.Add(100, 110, 120, 110); map.Add(110, 100, 110, 120); map.Calculate(); var third = map.Create();
        map.Repository.Transaction(s => { s.Lines[third.Code] = third with { Version = 1, Nodes = TestMap.Shape(third, 105, 105, 115, 115).Nodes }; return true; });
        var before = map.Snapshot(); Assert.AreEqual("LinePassesCross", map.Lines.Reverse(new(third.Code, third.Owner, 1)).Code);
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod, TestCategory("Http")]
    public async Task C_ReverseDraftOnlyUntilF6AndSavedSelectionUsesHttp()
    {
        var map = new TestMap(); await using var server = await LocalLineServer.StartAsync(repository: map.Repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress }; var client = await TestLogin.ClientAsync(http, server, "owner");
        var editor = new LineEditor(client); Assert.IsTrue(await editor.NewLineAsync()); Assert.IsTrue(await editor.BeginDrawingAsync());
        editor.AddPoint(new Point(100, 100)); editor.AddPoint(new Point(110, 100)); editor.AddPoint(new Point(120, 100));
        var id = editor.DraftCode!; Assert.IsTrue(await editor.ReverseDirectionAsync());
        CollectionAssert.AreEqual(new[] { 120, 110, 100 }, editor.DraftNodes.Select(n => n.X).ToArray());
        Assert.AreEqual(0, (await client.GetLineDetailAsync(id)).Data!.Nodes.Length);
        Assert.IsTrue(await editor.CompleteAsync()); editor.ToggleSelection(); editor.SelectClosest(new Point(115, 100), 1);
        Assert.IsTrue(await editor.ReverseDirectionAsync());
        CollectionAssert.AreEqual(new[] { 100, 110, 120 }, (await client.GetLineDetailAsync(id)).Data!.Nodes.Select(n => n.X).ToArray());
        Assert.IsTrue(CoordinateMapView.IsEditorKey(System.Windows.Input.Key.C));
    }

    [STATestMethod]
    public void DirectionPanel_SendsFixedForwardAndSelectedOpposite()
    {
        var client = new CaptureClient(); var line = new LineSnapshot("ZAAAAAAAAB", "owner", false, 1, []);
        var window = new LinePropertiesWindow(client, line, "owner");
        try
        {
            var selection = (ComboBox)window.FindName("PassDirectionInput"); Assert.AreEqual(0, selection.SelectedIndex); selection.SelectedIndex = 1;
            ((TextBox)window.FindName("SpeedInput")).Text = "500";
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsNotNull(client.Sent); Assert.AreEqual((short)1, client.Sent.PassInDirection);
            Assert.AreEqual((short)1, client.Sent.PassInOpposeDirection); Assert.AreEqual(500, client.Sent.Speed);
        }
        finally { window.Close(); }
    }
    private sealed class CaptureClient : ILinePropertiesClient
    {
        internal LineProperties? Sent;
        public Task<ServiceResult<LineSnapshot>> GetLineDetailAsync(string id) => Task.FromResult(ServiceResult<LineSnapshot>.Fail("Unused", "Unused"));
        public Task<ServiceResult<LineSnapshot>> ToggleLineLockAsync(string id, long version) => Task.FromResult(ServiceResult<LineSnapshot>.Fail("Unused", "Unused"));
        public Task<ServiceResult<LineSnapshot>> SaveLinePropertiesAsync(string id, long version, LineProperties properties)
        { Sent = properties; return Task.FromResult(ServiceResult<LineSnapshot>.Fail("Captured", "测试提交已捕获")); }
    }
}
