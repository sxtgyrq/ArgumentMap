using ArgumentMap.Rules;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class GeometryUnitTests
{
    private static LineNode N(int x, int y) => new("ZAAAAAAAAB", 0, x, y);

    [TestMethod]
    [DataRow(1, 0), DataRow(1, 1), DataRow(0, 1), DataRow(-1, 1)]
    [DataRow(-1, 0), DataRow(-1, -1), DataRow(0, -1), DataRow(1, -1)]
    public void EightDirections_AcceptEveryCompassDirection(int dx, int dy) =>
        Assert.IsTrue(LineGeometry.IsEightDirection(N(250000, 250000), N(250000 + dx * 17, 250000 + dy * 17)));

    [TestMethod]
    [DataRow(0, 0), DataRow(10, 9), DataRow(10, -9), DataRow(-10, 9)]
    public void EightDirections_RejectOtherSlopesAndZeroLength(int dx, int dy) =>
        Assert.IsFalse(LineGeometry.IsEightDirection(N(250000, 250000), N(250000 + dx, 250000 + dy)));

    [TestMethod]
    [DataRow(1, 0), DataRow(1, 1), DataRow(0, 1), DataRow(-1, 1)]
    [DataRow(-1, 0), DataRow(-1, -1), DataRow(0, -1), DataRow(1, -1)]
    public void Overlap_RejectContainedSegmentsInEveryDirectionAndOrder(int dx, int dy)
    {
        var a = N(100, 100); var b = N(100 + dx * 10, 100 + dy * 10);
        var c = N(100 + dx, 100 + dy); var d = N(100 + dx * 9, 100 + dy * 9);
        Assert.IsTrue(LineGeometry.Overlaps(a, b, c, d));
        Assert.IsTrue(LineGeometry.Overlaps(b, a, d, c));
    }

    [TestMethod]
    public void Overlap_AllowEndpointTouchAndSingleIntersection()
    {
        Assert.IsFalse(LineGeometry.Overlaps(N(0, 0), N(10, 0), N(10, 0), N(20, 0)));
        Assert.IsFalse(LineGeometry.Overlaps(N(0, 0), N(10, 10), N(0, 10), N(10, 0)));
    }

    [TestMethod]
    [DataRow(0, 0, 10, 0, 5, 0, 6, 1, 10, 0)]
    [DataRow(0, 0, 1, 1, 0, 1, 1, 0, 1, 1)]
    [DataRow(0, 0, 500000, 500000, 0, 500000, 500000, 0, 500000, 500000)]
    public void Intersection_KeepIntegerHalfAndLargeCoordinatesExact(int ax, int ay, int bx, int by, int cx, int cy, int dx, int dy, int x2, int y2)
    {
        Assert.IsTrue(LineGeometry.TryIntersection(N(ax, ay), N(bx, by), N(cx, cy), N(dx, dy), out var point));
        Assert.AreEqual(new CrossPosition(x2, y2), point);
    }

    [TestMethod]
    [DataRow(-10, 0, -5, 5, true)]
    [DataRow(-10, 0, -5, -5, true)]
    [DataRow(10, 10, 0, 1, true)]
    [DataRow(-10, 0, 0, 5, false)]
    [DataRow(-10, 0, 5, 5, false)]
    [DataRow(-10, 0, 5, -5, false)]
    [DataRow(-10, 0, 5, 0, false)]
    public void Corners_CheckInteriorAngleInsteadOfTravelTurn(int ax, int ay, int bx, int by, bool acute)
    {
        var nodes = new[] { N(100 + ax, 100 + ay), N(100, 100), N(100 + bx, 100 + by) };
        Assert.AreEqual(acute, LineGeometry.FindAcuteCorner(nodes).HasValue);
        Assert.AreEqual(acute, LineGeometry.FindAcuteCorner(nodes.Reverse().ToArray()).HasValue);
    }

    [TestMethod]
    public void CrossNode_PreferOutgoingSegmentAndZeroPosition()
    {
        var nodes = new[] { N(10, 10), N(20, 10), N(20, 20) };
        Assert.AreEqual((1, 0.0), LineGeometry.LocateCross(nodes, 0, new(40, 20)));
        Assert.AreEqual((1, 1.0), LineGeometry.LocateCross(nodes, 1, new(40, 40)));
    }

    [TestMethod]
    [DataRow(1, 1, true), DataRow(0, 0, true), DataRow(20, 20, true)]
    [DataRow(21, 21, false), DataRow(5, 6, false)]
    public void Passage_IncludesNodesAndHalfPointsButNotExtensionsOrNearMisses(int x2, int y2, bool contained) =>
        Assert.AreEqual(contained, LineGeometry.FindPointOnLine([N(0, 0), N(10, 10)], new(x2, y2)).HasValue);

    [TestMethod]
    [DataRow(123.4, 111.8), DataRow(500100.0, 500120.0), DataRow(-100.0, -90.0)]
    public void DrawingSnap_PreservesIntegersEightDirectionsAndBounds(double x, double y)
    {
        var start = new GridPoint(499990, 499990);
        var result = EightDirectionSnap.Snap(start, new(x, y));
        Assert.IsTrue(EightDirectionSnap.IsInside(result.ToPoint()));
        Assert.IsTrue(result == start || EightDirectionSnap.IsValidSegment(start, result));
    }
}
