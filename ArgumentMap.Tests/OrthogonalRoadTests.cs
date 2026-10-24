using System.Net.Http;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class OrthogonalRoadTests
{
    [TestMethod]
    [DataRow(1, 0), DataRow(1, 1), DataRow(0, 1), DataRow(-1, 1)]
    [DataRow(-1, 0), DataRow(-1, -1), DataRow(0, -1), DataRow(1, -1)]
    public void Save_AllCompassPairsRequireRightAngleIndependentOfTravelDirection(int ax, int ay)
    {
        foreach (var (bx, by) in new[] { (1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1) })
        {
            var map = new TestMap();
            map.Add(250000 - 20 * ax, 250000 - 20 * ay, 250000 + 20 * ax, 250000 + 20 * ay);
            var candidate = map.Create(); var before = map.Snapshot();
            var shape = TestMap.Shape(candidate, 250000 - 10 * bx, 250000 - 10 * by, 250000 + 10 * bx, 250000 + 10 * by);
            var result = map.Lines.CheckShapeIsOKThenSave(shape);
            if (ax * bx + ay * by == 0)
            {
                Assert.IsTrue(result.Success, result.Message); map.Calculate();
                Assert.AreEqual(new CrossPosition(500000, 500000), map.Crosses.GetAll().Data!.Single().Key.Position);
            }
            else
            {
                Assert.AreEqual(ax * by - ay * bx == 0 ? "SegmentOverlap" : "NonOrthogonalIntersection", result.Code);
                Assert.AreEqual(before, map.Snapshot(), "Rejected angle must preserve every map record and marker.");
            }
        }
    }

    [TestMethod]
    [DataRow(new int[] { 110, 110, 120, 120 }, false)]
    [DataRow(new int[] { 120, 100, 110, 110 }, false)]
    [DataRow(new int[] { 110, 110, 110, 130 }, true)]
    [DataRow(new int[] { 110, 90, 110, 110 }, true)]
    public void Save_EndpointOnOtherRoadIsChecked(int[] xy, bool allowed)
    {
        var map = new TestMap(); map.Add(100, 110, 140, 110); var candidate = map.Create(); var before = map.Snapshot();
        var result = map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate, xy));
        Assert.AreEqual(allowed, result.Success, result.Message);
        if (!allowed) { Assert.AreEqual("NonOrthogonalIntersection", result.Code); Assert.AreEqual(before, map.Snapshot()); }
    }

    [TestMethod, DataRow(true), DataRow(false)]
    public void Save_BendContactChecksIncomingAndOutgoingSegments(bool bendIsExistingRoad)
    {
        var bend = new[] { 100, 100, 110, 110, 110, 130 }; var axis = new[] { 100, 110, 120, 110 };
        var map = new TestMap(); map.Add(bendIsExistingRoad ? bend : axis); var candidate = map.Create(); var before = map.Snapshot();
        var result = map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate, bendIsExistingRoad ? axis : bend));
        Assert.AreEqual("NonOrthogonalIntersection", result.Code, "Outgoing vertical is perpendicular, incoming diagonal is not.");
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void Save_OrthogonalFirstCrossDoesNotHideLaterObliqueCross()
    {
        var map = new TestMap(); map.Add(100, 110, 160, 110); var candidate = map.Create(); var before = map.Snapshot();
        var result = map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate, 110, 100, 110, 120, 120, 130, 130, 130, 160, 100));
        Assert.AreEqual("NonOrthogonalIntersection", result.Code); StringAssert.Contains(result.Message, "第 4 段");
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void Save_UsesFiniteSegmentsAllowsNearMissesAndSameRoadObtuseBends()
    {
        var map = new TestMap(); map.Add(100, 110, 120, 110);
        map.Add(125, 115, 130, 120); // Its infinite extension intersects the first road.
        map.Add(101, 111, 109, 119); // Near the road, but no contact.
        map.Add(200, 200, 210, 200, 220, 210); // 135-degree bend belongs to the same road.
        Assert.AreEqual(4, map.Lines.GetAll().Data!.Length);
    }

    [TestMethod]
    public void Save_OppositeDiagonalsIgnoreHalfIntegerCrossAndKeepMaximumIntegerCoordinates()
    {
        var map = new TestMap(); map.Add(0, 0, 1, 1); map.Add(0, 1, 1, 0); map.Calculate();
        Assert.AreEqual(0, map.Crosses.GetAll().Data!.Length);
        var large = new TestMap(); large.Add(499980,499980,500000,500000); large.Add(499980,500000,500000,499980); large.Calculate();
        Assert.AreEqual(new CrossPosition(999980,999980), large.Crosses.GetAll().Data!.Single().Key.Position);
    }

    [TestMethod]
    public void Edit_RejectsNewObliqueCrossAndPreservesCrossStateAndLnPositions()
    {
        var map = new TestMap(); var road = map.Add(90, 110, 130, 110); var vertical = map.Add(120, 90, 120, 130); map.Calculate();
        var cross = map.Between(road, vertical); Assert.IsTrue(map.Crosses.Edit(new(cross.Key, road.Owner, cross.Version, CrossState.Connected)).Success);
        Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new(road.Owner, 100.5m, 110.5m, 6, new())).Success);
        var before = map.Snapshot();
        var rejected = map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(road, 90, 110, 110, 110, 130, 130));
        Assert.AreEqual("NonOrthogonalIntersection", rejected.Code); Assert.AreEqual(before, map.Snapshot());
        var corrected = map.Save(road, 90, 110, 110, 110, 130, 110); map.Calculate();
        Assert.AreEqual(CrossState.Connected, map.Between(corrected, vertical).State);
    }

    [TestMethod]
    public void Reverse_ValidShapeRemainsOrthogonalAndLegacyObliqueShapeCannotBypassSaveValidation()
    {
        var map = new TestMap(); var axis = map.Add(100, 110, 140, 110); var vertical = map.Add(120, 90, 120, 130);
        Assert.IsTrue(map.Lines.Reverse(new(vertical.Code, vertical.Owner, vertical.Version)).Success); map.Calculate();
        map.Repository.Transaction(s => { s.Lines[vertical.Code] = vertical with { Nodes = TestMap.Shape(vertical, 110, 100, 130, 120).Nodes }; return true; });
        var before = map.Snapshot();
        Assert.AreEqual("NonOrthogonalIntersection", map.Lines.Reverse(new(vertical.Code, vertical.Owner, vertical.Version)).Code);
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod, TestCategory("Http")]
    public async Task F6_HttpRejectionPreservesEditableDraftAndReportsRoadAndSegment()
    {
        var repository = new InMemoryLineRepository(); var baseRoad = LogicalNodeMySqlTests.Add(repository, 100, 110, 140, 110);
        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress }; var client = await TestLogin.ClientAsync(http, server, "owner");
        var editor = new LineEditor(client); Assert.IsTrue(await editor.NewLineAsync()); Assert.IsTrue(await editor.BeginDrawingAsync());
        editor.AddPoint(new Point(110, 100)); editor.AddPoint(new Point(130, 120));
        var draft = editor.DraftNodes.ToArray(); var before = LogicalNodeMySqlTests.Snapshot(repository);
        Assert.IsFalse(await editor.CompleteAsync()); StringAssert.Contains(editor.Message, "90°"); StringAssert.Contains(editor.Message, baseRoad.Code);
        CollectionAssert.AreEqual(draft, editor.DraftNodes.ToArray()); Assert.AreEqual(LineEditorMode.Drawing, editor.Mode); Assert.IsFalse(editor.IsBusy);
        Assert.AreEqual(before, LogicalNodeMySqlTests.Snapshot(repository));
    }
}
