using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LineSubdivisionMySqlTests
{
    [TestMethod,DataRow("LineNodes"),DataRow("LogicalNodePositions"),DataRow("Crosses")]
    public Task F6_SubdivisionAndLnUpdatesAndCrossFlags_RollBackTogetherOnWriteFailure_ThenRetry(string table)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=TestMap.HistoricalRoad(repository,0,100,601,100);await Add(client,300,90,300,110);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        var node=(await client.CreateLogicalNodeAsync(300.5m,100.5m,6,new(){Value="细分保留",Info="资料"})).Data!;
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var request=TestMap.Shape(road,0,100,601,100);
        Sql(settings,$"CREATE TRIGGER TestSubdivisionAtomic BEFORE {(table=="Crosses"?"UPDATE":"INSERT")} ON `{table}` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected subdivision failure'");
        try{Assert.AreEqual("DatabaseError",(await client.SaveAsync(road.Code,road.Version,request.Nodes)).Code);}finally{Sql(settings,"DROP TRIGGER TestSubdivisionAtomic");}
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(2,request.Nodes.Length);
        var result=await client.SaveAsync(road.Code,road.Version,request.Nodes);Assert.IsTrue(result.Success,result.Message);var saved=result.Data!;LineSubdivisionTests.AssertSegments(saved);
        Assert.AreEqual(297,saved.Nodes[1].X);Assert.AreEqual(road.Version+1,saved.Version);Assert.AreEqual(road.Code,saved.Code);
        var current=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(node.Properties,current.Properties);Assert.AreEqual(node.Version+1,current.Version);
        var position=current.Positions.Single(p=>p.RoadCode==road.Code);Assert.AreEqual(1,position.RoadOrder);Assert.AreEqual(saved.Version,position.RoadVersion);Assert.AreEqual((300.5m,100m),(position.X,position.Y));
        Assert.AreEqual(300.5,saved.Nodes[position.RoadOrder].X+(saved.Nodes[position.RoadOrder+1].X-saved.Nodes[position.RoadOrder].X)*position.RoadPercent,1e-12);
        Assert.IsTrue(repository.Transaction(s=>s.PendingSegments.Count==2&&s.StaleCrosses.Contains(cross.Key)&&s.DirtyLines.Contains(road.Code)));
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var recalculated=(await client.GetCrossesAsync()).Data!.Single();Assert.AreEqual(cross.Key,recalculated.Key);Assert.AreEqual(CrossState.Disconnected,recalculated.State);Assert.AreEqual((1,PercentPrecision.Normalize(3d/304)),recalculated.LocationOn(road.Code));
        before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var again=await client.SaveAsync(saved.Code,saved.Version,saved.Nodes);Assert.IsTrue(again.Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod,DataRow(false),DataRow(true)]
    public Task TwoServers_F6VersusNewLnOrOccupiedMidpoint_UsesLatestCommittedGeometry(bool occupiedNode)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        for(var i=0;i<3;i++)
        {
            var y=100+i*100;var road=(await alice.CreateAsync()).Data!;
            var otherRoad=occupiedNode?(await bob.CreateAsync()).Data!:null;
            var save=alice.SaveAsync(road.Code,road.Version,TestMap.Shape(road,0,y,601,y).Nodes);
            if(occupiedNode)
            {
                var other=bob.SaveAsync(otherRoad!.Code,otherRoad.Version,TestMap.Shape(otherRoad,300,y,300,y+10).Nodes);await Task.WhenAll(save,other);
                Assert.IsTrue(save.Result.Success,save.Result.Message);if(!other.Result.Success)Assert.AreEqual("PointIsOccupied",other.Result.Code);
            }
            else
            {
                var create=bob.CreateLogicalNodeAsync(300.5m,y+.5m,6,new());await Task.WhenAll(save,create);
                Assert.IsTrue(save.Result.Success,save.Result.Message);if(!create.Result.Success)Assert.AreEqual("LineNodeInsideLogicalNode",create.Result.Code);
            }
            LineSubdivisionTests.AssertSegments(save.Result.Data!);
            Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
            Assert.IsTrue(repository.Transaction(s=>{var points=s.Lines.Values.SelectMany(l=>l.Nodes).ToArray();return points.Length==points.Select(p=>(p.X,p.Y)).Distinct().Count();}));
            Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        }
    });
}
