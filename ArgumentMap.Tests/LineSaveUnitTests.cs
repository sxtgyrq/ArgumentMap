using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LineSaveUnitTests
{
    [TestMethod]
    [DataRow("TooFewNodes", new int[] { 100, 100 })]
    [DataRow("OutOfBounds", new int[] { -1, 100, 10, 100 })]
    [DataRow("OutOfBounds", new int[] { 500001, 100, 499990, 100 })]
    [DataRow("DuplicateNode", new int[] { 100, 100, 110, 100, 100, 100 })]
    [DataRow("InvalidDirection", new int[] { 100, 100, 110, 109 })]
    [DataRow("SegmentOverlap", new int[] { 100, 100, 110, 100, 105, 100 })]
    [DataRow("AcuteCorner", new int[] { 100, 100, 120, 100, 115, 95 })]
    public void F6_RejectInvalidShapeWithoutCommitting(string error, int[] xy)
    {
        var map = new TestMap(); var line = map.Create(); var before = map.Snapshot();
        var result = map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(line, xy));
        Assert.IsFalse(result.Success); Assert.AreEqual(error, result.Code);
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    [DataRow(new int[] { 100, 100, 110, 100, 110, 110 }, DisplayName = "F6 允许 90° 直角")]
    [DataRow(new int[] { 100, 100, 110, 100, 120, 110 }, DisplayName = "F6 允许 135° 钝角")]
    [DataRow(new int[] { 100, 100, 110, 100, 120, 100 }, DisplayName = "F6 允许直线延伸")]
    public void F6_AllowRightObtuseAndStraightCorners(int[] xy)
    {
        var map = new TestMap(); var line = map.Save(map.Create(), xy);
        Assert.AreEqual(1L, line.Version); Assert.AreEqual(xy.Length / 2, line.Nodes.Length);
    }

    [TestMethod]
    public void F6_RejectWrongLineIdAndNoncontinuousOrder()
    {
        var map = new TestMap(); var line = map.Create(); var shape = TestMap.Shape(line, 100, 100, 110, 100);
        shape.Nodes[1] = shape.Nodes[1] with { Order = 3 };
        Assert.AreEqual("InvalidOrder", map.Lines.CheckShapeIsOKThenSave(shape).Code);
        shape.Nodes[1] = shape.Nodes[1] with { Order = 1, LineCode = line.Code + 1 };
        Assert.AreEqual("LineCodeMismatch", map.Lines.CheckShapeIsOKThenSave(shape).Code);
    }

    [TestMethod]
    [DataRow(new int[] { 1, 0, 9, 0 })]
    [DataRow(new int[] { 8, 0, 20, 0 })]
    [DataRow(new int[] { 9, 0, 1, 0 })]
    public void F6_RejectOverlapWithOtherLine(int[] xy)
    {
        var map = new TestMap(); map.Add(0, 0, 10, 0); var candidate = map.Create(); var before = map.Snapshot();
        Assert.AreEqual("SegmentOverlap", map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate, xy)).Code);
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void F6_RejectNodesOccupiedAnywhereInMap()
    {
        var map = new TestMap(); map.Add(100, 100, 110, 100); var candidate = map.Create();
        Assert.AreEqual("PointIsOccupied", map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate, 100, 100, 100, 110)).Code);
    }

    [TestMethod]
    public void F6_EditChecksVersionAndKeepsLastCommittedShape()
    {
        var map = new TestMap(); var first = map.Add(100, 100, 110, 100);
        var changed = map.Save(first, 100, 101, 110, 101); var before = map.Snapshot();
        Assert.AreEqual("VersionConflict", map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(first, 100, 102, 110, 102)).Code);
        Assert.AreEqual(before, map.Snapshot());
        Assert.AreEqual(2L, changed.Version);
    }

    [TestMethod]
    public void F6_RequiresOwnerAndUnlockedLine()
    {
        var map = new TestMap(); var line = map.Add(100, 100, 110, 100);
        Assert.AreEqual("NotOwner", map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(line, 100, 101, 110, 101) with { Owner = "other" }).Code);
        map.Repository.Transaction(state => { state.Lines[line.Code] = line with { IsLocked = true }; return true; });
        Assert.AreEqual("LineIsLocked", map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(line, 100, 101, 110, 101)).Code);
    }
}
