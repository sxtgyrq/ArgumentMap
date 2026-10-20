using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeReviewTests
{
    private static LogicalNodeSnapshot Create(TestMap map,decimal x=110.5m,decimal y=110.5m,int size=6)
    {
        var result=new LogicalNodeService(map.Repository).Create(new("owner",x,y,size,new()));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    private static LogicalNodeSnapshot Read(TestMap map)=>new LogicalNodeService(map.Repository).GetAll().Data!.Single();

    [TestMethod,DataRow("null"),DataRow("value-null"),DataRow("info-null"),DataRow("value-long"),DataRow("info-long")]
    public void InvalidProperties_LeaveNodesPositionsAndRoadFlagsUnchanged(string testCase)
    {
        var map=new TestMap();map.Add(90,110,130,110);var before=map.Snapshot();
        LogicalNodeProperties? properties=testCase switch
        {
            "null"=>null,"value-null"=>new(){Value=null!},"info-null"=>new(){Info=null!},
            "value-long"=>new(){Value=new string('中',33)},_=>new(){Info=new string('文',256)}
        };
        var result=new LogicalNodeService(map.Repository).Create(new("owner",110.5m,110.5m,6,properties!));
        Assert.IsFalse(result.Success);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public void PropertyBoundariesAndResponseCopies_DoNotModifyCommittedPositions(bool empty)
    {
        var map=new TestMap();map.Add(90,110,130,110);var service=new LogicalNodeService(map.Repository);
        var properties=new LogicalNodeProperties{Value=empty?"":new string('中',32),Info=empty?"":new string('文',255),LogicLanguage="P"};
        var result=service.Create(new("owner",110.5m,110.5m,6,properties));Assert.IsTrue(result.Success,result.Message);
        var original=Read(map);var expected=original.Positions.Single();
        result.Data!.Positions[0]=expected with{RoadPercent=0};original.Positions[0]=expected with{X=1};
        Assert.AreEqual(expected,Read(map).Positions.Single());Assert.AreEqual(properties,Read(map).Properties);
    }
    [TestMethod,DataRow(6,false),DataRow(6,true),DataRow(12,false),DataRow(12,true),DataRow(18,false),DataRow(18,true)]
    public void DenseAxisGridAtMapEdges_RecordsEveryPositionAndDisconnectsEveryCross(int size,bool maximum)
    {
        var map=new TestMap();var center=maximum?500000m-size/2m-0.5m:size/2m+0.5m;var node=Create(map,center,center,size);
        var start=(int)decimal.Floor(node.Left);var end=(int)decimal.Ceiling(node.Right);
        for(var coordinate=start+1;coordinate<end;coordinate++)map.Add(start,coordinate,end,coordinate);
        for(var coordinate=start+1;coordinate<end;coordinate++)map.Add(coordinate,start,coordinate,end);
        node=Read(map);Assert.AreEqual(size*2,node.Positions.Length);Assert.AreEqual(size*2,node.Positions.Select(p=>p.Key).Distinct().Count());
        Assert.IsTrue(node.Positions.All(p=>p.RoadPercent==0.5));
        Assert.IsTrue(node.Positions.All(p=>node.Contains(p.X,p.Y)&&p.X%1m+p.Y%1m==0.5m));
        map.Calculate();Assert.AreEqual(size*size,map.Crosses.GetAll().Data!.Length);Assert.IsTrue(map.Crosses.GetAll().Data!.All(c=>c.State==CrossState.Disconnected));
        var before=map.Snapshot();Assert.IsTrue(new LogicalNodeService(map.Repository).Calculate().Success);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow(false,false),DataRow(false,true),DataRow(true,false),DataRow(true,true)]
    public void HalfUnitAndLargeCoordinateProjections_KeepExactFeetAndUsablePercent(bool vertical,bool reversed)
    {
        foreach(var center in new[]{3.5m,6.5m,250000.5m,499993.5m,499996.5m})
        {
            var map=new TestMap();Create(map,center,center);var axis=(int)decimal.Floor(center);
            var begin=reversed?500000:0;var end=reversed?0:500000;
            var road=vertical?map.Add(axis,begin,axis,end):map.Add(begin,axis,end,axis);
            var position=Read(map).Positions.Single();Assert.AreEqual(vertical?axis:center,position.X);Assert.AreEqual(vertical?center:axis,position.Y);
            var a=road.Nodes[position.RoadOrder];var b=road.Nodes[position.RoadOrder+1];
            var start=vertical?a.Y:a.X;var finish=vertical?b.Y:b.X;
            Assert.AreEqual((double)((center-start)/(finish-start)),position.RoadPercent,1e-12);
            Assert.IsTrue(Math.Abs(finish-start)<=500);
            Assert.AreEqual((double)position.X,a.X+(b.X-a.X)*position.RoadPercent,1e-9);Assert.AreEqual((double)position.Y,a.Y+(b.Y-a.Y)*position.RoadPercent,1e-9);
        }
    }
    [TestMethod]
    public void DiagonalFiniteSegments_MatchIndependentIntegerIntervalOracle()
    {
        var random=new Random(314159);var allowed=0;var rejected=0;
        for(var i=0;i<6000;i++)
        {
            var node=new LogicalNodeSnapshot("ZYXWVUTSRQ","owner",0,110.5m,110.5m,i%2==0?6:12,new());
            var ax=random.Next(80,141);var ay=random.Next(80,141);var bx=random.Next(80,141);if(ax==bx)bx++;
            var positive=random.Next(2)==0;var by=ay+(positive?1:-1)*(bx-ax);
            var line=new LineSnapshot("ABCDEFGHIJ","owner",false,1,[new("ABCDEFGHIJ",0,ax,ay),new("ABCDEFGHIJ",1,bx,by)]);
            var left=(int)(node.Left*2);var right=(int)(node.Right*2);var bottom=(int)(node.Bottom*2);var top=(int)(node.Top*2);
            var constant=positive?(ay-ax)*2:(ay+ax)*2;
            var lower=Math.Max(Math.Min(ax,bx)*2,Math.Max(left,positive?bottom-constant:constant-top));
            var upper=Math.Min(Math.Max(ax,bx)*2,Math.Min(right,positive?top-constant:constant-bottom));
            var expected=lower<upper;
            var state=new LineStoreState();state.Lines.Add(line.Code,line);var violation=LogicalNodeRules.ValidateNew(node,state);
            Assert.AreEqual(expected,violation is not null,$"size={node.SideLength}; ({ax},{ay})->({bx},{by})");
            if(expected)rejected++;else allowed++;
        }
        Assert.IsTrue(allowed>100&&rejected>100,"Both passing and non-passing finite diagonals must be exercised.");
    }
    [TestMethod]
    public void NoOpSaveAndWeightEdit_PreservePositionValues_AndJRemovesObsoleteCachedRelations()
    {
        var map=new TestMap();Create(map);var road=map.Add(90,110,130,110);var outside=map.Add(90,130,130,130);map.Calculate();var service=new LogicalNodeService(map.Repository);
        var before=map.Snapshot();map.Save(road,90,110,130,110);Assert.AreEqual(before,map.Snapshot());
        var node=Read(map);var position=node.Positions.Single();Assert.IsTrue(service.Update(new(node.Code,node.Owner,node.Version,"99","")).Success);Assert.AreEqual(position,Read(map).Positions.Single());
        map.Repository.Transaction(state=>
        {
            state.LogicalNodePositions[position.Key]=position with{IsChanged=true,RoadPercent=0,X=1};
            var ghost=new LogicalNodePosition(node.Code,outside.Code,0,0.5,110.5m,130m,outside.Version);
            state.LogicalNodePositions.Add(ghost.Key,ghost);return true;
        });
        Assert.IsTrue(service.Calculate().Success);Assert.AreEqual(position,Read(map).Positions.Single());
    }
    [TestMethod]
    public void ViewerRightsComeFromAccountPermission_AndCannotBeInferredFromAdminName()
    {
        var map=new TestMap();Create(map);map.Add(90,110,130,110);var accounts=new LoginService(map.Repository);accounts.CreateUser(new("manager","test-password"));accounts.CreateUser(new("admin","test-password"));
        map.Repository.Transaction(s=>{s.Accounts["manager"]=s.Accounts["manager"] with{EditAllLogicalNodes=true};s.Accounts["admin"]=s.Accounts["admin"] with{EditAllLogicalNodes=false};return true;});
        var service=new LogicalNodeService(map.Repository);var manager=service.GetAll("manager").Data!.Single();Assert.AreEqual(true,manager.CanEdit);Assert.AreEqual(false,service.GetAll("admin").Data!.Single().CanEdit);
        Assert.IsNull(map.Repository.Transaction(s=>s.LogicalNodes.Values.Single().CanEdit));
        var before=map.Snapshot();Assert.AreEqual("NotOwner",service.Update(new(manager.Code,"admin",manager.Version,"15","")).Code);Assert.AreEqual(before,map.Snapshot());
        var changed=service.Update(new(manager.Code,"manager",manager.Version,"15",""));Assert.IsTrue(changed.Success);Assert.AreEqual("manager",changed.Data!.Owner);CollectionAssert.AreEqual(manager.Positions,changed.Data.Positions);
        Assert.AreEqual(false,service.GetAll("owner").Data!.Single().CanEdit);Assert.AreEqual(true,service.GetAll("manager").Data!.Single().CanEdit);
    }
    [TestMethod]
    public void DeletingLnReleasesOnlyLnRestrictions_PreservesRoadsAndCrossSettings()
    {
        var map=new TestMap();Create(map);map.Add(90,110,130,110);map.Add(110,90,110,130);map.Calculate();
        var node=Read(map);var cross=map.Crosses.GetAll().Data!.Single();Assert.AreEqual(CrossState.Disconnected,cross.State);
        Assert.IsTrue(new LogicalNodeService(map.Repository).Delete(new(node.Code,node.Owner,node.Version)).Success);Assert.AreEqual(0,map.Repository.Transaction(s=>s.LogicalNodePositions.Count));
        Assert.AreEqual(cross,map.Crosses.GetAll().Data!.Single());Assert.AreEqual(2,map.Lines.GetAll().Data!.Length);
        Assert.IsTrue(map.Crosses.Edit(new(cross.Key,"owner",cross.Version,CrossState.Connected)).Success);
        var formerlyForbidden=map.Create();var beforeDiagonal=map.Snapshot();
        Assert.AreEqual("NonOrthogonalIntersection",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(formerlyForbidden,90,93,130,133)).Code,"Deleting LN does not release the independent road intersection rule.");
        Assert.AreEqual(beforeDiagonal,map.Snapshot());
        // Isolate LN deletion from existing roads: the same diagonal is valid once its square is removed.
        var isolated=new TestMap();var isolatedLn=Create(isolated);var isolatedRoad=isolated.Create();
        Assert.AreEqual("DiagonalInsideLogicalNode",isolated.Lines.CheckShapeIsOKThenSave(TestMap.Shape(isolatedRoad,90,93,130,133)).Code);
        Assert.IsTrue(new LogicalNodeService(isolated.Repository).Delete(new(isolatedLn.Code,isolatedLn.Owner,isolatedLn.Version)).Success);
        Assert.IsTrue(isolated.Lines.CheckShapeIsOKThenSave(TestMap.Shape(isolatedRoad,90,93,130,133)).Success);
        var throughExistingCross=map.Create();var before=map.Snapshot();Assert.AreEqual("LinePassesCross",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(throughExistingCross,90,90,130,130)).Code);Assert.AreEqual(before,map.Snapshot());
    }
}
