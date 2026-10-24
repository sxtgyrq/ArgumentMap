using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql")]
public sealed class LogicalNodeMySqlTests
{
    internal static async Task Isolated(Func<MySqlSettings,Task> action)
    {
        var production=MySqlSettings.Load();var settings=production with{Database="ArgumentMap_Integration_"+Guid.NewGuid().ToString("N")};
        using var admin=new MySqlConnection(production.ConnectionString(false));await admin.OpenAsync();
        using var command=admin.CreateCommand();command.CommandText="SELECT COUNT(*) FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=@db";command.Parameters.AddWithValue("@db",settings.Database);
        Assert.AreEqual(0,Convert.ToInt32(await command.ExecuteScalarAsync()));
        try{await action(settings);}finally
        {
            Assert.IsTrue(settings.Database.StartsWith("ArgumentMap_Integration_",StringComparison.Ordinal));Assert.AreNotEqual(production.Database,settings.Database);
            try
            {
                command.Parameters.Clear();command.CommandText=$"DROP DATABASE IF EXISTS `{settings.Database}`";await command.ExecuteNonQueryAsync();
            }
            finally
            {
                // Each GUID database has its own connection pool. Release this test's idle
                // sessions immediately instead of retaining them until the pool timeout.
                using var pooled=new MySqlConnection(settings.ConnectionString());
                MySqlConnection.ClearPool(pooled);
            }
        }
    }
    internal static void Sql(MySqlSettings settings,string sql)
    {using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var command=connection.CreateCommand();command.CommandText=sql;command.ExecuteNonQuery();}
    internal static string Snapshot(ILineRepository repository)=>repository.Transaction(s=>JsonSerializer.Serialize(new
    {
        Lines=s.Lines.Values.OrderBy(l=>l.Code).ToArray(),Nodes=s.LogicalNodes.Values.OrderBy(n=>n.Code).ToArray(),Positions=s.LogicalNodePositions.Values.OrderBy(p=>p.LogicalNodeCode).ThenBy(p=>p.RoadCode).ThenBy(p=>p.RoadOrder).ToArray(),Crosses=s.Crosses.Values.OrderBy(c=>c.Key).ToArray(),
        Pending=s.PendingSegments.OrderBy(p=>p.LineCode).ThenBy(p=>p.Segment).Select(p=>new{p.LineCode,p.Segment}).ToArray(),Stale=s.StaleCrosses.Order().ToArray(),Dirty=s.DirtyLines.Order().ToArray()
    }));
    internal static LineSnapshot Add(ILineRepository repository,params int[] xy)
    {var service=new LineSaveService(repository);var road=service.Create(new("owner")).Data!;var result=service.CheckShapeIsOKThenSave(TestMap.Shape(road,xy));Assert.IsTrue(result.Success,result.Message);return result.Data!;}
    private static void Crosses(ILineRepository repository)
    {var service=new CrossService(repository);for(var i=0;i<100;i++){var step=service.Calculate();Assert.IsTrue(step.Success,step.Message);if(!step.Data!.CanContinue)return;}Assert.Fail("K did not complete.");}
    internal static async Task<LineSnapshot> Add(HttpLineClient client,params int[] xy)
    {var road=(await client.CreateAsync()).Data!;var result=await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,xy).Nodes);Assert.IsTrue(result.Success,result.Message);return result.Data!;}

    [TestMethod]
    public Task Schema10Upgrade_UnicodeHalfCoordinatesConstraintsAndRestart()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var road=Add(repository,90,110,130,110);new LoginService(repository).CreateUser(new("admin","admin"));
        var before=Snapshot(repository);
        Sql(settings,"DROP TABLE LogicalNodePositions; DROP TABLE LogicalNodes; ALTER TABLE DictLogin DROP COLUMN EditAllLogicalNodes; UPDATE MapMetadata SET SchemaVersion=10 WHERE Id=1");
        Sql(settings,"CREATE TRIGGER TestLNPermissionMigration BEFORE UPDATE ON DictLogin FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN schema permission failure'");
        try { Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings)); }
        finally { Sql(settings,"DROP TRIGGER TestLNPermissionMigration"); }
        using(var checkConnection=new MySqlConnection(settings.ConnectionString()))
        {checkConnection.Open();using var check=checkConnection.CreateCommand();check.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(10,Convert.ToInt32(check.ExecuteScalar()),"Permission failure must not publish schema version.");}
        repository=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(repository));Assert.IsTrue(repository.Transaction(s=>s.Accounts["admin"].EditAllLogicalNodes));
        using(var connection=new MySqlConnection(settings.ConnectionString()))
        {connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(command.ExecuteScalar()));}
        var service=new LogicalNodeService(repository);var created=service.Create(new("owner",110.5m,110.5m,12,new(){Value="逻辑节点中文",Info="数据库持久化"}));Assert.IsTrue(created.Success,created.Message);
        LogicalNodeTests.Calculate(service);var node=service.GetAll().Data!.Single();Assert.AreEqual(110.5m,node.X);Assert.AreEqual(0.5125,node.Positions.Single().RoadPercent,1e-10);Assert.AreEqual(JsonSerializer.Serialize(node),JsonSerializer.Serialize(new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single()));
        before=Snapshot(repository);
        foreach(var change in new[]{"X=110","Y=110.1","SideLength=10","X=0.5","`Value`=REPEAT('x',33)","Code='1234567890'"})
        {try{Sql(settings,$"UPDATE LogicalNodes SET {change}");Assert.Fail($"Constraint accepted {change}");}catch(MySqlException ex){Assert.AreEqual(3819,ex.Number);}}
        Assert.AreEqual(before,Snapshot(repository));
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task FaultInjection_RollsBackRoadNodesCrossFlagsLogicalNodesAndDeletes()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");var road=await Add(client,90,110,130,110);
        var before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestLNInsert BEFORE INSERT ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN insert failure'");
        try{Assert.AreEqual("DatabaseError",(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Code);}finally{Sql(settings,"DROP TRIGGER TestLNInsert");}
        Assert.AreEqual(before,Snapshot(repository));var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Data!;
        while((await client.CalculateLogicalNodeAsync()).Data!.CanContinue){}node=(await client.GetLogicalNodesAsync()).Data!.Single();before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestLNUpdate BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN update failure'");
        try
        {
            Assert.AreEqual("DatabaseError",(await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,90,110,100,110,130,110).Nodes)).Code);Assert.AreEqual(before,Snapshot(repository));
            Assert.AreEqual("DatabaseError",(await client.ReverseLineAsync(road.Code,road.Version)).Code);Assert.AreEqual(before,Snapshot(repository));
            Assert.AreEqual("DatabaseError",(await client.DeleteAsync(road.Code,road.Version)).Code);Assert.AreEqual(before,Snapshot(repository));
            Assert.AreEqual("DatabaseError",(await client.UpdateLogicalNodeAsync(node.Code,node.Version,"99","")).Code);Assert.AreEqual(before,Snapshot(repository));
        }finally{Sql(settings,"DROP TRIGGER TestLNUpdate");}
        Assert.IsTrue((await client.ReverseLineAsync(road.Code,road.Version)).Success);before=Snapshot(repository);
        Sql(settings,"UPDATE LogicalNodePositions SET IsChanged=-1");before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestLNCalculate BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected J failure'");
        try{Assert.AreEqual("DatabaseError",(await client.CalculateLogicalNodeAsync()).Code);}finally{Sql(settings,"DROP TRIGGER TestLNCalculate");}
        Assert.AreEqual(before,Snapshot(new MySqlLineRepository(settings)));Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);
        await Add(client,110,90,110,130);before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestLNCross BEFORE INSERT ON Crosses FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected K inside LN failure'");
        try{Assert.AreEqual("DatabaseError",(await client.CalculateCrossAsync()).Code);}finally{Sql(settings,"DROP TRIGGER TestLNCross");}
        Assert.AreEqual(before,Snapshot(repository));Crosses(repository);Assert.AreEqual(CrossState.Disconnected,(await client.GetCrossesAsync()).Data!.Single().State);
        node=(await client.GetLogicalNodesAsync()).Data!.Single();before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestLNDelete BEFORE DELETE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN delete failure'");
        try{Assert.AreEqual("DatabaseError",(await client.DeleteLogicalNodeAsync(node.Code,node.Version)).Code);}finally{Sql(settings,"DROP TRIGGER TestLNDelete");}
        Assert.AreEqual(before,Snapshot(repository));Assert.IsTrue((await client.DeleteLogicalNodeAsync(node.Code,node.Version)).Success);
    });

    [TestMethod]
    public Task TwoServers_ConcurrentSquareCreationVersusRoadSaveAndCrossState()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(http1,first,"alice");var bob=await TestLogin.ClientAsync(http2,second,"bob");
        for(var i=0;i<6;i++)
        {
            var y=110+i*100;var road=await Add(alice,90,y,130,y);var intruder=(await bob.CreateAsync()).Data!;
            var create=alice.CreateLogicalNodeAsync(110.5m,y+0.5m,6,new());
            var badShape=i%2==0?TestMap.Shape(intruder,110,y+1,110,y+10):TestMap.Shape(intruder,109,y-1,129,y+19);
            var save=bob.SaveAsync(intruder.Code,intruder.Version,badShape.Nodes);
            await Task.WhenAll(create,save);Assert.AreEqual(1,(create.Result.Success?1:0)+(save.Result.Success?1:0),"Shared DB transaction lock must serialize the geometry decision.");
            Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
        }
        var a=await Add(alice,90,900,130,900);var b=await Add(bob,110,880,110,920);Crosses(repository);
        var cross=(await alice.GetCrossesAsync()).Data!.Single(c=>c.Key.Contains(a.Code)&&c.Key.Contains(b.Code));cross=(await alice.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Data!;
        for(var i=0;i<6;i++)
        {
            var create=alice.CreateLogicalNodeAsync(110.5m,900.5m,6,new());var edit=bob.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Connected);
            await Task.WhenAll(create,edit);Assert.AreEqual(1,(create.Result.Success?1:0)+(edit.Result.Success?1:0));Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
            if(create.Result.Success){var node=create.Result.Data!;Assert.IsTrue((await alice.DeleteLogicalNodeAsync(node.Code,node.Version)).Success);}
            cross=(await alice.GetCrossesAsync()).Data!.Single(c=>c.Key==cross.Key);cross=(await alice.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Data!;
        }
        var ln=(await alice.CreateLogicalNodeAsync(115.5m,950.5m,6,new())).Data!;var east=await Add(alice,90,950,130,950);var north=await Add(bob,115,930,115,970);Crosses(repository);
        Assert.AreEqual(CrossState.Disconnected,new CrossService(new MySqlLineRepository(settings)).GetAll().Data!.Single(c=>c.Key.Contains(east.Code)&&c.Key.Contains(north.Code)).State);
        Assert.IsNotNull(ln);
    });

    [TestMethod, TestCategory("Http")]
    public Task AuthenticatedHttp_ValidationPermissionsVersionAndDeletedRoadCleanup()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var owner=await TestLogin.ClientAsync(http,server,"owner");var other=await TestLogin.ClientAsync(http,server,"other");
        var road=await Add(owner,90,110,130,110);var before=Snapshot(repository);
        Assert.AreEqual("InvalidLogicalNodeCoordinate",(await owner.CreateLogicalNodeAsync(110,110.5m,6,new())).Code);Assert.AreEqual(before,Snapshot(repository));
        var node=(await owner.CreateLogicalNodeAsync(110.5m,110.5m,6,new(){Value="中文 LN"})).Data!;Assert.IsNotNull(node);
        Assert.AreEqual("NotOwner",(await other.UpdateLogicalNodeAsync(node.Code,node.Version,"99","")).Code);Assert.AreEqual("NotOwner",(await other.DeleteLogicalNodeAsync(node.Code,node.Version)).Code);
        Assert.IsTrue((await owner.CalculateLogicalNodeAsync()).Success);Assert.IsTrue((await owner.UpdateLogicalNodeAsync(node.Code,node.Version,"20","")).Success);Assert.AreEqual("VersionConflict",(await owner.UpdateLogicalNodeAsync(node.Code,node.Version,"20","")).Code);node=(await owner.GetLogicalNodesAsync()).Data!.Single();
        Assert.IsTrue((await owner.UpdateLogicalNodeAsync(node.Code,node.Version,"99","")).Success);
        using(var response=await http.PostAsJsonAsync("GetLogicalNodes",new SessionRequest("owner","invalid")))Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized,response.StatusCode);
        using(var response=await http.PostAsJsonAsync("CalculateLogicalNode",new SessionRequest("owner","invalid")))Assert.AreEqual(System.Net.HttpStatusCode.Unauthorized,response.StatusCode);
        Assert.IsTrue((await owner.DeleteAsync(road.Code,road.Version)).Success);Assert.AreEqual(0,(await owner.GetLogicalNodesAsync()).Data!.Single().Positions.Length);
        Assert.IsTrue((await other.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(1,(await owner.GetLogicalNodesAsync()).Data!.Length);
    });
}
