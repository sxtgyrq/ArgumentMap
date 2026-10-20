using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeChangedMySqlTests
{
    private static async Task<(LogicalNodeSnapshot Ln,LineSnapshot A,LineSnapshot B)> Fixture(HttpLineClient client)
    {
        var a=await Add(client,20,50,80,50);var b=await Add(client,50,20,50,80);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        return((await client.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){Value="LN值",LogicLanguage="S ∧ P",Info="保留资料"})).Data!,a,b);
    }
    private static async Task PrepareJ(MySqlSettings settings,HttpLineClient client,LogicalNodeSnapshot node,LineSnapshot a,LineSnapshot b)
    {
        var unused=await Add(client,200,200,230,200);
        Sql(settings,$"UPDATE LogicalNodePositions SET IsChanged=-1,RoadPercent=0.123 WHERE RoadCode='{a.Code}'; DELETE FROM LogicalNodePositions WHERE RoadCode='{b.Code}'; "+
            $"INSERT INTO LogicalNodePositions(LogicalNodeCode,RoadCode,RoadOrder,RoadPercent,X,Y,RoadVersion,IsChanged) VALUES ('{node.Code}','{unused.Code}',0,0.5,215,200,{unused.Version},1)");
    }
    private static void CreateAudit(MySqlSettings settings)
    {
        Sql(settings,"CREATE TABLE LNPositionAudit(Seq INT AUTO_INCREMENT PRIMARY KEY,Kind CHAR(1),OldFlag INT NULL,NewFlag INT NULL) ENGINE=InnoDB");
        Sql(settings,"CREATE TRIGGER AuditLnU AFTER UPDATE ON LogicalNodePositions FOR EACH ROW INSERT INTO LNPositionAudit(Kind,OldFlag,NewFlag) VALUES ('U',OLD.IsChanged,NEW.IsChanged)");
        Sql(settings,"CREATE TRIGGER AuditLnD AFTER DELETE ON LogicalNodePositions FOR EACH ROW INSERT INTO LNPositionAudit(Kind,OldFlag) VALUES ('D',OLD.IsChanged)");
        Sql(settings,"CREATE TRIGGER AuditLnI AFTER INSERT ON LogicalNodePositions FOR EACH ROW INSERT INTO LNPositionAudit(Kind,NewFlag) VALUES ('I',NEW.IsChanged)");
    }
    private static List<(string Kind,int? Old,int? New)> Audit(MySqlSettings settings)
    {
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText="SELECT Kind,OldFlag,NewFlag FROM LNPositionAudit ORDER BY Seq";
        using var r=cmd.ExecuteReader();var rows=new List<(string,int?,int?)>();while(r.Read())rows.Add((r.GetString(0),r.IsDBNull(1)?null:r.GetInt32(1),r.IsDBNull(2)?null:r.GetInt32(2)));return rows;
    }
    private static object Scalar(MySqlSettings settings,string sql)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;return cmd.ExecuteScalar()!;}

    [TestMethod]
    public Task JUsesNegativeToOne_DeleteAndInsert_WhileKOnlyCalculatesCross_IdempotentAfterRestart()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var (ln,a,b)=await Fixture(client);await PrepareJ(settings,client,ln,a,b);
        Sql(settings,$"UPDATE Crosses SET IsChanged=-1; UPDATE LineNodes SET IsChanged=-1 WHERE LineCode='{a.Code}' AND NodeOrder=0; UPDATE `Lines` SET IsChanged=-1 WHERE LineCode='{a.Code}'");
        CreateAudit(settings);Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);var events=Audit(settings);
        Assert.AreEqual(2,events.Count(e=>e.Kind=="U"&&e.New==-1));Assert.IsTrue(events.Take(2).All(e=>e.Kind=="U"&&e.New==-1));
        Assert.IsTrue(events.Any(e=>e.Kind=="U"&&e.Old==-1&&e.New==1));Assert.IsTrue(events.Any(e=>e.Kind=="D"&&e.Old==-1));Assert.IsTrue(events.Any(e=>e.Kind=="I"&&e.New==1));
        Assert.AreEqual(0,Convert.ToInt32(Scalar(settings,"SELECT COUNT(*) FROM LogicalNodePositions WHERE IsChanged<>1")));
        Assert.AreEqual(-1,Convert.ToInt32(Scalar(settings,"SELECT IsChanged FROM Crosses")),"J must not restore Cross flags.");
        var restored=(await client.GetLogicalNodesAsync()).Data!.Single();CollectionAssert.AreEqual(ln.Positions,restored.Positions);Assert.AreEqual(ln.Properties,restored.Properties);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));var count=Audit(settings).Count;
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.AreEqual(count,Audit(settings).Count,"K must not write LN positions.");
        Assert.AreEqual(0,Convert.ToInt32(Scalar(settings,"SELECT IsChanged FROM Crosses")));Assert.AreEqual(CrossState.Disconnected,(await client.GetCrossesAsync()).Data!.Single().State);
        var restart=new LogicalNodeService(new MySqlLineRepository(settings));CollectionAssert.AreEqual(restored.Positions,restart.GetAll().Data!.Single().Positions);
        before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.IsTrue(restart.Calculate().Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod,DataRow("mark"),DataRow("restore"),DataRow("delete"),DataRow("insert")]
    public Task JFailureAtEveryLifecycleStageRollsBackAllDataAndMarks_ThenRetry(string stage)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var (ln,a,b)=await Fixture(client);await PrepareJ(settings,client,ln,a,b);CreateAudit(settings);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        var action=stage switch{"delete"=>"DELETE","insert"=>"INSERT",_=>"UPDATE"};
        var body=stage switch{"mark"=>"BEGIN IF NEW.IsChanged=-1 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected mark failure'; END IF; END", "restore"=>"BEGIN IF OLD.IsChanged=-1 AND NEW.IsChanged=1 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected restore failure'; END IF; END",_=>"SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN relation failure'"};
        Sql(settings,$"CREATE TRIGGER FailLnJ BEFORE {action} ON LogicalNodePositions FOR EACH ROW {body}");
        try{Assert.AreEqual("DatabaseError",(await client.CalculateLogicalNodeAsync()).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(0,Audit(settings).Count);}
        finally{Sql(settings,"DROP TRIGGER FailLnJ");}
        Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(2,(await client.GetLogicalNodesAsync()).Data!.Single().Positions.Length);
    });

    [TestMethod]
    public Task GroupMoveMarksOldLnPositions_DeletesNegativeOldRows_InsertsRecalculatedPositivePositions()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var (ln,_,_)=await Fixture(client);CreateAudit(settings);var selected=(await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;
        var moved=await client.MoveMapAreaAsync(selected,100,100);Assert.IsTrue(moved.Success,moved.Message);var events=Audit(settings);
        Assert.AreEqual(2,events.Count(e=>e.Kind=="U"&&e.Old==1&&e.New==-1));Assert.IsTrue(events.Take(2).All(e=>e.Kind=="U"&&e.New==-1));
        Assert.AreEqual(2,events.Count(e=>e.Kind=="D"&&e.Old==-1));Assert.AreEqual(2,events.Count(e=>e.Kind=="I"&&e.New==1));
        var node=moved.Data!.LogicalNodes.Single();Assert.AreEqual(ln.Properties,node.Properties);Assert.AreEqual(150.5m,node.X);Assert.AreEqual(2,node.Positions.Length);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    public Task JAndRoadEditOnTwoServers_DoNotResurrectAnObsoletePosition()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};var client=await TestLogin.ClientAsync(http1,first,"owner");var other=new HttpLineClient(http2,client.Session);
        var (ln,a,b)=await Fixture(client);Sql(settings,$"UPDATE LogicalNodePositions SET IsChanged=-1 WHERE RoadCode='{a.Code}'");
        var j=client.CalculateLogicalNodeAsync();var edit=other.SaveAsync(a.Code,a.Version,TestMap.Shape(a,20,150,80,150).Nodes);await Task.WhenAll(j,edit);Assert.IsTrue(j.Result.Success,j.Result.Message);Assert.IsTrue(edit.Result.Success,edit.Result.Message);
        var after=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(b.Code,after.Positions.Single().RoadCode);Assert.AreEqual(ln.Code,after.Code);Assert.AreEqual(ln.Properties,after.Properties);
        Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(b.Code,(await client.GetLogicalNodesAsync()).Data!.Single().Positions.Single().RoadCode);
    });
}
