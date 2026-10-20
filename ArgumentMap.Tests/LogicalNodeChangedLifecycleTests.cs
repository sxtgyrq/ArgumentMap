using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeChangedLifecycleTests
{
    [TestMethod]
    public void JRestoresPendingRelations_DeletesUnusable_InsertsMissing_PreservesLnAndAllCrossWork()
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,CrossState.Disconnected);var node=MapAreaMoveTests.Node(map);var unused=map.Add(200,200,230,200);
        map.Repository.Transaction(s=>
        {
            var a=s.LogicalNodePositions.Values.Single(p=>p.RoadCode==pair.A.Code);s.LogicalNodePositions[a.Key]=a with{IsChanged=true,RoadPercent=.123};
            var b=s.LogicalNodePositions.Values.Single(p=>p.RoadCode==pair.B.Code);s.LogicalNodePositions.Remove(b.Key);
            var obsolete=new LogicalNodePosition(node.Code,unused.Code,0,.5,215,200,unused.Version,true);s.LogicalNodePositions.Add(obsolete.Key,obsolete);
            s.StaleCrosses.Add(pair.Cross.Key);return true;
        });
        var crossWork=CrossWork(map);var service=new LogicalNodeService(map.Repository);var result=service.Calculate();Assert.IsTrue(result.Success,result.Message);
        var restored=service.GetAll().Data!.Single();Assert.AreEqual(node.Properties,restored.Properties);Assert.AreEqual(node.Version+1,restored.Version);
        CollectionAssert.AreEqual(node.Positions,restored.Positions);Assert.AreEqual(crossWork,CrossWork(map),"J must not calculate or clean Cross/K tasks.");
        var before=map.Snapshot();Assert.IsTrue(service.Calculate().Success);Assert.AreEqual(before,map.Snapshot(),"A repeat J must be idempotent.");
    }

    [TestMethod]
    public void JRemovesLastInvalidRelation_ButNeverDeletesTheLogicalNode()
    {
        var map=new TestMap();var node=MapAreaMoveTests.Node(map,size:18);var unrelated=map.Add(200,200,230,200);
        map.Repository.Transaction(s=>{var p=new LogicalNodePosition(node.Code,unrelated.Code,0,.5,215,200,unrelated.Version,true);s.LogicalNodePositions.Add(p.Key,p);return true;});
        var service=new LogicalNodeService(map.Repository);Assert.IsTrue(service.Calculate().Success);var actual=service.GetAll().Data!.Single();
        Assert.AreEqual(0,actual.Positions.Length);Assert.AreEqual(node.Code,actual.Code);Assert.AreEqual(node.Properties,actual.Properties);Assert.IsTrue(actual.IsStart);
    }

    [TestMethod]
    public void PartialRoadMovePreservesPendingRelationOnAnotherRoadOfSameLn_UntilExplicitJ()
    {
        var map=new TestMap();var a=map.Add(20,50,80,50);var b=map.Add(15,52,85,52);map.Calculate();var node=MapAreaMoveTests.Node(map);
        map.Repository.Transaction(s=>{var p=s.LogicalNodePositions.Values.Single(p=>p.RoadCode==b.Code);s.LogicalNodePositions[p.Key]=p with{IsChanged=true};return true;});
        var selected=MapAreaMoveTests.Select(map,new(19,49,81,51));Assert.AreEqual(0,selected.LogicalNodes.Length);
        var moved=MapAreaMoveTests.Move(map,selected);Assert.IsTrue(moved.Success,moved.Message);var after=moved.Data!.LogicalNodes.Single();
        Assert.AreEqual(1,after.Positions.Length);Assert.AreEqual(b.Code,after.Positions.Single().RoadCode);Assert.IsTrue(after.Positions.Single().IsChanged);
        Assert.AreEqual(node.Version+1,after.Version);Assert.IsFalse(after.Positions.Any(p=>p.RoadCode==a.Code));
        Assert.IsTrue(new LogicalNodeService(map.Repository).Calculate().Success);after=new LogicalNodeService(map.Repository).GetAll().Data!.Single();Assert.IsFalse(after.Positions.Single().IsChanged);
    }

    private static string CrossWork(TestMap map)=>map.Repository.Transaction(s=>JsonSerializer.Serialize(new
    {Crosses=s.Crosses.Values.OrderBy(c=>c.Key),Stale=s.StaleCrosses.Order(),Dirty=s.DirtyLines.Order(),Pending=s.PendingSegments.OrderBy(p=>p.LineCode).ThenBy(p=>p.Segment).Select(p=>new{p.LineCode,p.Segment})}));
}
