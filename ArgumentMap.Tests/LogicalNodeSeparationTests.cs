using System.Net.Http;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeSeparationTests
{
    [TestMethod,DataRow(4,4),DataRow(4,6),DataRow(4,12),DataRow(4,18),DataRow(6,4),DataRow(12,4),DataRow(18,4),DataRow(6,6),DataRow(6,12),DataRow(6,18),DataRow(12,6),DataRow(12,12),DataRow(12,18),DataRow(18,6),DataRow(18,12),DataRow(18,18)]
    public void AllSizePairsRejectInteriorEdgeAndCornerContact_AllowOneUnitGap(int firstSize,int secondSize)
    {
        var first=new LogicalNodeSnapshot("ABCDEFGHIJ","owner",0,100.5m,100.5m,firstSize,new());
        var reach=(firstSize+secondSize)/2;
        var cases=new List<(int X,int Y,bool Touch)>{(0,0,true),(reach-1,reach-1,true),(reach-1,0,true)};
        foreach(var dx in new[]{-1,0,1})foreach(var dy in new[]{-1,0,1})
        {
            if(dx==0&&dy==0)continue;
            cases.Add((dx*reach,dy*reach,true));
            cases.Add((dx*(reach+1),dy*(reach+1),false));
        }
        foreach(var (x,y,touch) in cases)
        {
            var second=new LogicalNodeSnapshot("ZYXWVUTSRQ","other",0,100.5m+x,100.5m+y,secondSize,new());
            Assert.AreEqual(touch,first.TouchesOrOverlaps(second),$"Sizes {firstSize}/{secondSize}; offset {x},{y}");
            Assert.AreEqual(touch,second.TouchesOrOverlaps(first),"Containment and contact must be symmetric.");
        }
    }
    [TestMethod,DataRow(4,4),DataRow(4,6),DataRow(4,12),DataRow(4,18),DataRow(6,4),DataRow(12,4),DataRow(18,4),DataRow(6,6),DataRow(6,12),DataRow(6,18),DataRow(12,6),DataRow(12,12),DataRow(12,18),DataRow(18,6),DataRow(18,12)]
    public void CreateRejectsCompleteOverlapContainmentAndContactWithoutChangingStoredNodesOrPositions(int firstSize,int secondSize)
    {
        var map=new TestMap();map.Add(70,100,140,100);var service=new LogicalNodeService(map.Repository);
        var first=service.Create(new("owner",100.5m,100.5m,firstSize,new())).Data!;Assert.AreEqual(1,first.Positions.Length);
        var reach=(firstSize+secondSize)/2;
        foreach(var (x,y) in new[]{(0,0),(reach-1,0),(reach,0),(reach,reach)})
        {
            var before=map.Snapshot();var result=service.Create(new("other",100.5m+x,100.5m+y,secondSize,new()));
            Assert.AreEqual("LogicalNodesTouchOrOverlap",result.Code);Assert.AreEqual(before,map.Snapshot());
            StringAssert.Contains(result.Message,first.Code);StringAssert.Contains(result.Message,"边和顶点");
        }
        Assert.IsTrue(service.Create(new("other",101.5m+reach,100.5m,secondSize,new(){Value=secondSize==4?"False":"不接触的节点"})).Success);
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public void BoundaryCoordinatesAreExactAndDeletionReleasesTheSquare(bool upper)
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);var center=upper?499990.5m:9.5m;
        var first=service.Create(new("owner",center,center,18,new())).Data!;
        var direction=upper?-1:1;
        Assert.AreEqual("LogicalNodesTouchOrOverlap",service.Create(new("owner",center+direction*12,center,6,new())).Code);
        Assert.IsTrue(service.Create(new("owner",center+direction*13,center,6,new())).Success);
        Assert.IsTrue(service.Delete(new(first.Code,first.Owner,first.Version)).Success);
        Assert.IsTrue(service.Create(new("owner",center,center,18,new())).Success);
    }
    [TestMethod]
    public void LegacyTouchingNodesBlockF6KAndJ_ButCanBeDeletedToRepairTheMap()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);service.Create(new("owner",100.5m,100.5m,6,new()));
        var injected=new LogicalNodeSnapshot("ABCDEFGHIJ","owner",0,106.5m,106.5m,6,new(){LogicLanguage="Injected"});
        map.Repository.Transaction(s=>{s.LogicalNodes.Add(injected.Code,injected);return true;});
        var road=map.Create();var before=map.Snapshot();
        Assert.AreEqual("LogicalNodesTouchOrOverlap",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(road,200,200,220,200)).Code);
        Assert.AreEqual("LogicalNodesTouchOrOverlap",map.Crosses.Calculate().Code);Assert.AreEqual("LogicalNodesTouchOrOverlap",service.Calculate().Code);Assert.AreEqual(before,map.Snapshot());
        Assert.IsTrue(service.Delete(new(injected.Code,injected.Owner,injected.Version)).Success);
        Assert.IsTrue(service.Calculate().Success);Assert.IsTrue(map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(road,200,200,220,200)).Success);
    }
    [STATestMethod,DataRow(false),DataRow(true)]
    public void DirectAddRejectsContactWithCachedOrServerOnlyNode_AndKeepsPlacementForRetry(bool cached)
    {
        var handler=new DirectLogicalNodeHttpStub();var first=new LogicalNodeService(handler.Repository).Create(new("other",110.5m,110.5m,6,new())).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var map=new CoordinateMapView();map.ConfigureClient(new HttpLineClient(http,new("owner","test")));
        if(cached)Assert.IsTrue(map.RefreshLogicalNodesAsync().GetAwaiter().GetResult());
        Assert.IsTrue(map.BeginLogicalNodePlacement(6));var windows=0;map.LogicalNodeWindowRequested+=(_,_)=>windows++;
        var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(116.2,116.7)).GetAwaiter().GetResult());
        Assert.AreEqual(cached?0:1,handler.Creates);Assert.AreEqual(cached?1:0,map.LogicalNodeCount);Assert.AreEqual(0,windows);
        Assert.IsTrue(map.IsPlacingLogicalNode);StringAssert.Contains(map.OperationMessage,first.Code);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
        Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(117.2,116.7)).GetAwaiter().GetResult());Assert.IsTrue(map.IsPlacingLogicalNode);
        Assert.AreEqual(2,new LogicalNodeService(handler.Repository).GetAll().Data!.Length);
    }
}
