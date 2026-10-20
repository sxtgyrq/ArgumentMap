using System.Net.Http;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class StoredSegmentLengthMySqlTests
{
    [TestMethod,DataRow("merge",false),DataRow("merge",true),DataRow("split",false),DataRow("split",true),DataRow("reverse",false),DataRow("reverse",true)]
    public Task EveryGeometryWrite_StoresOnlyBoundedSegments_UpdatesDependentsAndRollsBackPartialWrite(string operation,bool injectFailure)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var a=operation=="merge"?await Add(client,0,100,10,100):TestMap.HistoricalRoad(repository,operation=="split"?[0,100,1000,100,1010,100,2010,100]:[0,100,1000,100]);
        var b=operation=="merge"?await Add(client,2000,100,2010,100):null;
        var other=operation=="merge"?await Add(client,2005,90,2005,110):await Add(client,400,90,400,110);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var old=(await client.GetCrossesAsync()).Data!.Single();old=(await client.SetCrossStateAsync(old.Key,old.Version,CrossState.Road1ToRoad2)).Data!;
        var node=(await client.CreateLogicalNodeAsync(operation=="merge"?1005.5m:250.5m,100.5m,6,new(){Value="长度约束",Info="保留"})).Data!;
        MergePreview? preview=null;
        if(b is not null)
        {
            var unchanged=LineTopologyMySqlTests.RawSnapshot(settings);var plans=await client.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version);Assert.IsTrue(plans.Success,plans.Message);preview=plans.Data!;Assert.AreEqual(unchanged,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        async Task<ServiceResult<LineSnapshot[]>> Apply()
        {
            if(operation=="reverse")
            {
                var result=await client.ReverseLineAsync(a.Code,a.Version);return new(result.Success,result.Code,result.Message,result.Data is {} line?[line]:null);
            }
            var topology=operation=="split"?await client.SplitLineAsync(a.Code,a.Version,2):await client.ApplyMergePlanAsync(preview!,preview!.Plans.Single());
            return new(topology.Success,topology.Code,topology.Message,topology.Data?.Lines);
        }
        if(injectFailure)
        {
            var before=LineTopologyMySqlTests.RawSnapshot(settings);
            Sql(settings,"CREATE TRIGGER TestBoundedSegmentsAtomic BEFORE INSERT ON LineNodes FOR EACH ROW BEGIN IF NEW.NodeOrder=2 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected normalized node failure'; END IF; END");
            try{Assert.AreEqual("DatabaseError",(await Apply()).Code);}finally{Sql(settings,"DROP TRIGGER TestBoundedSegmentsAtomic");}
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        var applied=await Apply();Assert.IsTrue(applied.Success,applied.Message);
        foreach(var line in applied.Data!)
        {
            LineSubdivisionTests.AssertSegments(line);var read=(await client.GetLineDetailAsync(line.Code)).Data!;CollectionAssert.AreEqual(line.Nodes,read.Nodes);Assert.AreEqual(line.Version,read.Version);
        }
        using(var connection=new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM LineNodes a JOIN LineNodes b ON b.LineCode=a.LineCode AND b.NodeOrder=a.NodeOrder+1 WHERE POW(b.X-a.X,2)+POW(b.Y-a.Y,2)>250000";
            Assert.AreEqual(0L,Convert.ToInt64(command.ExecuteScalar()));
        }
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.AreEqual(old.Key.Position,cross.Key.Position);
        Assert.AreEqual(old.AllowsTransfer(b?.Code??a.Code,other.Code),cross.AllowsTransfer(a.Code,other.Code));
        var current=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(node.Version+1,current.Version);Assert.AreEqual(node.Properties,current.Properties);
        var position=current.Positions.Single();var road=(await client.GetLineDetailAsync(position.RoadCode)).Data!;Assert.AreEqual(road.Version,position.RoadVersion);Assert.IsFalse(position.IsChanged);
        var start=road.Nodes[position.RoadOrder];var end=road.Nodes[position.RoadOrder+1];Assert.AreEqual((double)position.X,start.X+(end.X-start.X)*position.RoadPercent,1e-12);
        var settled=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.AreEqual(settled,LineTopologyMySqlTests.RawSnapshot(settings));
    });
}
