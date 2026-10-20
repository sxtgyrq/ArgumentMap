using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeDiagonalTests
{
    [TestMethod,DataRow(1,1),DataRow(-1,1),DataRow(-1,-1),DataRow(1,-1)]
    public void CreatingSquare_RejectsDiagonalInteriorPassageInBothSizes(int dx,int dy)
    {
        foreach(var size in new[]{6,12})
        {
            var map=new TestMap();var road=map.Add(110-20*dx,110-20*dy,110+20*dx,110+20*dy);var before=map.Snapshot();
            Assert.AreEqual("DiagonalInsideLogicalNode",LogicalNodeTests.Create(map,road,size:size).Code);Assert.AreEqual(before,map.Snapshot());
        }
    }
    [TestMethod,DataRow(6),DataRow(12)]
    public void F6RejectsDiagonalThroughSquare_ButAllowsFourCornerTangenciesAndOutsideSegments(int size)
    {
        var map=new TestMap();var source=map.Add(90,110,130,110);Assert.IsTrue(LogicalNodeTests.Create(map,source,size:size).Success);
        var road=map.Create();var before=map.Snapshot();Assert.AreEqual("DiagonalInsideLogicalNode",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(road,90,89,130,129)).Code);Assert.AreEqual(before,map.Snapshot());
        // Center 110.5 gives half-integer corners. These integer-endpoint lines meet exactly one corner.
        var radius=size/2;var lower=(int)(110.5m-radius);var upper=(int)(110.5m+radius);
        foreach(var pair in new[]{(dx:1,dy:1,constant:-size),(dx:1,dy:1,constant:size),(dx:1,dy:-1,constant:221-size),(dx:1,dy:-1,constant:221+size)})
        {
            var tangentMap=new TestMap();Assert.IsTrue(new LogicalNodeService(tangentMap.Repository).Create(new("owner",110.5m,110.5m,size,new())).Success);
            var tangent=tangentMap.Create();var x1=lower-10;var x2=upper+10;var y1=pair.dy*x1+pair.constant;var y2=pair.dy*x2+pair.constant;
            var saved=tangentMap.Lines.CheckShapeIsOKThenSave(TestMap.Shape(tangent,x1,y1,x2,y2));Assert.IsTrue(saved.Success,saved.Message);tangentMap.Calculate();
        }
        var outsideMap=new TestMap();var r=outsideMap.Add(90,110,130,110);Assert.IsTrue(LogicalNodeTests.Create(outsideMap,r,size:size).Success);
        var diagonal=outsideMap.Create();Assert.IsTrue(outsideMap.Lines.CheckShapeIsOKThenSave(TestMap.Shape(diagonal,80,80,90,90)).Success,"Infinite extension intersects, finite segment does not.");
    }
    [TestMethod]
    public void KRejectsLegacyDiagonalAndDoesNotClearPendingFlags()
    {
        var map=new TestMap();var road=map.Add(90,110,130,110);Assert.IsTrue(LogicalNodeTests.Create(map,road).Success);
        map.Repository.Transaction(s=>{s.Lines[road.Code]=road with{Nodes=TestMap.Shape(road,90,90,130,130).Nodes};return true;});var before=map.Snapshot();
        Assert.AreEqual("DiagonalInsideLogicalNode",map.Crosses.Calculate().Code);Assert.AreEqual(before,map.Snapshot());Assert.AreEqual("DiagonalInsideLogicalNode",new LogicalNodeService(map.Repository).Calculate().Code);Assert.AreEqual(before,map.Snapshot());
    }
}
