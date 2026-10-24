using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeConnectionTests;
using static ArgumentMap.Tests.LogicalNodeGoToTests;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeIntegerCrossConnectionTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public void AdjacentIntegerJoinRemainsReachableAndShortestWhenNearestPortProjectionIsHalfInteger(bool find)
    {
        var repository=new InMemoryLineRepository();
        var offRoad=Node(repository,50.5m,101.5m);
        var onRoad=Node(repository,200.5m,300.5m);
        var road=Add(repository,0,0,200,200,200,350);SetBidirectional(repository,road);
        var before=MapUndoTests.Content(repository);
        var plan=find?LogicalNodeFindTests.Find(repository,onRoad,offRoad):GoTo(repository,offRoad,onRoad);

        Assert.AreEqual(road.Code,plan.Join!.RoadCode);
        Assert.IsTrue(plan.Join.Position.IsInteger);
        Assert.AreEqual(plan.Join.Position.X2,plan.Join.Position.Y2,"The optimal join is on the diagonal, not the distant vertical segment.");
        // The closest source ports are (54,99) and (53,98), whose perpendicular
        // feet on y=x are half-integer. One cardinal step then 22 diagonal steps
        // reaches an integer join. The 12-unit LN stem plus this path attains
        // the integer-grid lower bound; rounding a foot or skipping the nearby
        // join would produce a longer route or an invalid intersection angle.
        Assert.AreEqual(13+22*Math.Sqrt(2),NewLength(plan),1e-8);
        Assert.AreEqual(before,MapUndoTests.Content(repository),"Preview does not change the map.");
        var saved=Save(repository,plan);Assert.IsTrue(saved.Success,saved.Message);
        var join=saved.Data!.Map.Crosses.Single(c=>c.Key.Contains(plan.LineCode));
        Assert.AreEqual(plan.Join.Position,join.Key.Position);Assert.AreEqual(CrossState.Connected,join.State);
        Assert.IsTrue(new MapUndoService(repository).Undo("owner").Success);
        Assert.AreEqual(before,MapUndoTests.Content(repository));
    }

    [TestMethod,DataRow(145,1),DataRow(146,0)]
    public void DirectConnectionCrossesOppositeDiagonal_OnlyIntegerIntersectionCreatesDisconnectedCross(int intercept,int count)
    {
        var repository=new InMemoryLineRepository();var source=Node(repository,50.5m,50.5m);var target=Node(repository,150.5m,150.5m);
        var old=Add(repository,75,intercept-75,intercept-50,50);
        var code=LineCode.Generate();int[] coordinates=[42,50,75,50,95,70,150,70,150,159];
        var nodes=Enumerable.Range(0,coordinates.Length/2).Select(i=>new LineNode(code,i,coordinates[2*i],coordinates[2*i+1])).ToArray();
        var plan=new LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor.From(source),LogicalNodeConnectionAnchor.From(target),code,nodes);
        Assert.IsTrue(LineGeometry.TryIntersection(nodes[1],nodes[2],old.Nodes[0],old.Nodes[1],out var geometric));
        Assert.AreEqual(count==1,geometric.IsInteger);
        var before=MapUndoTests.Content(repository);var saved=Save(repository,plan);Assert.IsTrue(saved.Success,saved.Message);
        Assert.AreEqual(count,saved.Data!.DisconnectedCrosses);Assert.AreEqual(count,saved.Data.Map.Crosses.Length);
        Assert.IsTrue(saved.Data.Map.Crosses.All(c=>c.Key.Position.IsInteger&&c.State==CrossState.Disconnected));
        var crosses=new CrossService(repository);while(crosses.Calculate().Data!.CanContinue){}
        Assert.AreEqual(count,crosses.GetAll().Data!.Length,"Later K must preserve the integer-only contract.");
        Assert.IsTrue(new MapUndoService(repository).Undo("owner").Success);
        Assert.AreEqual(before,MapUndoTests.Content(repository));
    }

    [TestMethod]
    public void ReachabilityIgnoresLegacyHalfIntegerTransfersAndRejectsHalfIntegerJoins()
    {
        var repository=new InMemoryLineRepository();var target=Node(repository,30.5m,80.5m);
        var main=Add(repository,10,10,30,30,30,100);var feeder=Add(repository,10,31,31,10);
        var snapshot=repository.Transaction(LineTopologyService.Clone);
        // A private stale snapshot must never make a former half-integer Cross
        // traversable. Do not insert this intentionally invalid record into DAL.
        var key=CrossKey.Create(main.Code,feeder.Code,new(41,41));
        snapshot.Crosses.Add(key,new(key,0,0,0,0,CrossState.Connected,0));
        var graph=LogicalNodeReachability.Build(snapshot,target);
        Assert.IsNull(graph.Path(new(feeder.Code,new(30,52))),"The feeder cannot transfer through the visual half-integer crossing.");
        Assert.IsNotNull(graph.Path(new(main.Code,new(40,40))),"Integer points on the target road remain reachable.");
        Assert.IsNull(graph.Path(new(main.Code,new(41,41))),"A half-integer point is never a connection join.");
    }
}
