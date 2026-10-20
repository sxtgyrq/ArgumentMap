using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class MapAreaContainmentTests
{
    [TestMethod,DataRow("moveX",-1),DataRow("moveX",0),DataRow("moveX",1),DataRow("moveX",2),DataRow("moveX",3),
        DataRow("moveY",-1),DataRow("moveY",0),DataRow("moveY",1),DataRow("moveY",2),DataRow("moveY",3),
        DataRow("delete",-1),DataRow("delete",0),DataRow("delete",1),DataRow("delete",2),DataRow("delete",3)]
    public void SkippedCrossDoesNotBlockMovingOrDeletingOneWholeRoad_RecalculatesOnlyAffectedRelations(string action,int setting)
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,(CrossState)setting);
        var node=MapAreaMoveTests.Node(map,70.5m,50.5m);
        var service=new MapAreaMoveService(map.Repository);var before=map.Snapshot();
        var selected=MapAreaMoveTests.Select(map,new(15,40,85,60));
        Assert.AreEqual(pair.A.Code,selected.Lines.Single().Code);Assert.AreEqual(node.Code,selected.LogicalNodes.Single().Code);
        Assert.AreEqual(0,selected.Crosses.Length);Assert.AreEqual(before,map.Snapshot());
        var forged=selected with{Crosses=[pair.Cross]};
        Assert.AreEqual("SelectionChanged",service.Move(new("owner",forged,10,0)).Code);
        Assert.AreEqual(before,map.Snapshot());
        var result=action=="delete"?service.Delete(new("owner",selected)):
            service.Move(new("owner",selected,action=="moveX"?10:0,action=="moveY"?10:0));
        Assert.IsTrue(result.Success,result.Message);
        var external=result.Data!.Lines.Single(l=>l.Code==pair.B.Code);
        CollectionAssert.AreEqual(pair.B.Nodes,external.Nodes);Assert.AreEqual(pair.B.Version,external.Version);
        if(action=="delete")
        {
            Assert.AreEqual(1,result.Data.Lines.Length);Assert.AreEqual(0,result.Data.LogicalNodes.Length);
            Assert.AreEqual(0,result.Data.Crosses.Length);
        }
        else
        {
            var cross=result.Data.Crosses.Single();
            Assert.AreEqual(action=="moveX"?pair.Cross.Key.Position:new CrossPosition(100,120),cross.Key.Position);
            Assert.AreEqual(action=="moveX"?(CrossState)setting:CrossState.Unset,cross.State);
            var moved=result.Data.LogicalNodes.Single();Assert.AreEqual(1,moved.Positions.Length);
            Assert.AreEqual(node.Properties,moved.Properties);
        }
        before=map.Snapshot();map.Calculate();Assert.AreEqual(before,map.Snapshot(),"Later K must not restore obsolete Crosses or reset reusable settings.");
    }

    [TestMethod,DataRow("move"),DataRow("delete")]
    public void LnOnlySelectionAtSkippedCrossKeepsBothUnselectedRoadsAndTheirCross(string action)
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,CrossState.Disconnected);
        var node=MapAreaMoveTests.Node(map);var service=new MapAreaMoveService(map.Repository);
        var selected=MapAreaMoveTests.Select(map,new(node.Left,node.Bottom,node.Right,node.Top));
        Assert.AreEqual(0,selected.Lines.Length);Assert.AreEqual(0,selected.Crosses.Length);
        Assert.AreEqual(node.Code,selected.LogicalNodes.Single().Code);
        var result=action=="move"?service.Move(new("owner",selected,100,100)):service.Delete(new("owner",selected));
        Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(pair.Cross,result.Data!.Crosses.Single());
        foreach(var old in new[]{pair.A,pair.B})
        {
            var retained=result.Data.Lines.Single(l=>l.Code==old.Code);
            Assert.AreEqual(old.Version,retained.Version);CollectionAssert.AreEqual(old.Nodes,retained.Nodes);
        }
    }

    [TestMethod]
    public void SkippedCrossWithoutAnyCompleteObjectReturnsEmptySelection_AndInvalidRectangleExplainsDragging()
    {
        var map=new TestMap();MapAreaMoveTests.Pair(map,CrossState.Connected);
        var service=new MapAreaMoveService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("EmptySelection",service.Select(new("owner",new(49,49,51,51))).Code);
        var invalid=service.Select(new("owner",new(49,49,49,51)));
        Assert.AreEqual("InvalidBounds",invalid.Code);StringAssert.Contains(invalid.Message,"按住鼠标左键拖出矩形");
        Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod, DataRow(4), DataRow(6), DataRow(8), DataRow(12), DataRow(18)]
    public void EveryLnRequiresEntireSquare_EachEdgeAndCornerIsCheckedWithoutRounding(int size)
    {
        var map=new TestMap();var node=MapAreaMoveTests.Node(map,size:size);
        var service=new MapAreaMoveService(map.Repository);
        var full=new MapAreaBounds(node.Left,node.Bottom,node.Right,node.Top);
        var before=map.Snapshot();
        foreach(var bounds in new[]{full,new MapAreaBounds(node.Left-0.01m,node.Bottom-0.01m,node.Right+0.01m,node.Top+0.01m)})
        {
            var selected=service.Select(new("owner",bounds));
            Assert.IsTrue(selected.Success,selected.Message);
            Assert.AreEqual(node.Code,selected.Data!.LogicalNodes.Single().Code);
        }
        var epsilon=0.01m;
        foreach(var bounds in new[]{
            full with{Left=node.Left+epsilon},full with{Right=node.Right-epsilon},
            full with{Bottom=node.Bottom+epsilon},full with{Top=node.Top-epsilon},
            full with{Left=node.Left+epsilon,Bottom=node.Bottom+epsilon},
            full with{Left=node.Left+epsilon,Top=node.Top-epsilon},
            full with{Right=node.Right-epsilon,Bottom=node.Bottom+epsilon},
            full with{Right=node.Right-epsilon,Top=node.Top-epsilon}})
        {
            Assert.IsTrue(bounds.Contains(node.X,node.Y),"Even a contained centre is insufficient.");
            var selected=service.Select(new("owner",bounds));
            Assert.AreEqual("EmptySelection",selected.Code,$"size={size}, bounds={bounds}: {selected.Message}");
        }
        Assert.AreEqual(before,map.Snapshot(),"Selection must not modify nodes, positions or K/J work.");
    }

    [TestMethod, DataRow("move"), DataRow("delete")]
    public void MixedSelectionExcludesPartialSquaresAndAnyRoadWithOutsideSegments(string operation)
    {
        var map=new TestMap();
        var complete=map.Add(20,20,80,20);
        var partialEnd=map.Add(20,80,100,80);
        var outsideMiddle=map.Add(20,70,0,70,0,60,20,60);
        var crossingOnly=map.Add(0,35,100,35);
        var inside=MapAreaMoveTests.Node(map);
        var partialNode=MapAreaMoveTests.Node(map,88.5m,50.5m,12);
        var service=new MapAreaMoveService(map.Repository);
        var before=map.Snapshot();
        var selection=MapAreaMoveTests.Select(map);
        Assert.AreEqual(complete.Code,selection.Lines.Single().Code);
        Assert.AreEqual(inside.Code,selection.LogicalNodes.Single().Code);
        Assert.AreEqual(0,selection.Crosses.Length);
        Assert.AreEqual(before,map.Snapshot());
        // Both endpoints are inside, but the detour leaves the selection rectangle.
        Assert.IsTrue(selection.Bounds.Contains(outsideMiddle.Nodes[0].X,outsideMiddle.Nodes[0].Y));
        Assert.IsTrue(selection.Bounds.Contains(outsideMiddle.Nodes[^1].X,outsideMiddle.Nodes[^1].Y));
        // The Server also rejects forged snapshots which try to include excluded objects.
        foreach(var forged in new[]{selection with{Lines=[..selection.Lines,outsideMiddle]},
            selection with{LogicalNodes=[..selection.LogicalNodes,partialNode]}})
        {
            var rejected=operation=="move"?service.Move(new("owner",forged,100,100)):service.Delete(new("owner",forged));
            Assert.AreEqual("SelectionChanged",rejected.Code,rejected.Message);
            Assert.AreEqual(before,map.Snapshot());
        }
        var result=operation=="move"?service.Move(new("owner",selection,100,100)):service.Delete(new("owner",selection));
        Assert.IsTrue(result.Success,result.Message);
        foreach(var excluded in new[]{partialEnd,outsideMiddle,crossingOnly})
        {
            var retained=result.Data!.Lines.Single(l=>l.Code==excluded.Code);
            Assert.AreEqual(excluded.Version,retained.Version);
            Assert.AreEqual(excluded.Properties,retained.Properties);
            CollectionAssert.AreEqual(excluded.Nodes,retained.Nodes);
        }
        var retainedNode=result.Data!.LogicalNodes.Single(n=>n.Code==partialNode.Code);
        Assert.AreEqual(partialNode.Version,retainedNode.Version);
        Assert.AreEqual(partialNode.Properties,retainedNode.Properties);
        Assert.AreEqual((partialNode.X,partialNode.Y),(retainedNode.X,retainedNode.Y));
        CollectionAssert.AreEqual(partialNode.Positions,retainedNode.Positions);
        if(operation=="move")
        {
            Assert.AreEqual(150.5m,result.Data.LogicalNodes.Single(n=>n.Code==inside.Code).X);
            CollectionAssert.AreEqual(complete.Nodes.Select(n=>n with{X=n.X+100,Y=n.Y+100}).ToArray(),
                result.Data.Lines.Single(l=>l.Code==complete.Code).Nodes);
        }
        else
        {
            Assert.IsFalse(result.Data.LogicalNodes.Any(n=>n.Code==inside.Code));
            Assert.IsFalse(result.Data.Lines.Any(l=>l.Code==complete.Code));
        }
    }

    [TestMethod]
    public void CrossOnSelectionBoundaryIsSkippedWithoutBothWholeRoads_CompleteObjectsRemainSelectable()
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,CrossState.Connected);
        var service=new MapAreaMoveService(map.Repository);var before=map.Snapshot();
        var partial=service.Select(new("owner",new(20,50,80,80)));
        Assert.IsTrue(partial.Success,partial.Message);Assert.AreEqual(1,partial.Data!.Lines.Length);
        Assert.AreEqual(0,partial.Data.Crosses.Length);StringAssert.Contains(partial.Message,"已跳过 1 个");
        var full=service.Select(new("owner",new(20,20,80,80)));
        Assert.IsTrue(full.Success,full.Message);
        Assert.AreEqual(2,full.Data!.Lines.Length);
        Assert.AreEqual(pair.Cross.Key,full.Data.Crosses.Single().Key);
        Assert.AreEqual(before,map.Snapshot());
    }
}
