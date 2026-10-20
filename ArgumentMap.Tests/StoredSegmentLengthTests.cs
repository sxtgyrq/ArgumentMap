using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class StoredSegmentLengthTests
{
    [TestMethod]
    public void MergeLongConnector_RefinesInPreviewAndCommit_UpdatesCrossAndLnInSameOperation()
    {
        var map=new TestMap();var a=map.Add(0,100,10,100);var b=map.Add(2000,100,2010,100);var other=map.Add(2005,90,2005,110);map.Calculate();
        var cross=map.Between(b,other);cross=map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Road1ToRoad2)).Data!;
        var lns=new LogicalNodeService(map.Repository);var ln=lns.Create(new("owner",1005.5m,100.5m,6,new(){Value="连接段"})).Data!;
        var service=new LineTopologyService(map.Repository);var before=map.Snapshot();var preview=service.GetMergePlans(LineTopologyTests.Request(a,b));Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
        var result=service.Merge(LineTopologyTests.Apply(preview.Data!,preview.Data!.Plans.Single()));Assert.IsTrue(result.Success,result.Message);var merged=result.Data!.Lines.Single();LineSubdivisionTests.AssertSegments(merged);
        Assert.AreEqual(a.Code,merged.Code);Assert.AreEqual(a.Version+1,merged.Version);Assert.IsTrue(merged.Nodes.Length>4);Assert.IsFalse(map.Lines.GetAll().Data!.Any(l=>l.Code==b.Code));
        var current=map.Between(merged,other);Assert.AreEqual(cross.AllowsTransfer(b.Code,other.Code),current.AllowsTransfer(a.Code,other.Code));Assert.AreEqual(cross.Key.Position,current.Key.Position);
        Assert.IsFalse(map.Crosses.GetAll().Data!.Any(c=>c.Key.Contains(b.Code)));
        var node=lns.GetAll().Data!.Single();Assert.AreEqual(ln.Version+1,node.Version);Assert.AreEqual(ln.Properties,node.Properties);Assert.IsTrue(merged.Nodes.All(p=>!node.Contains(p.X,p.Y)));
        var position=node.Positions.Single();Assert.AreEqual(merged.Code,position.RoadCode);Assert.AreEqual(merged.Version,position.RoadVersion);var start=merged.Nodes[position.RoadOrder];var end=merged.Nodes[position.RoadOrder+1];
        Assert.AreEqual(1005.5,start.X+(end.X-start.X)*position.RoadPercent,1e-12);
        before=map.Snapshot();map.Calculate();Assert.IsTrue(lns.Calculate().Success);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void ExtendedTerminalSegmentAlsoRefines_AndChosenEndpointIsPreserved()
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(1000,100,1010,100);
        var square=new LogicalNodeService(map.Repository).Create(new("owner",450.5m,100.5m,6,new())).Data!;
        var result=new LineTopologyService(map.Repository).Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",800,100,1000,100));Assert.IsTrue(result.Success,result.Message);
        var merged=result.Data!.Lines.Single();LineSubdivisionTests.AssertSegments(merged);Assert.IsTrue(merged.Nodes.Any(n=>n.X==800&&n.Y==100));Assert.IsTrue(merged.Nodes.All(n=>!square.Contains(n.X,n.Y)));Assert.AreEqual(447,merged.Nodes[1].X);
    }
    [TestMethod]
    public void SplitHistoricalLongRoad_RefinesBothResultsAndReassignsCrossAndLnLocations()
    {
        var map=new TestMap();var road=TestMap.HistoricalRoad(map.Repository,0,100,1000,100,1010,100,2010,100);
        var left=map.Add(400,90,400,110);var right=map.Add(1600,90,1600,110);map.Calculate();
        var c1=map.Between(road,left);var c2=map.Between(road,right);c1=map.Crosses.Edit(new(c1.Key,"owner",c1.Version,CrossState.Road1ToRoad2)).Data!;c2=map.Crosses.Edit(new(c2.Key,"owner",c2.Version,CrossState.Connected)).Data!;
        var lns=new LogicalNodeService(map.Repository);foreach(var x in new[]{250.5m,1260.5m})Assert.IsTrue(lns.Create(new("owner",x,100.5m,6,new())).Success);
        var result=new LineTopologyService(map.Repository).Split(new(road.Code,"owner",road.Version,2));Assert.IsTrue(result.Success,result.Message);
        var first=result.Data!.Lines[0];var second=result.Data.Lines[1];foreach(var part in result.Data.Lines){LineSubdivisionTests.AssertSegments(part);Assert.AreEqual(3,part.Nodes.Length);}
        Assert.AreEqual(road.Code,first.Code);Assert.AreEqual((0,.8d),map.Between(first,left).LocationOn(first.Code));Assert.AreEqual((1,.18d),map.Between(second,right).LocationOn(second.Code));
        Assert.AreEqual(c1.AllowsTransfer(road.Code,left.Code),map.Between(first,left).AllowsTransfer(first.Code,left.Code));Assert.AreEqual(CrossState.Connected,map.Between(second,right).State);
        foreach(var node in lns.GetAll().Data!){var p=node.Positions.Single();Assert.AreEqual(node.X==250.5m?first.Code:second.Code,p.RoadCode);Assert.AreEqual((0,.501d),(p.RoadOrder,p.RoadPercent));}
        var before=map.Snapshot();map.Calculate();Assert.IsTrue(lns.Calculate().Success);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void ReverseHistoricalLongRoad_CannotPersistAnyOverlongSegment()
    {
        var map=new TestMap();var road=TestMap.HistoricalRoad(map.Repository,0,100,1000,100);var other=map.Add(400,90,400,110);map.Calculate();
        var cross=map.Between(road,other);Assert.IsTrue(map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)).Success);
        var lns=new LogicalNodeService(map.Repository);Assert.IsTrue(lns.Create(new("owner",250.5m,100.5m,6,new())).Success);
        var result=map.Lines.Reverse(new(road.Code,"owner",road.Version));Assert.IsTrue(result.Success,result.Message);LineSubdivisionTests.AssertSegments(result.Data!);CollectionAssert.AreEqual(new[]{1000,500,0},result.Data!.Nodes.Select(n=>n.X).ToArray());
        Assert.AreEqual((1,.499d),(lns.GetAll().Data!.Single().Positions.Single().RoadOrder,lns.GetAll().Data!.Single().Positions.Single().RoadPercent));
        map.Calculate();Assert.AreEqual(cross.Key,map.Between(result.Data,other).Key);Assert.AreEqual(CrossState.Connected,map.Between(result.Data,other).State);Assert.AreEqual((1,.2d),map.Between(result.Data,other).LocationOn(road.Code));
    }
    [TestMethod]
    public void ImpossibleLongConnectorIsNotOffered_CommitFailsWithoutDeletingEitherRoad()
    {
        var map=new TestMap();var a=map.Add(0,100,1,100);var b=map.Add(601,100,602,100);
        map.Repository.Transaction(s=>{for(var x=2;x<=600;x++){var code=LineCode.Generate();s.Lines.Add(code,new(code,"other",false,1,[new(code,0,x,100),new(code,1,x,110)]));}return true;});
        var service=new LineTopologyService(map.Repository);var before=map.Snapshot();Assert.AreEqual("CannotMerge",service.GetMergePlans(LineTopologyTests.Request(a,b)).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual("CannotSubdivideSegment",service.Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",1,100,601,100)).Code);Assert.AreEqual(before,map.Snapshot());
    }
}
