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
public sealed class LogicalNodeFindTests
{
    internal static LogicalNodeConnectionPlan Find(ILineRepository repo,LogicalNodeSnapshot a,LogicalNodeSnapshot b)
    {
        var result=new LogicalNodeConnectionService(repo).Preview(new("owner",LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),Kind:LogicalNodeConnectionKind.Find));
        Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(LogicalNodeConnectionKind.Find,result.Data!.Kind);return result.Data;
    }

    [TestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(10),DataRow(12),DataRow(18)]
    public void EveryTargetLnTypeRetainsItsRulesAndCorrectOutsideTail(int size)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,100.5m,50.5m);LogicalNodeSnapshot b;
        if(size==10)
        {
            var parent=Node(repo,400.5m,400.5m,12);
            Assert.IsTrue(new LogicalNodeService(repo).Update(new(parent.Code,"owner",parent.Version,"HUMAN","")).Success);
            b=new LogicalNodeService(repo).Create(new("owner",50.5m,150.5m,10,new(){Value="human()"})).Data!;
        }
        else b=Node(repo,50.5m,150.5m,size);
        Add(repo,100,0,100,200);var properties=b.Properties;var plan=Find(repo,a,b);
        var end=plan.Nodes[^1];
        Assert.AreEqual(5.5m,Math.Max(Math.Max(b.Left-end.X,end.X-b.Right),Math.Max(b.Bottom-end.Y,end.Y-b.Top)));
        var saved=Save(repo,plan);Assert.IsTrue(saved.Success,saved.Message);
        Assert.AreEqual(properties,new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==b.Code).Properties);
    }

    [TestMethod]
    public void StartsAtNewCross_OnlyTargetTraversed_ForwardPreview_RealKAndOneUndo()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,100.5m,50.5m);var b=Node(repo,50.5m,150.5m);var old=Add(repo,100,0,100,200);
        var before=Snapshot(repo);var content=MapUndoTests.Content(repo);var plan=Find(repo,a,b);
        Assert.AreEqual(before,Snapshot(repo));Assert.AreEqual(old.Code,plan.Join!.RoadCode);
        Assert.AreEqual(100,plan.Nodes[0].X);Assert.AreEqual(42,plan.Nodes[^1].X);Assert.AreEqual(58d,NewLength(plan),1e-8);
        var road=new LineSnapshot(plan.LineCode,"owner",false,0,plan.Nodes);
        Assert.AreEqual(0,LogicalNodePositionCalculator.Calculate(a,[road]).Length);
        Assert.AreEqual(1,LogicalNodePositionCalculator.Calculate(b,[road]).Length);
        Assert.AreEqual(a.Y,plan.Continuation[0].Y);
        Assert.AreEqual(plan.Join.Position.X2/2m,plan.Continuation[^1].X);Assert.AreEqual(plan.Join.Position.Y2/2m,plan.Continuation[^1].Y);
        Assert.IsNull(LineGeometry.FindAcuteCorner(plan.Nodes));
        var saved=Save(repo,plan);Assert.IsTrue(saved.Success,saved.Message);
        var join=saved.Data!.Map.Crosses.Single(c=>c.Key.Contains(plan.LineCode));Assert.AreEqual(CrossState.Connected,join.State);
        Assert.IsTrue(join.AllowsTransfer(old.Code,plan.LineCode));Assert.IsTrue(join.AllowsTransfer(plan.LineCode,old.Code));
        var after=Snapshot(repo);Assert.IsTrue(Save(repo,plan).Success);Assert.AreEqual(after,Snapshot(repo));
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual(content,MapUndoTests.Content(repo));
        Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }

    [TestMethod,DataRow(false),DataRow(true)]
    public void ForwardGraphHonorsRoadDirectionAndBidirectionalBranches(bool bidirectional)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,100.5m,150.5m);var road=Add(repo,100,0,100,300);
        if(bidirectional)SetBidirectional(repo,road);
        var before=Snapshot(repo);
        var downstream=repo.Transaction(s=>LogicalNodeReachability.ForwardPath(s,a,new(road.Code,new(200,440))));
        var upstream=repo.Transaction(s=>LogicalNodeReachability.ForwardPath(s,a,new(road.Code,new(200,200))));
        Assert.IsNotNull(downstream);Assert.AreEqual(bidirectional,upstream is not null);Assert.AreEqual(before,Snapshot(repo));
        var b=Node(repo,50.5m,100.5m);var plan=Find(repo,a,b);
        if(bidirectional){Assert.AreEqual(58d,NewLength(plan),1e-8);Assert.IsTrue(plan.Join!.Position.Y<150);}
        else Assert.IsTrue(plan.Join!.Position.Y2/2m>a.Top);
        Assert.IsTrue(Save(repo,plan).Success);
    }

    [TestMethod,DataRow("forward"),DataRow("reverse"),DataRow("connected"),DataRow("disconnected"),DataRow("unset"),DataRow("missing")]
    public void ForwardTransfersRespectCrossDirectionAndCannotTravelBackwards(string transfer)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,200.5m,50.5m);
        var main=Add(repo,200,0,200,250);var branch=Add(repo,80,100,250,100);
        if(transfer!="missing")Transfer(repo,transfer=="reverse"?branch:main,transfer=="reverse"?main:branch,
            transfer=="connected"?CrossState.Connected:transfer=="disconnected"?CrossState.Disconnected:transfer=="unset"?CrossState.Unset:null);
        var before=Snapshot(repo);
        Assert.AreEqual(transfer is "forward" or "connected",repo.Transaction(s=>LogicalNodeReachability.ForwardPath(s,a,new(branch.Code,new(460,200)))) is not null);
        Assert.IsNull(repo.Transaction(s=>LogicalNodeReachability.ForwardPath(s,a,new(branch.Code,new(200,200)))));
        Assert.AreEqual(before,Snapshot(repo));
    }

    [TestMethod]
    public void OtherLnStopsForwardReachability_AndBidirectionalCyclesTerminate()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,200.5m,50.5m);Node(repo,200.5m,120.5m,4);var main=Add(repo,200,0,200,250);SetBidirectional(repo,main);
        Assert.IsNull(repo.Transaction(s=>LogicalNodeReachability.ForwardPath(s,a,new(main.Code,new(400,300)))));
        Assert.IsNotNull(repo.Transaction(s=>LogicalNodeReachability.ForwardPath(s,a,new(main.Code,new(400,200)))));
        var cycle=new InMemoryLineRepository();var start=Node(cycle,150.5m,100.5m);
        var roads=new[]{Add(cycle,0,50,200,50),Add(cycle,0,150,200,150),Add(cycle,50,0,50,200),Add(cycle,150,0,150,200)};
        foreach(var road in roads)SetBidirectional(cycle,road);
        var crosses=new CrossService(cycle);while(crosses.Calculate().Data!.CanContinue){}
        foreach(var cross in crosses.GetAll().Data!)Assert.IsTrue(crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)).Success);
        var path=cycle.Transaction(s=>LogicalNodeReachability.ForwardPath(s,start,new(roads[2].Code,new(100,200))));
        Assert.IsNotNull(path);Assert.IsTrue(path.Length<20);Assert.AreEqual(start.Y,path[0].Y);
    }

    [TestMethod]
    public void NearestMeansNewLineLength_EvenWhenExistingRouteIsLonger()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,200.5m,50.5m);var b=Node(repo,50.5m,210.5m);
        var main=Add(repo,200,0,200,250);var near=Add(repo,240,200,10,200);var far=Add(repo,240,100,10,100);
        Transfer(repo,main,near);Transfer(repo,main,far);
        var plan=Find(repo,a,b);Assert.AreEqual(near.Code,plan.Join!.RoadCode);Assert.AreEqual(19d,NewLength(plan),1e-8);
        Assert.IsTrue(Save(repo,plan).Success);
    }

    [TestMethod,DataRow("direction"),DataRow("ln"),DataRow("cross"),DataRow("sourceRemoved")]
    public void ConfirmationRechecksSourceDirectionObstaclesAndTransfers(string change)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,200.5m,50.5m);var b=Node(repo,50.5m,90.5m);
        var main=Add(repo,200,0,200,250);var branch=Add(repo,240,100,10,100);Transfer(repo,main,branch);
        var plan=Find(repo,a,b);Assert.AreEqual(branch.Code,plan.Join!.RoadCode);
        if(change=="direction")Assert.IsTrue(new LineSaveService(repo).Reverse(new(branch.Code,"owner",branch.Version)).Success);
        if(change=="ln")Node(repo,150.5m,100.5m,4);
        if(change=="cross")Transfer(repo,main,branch,CrossState.Disconnected);
        if(change=="sourceRemoved")Assert.IsTrue(new LogicalNodeService(repo).Delete(new(a.Code,"owner",repo.Transaction(s=>s.LogicalNodes[a.Code].Version))).Success);
        var before=Snapshot(repo);Assert.AreEqual(change=="sourceRemoved"?"ConnectionNodeMissing":"ConnectionPathChanged",Save(repo,plan).Code);
        Assert.AreEqual(before,Snapshot(repo));Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }

    [TestMethod,DataRow("half"),DataRow("occupied"),DataRow("tooFar"),DataRow("forgedPath"),DataRow("missingJoin")]
    public void CrossEndpointAdjustsAtMostOneUnit_AndClientPathIsNotTrusted(string scenario)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,120.5m,150.5m);var b=Node(repo,50.5m,50.5m);
        var diagonal=scenario is "half" or "tooFar";
        var old=diagonal?Add(repo,120,200,120,20,100,0):Add(repo,120,200,120,50,120,0);
        var xy=diagonal?new[]{113,12,75,50,42,50}:new[]{121,50,42,50};
        if(scenario=="tooFar")xy=[115,11,76,50,42,50];
        var code=LineCode.Generate();var join=new LogicalNodeConnectionJoin(old.Code,diagonal?new(225,25):new(240,100));
        if(scenario=="tooFar")join=join with{Position=new(226,26)};
        var plan=new LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),code,
            Enumerable.Range(0,xy.Length/2).Select(i=>new LineNode(code,i,xy[2*i],xy[2*i+1])).ToArray(),LogicalNodeConnectionKind.Find,scenario=="missingJoin"?null:join)
            {Continuation=[new("FAKE",0,0)]};
        var before=Snapshot(repo);var saved=Save(repo,plan);
        if(scenario is "half" or "tooFar" or "missingJoin"){Assert.AreEqual("InvalidConnectionJoin",saved.Code);Assert.AreEqual(before,Snapshot(repo));}
        else{Assert.IsTrue(saved.Success,saved.Message);Assert.AreEqual(CrossState.Connected,saved.Data!.Map.Crosses.Single(c=>c.Key.Contains(code)).State);}
    }

    [TestMethod,DataRow(false),DataRow(true)]
    public void NewLineAvoidsLnAndExistingTwoRoadCrosses(bool occupiedCrosses)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,120.5m,150.5m);var b=Node(repo,50.5m,50.5m);Add(repo,120,200,120,0);
        if(occupiedCrosses)for(var y=48;y<=53;y++)Add(repo,110,y,130,y);else Node(repo,85.5m,50.5m,12);
        var plan=Find(repo,a,b);Assert.AreEqual(78+(Math.Sqrt(2)-1)*(occupiedCrosses?1:4),NewLength(plan),1e-7);
        Assert.IsTrue(Save(repo,plan).Success);Assert.IsNull(repo.Transaction(CrossPassageValidator.FindInCurrentGeometry));
    }

    [TestMethod]
    public void NoSourceRoadDoesNotFallBackToDirect()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,50.5m);
        var before=Snapshot(repo);
        Assert.AreEqual("NoReachableSourceRoad",new LogicalNodeConnectionService(repo).Preview(new("owner",LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),Kind:LogicalNodeConnectionKind.Find)).Code);
        Assert.AreEqual(before,Snapshot(repo));
    }
}
