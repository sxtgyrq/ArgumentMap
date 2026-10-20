using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using ArgumentMap.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class DiagnosisDiagramTests
{
    [TestMethod]
    public void ImportCreatesRealSizedNodesOneWayRoadsCrossesAndEightProjections_RepeatDoesNotDuplicate()
    {
        var map=new TestMap();map.Add(500,500,510,500);var pending=map.Repository.Transaction(s=>s.PendingSegments.ToArray());
        var result=new EquipmentDiagramData(map.Repository).Import("owner",1000,1000);Assert.IsTrue(result.Success,result.Message);
        var nodes=result.Data!;Assert.AreEqual(10,nodes.Length);Assert.AreEqual(4,nodes.Count(n=>n.SideLength==4));Assert.AreEqual(2,nodes.Count(n=>n.SideLength==6));Assert.AreEqual(4,nodes.Count(n=>n.SideLength==12));
        Assert.AreEqual(12,nodes.Sum(n=>n.Positions.Length));Assert.IsTrue(nodes.All(n=>n.X%1==.5m&&n.Y%1==.5m&&n.Properties.LogicLanguage.Length>0));
        Assert.AreEqual(2,map.Crosses.GetAll().Data!.Length);Assert.IsTrue(map.Crosses.GetAll().Data!.All(c=>c.State==CrossState.Disconnected));
        Assert.IsNull(map.Repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));Assert.IsNull(map.Repository.Transaction(s=>CrossPassageValidator.FindInCurrentGeometry(s)));
        foreach(var road in map.Lines.GetAll().Data!.Where(r=>r.Properties.RoadDetail.StartsWith(EquipmentDiagramData.Marker))){Assert.AreEqual(0,road.Properties.PassInOpposeDirection);Assert.IsTrue(road.Nodes.Zip(road.Nodes.Skip(1)).All(p=>Math.Pow(p.First.X-p.Second.X,2)+Math.Pow(p.First.Y-p.Second.Y,2)<=250000));}
        CollectionAssert.AreEquivalent(pending,map.Repository.Transaction(s=>s.PendingSegments.ToArray()));
        var before=map.Snapshot();Assert.AreEqual("DiagramAlreadyExists",new EquipmentDiagramData(map.Repository).Import("owner",2000,2000).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow(50,50),DataRow(1000,110)]
    public void InvalidLayoutRollsBackAllEarlierNodesAndRoads(int x,int y)
    {var map=new TestMap();var before=map.Snapshot();Assert.IsFalse(new EquipmentDiagramData(map.Repository).Import("owner",x,y).Success);Assert.AreEqual(before,map.Snapshot());}
}

[TestClass,TestCategory("MySql")]
public sealed class DiagnosisDiagramMySqlTests
{
    [TestMethod,DataRow("LogicalNodes"),DataRow("LineNodes"),DataRow("Crosses"),DataRow("LogicalNodePositions")]
    public Task ImportIsOneTransaction_AllTablesRollbackOnFailureAndRetryPersists(string table)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,$"CREATE TRIGGER TestDiagram BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Diagram insert failure'");
        try{Assert.ThrowsExactly<MySqlException>(()=>new EquipmentDiagramData(repository).Import("owner",1000,1000));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER TestDiagram");}
        var result=new EquipmentDiagramData(repository).Import("owner",1000,1000);Assert.IsTrue(result.Success,result.Message);
        var saved=Snapshot(repository);Assert.AreEqual(saved,Snapshot(new MySqlLineRepository(settings)));return Task.CompletedTask;
    });
}
