using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LogicalNodeMoveTests
{
    internal static LogicalNodeSnapshot Create(TestMap map, int size = 6) =>
        new LogicalNodeService(map.Repository).Create(new("owner", 50.5m, 50.5m, size, new() { Value = size==4?"False":"中文值", Info = "保留说明" })).Data!;
    internal static ServiceResult<LogicalNodeSnapshot> Move(TestMap map, LogicalNodeSnapshot node, decimal x, decimal y, string owner = "owner") =>
        new LogicalNodeService(map.Repository).Move(new(node.Code, owner, node.Version, x, y));

    [TestMethod, DataRow(4), DataRow(6), DataRow(12), DataRow(18)]
    public void MovePreservesIdentityOwnerSizeAndMetadata_ExcludesSelfAndAllowsWorldCorners(int size)
    {
        var map = new TestMap(); var node = Create(map, size);
        foreach (var center in new[] { 51.5m, size / 2m + 0.5m, 500000 - size / 2m - 0.5m })
        {
            var result = Move(map, node, center, center); Assert.IsTrue(result.Success, result.Message);
            var moved = result.Data!;
            Assert.AreEqual(node.Code, moved.Code); Assert.AreEqual(node.Owner, moved.Owner);
            Assert.AreEqual(node.Properties, moved.Properties); Assert.AreEqual(size, moved.SideLength);
            Assert.AreEqual(node.Version + 1, moved.Version); Assert.AreEqual(center, moved.X); Assert.AreEqual(center, moved.Y);
            Assert.AreEqual(1, map.Repository.Transaction(s => s.LogicalNodes.Count));
            node = moved;
        }
        var before = map.Snapshot(); var same = Move(map, node, node.X, node.Y);
        Assert.IsTrue(same.Success); Assert.AreEqual(node.Version, same.Data!.Version); Assert.AreEqual(before, map.Snapshot());
        if (size == 18) Assert.AreEqual("StartLogicalNodeExists", new LogicalNodeService(map.Repository).Create(new("owner", 100.5m, 100.5m, 18, new())).Code);
    }

    [TestMethod, DataRow("100", "100.5", "InvalidLogicalNodeCoordinate"), DataRow("100.5", "100.1", "InvalidLogicalNodeCoordinate"),
     DataRow("-0.5", "100.5", "InvalidLogicalNodeCoordinate"), DataRow("2.5", "100.5", "LogicalNodeOutOfBounds"),
     DataRow("100.5", "499997.5", "LogicalNodeOutOfBounds")]
    public void InvalidDestinationRollsBack(string x, string y, string code)
    {
        var map = new TestMap(); var node = Create(map); var before = map.Snapshot();
        Assert.AreEqual(code, Move(map, node, decimal.Parse(x, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(y, System.Globalization.CultureInfo.InvariantCulture)).Code);
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod, DataRow(110.5,110.5), DataRow(104.5,110.5), DataRow(104.5,104.5)]
    public void OtherSquareOverlapEdgeAndCornerContactReject(double x, double y)
    {
        var map = new TestMap(); var node = Create(map);
        Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("other", 110.5m, 110.5m, 6, new())).Success);
        var before = map.Snapshot(); Assert.IsFalse(Move(map, node, (decimal)x, (decimal)y).Success); Assert.AreEqual(before, map.Snapshot());
        Assert.IsTrue(Move(map, node, 103.5m, 110.5m).Success);
    }

    [TestMethod, DataRow(0), DataRow(1), DataRow(2)]
    public void AllPolylineVerticesAtDestinationReject(int order)
    {
        var map = new TestMap(); var node = Create(map);
        map.Add(order switch { 0 => [110,110,120,110,130,110], 1 => [90,110,110,110,130,110], _ => [90,110,100,110,110,110] });
        var before = map.Snapshot(); Assert.AreEqual("LineNodeInsideLogicalNode", Move(map, node, 110.5m, 110.5m).Code); Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod, DataRow(1), DataRow(-1)]
    public void DiagonalInteriorRejectsButCornerTangentAllows(int direction)
    {
        var map = new TestMap(); var node = Create(map); map.Add(90, direction == 1 ? 90 : 130, 130, direction == 1 ? 130 : 90);
        var before = map.Snapshot(); Assert.AreEqual("DiagonalInsideLogicalNode", Move(map, node, 110.5m, 110.5m).Code); Assert.AreEqual(before, map.Snapshot());
        // y=x touches bottom-right of the destination; y=220-x touches top-right.
        Assert.IsTrue(Move(map, node, 107.5m, direction == 1 ? 113.5m : 106.5m).Success);
        Assert.AreEqual(0, new LogicalNodeService(map.Repository).GetAll().Data!.Single().Positions.Length);
    }

    [TestMethod, DataRow(CrossState.Unset), DataRow(CrossState.Connected), DataRow(CrossState.Road1ToRoad2), DataRow(CrossState.Road2ToRoad1), DataRow(CrossState.Disconnected)]
    public void DestinationCrossMustAlreadyBeDisconnected_StatesNeverChangedByMove(CrossState state)
    {
        var map = new TestMap(); var node = Create(map); var a = map.Add(90,110,130,110); var b = map.Add(110,90,110,130); map.Calculate(); var cross = map.Between(a,b);
        Assert.IsTrue(map.Crosses.Edit(new(cross.Key, "owner", cross.Version, state)).Success);
        var before = map.Snapshot(); var result = Move(map, node, 110.5m, 110.5m);
        Assert.AreEqual(state == CrossState.Disconnected, result.Success, result.Message);
        Assert.AreEqual(state, map.Between(a,b).State);
        if (!result.Success) Assert.AreEqual(before, map.Snapshot()); else Assert.AreEqual(2, result.Data!.Positions.Length);
    }

    [TestMethod]
    public void MissingCrossBeforeKAlsoRejects()
    {
        var map = new TestMap(); var node = Create(map); map.Add(90,110,130,110); map.Add(110,90,110,130);
        var before = map.Snapshot(); Assert.AreEqual("LogicalNodeCrossNotDisconnected", Move(map, node, 110.5m, 110.5m).Code); Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void RebuildAllPositions_ZeroToManyIncludingSameRoadToOneToZero_LeavesOtherNodesAndCrossesAlone()
    {
        var map = new TestMap(); var node = Create(map);
        var a = map.Add(90,110,130,110,130,112,90,112); var b = map.Add(110,90,110,130); var c = map.Add(190,210,230,210); map.Calculate();
        foreach (var cross in map.Crosses.GetAll().Data!) Assert.IsTrue(map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Disconnected)).Success);
        var service = new LogicalNodeService(map.Repository); var other = service.Create(new("other",220.5m,210.5m,6,new())).Data!;
        var crosses = System.Text.Json.JsonSerializer.Serialize(map.Crosses.GetAll().Data);
        node = Move(map,node,110.5m,110.5m).Data!; Assert.AreEqual(3,node.Positions.Length);
        Assert.AreEqual(2,node.Positions.Count(p=>p.RoadCode==a.Code));
        foreach (var p in node.Positions)
        {
            Assert.IsFalse(p.IsChanged); Assert.AreEqual(node.Code,p.LogicalNodeCode);
            Assert.AreEqual(p.RoadCode==a.Code ? a.Version : b.Version,p.RoadVersion);
            Assert.IsTrue(p.X == node.X || p.Y == node.Y);
            var road=p.RoadCode==a.Code?a:b; var start=road.Nodes[p.RoadOrder]; var end=road.Nodes[p.RoadOrder+1];
            Assert.AreEqual((double)(start.X==end.X ? (p.Y-start.Y)/(end.Y-start.Y) : (p.X-start.X)/(end.X-start.X)),p.RoadPercent,1e-15);
        }
        node = Move(map,node,200.5m,210.5m).Data!; Assert.AreEqual(c.Code,node.Positions.Single().RoadCode); Assert.AreEqual(200.5m,node.Positions.Single().X);
        node = Move(map,node,300.5m,300.5m).Data!; Assert.AreEqual(0,node.Positions.Length);
        Assert.AreEqual(1,map.Repository.Transaction(s=>s.LogicalNodePositions.Count));
        Assert.AreEqual(System.Text.Json.JsonSerializer.Serialize(other with { CanEdit=null }),System.Text.Json.JsonSerializer.Serialize(service.GetAll().Data!.Single(n=>n.Code==other.Code)));
        Assert.AreEqual(crosses,System.Text.Json.JsonSerializer.Serialize(map.Crosses.GetAll().Data));
    }

    [TestMethod]
    public void PermissionsVersionAndDeletedNode_RejectWithoutMutation_AdminMovePreservesOwner()
    {
        var map = new TestMap(); var node = Create(map); var before = map.Snapshot();
        Assert.AreEqual("NotOwner",Move(map,node,100.5m,100.5m,"other").Code);
        Assert.AreEqual("VersionConflict",Move(map,node with {Version=9},100.5m,100.5m).Code);
        Assert.AreEqual("LogicalNodeNotFound",Move(map,node with {Code="ZYXWVUTSRQ"},100.5m,100.5m).Code); Assert.AreEqual(before,map.Snapshot());
        new LoginService(map.Repository).CreateUser(new("admin","admin"));
        var moved=Move(map,node,100.5m,100.5m,"admin"); Assert.IsTrue(moved.Success,moved.Message); Assert.AreEqual("owner",moved.Data!.Owner);
        Assert.AreEqual("VersionConflict",Move(map,node,200.5m,200.5m).Code);
    }
}
