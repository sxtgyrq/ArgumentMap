using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LineTopologyTests
{
    internal static MergeLinesRequest Request(LineSnapshot first,LineSnapshot second)=>new(first.Code,first.Version,second.Code,second.Version,first.Owner);
    internal static ApplyMergeRequest Apply(MergePreview preview,MergePlan plan)=>new(preview.First.Code,preview.First.Version,preview.Second.Code,preview.Second.Version,preview.First.Owner,
        plan.FirstX,plan.FirstY,plan.SecondX,plan.SecondY);
    internal static LineSnapshot Fixed(TestMap map,string code,params int[] xy)
    {
        var created=map.Create();var renamed=created with{Code=code};map.Repository.Transaction(s=>{s.Lines.Remove(created.Code);s.Lines.Add(code,renamed);return true;});return map.Save(renamed,xy);
    }
    [TestMethod,DataRow("alice","bob","alice"),DataRow("bob","alice","alice"),
        DataRow("alice","alice","admin"),DataRow("admin","bob","admin"),DataRow("bob","admin","admin")]
    public void Merge_BothRoadsMustBelongToCurrentUser_InPreviewAndCommit(string firstOwner,string secondOwner,string currentUser)
    {
        var map=new TestMap();var first=map.Save(map.Create(firstOwner),100,100,110,100);
        var second=map.Save(map.Create(secondOwner),130,100,140,100);var service=new LineTopologyService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("NotOwner",service.GetMergePlans(new(first.Code,first.Version,second.Code,second.Version,currentUser)).Code);
        Assert.AreEqual("NotOwner",service.Merge(new(first.Code,first.Version,second.Code,second.Version,currentUser,110,100,130,100)).Code);
        Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow("alice"),DataRow("admin")]
    public void Merge_CurrentUserCanMergeTheirOwnTwoRoads(string owner)
    {
        var map=new TestMap();var first=map.Save(map.Create(owner),100,100,110,100);
        var second=map.Save(map.Create(owner),130,100,140,100);var service=new LineTopologyService(map.Repository);
        var preview=service.GetMergePlans(Request(first,second));Assert.IsTrue(preview.Success,preview.Message);
        var result=service.Merge(Apply(preview.Data!,preview.Data!.Plans.Single()));Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(owner,result.Data!.Lines.Single().Owner);
    }
    [TestMethod,DataRow(true),DataRow(false)]
    public void Merge_OwnershipChangedAfterPreviewIsRecheckedAtCommit(bool changeFirst)
    {
        var map=new TestMap();var first=map.Add(100,100,110,100);var second=map.Add(130,100,140,100);
        var service=new LineTopologyService(map.Repository);var preview=service.GetMergePlans(Request(first,second)).Data!;
        // Keep the version unchanged to isolate the ownership check from optimistic version validation.
        var code=changeFirst?first.Code:second.Code;
        map.Repository.Transaction(s=>{s.Lines[code]=s.Lines[code] with{Owner="other"};return true;});var before=map.Snapshot();
        Assert.AreEqual("NotOwner",service.Merge(Apply(preview,preview.Plans.Single())).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void Split_DistributesCrossSettingsAndLnPositions_RemovesOnlyGapRelations()
    {
        var map=new TestMap();var road=map.Add(0,100,10,100,20,100,30,100,40,100,50,100);
        var lns=new LogicalNodeService(map.Repository);
        foreach(var x in new[]{5.5m,25.5m,35.5m})Assert.IsTrue(lns.Create(new("owner",x,100.5m,6,new())).Success);
        var a=map.Add(15,90,15,110);var b=map.Add(45,90,45,110);var gap=map.Add(25,90,25,110);map.Calculate();
        var oldA=map.Between(road,a);var oldB=map.Between(road,b);
        oldA=map.Crosses.Edit(new(oldA.Key,"owner",oldA.Version,CrossState.Road1ToRoad2)).Data!;
        oldB=map.Crosses.Edit(new(oldB.Key,"owner",oldB.Version,CrossState.Road2ToRoad1)).Data!;
        var beforeNodes=lns.GetAll().Data!.ToDictionary(n=>n.Code);
        var result=new LineTopologyService(map.Repository).Split(new(road.Code,road.Owner,road.Version,3));Assert.IsTrue(result.Success,result.Message);
        var first=result.Data!.Lines[0];var second=result.Data.Lines[1];Assert.AreEqual(road.Code,first.Code);Assert.AreNotEqual(first.Code,second.Code);Assert.IsTrue(LineCode.IsValid(second.Code));
        CollectionAssert.AreEqual(new[]{0,10,20},first.Nodes.Select(n=>n.X).ToArray());CollectionAssert.AreEqual(new[]{30,40,50},second.Nodes.Select(n=>n.X).ToArray());
        Assert.AreEqual(oldA.State,map.Between(first,a).State);
        Assert.AreEqual(oldB.AllowsTransfer(road.Code,b.Code),map.Between(second,b).AllowsTransfer(second.Code,b.Code));
        Assert.AreEqual((1,.5d),map.Between(first,a).LocationOn(first.Code));Assert.AreEqual((1,.5d),map.Between(second,b).LocationOn(second.Code));
        Assert.IsTrue(map.Between(first,a).Version>oldA.Version);Assert.IsTrue(map.Between(second,b).Version>oldB.Version);
        Assert.IsFalse(map.Crosses.GetAll().Data!.Any(c=>c.Key.Contains(gap.Code)));
        var nodes=lns.GetAll().Data!;Assert.AreEqual(3,nodes.Length);
        Assert.AreEqual(first.Code,nodes.Single(n=>n.X==5.5m).Positions.Single().RoadCode);
        Assert.AreEqual(second.Code,nodes.Single(n=>n.X==35.5m).Positions.Single().RoadCode);
        Assert.IsFalse(nodes.Single(n=>n.X==25.5m).Positions.Any(p=>p.RoadCode==first.Code||p.RoadCode==second.Code));
        foreach(var node in nodes){Assert.AreEqual(beforeNodes[node.Code].Version+1,node.Version);Assert.AreEqual(beforeNodes[node.Code].Properties,node.Properties);Assert.IsTrue(node.Positions.All(p=>!p.IsChanged));}
        foreach(var x in new[]{5.5m,35.5m})
        {
            var position=nodes.Single(n=>n.X==x).Positions.Single();Assert.AreEqual((0,.55d),(position.RoadOrder,position.RoadPercent));Assert.AreEqual((x,100m),(position.X,position.Y));
            Assert.AreEqual(x==5.5m?first.Version:second.Version,position.RoadVersion);
        }
        Assert.IsTrue(map.Repository.Transaction(s=>s.PendingSegments.Count==0&&s.StaleCrosses.Count==0&&s.DirtyLines.Count==0));
        var before=map.Snapshot();map.Calculate();Assert.IsTrue(lns.Calculate().Success);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void Split_SelfCrossInsideLnBecomesDisconnectedRelationAndTwoRoadPositions()
    {
        var map=new TestMap();var road=map.Add(100,110,140,110,140,140,110,140,110,100);
        var lns=new LogicalNodeService(map.Repository);Assert.IsTrue(lns.Create(new("owner",110.5m,110.5m,6,new())).Success);
        var result=new LineTopologyService(map.Repository).Split(new(road.Code,"owner",road.Version,2));Assert.IsTrue(result.Success,result.Message);
        var cross=map.Crosses.GetAll().Data!.Single();Assert.AreEqual(CrossState.Disconnected,cross.State);Assert.AreEqual(new CrossPosition(220,220),cross.Key.Position);
        var positions=lns.GetAll().Data!.Single().Positions;Assert.AreEqual(2,positions.Length);Assert.AreEqual(2,positions.Select(p=>p.RoadCode).Distinct().Count());
    }
    [TestMethod]
    public void CrossExactlyAtSplitBoundary_UsesHeadFinalOneAndTailFirstZero()
    {
        var map=new TestMap();var road=map.Add(0,100,10,100,20,100,30,100);var firstOther=map.Add(10,90,10,110);var secondOther=map.Add(20,90,20,110);map.Calculate();
        var firstOld=map.Between(road,firstOther);var secondOld=map.Between(road,secondOther);
        Assert.AreEqual((1,0d),firstOld.LocationOn(road.Code));Assert.AreEqual((2,0d),secondOld.LocationOn(road.Code));
        firstOld=map.Crosses.Edit(new(firstOld.Key,"owner",firstOld.Version,CrossState.Connected)).Data!;
        secondOld=map.Crosses.Edit(new(secondOld.Key,"owner",secondOld.Version,CrossState.Road1ToRoad2)).Data!;
        var result=new LineTopologyService(map.Repository).Split(new(road.Code,"owner",road.Version,2));Assert.IsTrue(result.Success,result.Message);
        var first=result.Data!.Lines[0];var second=result.Data.Lines[1];Assert.AreEqual((0,1d),map.Between(first,firstOther).LocationOn(first.Code));
        var cross=map.Between(second,secondOther);Assert.AreEqual((0,0d),cross.LocationOn(second.Code));Assert.AreEqual(secondOld.AllowsTransfer(road.Code,secondOther.Code),cross.AllowsTransfer(second.Code,secondOther.Code));
        Assert.AreEqual(CrossState.Connected,map.Between(first,firstOther).State);
    }
    [TestMethod,DataRow(0),DataRow(1),DataRow(3),DataRow(-1),DataRow(99)]
    public void Split_RejectsInsufficientPartsWithoutChangingAnything(int cut)
    {
        var map=new TestMap();var road=map.Add(100,100,110,100,120,100,130,100);var before=map.Snapshot();
        Assert.AreEqual("InvalidSplit",new LineTopologyService(map.Repository).Split(new(road.Code,"owner",road.Version,cut)).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void Merge_PreviewIsReadOnly_TransfersCodeOrderDirectionSettingsAndLnPositions()
    {
        var map=new TestMap();var a=Fixed(map,"ZZZZZZZZZZ",100,100,110,100);var b=Fixed(map,"AAAAAAAAAA",120,100,130,100);var other=Fixed(map,"MMMMMMMMMM",125,90,125,110);map.Calculate();
        a=map.Lines.SaveProperties(new(a.Code,"owner",a.Version,a.Properties with{Speed=17,PassInOpposeDirection=1,RoadName="第一条"})).Data!;
        var old=map.Between(b,other);old=map.Crosses.Edit(new(old.Key,"owner",old.Version,CrossState.Road1ToRoad2)).Data!;
        Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",105.5m,100.5m,6,new())).Success);
        var service=new LineTopologyService(map.Repository);var before=map.Snapshot();var preview=service.GetMergePlans(Request(a,b));Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
        var plan=preview.Data!.Plans.Single();Assert.AreEqual(0,plan.AdjustedGridSteps);var result=service.Merge(Apply(preview.Data,plan));Assert.IsTrue(result.Success,result.Message);
        var merged=result.Data!.Lines.Single();Assert.AreEqual(a.Code,merged.Code);Assert.AreEqual(a.Properties,merged.Properties);Assert.IsFalse(map.Repository.Transaction(s=>s.Lines.ContainsKey(b.Code)));
        var cross=map.Between(merged,other);Assert.AreEqual(CrossState.Road2ToRoad1,cross.State);Assert.AreEqual(old.AllowsTransfer(other.Code,b.Code),cross.AllowsTransfer(other.Code,a.Code));
        Assert.AreEqual(2,cross.LocationOn(a.Code).RoadOrder);Assert.AreEqual(0.5,cross.LocationOn(a.Code).Percent);
        Assert.AreEqual(a.Code,new LogicalNodeService(map.Repository).GetAll().Data!.Single().Positions.Single().RoadCode);
    }
    [TestMethod]
    public void Merge_AdjustedEndpointsReassignCrossAndEveryLnPosition_UpdatesVersionsAndIsIdempotent()
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(130,100,140,100,150,100);
        var added=map.Add(115,90,115,110);var bridge=map.Add(132,90,132,110);var retained=map.Add(145,90,145,110);map.Calculate();
        var oldBridge=map.Between(b,bridge);Assert.IsTrue(map.Crosses.Edit(new(oldBridge.Key,"owner",oldBridge.Version,CrossState.Connected)).Success);
        var oldRetained=map.Between(b,retained);Assert.IsTrue(map.Crosses.Edit(new(oldRetained.Key,"owner",oldRetained.Version,CrossState.Disconnected)).Success);
        var lns=new LogicalNodeService(map.Repository);
        foreach(var x in new[]{105.5m,125.5m,145.5m})Assert.IsTrue(lns.Create(new("owner",x,100.5m,6,new(){Value=$"值{x}",Info="保持资料"})).Success);
        var beforeNodes=lns.GetAll().Data!.ToDictionary(n=>n.Code);var unaffected=beforeNodes.Values.Single(n=>n.X==145.5m).Positions.Single(p=>p.RoadCode==retained.Code);
        var result=new LineTopologyService(map.Repository).Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",120,100,135,100));Assert.IsTrue(result.Success,result.Message);
        var merged=result.Data!.Lines.Single();Assert.AreEqual((0,.75d),map.Between(merged,added).LocationOn(a.Code));Assert.AreEqual(CrossState.Unset,map.Between(merged,added).State);
        Assert.AreEqual((1,.8d),map.Between(merged,bridge).LocationOn(a.Code));Assert.AreEqual(CrossState.Connected,map.Between(merged,bridge).State);
        Assert.AreEqual((3,.5d),map.Between(merged,retained).LocationOn(a.Code));Assert.AreEqual(CrossState.Disconnected,map.Between(merged,retained).State);
        var nodes=lns.GetAll().Data!;
        foreach(var node in nodes)
        {
            var old=beforeNodes[node.Code];Assert.AreEqual(old.Version+1,node.Version);Assert.AreEqual(old.Properties,node.Properties);Assert.AreEqual((old.X,old.Y,old.SideLength),(node.X,node.Y,node.SideLength));
            var position=node.Positions.Single(p=>p.RoadCode==a.Code);
            Assert.AreEqual(merged.Version,position.RoadVersion);Assert.IsFalse(position.IsChanged);Assert.AreEqual((node.X,100m),(position.X,position.Y));
            Assert.AreEqual(node.X switch{105.5m=>(0,.275d),125.5m=>(1,PercentPrecision.Normalize((double)(5.5m/15m))),_=>(3,.55d)},(position.RoadOrder,position.RoadPercent));
            Assert.IsFalse(node.Positions.Any(p=>p.RoadCode==b.Code));
        }
        Assert.AreEqual(unaffected,nodes.Single(n=>n.X==145.5m).Positions.Single(p=>p.RoadCode==retained.Code));
        Assert.IsFalse(map.Crosses.GetAll().Data!.Any(c=>c.Key.Contains(b.Code)));
        Assert.IsTrue(map.Repository.Transaction(s=>s.Crosses.Values.All(c=>c.RoadVersion1==s.Lines[c.Key.RoadCode1].Version&&c.RoadVersion2==s.Lines[c.Key.RoadCode2].Version)&&s.PendingSegments.Count==0&&s.StaleCrosses.Count==0&&s.DirtyLines.Count==0));
        var settled=map.Snapshot();map.Calculate();Assert.IsTrue(lns.Calculate().Success);Assert.AreEqual(settled,map.Snapshot());
    }

    [TestMethod]
    public void Merge_ShrinkingBothEndsDeletesLostCrossesAndCreatesNewCross_UpdatesLnPercent()
    {
        var map=new TestMap();var a=map.Add(100,100,140,100);var b=map.Add(160,120,160,160);
        var lostA=map.Add(135,99,135,101);var lostB=map.Add(159,125,161,125);var retained=map.Add(150,150,170,150);var added=map.Add(140,120,150,110);map.Calculate();
        var old=map.Between(b,retained);Assert.IsTrue(map.Crosses.Edit(new(old.Key,"owner",old.Version,CrossState.Disconnected)).Success);
        var lns=new LogicalNodeService(map.Repository);Assert.IsTrue(lns.Create(new("owner",110.5m,100.5m,6,new())).Success);Assert.IsTrue(lns.Create(new("owner",160.5m,150.5m,6,new())).Success);
        var result=new LineTopologyService(map.Repository).Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",130,100,160,130));Assert.IsTrue(result.Success,result.Message);
        var merged=result.Data!.Lines.Single();var crosses=map.Crosses.GetAll().Data!;
        Assert.AreEqual(2,crosses.Length);Assert.IsFalse(crosses.Any(c=>c.Key.Contains(lostA.Code)||c.Key.Contains(lostB.Code)));
        Assert.AreEqual(new CrossPosition(290,230),map.Between(merged,added).Key.Position);Assert.AreEqual(CrossState.Unset,map.Between(merged,added).State);
        Assert.AreEqual(CrossState.Disconnected,map.Between(merged,retained).State);Assert.AreEqual((2,PercentPrecision.Normalize(20d/30)),map.Between(merged,retained).LocationOn(a.Code));
        var positions=lns.GetAll().Data!;
        var left=positions.Single(n=>n.X==110.5m).Positions.Single();Assert.AreEqual((0,.35d),(left.RoadOrder,left.RoadPercent));Assert.AreEqual((110.5m,100m),(left.X,left.Y));
        var right=positions.Single(n=>n.X==160.5m).Positions.Single(p=>p.RoadCode==a.Code);Assert.AreEqual((2,PercentPrecision.Normalize(20.5/30)),(right.RoadOrder,right.RoadPercent));Assert.AreEqual((160m,150.5m),(right.X,right.Y));
        Assert.AreEqual(merged.Version,left.RoadVersion);Assert.AreEqual(merged.Version,right.RoadVersion);
        var settled=map.Snapshot();map.Calculate();Assert.IsTrue(lns.Calculate().Success);Assert.AreEqual(settled,map.Snapshot());
    }

    [TestMethod]
    public void Plans_Prefer135Degrees_IncludeRightAngleAlternativeAndLargeExtensions()
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(110,110,110,120);var service=new LineTopologyService(map.Repository);
        var preview=service.GetMergePlans(Request(a,b));Assert.IsTrue(preview.Success,preview.Message);var plans=preview.Data!.Plans;
        Assert.AreEqual(135,plans[0].FirstCorner);Assert.AreEqual(135,plans[0].SecondCorner);Assert.IsTrue(plans.Any(p=>p.FirstCorner==90||p.SecondCorner==90));
        Assert.AreEqual(plans.Length,plans.Select(p=>(p.DirectionX,p.DirectionY)).Distinct().Count());
        var far=new TestMap();a=far.Add(0,100,10,100);b=far.Add(100,300,100,310);
        var big=new LineTopologyService(far.Repository).GetMergePlans(Request(a,b));Assert.IsTrue(big.Success,big.Message);
        var preferred=big.Data!.Plans[0];Assert.AreEqual(135,preferred.FirstCorner);Assert.IsTrue(preferred.AdjustedGridSteps>12);Assert.IsTrue(preferred.SecondY<b.Nodes[0].Y);
        Assert.IsTrue(new LineTopologyService(far.Repository).Merge(Apply(big.Data,preferred)).Success);
    }
    [TestMethod]
    public void RightAnglesRemainAvailableWhenWorldBoundsExclude135Degrees()
    {
        var map=new TestMap();var a=map.Add(499999,0,500000,0);var b=map.Add(500000,1,499999,1);
        var preview=new LineTopologyService(map.Repository).GetMergePlans(Request(a,b));Assert.IsTrue(preview.Success,preview.Message);
        var plan=preview.Data!.Plans.Single();Assert.AreEqual(90,plan.FirstCorner);Assert.AreEqual(90,plan.SecondCorner);Assert.AreEqual(0,plan.AdjustedGridSteps);
    }
    [TestMethod,DataRow(0,false),DataRow(1,false),DataRow(2,false),DataRow(3,false),DataRow(0,true),DataRow(1,true),DataRow(2,true),DataRow(3,true)]
    public void EachDirectionReturnsMinimumMovement_AgainstAllBoundedEndpointCombinations(int rotation,bool mirror)
    {
        (int X,int Y) Transform(int x,int y){if(mirror)x=500000-x;for(var i=0;i<rotation;i++)(x,y)=(500000-y,x);return(x,y);}
        int[] Coordinates(params int[] xy)=>Enumerable.Range(0,xy.Length/2).SelectMany(i=>{var p=Transform(xy[2*i],xy[2*i+1]);return new[]{p.X,p.Y};}).ToArray();
        var map=new TestMap();var a=map.Add(Coordinates(499996,1,499998,1));var b=map.Add(Coordinates(499998,2,499998,4));
        var preview=new LineTopologyService(map.Repository).GetMergePlans(Request(a,b));Assert.IsTrue(preview.Success,preview.Message);
        var expected=new Dictionary<(int,int),int>();
        for(var x=499997;x<=500000;x++)for(var y=0;y<=3;y++)
        {
            var isolated=new TestMap();var line=isolated.Create();var shape=TestMap.Shape(line,Coordinates(499996,1,x,1,499998,y,499998,4));
            if(!isolated.Lines.CheckShapeIsOKThenSave(shape).Success)continue;
            var p=Transform(x,1);var q=Transform(499998,y);var direction=(Math.Sign(q.X-p.X),Math.Sign(q.Y-p.Y));var cost=Math.Abs(x-499998)+Math.Abs(y-2);
            if(!expected.TryGetValue(direction,out var previous)||cost<previous)expected[direction]=cost;
        }
        Assert.AreEqual(expected.Count,preview.Data!.Plans.Length);
        foreach(var plan in preview.Data.Plans)Assert.AreEqual(expected[(plan.DirectionX,plan.DirectionY)],plan.AdjustedGridSteps);
    }
    [TestMethod,DataRow(0),DataRow(1),DataRow(2),DataRow(3)]
    public void DiagonalSourceRays_RespectIntegerParityAndReturnEveryLegalDirection(int rotation)
    {
        (int X,int Y) Transform(int x,int y){for(var i=0;i<rotation;i++)(x,y)=(500000-y,x);return(x,y);}
        int[] Coordinates(params int[] xy)=>Enumerable.Range(0,xy.Length/2).SelectMany(i=>{var p=Transform(xy[2*i],xy[2*i+1]);return new[]{p.X,p.Y};}).ToArray();
        var map=new TestMap();var a=map.Add(Coordinates(499996,499996,499998,499998));var b=map.Add(Coordinates(499999,499997,499997,499999));
        var preview=new LineTopologyService(map.Repository).GetMergePlans(Request(a,b));Assert.IsTrue(preview.Success,preview.Message);var expected=new Dictionary<(int,int),int>();
        for(var t=1;t<=4;t++)for(var v=1;v<=3;v++)
        {
            var x=499996+t;var y=499996+t;var qx=499997+v;var qy=499999-v;
            var test=new TestMap();var line=test.Create();if(!test.Lines.CheckShapeIsOKThenSave(TestMap.Shape(line,Coordinates(499996,499996,x,y,qx,qy,499997,499999))).Success)continue;
            var p=Transform(x,y);var q=Transform(qx,qy);var direction=(Math.Sign(q.X-p.X),Math.Sign(q.Y-p.Y));var cost=Math.Abs(t-2)+Math.Abs(v-2);
            if(!expected.TryGetValue(direction,out var old)||cost<old)expected[direction]=cost;
        }
        Assert.AreEqual(expected.Count,preview.Data!.Plans.Length);foreach(var plan in preview.Data.Plans)Assert.AreEqual(expected[(plan.DirectionX,plan.DirectionY)],plan.AdjustedGridSteps);
    }
    [TestMethod]
    public void LnValidation_RejectsInteriorNodesAndDiagonalPassage_OffersCornerTangentAlternative()
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(130,120,130,130);
        Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",120.5m,110.5m,6,new())).Success);
        var service=new LineTopologyService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("DiagonalInsideLogicalNode",service.Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",110,100,130,120)).Code);Assert.AreEqual(before,map.Snapshot());
        var preview=service.GetMergePlans(Request(a,b));Assert.IsTrue(preview.Success,preview.Message);
        var diagonal=preview.Data!.Plans.Single(p=>p.DirectionX==1&&p.DirectionY==1);Assert.AreEqual(12,diagonal.AdjustedGridSteps);
        Assert.IsTrue(service.Merge(Apply(preview.Data,diagonal)).Success);
        var secondMap=new TestMap();a=secondMap.Add(100,100,110,100);b=secondMap.Add(130,100,140,100);
        Assert.IsTrue(new LogicalNodeService(secondMap.Repository).Create(new("owner",120.5m,100.5m,6,new())).Success);before=secondMap.Snapshot();
        Assert.AreEqual("LineNodeInsideLogicalNode",new LineTopologyService(secondMap.Repository).Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",120,100,130,100)).Code);Assert.AreEqual(before,secondMap.Snapshot());
        var allowed=new LineTopologyService(secondMap.Repository).GetMergePlans(Request(a,b));Assert.IsTrue(allowed.Success);Assert.IsTrue(new LineTopologyService(secondMap.Repository).Merge(Apply(allowed.Data!,allowed.Data!.Plans.Single())).Success);
        Assert.AreEqual(1,new LogicalNodeService(secondMap.Repository).GetAll().Data!.Single().Positions.Length);
    }
    [TestMethod,DataRow("cross"),DataRow("overlap"),DataRow("angle"),DataRow("occupied")]
    public void MergeCommit_RevalidatesWholeResultAndRollsBackInvalidBridge(string kind)
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(130,100,140,100);
        if(kind=="cross"){map.Add(115,95,125,105);map.Add(115,105,125,95);map.Calculate();}
        if(kind=="overlap")map.Add(115,100,125,100);
        if(kind=="angle")map.Add(115,95,125,105);
        if(kind=="occupied")map.Add(120,100,120,110);
        var before=map.Snapshot();var result=new LineTopologyService(map.Repository).Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",kind=="occupied"?120:110,100,130,100));
        Assert.AreEqual(kind switch{"cross"=>"LinePassesCross","overlap"=>"SegmentOverlap","angle"=>"NonOrthogonalIntersection",_=>"PointIsOccupied"},result.Code);Assert.AreEqual(before,map.Snapshot());
        if(kind=="overlap")Assert.AreEqual("CannotMerge",new LineTopologyService(map.Repository).GetMergePlans(Request(a,b)).Code);
    }
    [TestMethod,DataRow(100,100),DataRow(109,101),DataRow(500001,100),DataRow(90,100)]
    public void SelectedEndpointCannotBeOffRayBeyondAnchorOrOutsideWorld(int x,int y)
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(130,100,140,100);var before=map.Snapshot();
        Assert.AreEqual("InvalidEndpointAdjustment",new LineTopologyService(map.Repository).Merge(new(a.Code,a.Version,b.Code,b.Version,"owner",x,y,130,100)).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void PreviewBecomesStaleAfterOtherUserRoadChange_CommitCannotOverwrite()
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(130,100,140,100);var service=new LineTopologyService(map.Repository);
        var preview=service.GetMergePlans(Request(a,b)).Data!;b=map.Save(b,130,101,140,101);var before=map.Snapshot();
        Assert.AreEqual("VersionConflict",service.Merge(Apply(preview,preview.Plans.Single())).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void NewThirdPartyCrossAfterPreview_IsCaughtAtCommitAndLeavesEveryRecordUnchanged()
    {
        var map=new TestMap();var a=map.Add(100,100,110,100);var b=map.Add(130,120,130,130);var service=new LineTopologyService(map.Repository);
        var preview=service.GetMergePlans(Request(a,b)).Data!;var plan=preview.Plans.Single(p=>p.DirectionX==1&&p.DirectionY==1);
        map.Add(115,110,125,110);map.Add(120,105,120,115);map.Calculate();var before=map.Snapshot();
        Assert.AreEqual("LinePassesCross",service.Merge(Apply(preview,plan)).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void ReturnedPreviewAndSplitArrays_CannotMutateStoredRoads()
    {
        var map=new TestMap();var a=map.Add(100,100,110,100,120,100,130,100);var b=map.Add(140,100,150,100);var service=new LineTopologyService(map.Repository);
        var before=map.Snapshot();var preview=service.GetMergePlans(Request(a,b)).Data!;preview.First.Nodes[0]=preview.First.Nodes[0] with{X=101};Assert.AreEqual(before,map.Snapshot());
        var split=service.Split(new(a.Code,"owner",a.Version,2)).Data!;before=map.Snapshot();split.Lines[0].Nodes[0]=split.Lines[0].Nodes[0] with{X=102};Assert.AreEqual(before,map.Snapshot());
    }
}
