using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeConnectionTests;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeGoToTests
{
    internal static LogicalNodeConnectionPlan GoTo(ILineRepository repo,LogicalNodeSnapshot a,LogicalNodeSnapshot b)
    {
        var result=new LogicalNodeConnectionService(repo).Preview(new("owner",LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),Kind:LogicalNodeConnectionKind.GoTo));
        Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(LogicalNodeConnectionKind.GoTo,result.Data!.Kind);return result.Data;
    }
    internal static void SetBidirectional(ILineRepository repo,LineSnapshot road)
    {var r=new LineSaveService(repo).SaveProperties(new(road.Code,"owner",road.Version,road.Properties with{PassInOpposeDirection=1}));Assert.IsTrue(r.Success,r.Message);}
    internal static void Transfer(ILineRepository repo,LineSnapshot from,LineSnapshot to,CrossState? value=null)
    {
        var service=new CrossService(repo);while(service.Calculate().Data!.CanContinue){}
        var cross=service.GetAll().Data!.Single(c=>c.Key.Contains(from.Code)&&c.Key.Contains(to.Code));
        Assert.IsTrue(service.Edit(new(cross.Key,"owner",cross.Version,value??(cross.Key.RoadCode1==from.Code?CrossState.Road1ToRoad2:CrossState.Road2ToRoad1))).Success);
    }
    internal static double NewLength(LogicalNodeConnectionPlan plan)=>plan.Nodes.Zip(plan.Nodes.Skip(1)).Sum(p=>
        Math.Sqrt(Math.Pow(p.Second.X-p.First.X,2)+Math.Pow(p.Second.Y-p.First.Y,2)));
    [TestMethod]
    public void StopsAtNewCross_OnlySourceTraversed_RealKAndOrdinaryConnectionAndSingleUndo()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,100.5m,150.5m);var old=Add(repo,100,0,100,200);
        var before=MapUndoTests.Content(repo);var plan=GoTo(repo,a,b);
        Assert.AreEqual(before,MapUndoTests.Content(repo));Assert.AreEqual(old.Code,plan.Join!.RoadCode);
        Assert.AreEqual(100,plan.Nodes[^1].X);Assert.AreEqual(58d,NewLength(plan),1e-8);
        Assert.AreEqual(1,LogicalNodePositionCalculator.Calculate(a,[new(plan.LineCode,"owner",false,0,plan.Nodes)]).Length);
        Assert.AreEqual(0,LogicalNodePositionCalculator.Calculate(b,[new(plan.LineCode,"owner",false,0,plan.Nodes)]).Length);
        Assert.IsTrue(plan.Continuation.Length>=2);Assert.AreEqual(b.Y,plan.Continuation[^1].Y);
        var saved=Save(repo,plan);Assert.IsTrue(saved.Success,saved.Message);
        var join=saved.Data!.Map.Crosses.Single(c=>c.Key.Contains(plan.LineCode));Assert.AreEqual(CrossState.Connected,join.State);
        Assert.IsTrue(join.AllowsTransfer(plan.LineCode,old.Code));Assert.IsTrue(join.AllowsTransfer(old.Code,plan.LineCode));
        Assert.IsTrue(Save(repo,plan).Success);Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);
        Assert.AreEqual(before,MapUndoTests.Content(repo));Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public void ReverseGraphHonorsSingleAndBidirectionalRoads(bool bidirectional)
    {
        var repo=new InMemoryLineRepository();Node(repo,50.5m,220.5m);var b=Node(repo,100.5m,150.5m);var road=Add(repo,100,0,100,300);
        if(bidirectional)SetBidirectional(repo,road);
        var graph=repo.Transaction(s=>LogicalNodeReachability.Build(s,b));
        Assert.IsNotNull(graph.Path(new(road.Code,new(200,200)))); // y=100 is upstream in both cases.
        Assert.AreEqual(bidirectional,graph.Path(new(road.Code,new(200,440))) is not null);
        if(bidirectional)
        {
            var a=repo.Transaction(s=>s.LogicalNodes.Values.Single(n=>n.Code!=b.Code));var plan=GoTo(repo,a,b);
            Assert.AreEqual(58d,NewLength(plan),1e-8);Assert.IsTrue(plan.Join!.Position.Y>200);
            Assert.IsTrue(Save(repo,plan).Success);
        }
    }
    [TestMethod,DataRow("forward"),DataRow("reverse"),DataRow("disconnected"),DataRow("unset"),DataRow("missing")]
    public void ReverseBranchesOnlyFollowAllowedCrossTransfers(string state)
    {
        var repo=new InMemoryLineRepository();Node(repo,50.5m,50.5m);var b=Node(repo,200.5m,150.5m);
        var main=Add(repo,200,0,200,250);var feeder=Add(repo,80,100,250,100);
        if(state!="missing")Transfer(repo,state=="reverse"?main:feeder,state=="reverse"?feeder:main,
            state=="disconnected"?CrossState.Disconnected:state=="unset"?CrossState.Unset:null);
        var graph=repo.Transaction(s=>LogicalNodeReachability.Build(s,b));
        Assert.AreEqual(state=="forward",graph.Path(new(feeder.Code,new(200,200))) is not null);
        Assert.IsNull(graph.Path(new(feeder.Code,new(460,200))),"A one-way feeder's downstream side cannot return to the old crossing.");
    }
    [TestMethod]
    public void AnyOtherLnBlocksOldContinuation_AndBidirectionalCyclesTerminate()
    {
        var repo=new InMemoryLineRepository();var b=Node(repo,200.5m,150.5m);var blocker=Node(repo,200.5m,80.5m,4);var main=Add(repo,200,0,200,250);SetBidirectional(repo,main);
        var graph=repo.Transaction(s=>LogicalNodeReachability.Build(s,b));Assert.IsNull(graph.Path(new(main.Code,new(400,100))));Assert.IsNotNull(graph.Path(new(main.Code,new(400,200))));
        var cycle=new InMemoryLineRepository();var target=Node(cycle,150.5m,100.5m);
        var roads=new[]{Add(cycle,0,50,200,50),Add(cycle,0,150,200,150),Add(cycle,50,0,50,200),Add(cycle,150,0,150,200)};
        foreach(var road in roads)SetBidirectional(cycle,road);
        var service=new CrossService(cycle);while(service.Calculate().Data!.CanContinue){}
        foreach(var cross in service.GetAll().Data!)Assert.IsTrue(service.Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)).Success);
        var reachable=cycle.Transaction(s=>LogicalNodeReachability.Build(s,target));var path=reachable.Path(new(roads[2].Code,new(100,200)));
        Assert.IsNotNull(path);Assert.IsTrue(path.Length<20);Assert.AreEqual(target.Y,path[^1].Y);
    }
    [TestMethod]
    public void MinimizesNewLength_EvenWhenTheExistingContinuationIsLonger()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,40.5m);var b=Node(repo,200.5m,200.5m);
        var main=Add(repo,200,0,200,250);var near=Add(repo,10,50,240,50);var far=Add(repo,100,150,240,150);
        Transfer(repo,near,main);Transfer(repo,far,main);
        var plan=GoTo(repo,a,b);Assert.AreEqual(near.Code,plan.Join!.RoadCode);Assert.AreEqual(18d,NewLength(plan),1e-8);
        Assert.AreEqual(50,plan.Nodes[^1].Y);Assert.IsTrue(Save(repo,plan).Success);
    }
    [TestMethod,DataRow("direction"),DataRow("ln"),DataRow("cross")]
    public void ConfirmationRechecksOldRoadDirectionLnObstaclesAndCrossState(string change)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,40.5m);var b=Node(repo,200.5m,200.5m);
        var main=Add(repo,200,0,200,250);var feeder=Add(repo,10,50,240,50);Transfer(repo,feeder,main);
        var plan=GoTo(repo,a,b);Assert.AreEqual(feeder.Code,plan.Join!.RoadCode);
        if(change=="direction")Assert.IsTrue(new LineSaveService(repo).Reverse(new(feeder.Code,"owner",feeder.Version)).Success);
        if(change=="ln")Node(repo,150.5m,50.5m,4);
        if(change=="cross")Transfer(repo,feeder,main,CrossState.Disconnected);
        var before=Snapshot(repo);Assert.AreEqual("ConnectionPathChanged",Save(repo,plan).Code);Assert.AreEqual(before,Snapshot(repo));
        Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }
    [TestMethod,DataRow("half"),DataRow("occupied"),DataRow("tooFar"),DataRow("forgedPath"),DataRow("missingJoin"),DataRow("wrongPoint")]
    public void EndpointAdjustmentIsLimited_ContinuationIsNeverTrusted(string scenario)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,120.5m,150.5m);
        var diagonal=scenario is "half" or "tooFar";var old=diagonal?Add(repo,100,0,120,20,120,200):Add(repo,120,0,120,50,120,200);
        var code=LineCode.Generate();var nodes=diagonal?new[]{42,50,75,50,113,12}:new[]{42,50,121,50};
        if(scenario=="tooFar")nodes=[42,50,76,50,115,11];
        var join=new LogicalNodeConnectionJoin(old.Code,diagonal?new(225,25):new(240,100));
        if(scenario=="tooFar")join=join with{Position=new(226,26)};
        var plan=new LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),code,
            Enumerable.Range(0,nodes.Length/2).Select(i=>new LineNode(code,i,nodes[2*i],nodes[2*i+1])).ToArray(),LogicalNodeConnectionKind.GoTo,scenario=="missingJoin"?null:join)
            {Continuation=[new("FAKE",0,0)]};
        if(scenario=="wrongPoint")plan=plan with{Join=join with{Position=new(240,102)}};
        var before=Snapshot(repo);var result=Save(repo,plan);
        if(scenario is "half" or "tooFar" or "missingJoin" or "wrongPoint"){Assert.AreEqual("InvalidConnectionJoin",result.Code);Assert.AreEqual(before,Snapshot(repo));}
        else{Assert.IsTrue(result.Success,result.Message);Assert.IsTrue(result.Data!.Map.Crosses.Single(c=>c.Key.Contains(code)).AllowsTransfer(code,old.Code));}
    }
    [TestMethod]
    public void NoTargetRoadOrRouteNeverFallsBackToDirect_AndInvalidKindIsRejected()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,50.5m);
        var service=new LogicalNodeConnectionService(repo);var request=new PreviewLogicalNodeConnectionRequest("owner",LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),Kind:LogicalNodeConnectionKind.GoTo);
        var before=Snapshot(repo);Assert.AreEqual("NoReachableTargetRoad",service.Preview(request).Code);Assert.AreEqual(before,Snapshot(repo));
        Assert.AreEqual("InvalidConnectionKind",service.Preview(request with{Kind=(LogicalNodeConnectionKind)99}).Code);
    }
    [TestMethod]
    public void ExistingContinuationCannotEvenTouchAnotherLnCorner()
    {
        var repo=new InMemoryLineRepository();var target=Node(repo,120.5m,150.5m);Node(repo,108.5m,12.5m,4);
        var road=Add(repo,100,0,120,20,120,200);
        var graph=repo.Transaction(s=>LogicalNodeReachability.Build(s,target));
        Assert.IsNull(graph.Path(new(road.Code,new(210,10))));
        Assert.IsNotNull(graph.Path(new(road.Code,new(230,30))));
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public void NewRoadDetoursAroundLn_OrShiftsOneUnitAwayFromOccupiedOldCrosses(bool occupiedCrosses)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,120.5m,150.5m);var main=Add(repo,120,0,120,200);
        if(occupiedCrosses)for(var y=48;y<=53;y++)Add(repo,110,y,130,y);
        else Node(repo,85.5m,50.5m,12);
        var plan=GoTo(repo,a,b);Assert.AreEqual(main.Code,plan.Join!.RoadCode);
        Assert.AreEqual(78+(Math.Sqrt(2)-1)*(occupiedCrosses?1:4),NewLength(plan),1e-7);
        Assert.IsTrue(Save(repo,plan).Success);Assert.IsNull(repo.Transaction(s=>CrossPassageValidator.FindInCurrentGeometry(s)));
    }
    [TestMethod]
    public void OccupiedFirstGateMayAlreadyPassTheBestJoin_WithoutReversingOrAddingAnotherUnit()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m,4);var b=Node(repo,100.5m,53.5m);
        Add(repo,30,53,48,53,49,53,50,53,51,53,52,53,53,53,150,53);
        var plan=GoTo(repo,a,b);Assert.AreEqual(11d,NewLength(plan),1e-8);
        Assert.AreEqual(54,plan.Nodes[^1].Y);Assert.AreEqual(53,plan.Join!.Position.Y);
        Assert.IsTrue(Save(repo,plan).Success);
    }
    [TestMethod]
    public void GoToUsesOrdinaryJoin_OnlyOtherNewCrossesDisconnected_ExistingStatesAndPendingPreserved()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,40.5m);var b=Node(repo,200.5m,200.5m);
        var main=Add(repo,200,0,200,250);var feeder=Add(repo,10,50,240,50);Transfer(repo,feeder,main);
        var oldCross=new CrossService(repo).GetAll().Data!.Single();var other=Add(repo,0,35,100,35);
        var marks=repo.Transaction(s=>(s.PendingSegments.ToHashSet(),s.DirtyLines.ToHashSet(),s.StaleCrosses.ToHashSet()));
        var before=MapUndoTests.Content(repo);var plan=GoTo(repo,a,b);var saved=Save(repo,plan);Assert.IsTrue(saved.Success,saved.Message);
        var joins=saved.Data!.Map.Crosses.Where(c=>c.Key.Contains(plan.LineCode)).ToArray();Assert.AreEqual(2,joins.Length);
        var join=joins.Single(c=>c.Key.Contains(feeder.Code));Assert.AreEqual(CrossState.Connected,join.State);
        Assert.IsTrue(join.AllowsTransfer(plan.LineCode,feeder.Code));Assert.IsTrue(join.AllowsTransfer(feeder.Code,plan.LineCode));
        Assert.AreEqual(CrossState.Disconnected,joins.Single(c=>c.Key.Contains(other.Code)).State);
        Assert.AreEqual(oldCross,saved.Data.Map.Crosses.Single(c=>c.Key==oldCross.Key));
        repo.Transaction(s=>{Assert.IsTrue(marks.Item1.SetEquals(s.PendingSegments));Assert.IsTrue(marks.Item2.SetEquals(s.DirtyLines));Assert.IsTrue(marks.Item3.SetEquals(s.StaleCrosses));return true;});
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual(before,MapUndoTests.Content(repo));
    }
}
