using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class MapAreaMoveTests
{
    internal static readonly MapAreaBounds Bounds = new(10,10,90,90);
    internal static MapAreaSelection Select(TestMap map, MapAreaBounds? bounds=null)
    {
        var result=new MapAreaMoveService(map.Repository).Select(new("owner",bounds??Bounds));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    internal static ServiceResult<MapAreaSnapshot> Move(TestMap map,MapAreaSelection selection,int x=100,int y=100)=>
        new MapAreaMoveService(map.Repository).Move(new("owner",selection,x,y));
    internal static LogicalNodeSnapshot Node(TestMap map,decimal x=50.5m,decimal y=50.5m,int size=6)
    {
        var result=new LogicalNodeService(map.Repository).Create(new("owner",x,y,size,new(){Value=size==4?"False":size==8?"=":"值",Info="说明",LogicLanguage=size==4?"":size==8?"=":size==6?"Fixture_"+LineCode.Generate():"S ∧ ¬P"}));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    internal static (LineSnapshot A,LineSnapshot B,CrossSnapshot Cross) Pair(TestMap map,CrossState state)
    {
        var a=map.Add(20,50,80,50);var b=map.Add(50,20,50,80);map.Calculate();var cross=map.Between(a,b);
        if(state!=CrossState.Unset) {var edit=map.Crosses.Edit(new(cross.Key,"owner",cross.Version,state));Assert.IsTrue(edit.Success,edit.Message);cross=edit.Data!;}
        return(a,b,cross);
    }
    private static void RejectUnchanged(TestMap map,MapAreaSelection selected,string code,int dx=100,int dy=100)
    {var before=map.Snapshot();var result=Move(map,selected,dx,dy);Assert.AreEqual(code,result.Code,result.Message);Assert.AreEqual(before,map.Snapshot());}

    [TestMethod]
    public void SelectRequiresCompleteRoadsAndSquares_SkipsCrossMissingWholeRoads()
    {
        var map=new TestMap();Pair(map,CrossState.Disconnected);Node(map);Node(map,88.5m,30.5m);map.Add(10,85,200,85);map.Calculate();
        var selected=Select(map);Assert.AreEqual(2,selected.Lines.Length);Assert.AreEqual(1,selected.LogicalNodes.Length);Assert.AreEqual(1,selected.Crosses.Length);
        var before=map.Snapshot();var incomplete=new MapAreaMoveService(map.Repository).Select(new("owner",new(15,40,85,60)));
        Assert.IsTrue(incomplete.Success,incomplete.Message);Assert.AreEqual(1,incomplete.Data!.Lines.Length);
        Assert.AreEqual(1,incomplete.Data.LogicalNodes.Length);Assert.AreEqual(0,incomplete.Data.Crosses.Length);
        StringAssert.Contains(incomplete.Message,"已跳过 1 个");Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual("EmptySelection",new MapAreaMoveService(map.Repository).Select(new("owner",new(300,300,310,310))).Code);
    }

    [TestMethod,DataRow(-1),DataRow(0),DataRow(1),DataRow(2),DataRow(3)]
    public void TranslationPreservesCrossSettingsCodesOrderAndPercent_UpdatesVersionsAndClearsOldCoordinates(int state)
    {
        var map=new TestMap();var pair=Pair(map,(CrossState)state);var selected=Select(map);
        var moved=Move(map,selected);Assert.IsTrue(moved.Success,moved.Message);
        foreach(var road in selected.Lines)
        {
            var after=moved.Data!.Lines.Single(l=>l.Code==road.Code);Assert.AreEqual(road.Version+1,after.Version);
            Assert.AreEqual(road.Properties,after.Properties);Assert.AreEqual(road.Owner,after.Owner);
            CollectionAssert.AreEqual(road.Nodes.Select(n=>n with{X=n.X+100,Y=n.Y+100}).ToArray(),after.Nodes);
        }
        var cross=moved.Data!.Crosses.Single();Assert.AreEqual((CrossState)state,cross.State);Assert.AreEqual(pair.Cross.Version+1,cross.Version);
        Assert.AreEqual(pair.Cross.Key.RoadCode1,cross.Key.RoadCode1);Assert.AreEqual(pair.Cross.Key.RoadCode2,cross.Key.RoadCode2);
        Assert.AreEqual(new CrossPosition(300,300),cross.Key.Position);Assert.AreEqual(pair.Cross.Percent1,cross.Percent1);Assert.AreEqual(pair.Cross.Percent2,cross.Percent2);
        Assert.AreEqual(pair.Cross.RoadOrder1,cross.RoadOrder1);Assert.AreEqual(pair.Cross.RoadOrder2,cross.RoadOrder2);
        map.Repository.Transaction(s=>{Assert.AreEqual(0,s.DirtyLines.Count);Assert.AreEqual(0,s.PendingSegments.Count);Assert.AreEqual(0,s.StaleCrosses.Count);return true;});
        var before=map.Snapshot();map.Calculate();Assert.AreEqual(before,map.Snapshot(),"A later K must not reset the recovered setting.");
    }

    [TestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(12),DataRow(18)]
    public void WholeGroupMovesTogether_RebuildsAllLnPositionsAndPreservesMetadata(int size)
    {
        var map=new TestMap();Pair(map,CrossState.Disconnected);var node=Node(map,size:size);var selected=Select(map);
        var result=Move(map,selected);Assert.IsTrue(result.Success,result.Message);var after=result.Data!.LogicalNodes.Single();
        Assert.AreEqual(node.Code,after.Code);Assert.AreEqual(node.Owner,after.Owner);Assert.AreEqual(node.Properties,after.Properties);Assert.AreEqual(size,after.SideLength);
        Assert.AreEqual(node.Version+1,after.Version);Assert.AreEqual(150.5m,after.X);Assert.AreEqual(150.5m,after.Y);Assert.AreEqual(2,after.Positions.Length);
        foreach(var old in node.Positions)
        {
            var p=after.Positions.Single(p=>p.RoadCode==old.RoadCode&&p.RoadOrder==old.RoadOrder);
            Assert.AreEqual(old.X+100,p.X);Assert.AreEqual(old.Y+100,p.Y);Assert.AreEqual(old.RoadPercent,p.RoadPercent);Assert.AreEqual(old.RoadVersion+1,p.RoadVersion);Assert.IsFalse(p.IsChanged);
        }
    }

    [TestMethod]
    public void SelfIntersectionNeverCreatesCross_OutgoingNodeUsesHigherOrderAndZero()
    {
        var map=new TestMap();var a=map.Add(20,50,50,50,80,50);var b=map.Add(50,20,50,80);map.Calculate();
        var result=Move(map,Select(map));Assert.IsTrue(result.Success,result.Message);Assert.AreEqual((1,0d),result.Data!.Crosses.Single().LocationOn(a.Code));
        map=new();map.Add(20,50,80,50,80,80,50,80,50,20);map.Calculate();result=Move(map,Select(map));Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(0,result.Data!.Crosses.Length);
    }

    [TestMethod,DataRow("node"),DataRow("overlap"),DataRow("diagonal"),DataRow("third")]
    public void ExternalRoadConflictsRollbackWholeGroup(string kind)
    {
        var map=new TestMap();map.Add(20,50,80,50);map.Calculate();var selected=Select(map);
        switch(kind)
        {
            case "node":map.Add(120,150,120,180);break;
            case "overlap":map.Add(130,150,170,150);break;
            case "diagonal":map.Add(140,140,160,160);break;
            default:map.Add(140,140,160,160);map.Add(140,160,160,140);break;
        }
        map.Calculate();RejectUnchanged(map,selected,kind switch{"node"=>"PointIsOccupied","overlap"=>"SegmentOverlap","diagonal"=>"NonOrthogonalIntersection",_=>"LinePassesCross"});
    }

    [TestMethod,DataRow("ln"),DataRow("node"),DataRow("diagonal"),DataRow("crossUnset"),DataRow("crossConnected"),DataRow("crossMissing")]
    public void DestinationLnConstraintsRollbackEntireMove(string kind)
    {
        var map=new TestMap();Node(map);map.Calculate();var selected=Select(map);
        if(kind=="ln")Node(map,156.5m,150.5m);
        if(kind=="node")map.Add(150,150,150,180);
        if(kind=="diagonal")map.Add(140,140,160,160);
        if(kind.StartsWith("cross"))
        {
            map.Add(120,150,180,150);map.Add(150,120,150,180);
            if(kind!="crossMissing")map.Calculate();
            if(kind=="crossConnected") {var c=map.Crosses.GetAll().Data!.Single();Assert.IsTrue(map.Crosses.Edit(new(c.Key,"owner",c.Version,CrossState.Connected)).Success);}
        }
        RejectUnchanged(map,selected,kind switch{"ln"=>"LogicalNodesTouchOrOverlap","node"=>"LineNodeInsideLogicalNode","diagonal"=>"DiagonalInsideLogicalNode",_=>"LogicalNodeCrossNotDisconnected"});
    }

    [TestMethod,DataRow(0),DataRow(1),DataRow(2)]
    public void LnOverlapEdgeAndCornerContactAllReject(int contact)
    {var map=new TestMap();Node(map);var selected=Select(map);Node(map,contact==0?150.5m:156.5m,contact==2?156.5m:150.5m);RejectUnchanged(map,selected,"LogicalNodesTouchOrOverlap");}

    [TestMethod]
    public void NewCrossInsideExistingLnIsDisconnected_OutsideIsUnset_UpdatesExternalLnPositions()
    {
        foreach(var withNode in new[]{false,true})
        {
            var map=new TestMap();map.Add(20,50,80,50);map.Add(150,120,150,180);map.Calculate();
            var node=withNode?Node(map,150.5m,150.5m):null;var selected=Select(map);
            var result=Move(map,selected);Assert.IsTrue(result.Success,result.Message);
            Assert.AreEqual(withNode?CrossState.Disconnected:CrossState.Unset,result.Data!.Crosses.Single().State);
            if(node is not null){var after=result.Data.LogicalNodes.Single();Assert.AreEqual(node.Version+1,after.Version);Assert.AreEqual(2,after.Positions.Length);Assert.AreEqual(node.X,after.X);}
        }
    }

    [TestMethod]
    public void ExternalLnLosesOldRoadProjection_AndUnrelatedPendingWorkIsUntouched()
    {
        var map=new TestMap();map.Add(20,50,80,50);map.Calculate();var node=Node(map); // Select road without entire square: cannot with full road bounds; use narrow Y bounds.
        var selected=Select(map,new(19,49,81,51));var pending=map.Add(300,300,330,300);
        var result=Move(map,selected);Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(0,result.Data!.LogicalNodes.Single().Positions.Length);Assert.AreEqual(node.Version+1,result.Data.LogicalNodes.Single().Version);
        map.Repository.Transaction(s=>{Assert.IsTrue(s.DirtyLines.SetEquals([pending.Code]));Assert.AreEqual(1,s.PendingSegments.Count);return true;});
    }

    [TestMethod,DataRow("road"),DataRow("ln"),DataRow("cross"),DataRow("insert"),DataRow("delete")]
    public void SourceChangedAfterSelectionRejects_NoOverwriteOfOtherUserChanges(string kind)
    {
        var map=new TestMap();var pair=Pair(map,CrossState.Disconnected);var node=kind=="cross"?null:Node(map);var selected=Select(map);
        switch(kind)
        {
            case "road":Assert.IsTrue(map.Lines.SaveProperties(new(pair.A.Code,"owner",pair.A.Version,pair.A.Properties with{Speed=40})).Success);break;
            case "ln":Assert.IsTrue(new LogicalNodeService(map.Repository).Update(new(node!.Code,"owner",node.Version,"新值","新说明")).Success);break;
            case "cross":Assert.IsTrue(map.Crosses.Edit(new(pair.Cross.Key,"owner",pair.Cross.Version,CrossState.Connected)).Success);break;
            case "insert":Node(map,30.5m,30.5m);break;
            case "delete":Assert.IsTrue(new LogicalNodeService(map.Repository).Delete(new(node!.Code,"owner",node.Version)).Success);break;
        }
        RejectUnchanged(map,selected,"SelectionChanged");
    }

    [TestMethod]
    public void PendingKAllowsSelection_PermissionsLocksAndMalformedSelectionsReject_NoOpDoesNotMutate()
    {
        var map=new TestMap();var a=map.Add(20,50,80,50);var service=new MapAreaMoveService(map.Repository);
        var pending=map.Snapshot();Assert.IsTrue(service.Select(new("owner",Bounds)).Success);Assert.AreEqual(pending,map.Snapshot());map.Calculate();var selected=Select(map);
        Assert.AreEqual("NotOwner",service.Select(new("other",Bounds)).Code);
        var before=map.Snapshot();Assert.IsTrue(Move(map,selected,0,0).Success);Assert.AreEqual(before,map.Snapshot());
        RejectUnchanged(map,selected with{Lines=[selected.Lines[0] with{Code=null!}]},"SelectionChanged");
        RejectUnchanged(map,selected with{Lines=[selected.Lines[0],selected.Lines[0]]},"SelectionChanged");
        RejectUnchanged(map,selected with{LogicalNodes=null!},"InvalidSelection");
        RejectUnchanged(map,selected,"OutOfBounds",-100,0);
        map.Repository.Transaction(s=>{s.Lines[a.Code]=a with{IsLocked=true};return true;});Assert.AreEqual("LineIsLocked",service.Select(new("owner",Bounds)).Code);
    }

    [TestMethod]
    public void ClientGeometryIsIgnored_ServerTranslatesAuthoritativeSnapshots()
    {
        var map=new TestMap();var a=map.Add(20,50,80,50);map.Calculate();var selected=Select(map);
        selected=selected with{Lines=[a with{Nodes=[],Owner="hacker",Properties=new(){Speed=999}}]};
        var result=Move(map,selected);Assert.IsTrue(result.Success,result.Message);var line=result.Data!.Lines.Single();
        Assert.AreEqual(120,line.Nodes[0].X);Assert.AreEqual(a.Properties,line.Properties);Assert.AreEqual("owner",line.Owner);
    }

    [TestMethod]
    public void PerpendicularDiagonalRoadsKeepHalfIntegerCrossAndRoadLength_AfterNegativeTranslation()
    {
        var map=new TestMap();var a=map.Add(20,20,81,81);var b=map.Add(20,81,81,20);map.Calculate();var original=map.Between(a,b);
        Assert.AreEqual(new CrossPosition(101,101),original.Key.Position);
        Assert.IsTrue(map.Crosses.Edit(new(original.Key,"owner",original.Version,CrossState.Road2ToRoad1)).Success);
        var result=Move(map,Select(map),-10,50);Assert.IsTrue(result.Success,result.Message);var cross=result.Data!.Crosses.Single();
        Assert.AreEqual(new CrossPosition(81,201),cross.Key.Position);Assert.AreEqual(CrossState.Road2ToRoad1,cross.State);
        foreach(var road in result.Data.Lines){Assert.AreEqual(2,road.Nodes.Length);Assert.AreEqual(61,Math.Abs(road.Nodes[1].X-road.Nodes[0].X));Assert.AreEqual(61,Math.Abs(road.Nodes[1].Y-road.Nodes[0].Y));}
    }

    [TestMethod]
    public void TranslatedCrossOccupyingAnotherOldKey_RecoversItsOwnSettingAndCleansOnlyObsoleteKeys()
    {
        var map=new TestMap();map.Add(20,50,80,50);map.Add(40,20,40,80,60,80,60,20);map.Calculate();
        foreach(var c in map.Crosses.GetAll().Data!)Assert.IsTrue(map.Crosses.Edit(new(c.Key,"owner",c.Version,c.Key.Position.X==40?CrossState.Connected:CrossState.Road2ToRoad1)).Success);
        var result=Move(map,Select(map),20,0);Assert.IsTrue(result.Success,result.Message);var crosses=result.Data!.Crosses;
        Assert.AreEqual(2,crosses.Length);Assert.AreEqual(CrossState.Connected,crosses.Single(c=>c.Key.Position.X==60).State);Assert.AreEqual(CrossState.Road2ToRoad1,crosses.Single(c=>c.Key.Position.X==80).State);
        Assert.IsFalse(crosses.Any(c=>c.Key.Position.X==40));Assert.AreEqual(0,map.Repository.Transaction(s=>s.StaleCrosses.Count));
    }

    [TestMethod]
    public void MoveCleansUnrecoveredGroupCross_LeavesUnrelatedCrossAndPendingFlagsUntouched()
    {
        var map=new TestMap();var pair=Pair(map,CrossState.Connected);var external=map.Add(300,310,330,310);map.Add(315,300,315,330);map.Calculate();
        Assert.IsTrue(map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(external,300,310,335,310)).Success);
        // Model a leftover positive record from legacy data: it no longer lies on its roads.
        var obsolete=pair.Cross.Key with{Position=new(60,60)};
        map.Repository.Transaction(s=>{s.Crosses.Add(obsolete,pair.Cross with{Key=obsolete});return true;});
        var expected=map.Repository.Transaction(s=>(Stale:s.StaleCrosses.ToHashSet(),Dirty:s.DirtyLines.ToHashSet(),Pending:s.PendingSegments.ToHashSet(),Cross:s.Crosses.Values.Single(c=>c.Key.Contains(external.Code))));
        var result=Move(map,Select(map));Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(2,result.Data!.Crosses.Length);
        map.Repository.Transaction(s=>{Assert.IsTrue(s.StaleCrosses.SetEquals(expected.Stale));Assert.IsTrue(s.DirtyLines.SetEquals(expected.Dirty));Assert.IsTrue(s.PendingSegments.SetEquals(expected.Pending));Assert.AreEqual(expected.Cross,s.Crosses[expected.Cross.Key]);Assert.IsFalse(s.Crosses.ContainsKey(obsolete));return true;});
    }
}
