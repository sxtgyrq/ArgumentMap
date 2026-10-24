using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class MapAreaDeleteTests
{
    internal static ServiceResult<MapAreaSnapshot> Delete(TestMap map,MapAreaSelection selected,string owner="owner")=>new MapAreaMoveService(map.Repository).Delete(new(owner,selected));

    [TestMethod]
    public void DeleteGroupRemovesWholeRoadsNodesCrossesAndPositions_PreservesUnrelatedPendingWork()
    {
        var map=new TestMap();MapAreaMoveTests.Pair(map,CrossState.Disconnected);MapAreaMoveTests.Node(map,size:18);
        var other=map.Add(200,250,280,250);var other2=map.Add(250,200,250,280);map.Calculate();var cross=map.Between(other,other2);
        cross=map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Disconnected)).Data!;var outside=MapAreaMoveTests.Node(map,250.5m,250.5m);
        map.Repository.Transaction(s=>{s.StaleCrosses.Add(cross.Key);s.DirtyLines.Add(other.Code);s.PendingSegments.Add((other.Code,0));var p=s.LogicalNodePositions.Values.First(p=>p.LogicalNodeCode==outside.Code);s.LogicalNodePositions[p.Key]=p with{IsChanged=true};return 0;});
        var selected=MapAreaMoveTests.Select(map);var before=map.Repository.Transaction(s=>s.Lines[other.Code]);var result=Delete(map,selected);Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(2,result.Data!.Lines.Length);Assert.AreEqual(1,result.Data.LogicalNodes.Length);Assert.AreEqual(0,result.Data.Crosses.Length);
        map.Repository.Transaction(s=>
        {
            Assert.IsTrue(selected.Lines.All(l=>!s.Lines.ContainsKey(l.Code)));Assert.IsTrue(selected.LogicalNodes.All(n=>!s.LogicalNodes.ContainsKey(n.Code)));
            var kept=s.Lines[other.Code];Assert.AreEqual(before.Version,kept.Version);Assert.AreEqual(before.Owner,kept.Owner);Assert.AreEqual(before.Properties,kept.Properties);CollectionAssert.AreEqual(before.Nodes,kept.Nodes);
            Assert.AreEqual(2,s.LogicalNodePositions.Count);Assert.IsTrue(s.LogicalNodePositions.Values.Any(p=>p.IsChanged));
            Assert.AreEqual(cross,s.Crosses[cross.Key]);
            CollectionAssert.AreEquivalent(new[]{other.Code},s.DirtyLines.ToArray());Assert.IsTrue(s.PendingSegments.SetEquals(new[]{(other.Code,0)}));Assert.IsTrue(s.StaleCrosses.SetEquals(new[]{cross.Key}));return 0;
        });
        Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",50.5m,50.5m,18,new())).Success,"Deleting the start LN must free the unique start slot.");
    }

    [TestMethod]
    public void RetainedLnLosesOnlyDeletedRoadPosition_AndRetainsOtherRoadPendingPosition()
    {
        var map=new TestMap();var ln=MapAreaMoveTests.Node(map,50.5m,12.5m);var selectedRoad=map.Add(20,14,80,14);var keptRoad=map.Add(0,11,100,11);map.Calculate();
        var selected=MapAreaMoveTests.Select(map);Assert.AreEqual(0,selected.LogicalNodes.Length);Assert.AreEqual(selectedRoad.Code,selected.Lines.Single().Code);
        var kept=map.Repository.Transaction(s=>{var p=s.LogicalNodePositions.Values.Single(p=>p.RoadCode==keptRoad.Code) with{IsChanged=true};s.LogicalNodePositions[p.Key]=p;s.DirtyLines.Add(keptRoad.Code);s.PendingSegments.Add((keptRoad.Code,0));return p;});
        var version=new LogicalNodeService(map.Repository).GetAll().Data!.Single().Version;
        var result=Delete(map,selected);Assert.IsTrue(result.Success,result.Message);var after=result.Data!.LogicalNodes.Single();
        Assert.AreEqual(ln.Code,after.Code);Assert.AreEqual(version+1,after.Version);Assert.AreEqual(ln.Properties,after.Properties);Assert.AreEqual(kept,after.Positions.Single());
        Assert.IsTrue(map.Repository.Transaction(s=>s.DirtyLines.Contains(keptRoad.Code)&&s.PendingSegments.Contains((keptRoad.Code,0))));
    }

    [TestMethod]
    public void LnOnlyDeletionKeepsItsCrossingRoadsAndCrossState()
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,CrossState.Road1ToRoad2);var ln=MapAreaMoveTests.Node(map,70.5m,50.5m);
        var selected=MapAreaMoveTests.Select(map,new(66,46,75,55));Assert.AreEqual(0,selected.Lines.Length);Assert.AreEqual(ln.Code,selected.LogicalNodes.Single().Code);
        var result=Delete(map,selected);Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(2,result.Data!.Lines.Length);Assert.AreEqual(pair.Cross,result.Data.Crosses.Single());Assert.AreEqual(0,result.Data.LogicalNodes.Length);
        Assert.AreEqual(0,map.Repository.Transaction(s=>s.LogicalNodePositions.Count));
    }

    [TestMethod,DataRow("road"),DataRow("ln"),DataRow("cross"),DataRow("new"),DataRow("removed"),DataRow("locked"),DataRow("owner")]
    public void ConcurrentSourceChangesAndPermissionsRejectWholeDeletion(string kind)
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,CrossState.Disconnected);var ln=MapAreaMoveTests.Node(map);var selected=MapAreaMoveTests.Select(map);
        map.Repository.Transaction(s=>
        {
            if(kind=="road")s.Lines[pair.A.Code]=s.Lines[pair.A.Code] with{Version=pair.A.Version+1};
            if(kind=="ln")s.LogicalNodes[ln.Code]=s.LogicalNodes[ln.Code] with{Version=ln.Version+1};
            if(kind=="cross")s.Crosses[pair.Cross.Key]=pair.Cross with{Version=pair.Cross.Version+1,State=CrossState.Connected};
            if(kind=="locked")s.Lines[pair.A.Code]=s.Lines[pair.A.Code] with{IsLocked=true};return 0;
        });
        if(kind=="new")MapAreaMoveTests.Node(map,30.5m,30.5m);
        if(kind=="removed")Assert.IsTrue(new LogicalNodeService(map.Repository).Delete(new(ln.Code,"owner",ln.Version)).Success);
        var before=map.Snapshot();var result=Delete(map,selected,kind=="owner"?"other":"owner");Assert.IsFalse(result.Success);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow("null"),DataRow("array"),DataRow("duplicate"),DataRow("missing"),DataRow("version")]
    public void MalformedOrForgedSelectionCannotDeleteAnything(string kind)
    {
        var map=new TestMap();MapAreaMoveTests.Pair(map,CrossState.Connected);var selected=MapAreaMoveTests.Select(map);
        var bad=kind switch{"null"=>null,"array"=>selected with{Lines=null!},"duplicate"=>selected with{Lines=[selected.Lines[0],selected.Lines[0]]},"missing"=>selected with{Crosses=[]},_=>selected with{Lines=selected.Lines.Select(l=>l with{Version=l.Version-1}).ToArray()}};
        var before=map.Snapshot();var result=Delete(map,bad!);Assert.IsFalse(result.Success);Assert.AreEqual(before,map.Snapshot());
    }
}
