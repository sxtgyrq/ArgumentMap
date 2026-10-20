using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class CrossUnitTests
{
    private static (TestMap Map, LineSnapshot A, LineSnapshot B, CrossSnapshot Cross) Pair()
    {
        var map = new TestMap(); var a = map.Add(100, 110, 120, 110); var b = map.Add(110, 100, 110, 120);
        map.Calculate(); return (map, a, b, map.Between(a, b));
    }

    [TestMethod]
    public void F6_DefersCrossAndKUsesPerSegmentThenCleanupTransactions()
    {
        var map = new TestMap(); map.Add(100, 110, 120, 110); map.Add(110, 100, 110, 120);
        Assert.AreEqual(0, map.Crosses.GetAll().Data!.Length);
        var first = map.Crosses.Calculate(); Assert.IsTrue(first.Success); Assert.IsTrue(first.Data!.CanContinue);
        Assert.AreEqual(1, first.Data.RemainingSegments); Assert.AreEqual(1, first.Data.CrossCount);
        var second = map.Crosses.Calculate(); Assert.IsTrue(second.Data!.CanContinue); Assert.AreEqual(0, second.Data.RemainingSegments);
        Assert.IsFalse(map.Crosses.Calculate().Data!.CanContinue);
        Assert.IsTrue(map.Repository.Transaction(s => s.PendingSegments.Count == 0 && s.StaleCrosses.Count == 0 && s.DirtyLines.Count == 0));
    }

    [TestMethod]
    [DataRow(CrossState.Connected), DataRow(CrossState.Disconnected)]
    public void EditingNodes_PreservesCrossIdentityAndStateAcrossRenumbering(CrossState state)
    {
        var (map, a, b, cross) = Pair(); cross = map.Crosses.Edit(new(cross.Key, a.Owner, cross.Version, state)).Data!;
        a = map.Save(a, 100, 110, 110, 110, 120, 110); map.Calculate();
        var inserted = map.Between(a, b);
        Assert.AreEqual(cross.Key, inserted.Key); Assert.AreEqual(state, inserted.State);
        Assert.AreEqual(1, inserted.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.0, inserted.LocationOn(a.Code).Percent);
        a = map.Save(a, 100, 110, 120, 110); map.Calculate();
        var removed = map.Between(a, b);
        Assert.AreEqual(cross.Key, removed.Key); Assert.AreEqual(state, removed.State);
        Assert.AreEqual(0, removed.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.5, removed.LocationOn(a.Code).Percent);
    }

    [TestMethod]
    public void MovingLine_RemovesDisappearedCrossDuringFinalCleanup()
    {
        var (map, a, b, cross) = Pair(); cross = map.Crosses.Edit(new(cross.Key, a.Owner, cross.Version, CrossState.Connected)).Data!;
        a = map.Save(a, 100, 111, 120, 111);
        Assert.IsTrue(map.Crosses.GetAll().Data!.Contains(cross));
        Assert.IsTrue(map.Crosses.Calculate().Data!.CanContinue);
        Assert.AreEqual(2, map.Crosses.GetAll().Data!.Length);
        Assert.IsFalse(map.Crosses.Calculate().Data!.CanContinue);
        var current = map.Between(a, b);
        Assert.AreNotEqual(cross.Key, current.Key); Assert.AreEqual(CrossState.Unset, current.State);
        Assert.AreEqual(new CrossPosition(220, 222), current.Key.Position);
    }

    [TestMethod]
    public void DeleteWholeLine_AlsoRemovesCrossAndItsPendingWork()
    {
        var (map, a, b, _) = Pair(); a = map.Save(a, 100, 110, 110, 110, 120, 110);
        Assert.IsTrue(map.Lines.Delete(new(a.Code, a.Owner, a.Version)).Success);
        Assert.AreEqual(0, map.Crosses.GetAll().Data!.Length);
        Assert.IsTrue(map.Repository.Transaction(s => !s.PendingSegments.Any(p => p.LineCode == a.Code) && !s.DirtyLines.Contains(a.Code)));
        map.Calculate(); Assert.AreEqual(0, map.Crosses.GetAll().Data!.Length);
    }

    [TestMethod]
    [DataRow(new int[] { 105, 105, 115, 115 }, DisplayName = "第三条线路的线段穿过 Cross")]
    [DataRow(new int[] { 105, 105, 110, 110, 115, 115 }, DisplayName = "第三条线路的中间节点位于 Cross")]
    [DataRow(new int[] { 110, 110, 115, 115 }, DisplayName = "第三条线路的起点位于 Cross")]
    [DataRow(new int[] { 105, 105, 110, 110 }, DisplayName = "第三条线路的终点位于 Cross")]
    public void F6_RejectThirdPartyCrossPassageAndRollback(int[] xy)
    {
        var (map, _, _, _) = Pair(); var third = map.Create(); var before = map.Snapshot();
        var result = map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(third, xy));
        Assert.IsFalse(result.Success); Assert.AreEqual("LinePassesCross", result.Code);
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void F6_DetectsThirdPartyPassageBeforeCrossHasBeenCalculated()
    {
        var map = new TestMap(); map.Add(100, 110, 120, 110); map.Add(110, 100, 110, 120); var third = map.Create();
        Assert.AreEqual(0, map.Crosses.GetAll().Data!.Length);
        Assert.AreEqual("LinePassesCross", map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(third, 105, 105, 115, 115)).Code);
    }

    [TestMethod]
    public void K_RejectsLegacyTriplePassageWithoutChangingMarkersOrCrossSettings()
    {
        var (map, _, _, _) = Pair(); var third = map.Create();
        map.Repository.Transaction(s => { s.Lines[third.Code] = third with { Version = 1, Nodes = TestMap.Shape(third, 105, 105, 115, 115).Nodes }; s.PendingSegments.Add((third.Code, 0)); s.DirtyLines.Add(third.Code); return true; });
        var before = map.Snapshot(); var result = map.Crosses.Calculate();
        Assert.IsFalse(result.Success); Assert.AreEqual("LinePassesCross", result.Code);
        StringAssert.Contains(result.Message, "计算前"); Assert.AreEqual(before, map.Snapshot());
        Assert.IsNotNull(map.Repository.Transaction(CrossPassageValidator.FindThroughCalculatedCrosses));
    }

    [TestMethod]
    public void StaleCross_DoesNotBlockPassageAfterItsParticipatingLineMovesAway()
    {
        var (map, a, b, cross) = Pair(); map.Save(a, 100, 112, 120, 112);
        map.Save(b, 110, 112, 110, 120); // Clear the old coordinate without making a new 45-degree intersection.
        Assert.IsTrue(map.Crosses.GetAll().Data!.Contains(cross));
        map.Add(109, 109, 111, 111); map.Calculate();
        Assert.IsFalse(map.Crosses.GetAll().Data!.Any(c => c.Key == cross.Key));
        Assert.IsNull(map.Repository.Transaction(CrossPassageValidator.FindThroughCalculatedCrosses));
    }
}
