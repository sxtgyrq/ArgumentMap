using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeConnectionTests
{
    internal static LogicalNodeSnapshot Node(ILineRepository repo,decimal x,decimal y,int size=6,string owner="owner")
    {
        var p=new LogicalNodeProperties{Value=size==4?"False":size==8?"=":"对象",LogicLanguage=size==6?"P_"+x+"_"+y:""};
        var result=new LogicalNodeService(repo).Create(new(owner,x,y,size,p));Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    internal static LogicalNodeConnectionPlan Plan(ILineRepository repo,LogicalNodeSnapshot a,LogicalNodeSnapshot b)
    {var result=new LogicalNodeConnectionService(repo).Preview(new("owner",LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b)));Assert.IsTrue(result.Success,result.Message);return result.Data!;}
    internal static ServiceResult<LogicalNodeConnectionResult> Save(ILineRepository repo,LogicalNodeConnectionPlan plan)=>
        MapUndoTests.Edit(repo,r=>new LogicalNodeConnectionService(r).Save(new("owner",plan)));
    internal static void Check(ILineRepository repo,LogicalNodeConnectionPlan plan,LogicalNodeSnapshot a,LogicalNodeSnapshot b)
    {
        var line=new LineSnapshot(plan.LineCode,"owner",false,0,plan.Nodes);
        Assert.AreEqual(1,LogicalNodePositionCalculator.Calculate(a,[line]).Length);Assert.AreEqual(1,LogicalNodePositionCalculator.Calculate(b,[line]).Length);
        Assert.IsNull(LineGeometry.FindAcuteCorner(plan.Nodes));
        Assert.IsTrue(plan.Nodes.Zip(plan.Nodes.Skip(1)).All(p=>LineGeometry.IsEightDirection(p.First,p.Second)));
        foreach(var n in repo.Transaction(s=>s.LogicalNodes.Values.ToArray()))Assert.IsFalse(plan.Nodes.Any(p=>n.Contains(p.X,p.Y)));
        static decimal Distance(LogicalNodeSnapshot n,LineNode p)=>Math.Max(Math.Max(n.Left-p.X,p.X-n.Right),Math.Max(n.Bottom-p.Y,p.Y-n.Top));
        Assert.AreEqual(5.5m,Distance(a,plan.Nodes[0]));Assert.AreEqual(5.5m,Distance(b,plan.Nodes[^1]));
    }
    [TestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(10),DataRow(12),DataRow(18)]
    public void EveryLnSizeIsConnectable_WithCorrectTailsAndSingleUndo(int size)
    {
        var repo=new InMemoryLineRepository();LogicalNodeSnapshot a;
        if(size==10){var parent=Node(repo,300.5m,300.5m,12);var changed=new LogicalNodeService(repo).Update(new(parent.Code,parent.Owner,parent.Version,"HUMAN","",LogicLanguage:"HUMAN"));Assert.IsTrue(changed.Success);a=new LogicalNodeService(repo).Create(new("owner",50.5m,50.5m,10,new(){Value="human(id=1)"})).Data!;}
        else a=Node(repo,50.5m,50.5m,size);
        var b=Node(repo,150.5m,80.5m,6,"bob");var before=MapUndoTests.Content(repo);var plan=Plan(repo,a,b);Check(repo,plan,a,b);
        Assert.AreEqual(before,MapUndoTests.Content(repo),"Preview must not write.");var saved=Save(repo,plan);Assert.IsTrue(saved.Success,saved.Message);
        Assert.AreEqual(100,saved.Data!.Line.Properties.Speed);Assert.AreEqual(0,saved.Data.Line.Properties.PassInOpposeDirection);
        Assert.AreEqual(1,saved.Data.Map.LogicalNodes.Single(n=>n.Code==a.Code).Positions.Length);
        var undo=new MapUndoService(repo).Undo("owner");Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(before,MapUndoTests.Content(repo));
        Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }
    [TestMethod,DataRow(0),DataRow(1),DataRow(2),DataRow(3)]
    public void DetoursAroundOtherLn_InEveryQuadrant_AndLongRoadsAreSubdivided(int quadrant)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,1000.5m,1000.5m);var dx=quadrant%2==0?1100:-800;var dy=quadrant<2?0:500;
        var b=Node(repo,a.X+dx,a.Y+dy);Node(repo,a.X+dx/2m,a.Y+dy/2m,12);
        var plan=Plan(repo,a,b);Check(repo,plan,a,b);Assert.IsTrue(plan.Nodes.Length>2);
        var result=Save(repo,plan);Assert.IsTrue(result.Success,result.Message);
        Assert.IsTrue(result.Data!.Line.Nodes.Zip(result.Data.Line.Nodes.Skip(1)).All(p=>Math.Pow(p.First.X-p.Second.X,2)+Math.Pow(p.First.Y-p.Second.Y,2)<=250000));
        var obstruction=result.Data.Map.LogicalNodes.Single(n=>n.Code!=a.Code&&n.Code!=b.Code);Assert.AreEqual(0,obstruction.Positions.Length);
    }
    [TestMethod]
    public void SavesThenKThenO_OnlyNewCrossesDisconnected_UnrelatedPendingAndSettingsSurvive()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,50.5m);
        LogicalNodeMySqlTests.Add(repo,100,0,100,100);
        var c=LogicalNodeMySqlTests.Add(repo,300,300,400,300);var d=LogicalNodeMySqlTests.Add(repo,350,250,350,350);
        var crosses=new CrossService(repo);while(crosses.Calculate().Data!.CanContinue){}
        var old=crosses.GetAll().Data!.Single();Assert.IsTrue(crosses.Edit(new(old.Key,"owner",old.Version,CrossState.Road1ToRoad2)).Success);
        var pending=LogicalNodeMySqlTests.Add(repo,400,400,450,400);var marks=repo.Transaction(s=>(s.PendingSegments.ToHashSet(),s.DirtyLines.ToHashSet(),s.StaleCrosses.ToHashSet()));
        var before=MapUndoTests.Content(repo);var plan=Plan(repo,a,b);var saved=Save(repo,plan);Assert.IsTrue(saved.Success,saved.Message);
        Assert.IsTrue(saved.Data!.DisconnectedCrosses>0);Assert.IsTrue(saved.Data.Map.Crosses.Where(x=>x.Key.Contains(plan.LineCode)).All(x=>x.State==CrossState.Disconnected));
        Assert.AreEqual(CrossState.Road1ToRoad2,saved.Data.Map.Crosses.Single(x=>x.Key==old.Key).State);
        repo.Transaction(s=>{Assert.IsTrue(marks.Item1.SetEquals(s.PendingSegments));Assert.IsTrue(marks.Item2.SetEquals(s.DirtyLines));Assert.IsTrue(marks.Item3.SetEquals(s.StaleCrosses));return true;});
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual(before,MapUndoTests.Content(repo));
    }
    [TestMethod]
    public void StaleTargetOrNewObstacleRejectsAtomically_ButMetadataAndJDoNotInvalidatePreview()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,50.5m);var plan=Plan(repo,a,b);
        var edit=new LogicalNodeService(repo).Update(new(a.Code,"owner",a.Version,"new name","",LogicLanguage:"newP"));Assert.IsTrue(edit.Success);Assert.IsTrue(new LogicalNodeService(repo).Calculate().Success);
        Assert.IsTrue(Save(repo,plan).Success,"Metadata and derived versions are not geometry conflicts.");
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);
        var current=repo.Transaction(s=>s.LogicalNodes[b.Code]);Assert.IsTrue(new LogicalNodeService(repo).Move(new(b.Code,"owner",current.Version,180.5m,50.5m)).Success);
        var before=LogicalNodeMySqlTests.Snapshot(repo);Assert.AreEqual("ConnectionNodeChanged",Save(repo,plan).Code);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));
    }
    [TestMethod,DataRow("tail"),DataRow("node"),DataRow("acute"),DataRow("outside"),DataRow("same"),DataRow("third")]
    public void TamperedPlansNeverLeaveAnEmptyLineOrHistory(string kind)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,50.5m);var plan=Plan(repo,a,b);
        if(kind=="tail")plan=plan with{Nodes=plan.Nodes.Select((n,i)=>i==0?n with{X=n.X+1}:n).ToArray()};
        if(kind=="node")plan=plan with{Nodes=plan.Nodes.Select((n,i)=>i==0?n with{Order=7}:n).ToArray()};
        if(kind=="outside")plan=plan with{Nodes=plan.Nodes.Select((n,i)=>i==0?n with{X=int.MinValue}:n).ToArray()};
        if(kind=="same")plan=plan with{Target=plan.Source};
        if(kind=="acute")plan=plan with{Nodes=[new(plan.LineCode,0,41,50),new(plan.LineCode,1,60,50),new(plan.LineCode,2,59,51),new(plan.LineCode,3,160,51)]};
        if(kind=="third"){var position=plan.Nodes[0];Node(repo,decimal.Floor((position.X+plan.Nodes[^1].X)/2m)+.5m,position.Y+.5m,12);}
        var before=LogicalNodeMySqlTests.Snapshot(repo);var result=Save(repo,plan);Assert.IsFalse(result.Success,result.Message);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }
    [TestMethod]
    public void GeometrySnapshotDoesNotHoldTheRepositoryLock_AndCompletedRequestCanBeRetried()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,70.5m);var service=new LogicalNodeConnectionService(repo);
        var prepared=service.Prepare(new("owner",LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b))).Data!;
        Node(repo,300.5m,300.5m,6,"bob");var plan=LogicalNodeConnectionService.Plan(prepared).Data!;var first=Save(repo,plan);Assert.IsTrue(first.Success,first.Message);
        Assert.IsTrue(Save(repo,plan).Success);Assert.AreEqual(1,repo.Transaction(s=>s.Lines.Count));Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);Assert.AreEqual("NothingToUndo",new MapUndoService(repo).Undo("owner").Code);
    }
    [TestMethod]
    public void SearchAvoidsAnUncalculatedOldCross_AndCanCrossDiagonalsLegally()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,100.5m,100.5m);var b=Node(repo,220.5m,130.5m);
        LogicalNodeMySqlTests.Add(repo,155,65,195,105);LogicalNodeMySqlTests.Add(repo,150,106,196,60);
        var plan=Plan(repo,a,b);Check(repo,plan,a,b);var saved=Save(repo,plan);Assert.IsTrue(saved.Success,saved.Message);Assert.IsNull(repo.Transaction(s=>CrossPassageValidator.FindInCurrentGeometry(s)));
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public void SavedConnectionAllows135DegreeCornersAndPerpendicularDiagonals_ButRejects45DegreeCorners(bool acute)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,150.5m);var code=LineCode.Generate();
        var coordinates=acute?new[]{42,50,70,50,60,60,150,60,150,159}:new[]{42,50,75,50,95,70,150,70,150,159};
        var plan=new LogicalNodeConnectionPlan(LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b),code,
            Enumerable.Range(0,coordinates.Length/2).Select(i=>new LineNode(code,i,coordinates[2*i],coordinates[2*i+1])).ToArray());
        if(!acute)LogicalNodeMySqlTests.Add(repo,75,70,95,50);
        var before=LogicalNodeMySqlTests.Snapshot(repo);var result=Save(repo,plan);
        if(acute){Assert.AreEqual("AcuteCorner",result.Code);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));}
        else{Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(1,result.Data!.DisconnectedCrosses);}
    }
    [TestMethod]
    public void ExistingRoadThroughBothTargets_IsCrossedInsideEachLnAfterK_AsDisconnected()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,50.5m,50.5m);var b=Node(repo,150.5m,50.5m);
        // Occupy every possible horizontal passage, forcing a new vertical passage in each LN.
        for(var y=48;y<=53;y++)LogicalNodeMySqlTests.Add(repo,0,y,200,y);
        var plan=Plan(repo,a,b);Check(repo,plan,a,b);var result=Save(repo,plan);Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(12,result.Data!.DisconnectedCrosses);
        Assert.IsTrue(result.Data.Map.Crosses.All(c=>c.State==CrossState.Disconnected));
    }
    [TestMethod]
    public void EnclosedLnHasNoLegalTail_ReportsFailureWithoutWriting_AndCancellationIsSupported()
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,100.5m,100.5m,4);var b=Node(repo,200.5m,200.5m);
        foreach(var (dx,dy) in new[]{(-6,0),(6,0),(0,-6),(0,6)})Node(repo,a.X+dx,a.Y+dy,4);
        var request=new PreviewLogicalNodeConnectionRequest("owner",LogicalNodeConnectionAnchor.From(a),LogicalNodeConnectionAnchor.From(b));var service=new LogicalNodeConnectionService(repo);var before=LogicalNodeMySqlTests.Snapshot(repo);
        Assert.AreEqual("ConnectionRouteNotFound",service.Preview(request).Code);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        // A canceled request with geometry work to do must exit before searching or committing.
        LogicalNodeMySqlTests.Add(repo,300,300,350,300);
        Assert.ThrowsExactly<OperationCanceledException>(()=>service.Preview(request,cancellation.Token));
    }
}
