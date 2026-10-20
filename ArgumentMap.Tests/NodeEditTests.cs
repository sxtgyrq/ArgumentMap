using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class NodeEditTests
{
    internal static FindNodePositionRequest Request(LineSnapshot line,int index,double x,double y)=>new(line.Code,line.Owner,line.Version,line.Nodes,index,x,y);
    [TestMethod,DataRow(0,0),DataRow(1,0),DataRow(2,0),DataRow(3,0),DataRow(0,2),DataRow(1,2),DataRow(2,2),DataRow(3,2)]
    public void NearestCandidate_MatchesBoundedExhaustiveFullSaveOracle_AtBothWorldCorners(int index,int rotation)
    {
        (int X,int Y) Transform(int x,int y)=>rotation==0?(x,y):(500000-x,500000-y);
        (double X,double Y) Target(double x,double y)=>rotation==0?(x,y):(500000-x,500000-y);
        var shape=new[]{2,2,9,2,9,9,18,9};
        var map=new TestMap();var line=map.Add(Enumerable.Range(0,4).SelectMany(i=>{var p=Transform(shape[2*i],shape[2*i+1]);return new[]{p.X,p.Y};}).ToArray());
        // The original legal position bounds the answer's radius. Every point outside this
        // exhaustive square is farther than that bound, including near the upper world corner.
        var legal=new List<(int X,int Y)>();
        for(var x=0;x<=36;x++)for(var y=0;y<=36;y++)
        {
            var p=Transform(x,y);var reference=new TestMap();var empty=reference.Create();
            var nodes=line.Nodes.Select(n=>n with{LineCode=empty.Code}).ToArray();nodes[index]=nodes[index] with{X=p.X,Y=p.Y};
            if(reference.Lines.CheckShapeIsOKThenSave(new(empty.Code,"owner",empty.Version,nodes)).Success)legal.Add(p);
        }
        foreach(var target in new[]{Target(4.25,9.75),Target(12.5,4.5),Target(15.1,14.7)})
        {
            var original=line.Nodes[index];var expected=legal.OrderBy(p=>(p.X-target.X)*(p.X-target.X)+(p.Y-target.Y)*(p.Y-target.Y))
                .ThenBy(p=>Math.Abs(p.X-original.X)+Math.Abs(p.Y-original.Y)).ThenBy(p=>p.X).ThenBy(p=>p.Y).First();
            var before=map.Snapshot();var request=Request(line,index,target.X,target.Y);var input=request.Nodes.ToArray();var result=new NodeEditService(map.Repository).FindPosition(request);
            Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(expected,(result.Data!.X,result.Data.Y));Assert.AreEqual(index,result.Data.NodeOrder);Assert.AreEqual(before,map.Snapshot());CollectionAssert.AreEqual(input,request.Nodes);
        }
    }
    [TestMethod,DataRow("ln-node",127,100),DataRow("ln-diagonal",127,127),DataRow("cross",129,100),DataRow("occupied",129,100),DataRow("overlap",124,100),DataRow("angle",129,100),DataRow("acute",110,95),DataRow("duplicate",110,101)]
    public void NearestPosition_AvoidsEveryKindOfConflict(string kind,int expectedX,int expectedY)
    {
        var map=new TestMap();var line=kind is "acute" or "duplicate"?map.Add(100,100,110,100,110,110):kind=="ln-diagonal"?map.Add(100,100,110,110):map.Add(100,100,110,100);
        var x=140d;var y=100d;
        if(kind=="ln-node"){Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",130.5m,100.5m,6,new())).Success);x=130.4;y=100.2;}
        if(kind=="ln-diagonal"){Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",130.5m,130.5m,6,new())).Success);y=140;}
        if(kind=="cross"){map.Add(125,95,135,105);map.Add(125,105,135,95);map.Calculate();}
        if(kind=="occupied"){map.Add(130,100,130,110);x=130;}
        if(kind=="overlap")map.Add(125,100,135,100);
        if(kind=="angle")map.Add(125,95,135,105);
        if(kind=="acute"){x=105;y=95;}
        if(kind=="duplicate"){x=100;y=100;}
        var before=map.Snapshot();var result=new NodeEditService(map.Repository).FindPosition(Request(line,line.Nodes.Length-1,x,y));Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual((expectedX,expectedY),(result.Data!.X,result.Data.Y));Assert.AreEqual(before,map.Snapshot());
        var nodes=line.Nodes.ToArray();nodes[^1]=nodes[^1] with{X=result.Data.X,Y=result.Data.Y};Assert.IsTrue(map.Lines.CheckShapeIsOKThenSave(new(line.Code,"owner",line.Version,nodes)).Success);
    }
    [TestMethod]
    public void UnfixableOtherNode_FailsWithoutChangingAnyMapData()
    {
        var map=new TestMap();Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",100.5m,100.5m,6,new())).Success);var line=map.Create();
        var draft=TestMap.Shape(line,100,100,110,100,120,100,130,100);var before=map.Snapshot();
        var result=new NodeEditService(map.Repository).FindPosition(Request(line,3,150,100) with{Nodes=draft.Nodes});Assert.AreEqual("NoValidNodePosition",result.Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow("owner","NotOwner"),DataRow("admin","NotOwner"),DataRow("locked","LineIsLocked"),DataRow("version","VersionConflict"),DataRow("index","InvalidNodeOrder"),DataRow("bounds","OutOfBounds"),DataRow("nan","OutOfBounds"),DataRow("node-order","InvalidNode"),DataRow("null-node","InvalidNode"),DataRow("code","InvalidNode"),DataRow("empty","TooFewNodes")]
    public void InvalidRequestsAndAccessFailures_DoNotMutateStoredData(string kind,string expected)
    {
        var map=new TestMap();var line=map.Add(100,100,110,100);var request=Request(line,1,120,100) with{Nodes=line.Nodes.ToArray()};
        if(kind is "owner" or "admin")request=request with{Owner=kind=="admin"?"admin":"other"};
        if(kind=="locked")map.Repository.Transaction(s=>{s.Lines[line.Code]=line with{IsLocked=true};return true;});
        if(kind=="version")request=request with{Version=line.Version-1};
        if(kind=="index")request=request with{NodeOrder=2};
        if(kind=="bounds")request=request with{TargetX=500001};
        if(kind=="nan")request=request with{TargetX=double.NaN};
        if(kind=="node-order")request.Nodes[0]=request.Nodes[0] with{Order=9};
        if(kind=="null-node")request.Nodes[0]=null!;
        if(kind=="code")request.Nodes[0]=request.Nodes[0] with{LineCode="XXXXXXXXXX"};
        if(kind=="empty")request=request with{Nodes=[]};
        var before=map.Snapshot();Assert.AreEqual(expected,new NodeEditService(map.Repository).FindPosition(request).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void ProposalIsAdvisory_F6RechecksNewLnAndVersionAndPerformsLengthRefinement()
    {
        var map=new TestMap();var line=map.Add(100,100,110,100);var service=new NodeEditService(map.Repository);var result=service.FindPosition(Request(line,1,2000,100));Assert.IsTrue(result.Success);
        Assert.AreEqual((2000,100),(result.Data!.X,result.Data.Y));var draft=line.Nodes.ToArray();draft[1]=draft[1] with{X=2000};
        var square=new LogicalNodeService(map.Repository).Create(new("other",2000.5m,100.5m,6,new()));Assert.IsTrue(square.Success);var before=map.Snapshot();
        Assert.AreEqual("LineNodeInsideLogicalNode",map.Lines.CheckShapeIsOKThenSave(new(line.Code,"owner",line.Version,draft)).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.IsTrue(new LogicalNodeService(map.Repository).Delete(new(square.Data!.Code,"other",square.Data.Version)).Success);
        var saved=map.Lines.CheckShapeIsOKThenSave(new(line.Code,"owner",line.Version,draft));Assert.IsTrue(saved.Success);LineSubdivisionTests.AssertSegments(saved.Data!);
        before=map.Snapshot();Assert.AreEqual("VersionConflict",service.FindPosition(Request(line,1,120,100)).Code);Assert.AreEqual(before,map.Snapshot());
    }
}
