using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodePositionTests
{
    private static LogicalNodeSnapshot AddNode(TestMap map,int size=6)
    {var result=new LogicalNodeService(map.Repository).Create(new("owner",110.5m,110.5m,size,new()));Assert.IsTrue(result.Success,result.Message);return result.Data!;}
    private static LogicalNodeSnapshot Node(TestMap map)=>new LogicalNodeService(map.Repository).GetAll().Data!.Single();

    [TestMethod]
    public void EmptyLnCanExist_OnePositionAppearsOnRoadSaveAndDisappearsOnRoadDeletion()
    {
        var map=new TestMap();var node=AddNode(map);Assert.AreEqual(0,node.Positions.Length);
        var road=map.Add(90,110,130,110);node=Node(map);Assert.AreEqual(1,node.Positions.Length);Assert.AreEqual(road.Code,node.Positions[0].RoadCode);
        Assert.IsTrue(map.Lines.Delete(new(road.Code,road.Owner,road.Version)).Success);node=Node(map);Assert.AreEqual(0,node.Positions.Length);Assert.IsTrue(map.Repository.Transaction(s=>s.LogicalNodes.ContainsKey(node.Code)));
        Assert.IsFalse(new LogicalNodeService(map.Repository).Calculate().Data!.CanContinue);Assert.AreEqual(1,new LogicalNodeService(map.Repository).GetAll().Data!.Length);
    }

    [TestMethod,DataRow(6),DataRow(12)]
    public void OneLnRecordsEveryPassingSegment_IncludingMultipleSegmentsOfSameRoad(int size)
    {
        var map=new TestMap();AddNode(map,size);var a=map.Add(90,108,130,108,130,112,90,112);var b=map.Add(110,90,110,130);
        var node=Node(map);Assert.AreEqual(3,node.Positions.Length);Assert.AreEqual(2,node.Positions.Count(p=>p.RoadCode==a.Code));
        var top=node.Positions.Single(p=>p.RoadCode==a.Code&&p.RoadOrder==2);Assert.AreEqual(110.5m,top.X);Assert.AreEqual(112m,top.Y);Assert.AreEqual(0.4875,top.RoadPercent,1e-12);
        var bottom=node.Positions.Single(p=>p.RoadCode==a.Code&&p.RoadOrder==0);Assert.AreEqual(110.5m,bottom.X);Assert.AreEqual(108m,bottom.Y);Assert.AreEqual(0.5125,bottom.RoadPercent,1e-12);
        var vertical=node.Positions.Single(p=>p.RoadCode==b.Code);Assert.AreEqual(110m,vertical.X);Assert.AreEqual(110.5m,vertical.Y);Assert.AreEqual(0.5125,vertical.RoadPercent,1e-12);
        map.Calculate();Assert.AreEqual(2,map.Crosses.GetAll().Data!.Length);Assert.IsTrue(map.Crosses.GetAll().Data!.All(c=>c.State==CrossState.Disconnected));
        Assert.IsTrue(node.Positions.All(p=>!p.IsChanged));
    }

    [TestMethod,DataRow(1,0),DataRow(-1,0),DataRow(0,1),DataRow(0,-1)]
    public void PercentUsesOrderedSegmentAndPerpendicularFoot(int dx,int dy)
    {
        var map=new TestMap();AddNode(map);var road=map.Add(110-20*dx,110-20*dy,110+20*dx,110+20*dy);var p=Node(map).Positions.Single();
        Assert.AreEqual(dx==0?110m:110.5m,p.X);Assert.AreEqual(dy==0?110m:110.5m,p.Y);Assert.AreEqual(dx+dy>0?0.5125:0.4875,p.RoadPercent,1e-12);Assert.AreEqual(road.Version,p.RoadVersion);
        var a=road.Nodes[0];var b=road.Nodes[1];Assert.AreEqual((double)p.X,a.X+(b.X-a.X)*p.RoadPercent,1e-10);Assert.AreEqual((double)p.Y,a.Y+(b.Y-a.Y)*p.RoadPercent,1e-10);
    }

    [TestMethod]
    public void NearbyRoadsAndDiagonalCornerTangencies_DoNotProducePositions()
    {
        var map=new TestMap();AddNode(map);map.Add(90,115,100,115);map.Add(80,100,90,100);map.Add(90,96,130,136);
        Assert.AreEqual(0,Node(map).Positions.Length);
    }

    [TestMethod]
    public void NodeInsertionDeletionAndReversal_UpdateAllPositionsImmediatelyAndKeepOtherRoads()
    {
        var map=new TestMap();AddNode(map);var a=map.Add(90,108,130,108,130,112,90,112);var b=map.Add(110,90,110,130);var original=Node(map);var vertical=original.Positions.Single(p=>p.RoadCode==b.Code);
        a=map.Save(a,90,108,100,108,130,108,130,112,90,112);var node=Node(map);Assert.AreEqual(vertical,node.Positions.Single(p=>p.RoadCode==b.Code));Assert.AreEqual(1,node.Positions.Single(p=>p.RoadCode==a.Code&&p.Y==108).RoadOrder);Assert.AreEqual(0.35,node.Positions.Single(p=>p.RoadCode==a.Code&&p.Y==108).RoadPercent,1e-12);
        a=map.Save(a,90,108,130,108,130,112,90,112);Assert.AreEqual(0,Node(map).Positions.Single(p=>p.RoadCode==a.Code&&p.Y==108).RoadOrder);
        a=map.Lines.Reverse(new(a.Code,a.Owner,a.Version)).Data!;node=Node(map);Assert.AreEqual(0,node.Positions.Single(p=>p.RoadCode==a.Code&&p.Y==112).RoadOrder);Assert.AreEqual(2,node.Positions.Single(p=>p.RoadCode==a.Code&&p.Y==108).RoadOrder);
        Assert.AreEqual(0.4875,node.Positions.Single(p=>p.RoadCode==a.Code&&p.Y==108).RoadPercent,1e-12);Assert.AreEqual(vertical,node.Positions.Single(p=>p.RoadCode==b.Code));
        a=map.Save(a,90,122,130,122,130,118,90,118);Assert.IsFalse(Node(map).Positions.Any(p=>p.RoadCode==a.Code),"Moving all segments outside must delete only this road's position rows.");
        Assert.AreEqual(vertical,Node(map).Positions.Single());
        Assert.IsTrue(map.Lines.Delete(new(a.Code,a.Owner,a.Version)).Success);Assert.AreEqual(1,Node(map).Positions.Length);Assert.AreEqual(vertical,Node(map).Positions.Single());
    }

    [TestMethod]
    public void LnDeletionRemovesAllPositions_AndFailedSavesKeepWholeSnapshot()
    {
        var map=new TestMap();AddNode(map);var road=map.Add(90,108,130,108,130,112,90,112);var before=map.Snapshot();
        Assert.AreEqual("LineNodeInsideLogicalNode",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(road,90,108,110,108,130,108,130,112,90,112)).Code);Assert.AreEqual(before,map.Snapshot());
        var node=Node(map);Assert.AreEqual("VersionConflict",new LogicalNodeService(map.Repository).Delete(new(node.Code,node.Owner,node.Version-1)).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.IsTrue(new LogicalNodeService(map.Repository).Delete(new(node.Code,node.Owner,node.Version)).Success);Assert.AreEqual(0,map.Repository.Transaction(s=>s.LogicalNodePositions.Count));Assert.AreEqual(1,map.Lines.GetAll().Data!.Length);
    }

    [TestMethod]
    public void JRebuildsEveryLnAtomically_AndUnchangedRecalculationIsIdempotent()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);AddNode(map);Assert.IsTrue(service.Create(new("owner",120.5m,110.5m,6,new())).Success);map.Add(90,110,130,110);
        map.Repository.Transaction(s=>{foreach(var key in s.LogicalNodePositions.Keys.ToArray())s.LogicalNodePositions[key]=s.LogicalNodePositions[key] with{IsChanged=true,RoadPercent=0};return true;});
        var result=service.Calculate();Assert.IsTrue(result.Success);Assert.IsFalse(result.Data!.CanContinue);Assert.AreEqual(0,result.Data.RemainingNodes);Assert.AreEqual(2,service.GetAll().Data!.Sum(n=>n.Positions.Length));Assert.IsTrue(service.GetAll().Data!.SelectMany(n=>n.Positions).All(p=>!p.IsChanged&&p.RoadPercent>0));
        var before=map.Snapshot();Assert.IsTrue(service.Calculate().Success);Assert.AreEqual(before,map.Snapshot());
    }
}
