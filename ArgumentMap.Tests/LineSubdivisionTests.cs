using ArgumentMap.Rules;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LineSubdivisionTests
{
    internal static void AssertSegments(LineSnapshot road)
    {
        Assert.AreEqual(road.Nodes.Length,road.Nodes.Select(n=>(n.X,n.Y)).Distinct().Count());
        for(var i=0;i<road.Nodes.Length;i++)
        {
            var n=road.Nodes[i];Assert.AreEqual(i,n.Order);Assert.AreEqual(road.Code,n.LineCode);Assert.IsTrue(n.X is >=0 and <=500000&&n.Y is >=0 and <=500000);
            if(i==0)continue;var a=road.Nodes[i-1];long dx=n.X-a.X,dy=n.Y-a.Y;
            Assert.IsTrue(dx*dx+dy*dy is >0 and <=250000,$"Overlong segment {i}: ({a.X},{a.Y}) to ({n.X},{n.Y})");Assert.IsTrue(LineGeometry.IsEightDirection(a,n));
        }
    }
    [TestMethod,DataRow(499,false,2),DataRow(500,false,2),DataRow(501,false,3),DataRow(1000,false,3),DataRow(1001,false,4),DataRow(353,true,2),DataRow(354,true,3),DataRow(707,true,4)]
    public void F6_ActualLengthThresholdAndRepeatedNearMidpointRefinement(int steps,bool diagonal,int expectedNodes)
    {
        var map=new TestMap();var line=map.Create();var request=TestMap.Shape(line,0,0,steps,diagonal?steps:0);var input=request.Nodes.ToArray();
        var result=map.Lines.CheckShapeIsOKThenSave(request);Assert.IsTrue(result.Success,result.Message);var road=result.Data!;AssertSegments(road);
        Assert.AreEqual(expectedNodes,road.Nodes.Length);CollectionAssert.AreEqual(input,request.Nodes);Assert.AreEqual(line.Code,road.Code);
        Assert.AreEqual((0,0),(road.Nodes[0].X,road.Nodes[0].Y));Assert.AreEqual((steps,diagonal?steps:0),(road.Nodes[^1].X,road.Nodes[^1].Y));
        var before=map.Snapshot();var unchanged=map.Lines.CheckShapeIsOKThenSave(new(road.Code,road.Owner,road.Version,road.Nodes));Assert.IsTrue(unchanged.Success);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow(1,0),DataRow(1,1),DataRow(0,1),DataRow(-1,1),DataRow(-1,0),DataRow(-1,-1),DataRow(0,-1),DataRow(1,-1)]
    public void AllEightDirections_ChooseClosestIntegerMidpointAndPreserveOriginalOrder(int dx,int dy)
    {
        var map=new TestMap();var road=map.Add(1000,1000,1000+601*dx,1000+601*dy);AssertSegments(road);
        Assert.AreEqual(3,road.Nodes.Length);Assert.AreEqual((1000+300*dx,1000+300*dy),(road.Nodes[1].X,road.Nodes[1].Y));
    }
    [TestMethod,DataRow(6),DataRow(12),DataRow(18)]
    public void MidpointInsideLn_MovesToNearestLegalIntegerWithoutChangingPath(int size)
    {
        var map=new TestMap();var nodes=new LogicalNodeService(map.Repository);var node=nodes.Create(new("owner",300.5m,100.5m,size,new())).Data!;
        var road=map.Add(0,100,601,100);AssertSegments(road);Assert.AreEqual(3,road.Nodes.Length);
        Assert.AreEqual((int)decimal.Floor(node.Left),road.Nodes[1].X);Assert.IsTrue(road.Nodes.All(n=>!node.Contains(n.X,n.Y)));Assert.IsTrue(road.Nodes.All(n=>n.Y==100));
        var position=nodes.GetAll().Data!.Single().Positions.Single();var a=road.Nodes[position.RoadOrder];var b=road.Nodes[position.RoadOrder+1];
        Assert.AreEqual((300.5m,100m),(position.X,position.Y));Assert.AreEqual(300.5,a.X+(b.X-a.X)*position.RoadPercent,1e-12);
    }
    [TestMethod]
    public void OccupiedMidpointAndOtherSubmittedNodesAreAvoided()
    {
        var map=new TestMap();map.Add(300,100,300,110);var road=map.Add(0,100,601,100);AssertSegments(road);Assert.AreEqual(301,road.Nodes[1].X);map.Calculate();Assert.AreEqual(1,map.Crosses.GetAll().Data!.Length);
        var self=new TestMap();road=self.Add(0,600,1200,600,1200,1200,600,1200,600,0);AssertSegments(road);
        Assert.AreEqual(1,road.Nodes.Count(p=>p.X==600&&p.Y==600));self.Calculate();Assert.AreEqual(0,self.Crosses.GetAll().Data!.Length);
    }
    [TestMethod]
    public void LnCornerTangencyRemainsLegalForDiagonalRefinement()
    {
        var map=new TestMap();var square=new LogicalNodeService(map.Repository).Create(new("owner",300.5m,306.5m,6,new())).Data!;
        var road=map.Add(0,0,601,601);AssertSegments(road);Assert.AreEqual(3,road.Nodes.Length);Assert.IsTrue(road.Nodes.All(n=>!square.Contains(n.X,n.Y)));
    }
    [TestMethod]
    public void ExistingCrossCanBeASplitNode_UsesOutgoingZeroAndKeepsItsState()
    {
        var map=new TestMap();var road=TestMap.HistoricalRoad(map.Repository,0,100,1000,100);var other=map.Add(500,90,500,110);map.Calculate();
        var old=map.Between(road,other);Assert.IsTrue(map.Crosses.Edit(new(old.Key,"owner",old.Version,CrossState.Road1ToRoad2)).Success);
        var saved=map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(road,0,100,1000,100));Assert.IsTrue(saved.Success,saved.Message);Assert.AreEqual(500,saved.Data!.Nodes[1].X);
        map.Calculate();var current=map.Between(saved.Data,other);Assert.AreEqual(old.Key,current.Key);Assert.AreEqual(CrossState.Road1ToRoad2,current.State);Assert.AreEqual((1,0d),current.LocationOn(road.Code));
    }
    [TestMethod]
    public void AllIntegerCandidatesOccupied_FailsWholeSaveAndDoesNotMutateRequest()
    {
        var map=new TestMap();var candidate=map.Create();
        map.Repository.Transaction(s=>{for(var x=1;x<600;x++){var code=LineCode.Generate();s.Lines.Add(code,new(code,"other",false,1,[new(code,0,x,100),new(code,1,x,110)]));}return true;});
        var shape=TestMap.Shape(candidate,0,100,600,100);var before=map.Snapshot();var result=map.Lines.CheckShapeIsOKThenSave(shape);
        Assert.AreEqual("CannotSubdivideSegment",result.Code);Assert.AreEqual(before,map.Snapshot());Assert.AreEqual(2,shape.Nodes.Length);
    }
    [TestMethod,DataRow("cross"),DataRow("diagonal-ln"),DataRow("acute"),DataRow("overlap"),DataRow("angle")]
    public void SubdivisionCannotRepairInvalidOriginalGeometry(string kind)
    {
        var map=new TestMap();var candidate=map.Create();var xy=new[]{0,100,601,100};
        if(kind=="cross"){map.Add(295,95,305,105);map.Add(295,105,305,95);map.Calculate();}
        if(kind=="diagonal-ln"){Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",300.5m,300.5m,6,new())).Success);xy=[0,0,601,601];}
        if(kind=="acute")xy=[0,100,601,100,590,89];
        if(kind=="overlap")map.Add(290,100,310,100);
        if(kind=="angle")map.Add(290,90,310,110);
        var before=map.Snapshot();var result=map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate,xy));
        Assert.AreEqual(kind switch{"cross"=>"LinePassesCross","diagonal-ln"=>"DiagonalInsideLogicalNode","acute"=>"AcuteCorner","overlap"=>"SegmentOverlap",_=>"NonOrthogonalIntersection"},result.Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public void FullWorldSpan_IsBoundedAndKeepsIntegerPath(bool diagonal)
    {
        var map=new TestMap();var road=map.Add(0,0,500000,diagonal?500000:0);AssertSegments(road);Assert.IsTrue(road.Nodes.Length<4096);
        Assert.IsTrue(road.Nodes.All(n=>diagonal?n.X==n.Y:n.Y==0));
    }
    [TestMethod]
    public async Task WpfF6_UsesReturnedRefinedNodesAndReportsAdditionalNodes()
    {
        var map=new TestMap();var editor=new WpfArgumentMap.LineEditor(new FrontAdditionTests.Client(map));
        Assert.IsTrue(await editor.NewLineAsync());Assert.IsTrue(await editor.BeginDrawingAsync());var code=editor.DraftCode;
        Assert.IsTrue(editor.AddPoint(new(0,100)));Assert.IsTrue(editor.AddPoint(new(1200,100)));Assert.IsTrue(await editor.CompleteAsync(),editor.Message);
        Assert.AreEqual(WpfArgumentMap.LineEditorMode.Idle,editor.Mode);Assert.AreEqual(5,editor.Lines.Single().Nodes.Count);Assert.AreEqual(code,editor.Lines.Single().Code);StringAssert.Contains(editor.Message,"新增 3");
        AssertSegments(map.Lines.GetAll().Data!.Single());
    }
}
