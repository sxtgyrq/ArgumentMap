using System.Net.Http;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodePositionMySqlTests
{
    private static LogicalNodeSnapshot Node(ILineRepository repository,string code)=>new LogicalNodeService(repository).GetAll().Data!.Single(n=>n.Code==code);
    private static string Fingerprint(LogicalNodeSnapshot node)=>JsonSerializer.Serialize(node);

    [TestMethod]
    public Task SqlPositionsFaults_RollBackLnCreationRoadEditsReversalDeletionAndFullJ()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,90,108,130,108,130,112,90,112);var before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestPositionInsert BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected positions insert failure'");
        try { Assert.AreEqual("DatabaseError",(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Code);Assert.AreEqual(before,Snapshot(repository)); }
        finally{Sql(settings,"DROP TRIGGER TestPositionInsert");}
        var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Data!;Assert.AreEqual(2,node.Positions.Length);before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestPositionWrite BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected road positions write failure'");
        try
        {
            Assert.AreEqual("DatabaseError",(await client.SaveAsync(road.Code,road.Version,TestMap.Shape(road,90,108,100,108,130,108,130,112,90,112).Nodes)).Code);Assert.AreEqual(before,Snapshot(repository));
            Assert.AreEqual("DatabaseError",(await client.ReverseLineAsync(road.Code,road.Version)).Code);Assert.AreEqual(before,Snapshot(repository));
        }finally{Sql(settings,"DROP TRIGGER TestPositionWrite");}
        Sql(settings,"CREATE TRIGGER TestPositionDelete BEFORE DELETE ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected positions cleanup failure'");
        try
        {
            Assert.AreEqual("DatabaseError",(await client.DeleteAsync(road.Code,road.Version)).Code);Assert.AreEqual(before,Snapshot(repository));
            Assert.AreEqual("DatabaseError",(await client.DeleteLogicalNodeAsync(node.Code,node.Version)).Code);Assert.AreEqual(before,Snapshot(repository));
        }finally{Sql(settings,"DROP TRIGGER TestPositionDelete");}
        var second=(await client.CreateLogicalNodeAsync(120.5m,110.5m,6,new())).Data!;
        Sql(settings,"UPDATE LogicalNodePositions SET IsChanged=-1,RoadPercent=0");before=Snapshot(repository);
        // Fail only the later LN to prove the earlier LN's already executed writes are also rolled back.
        var later=new[]{node.Code,second.Code}.Order(StringComparer.Ordinal).Last();
        Sql(settings,$"CREATE TRIGGER TestPositionJ BEFORE UPDATE ON LogicalNodePositions FOR EACH ROW BEGIN IF NEW.LogicalNodeCode='{later}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected later LN recalculation failure'; END IF; END");
        try{Assert.AreEqual("DatabaseError",(await client.CalculateLogicalNodeAsync()).Code);Assert.AreEqual(before,Snapshot(repository));}
        finally{Sql(settings,"DROP TRIGGER TestPositionJ");}
        var complete=await client.CalculateLogicalNodeAsync();Assert.IsTrue(complete.Success);Assert.IsFalse(complete.Data!.CanContinue);
        Assert.IsTrue((await client.GetLogicalNodesAsync()).Data!.SelectMany(n=>n.Positions).All(p=>!p.IsChanged&&p.RoadPercent>0));
        road=(await client.ReverseLineAsync(road.Code,road.Version)).Data!;
        Assert.AreEqual(Fingerprint(Node(repository,node.Code)),Fingerprint(Node(new MySqlLineRepository(settings),node.Code)));
        Assert.IsTrue((await client.DeleteAsync(road.Code,road.Version)).Success);Assert.AreEqual(2,(await client.GetLogicalNodesAsync()).Data!.Length);Assert.IsTrue((await client.GetLogicalNodesAsync()).Data!.All(n=>n.Positions.Length==0));
    });

    [TestMethod]
    public Task IndependentServersConcurrentValidRoadsAndLnCreation_NeverLosePositions()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var a=await TestLogin.ClientAsync(http1,first,"alice");var b=await TestLogin.ClientAsync(http2,second,"bob");
        for(var i=0;i<4;i++)
        {
            var y=110+i*100;
            var create=a.CreateLogicalNodeAsync(110.5m,y+0.5m,6,new());var road=b.CreateAsync();await Task.WhenAll(create,road);Assert.IsTrue(create.Result.Success);
            var r=road.Result.Data!;Assert.IsTrue((await b.SaveAsync(r.Code,r.Version,TestMap.Shape(r,90,y,130,y).Nodes)).Success);Assert.AreEqual(1,Node(repository,create.Result.Data!.Code).Positions.Length);
        }
        var ln=(await a.CreateLogicalNodeAsync(110.5m,550.5m,6,new())).Data!;
        var one=(await a.CreateAsync()).Data!;var two=(await b.CreateAsync()).Data!;
        var saves=await Task.WhenAll(a.SaveAsync(one.Code,one.Version,TestMap.Shape(one,90,550,130,550).Nodes),b.SaveAsync(two.Code,two.Version,TestMap.Shape(two,110,530,110,570).Nodes));
        Assert.IsTrue(saves.All(r=>r.Success));var current=Node(repository,ln.Code);Assert.AreEqual(2,current.Positions.Length);Assert.AreEqual(2,current.Version);
        one=saves[0].Data!;two=saves[1].Data!;
        await Task.WhenAll(a.ReverseLineAsync(one.Code,one.Version),b.ReverseLineAsync(two.Code,two.Version));current=Node(new MySqlLineRepository(settings),ln.Code);Assert.AreEqual(2,current.Positions.Length);Assert.IsTrue(current.Positions.All(p=>Math.Abs(p.RoadPercent-0.4875)<1e-12));
        var fresh=(await a.GetLogicalNodesAsync()).Data!.Single(n=>n.Code==ln.Code);
        var deleteLn=a.DeleteLogicalNodeAsync(fresh.Code,fresh.Version);var deleteRoad=b.DeleteAsync(two.Code,two.Version+1);await Task.WhenAll(deleteLn,deleteRoad);
        Assert.IsTrue(deleteRoad.Result.Success);if(!deleteLn.Result.Success){Assert.AreEqual("VersionConflict",deleteLn.Result.Code);fresh=Node(repository,ln.Code);Assert.IsTrue((await a.DeleteLogicalNodeAsync(fresh.Code,fresh.Version)).Success);}
        Assert.IsFalse(repository.Transaction(s=>s.LogicalNodePositions.Keys.Any(k=>k.LogicalNodeCode==ln.Code)));
    });

    [TestMethod]
    public Task PositionForeignKeysConstraintsAndRestart_EnforceOneToManyIdentity()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var service=new LogicalNodeService(repository);var node=service.Create(new("owner",110.5m,110.5m,6,new())).Data!;
        var road=Add(repository,90,108,130,108,130,112,90,112);node=Node(repository,node.Code);Assert.AreEqual(2,node.Positions.Length);
        foreach(var change in new[]{"RoadPercent=1.1","RoadOrder=-1","X=0.1","Y=-1","IsChanged=0","RoadVersion=-1"})
            Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE LogicalNodePositions SET "+change));
        Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE LogicalNodePositions SET RoadOrder=99"));
        Assert.AreEqual(Fingerprint(node),Fingerprint(Node(new MySqlLineRepository(settings),node.Code)));
        Sql(settings,$"DELETE FROM LineNodes WHERE LineCode='{road.Code}' AND NodeOrder=2");Assert.AreEqual(1,repository.Transaction(s=>s.LogicalNodePositions.Count));
        Sql(settings,$"DELETE FROM LogicalNodes WHERE Code='{node.Code}'");Assert.AreEqual(0,repository.Transaction(s=>s.LogicalNodePositions.Count));
        return Task.CompletedTask;
    });

    [TestMethod,DataRow(false),DataRow(true)]
    public Task ActualSchema11Migration_RebuildsAllPassingRoadsRetainsEmptyLnAndRecoversInterruptedPublication(bool failCopy)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var a=Add(repository,90,108,130,108,130,112,90,112);var b=Add(repository,110,90,110,130);
        Sql(settings,"DROP TABLE LogicalNodePositions; DROP TABLE LogicalNodes");
        Sql(settings,"CREATE TABLE LogicalNodes (Code VARCHAR(10) CHARACTER SET ascii COLLATE ascii_bin PRIMARY KEY,Owner VARCHAR(255) NOT NULL,Version BIGINT NOT NULL,X DECIMAL(7,1) NOT NULL,Y DECIMAL(7,1) NOT NULL,SideLength INT NOT NULL,RoadCode VARCHAR(10) NOT NULL,Name VARCHAR(32) NOT NULL,Info VARCHAR(255) NOT NULL,NodeType INT NOT NULL,Weight INT NOT NULL,IsChanged INT NOT NULL,RoadOrder INT NULL,RoadPercent DOUBLE NULL,RoadVersion BIGINT NULL,UpdatedAt DATETIME(6) NOT NULL) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4");
        Sql(settings,$"INSERT INTO LogicalNodes VALUES ('ABCDEFGHIJ','owner',7,110.5,110.5,6,'{a.Code}','中文 LN','保留说明',99,35,1,0,0.5125,{a.Version},UTC_TIMESTAMP(6)),('ZYXWVUTSRQ','owner',9,300.5,300.5,12,'{a.Code}','空 LN','原最近投影已不适用',99,40,-1,NULL,NULL,NULL,UTC_TIMESTAMP(6))");
        Sql(settings,"UPDATE MapMetadata SET SchemaVersion=11 WHERE Id=1");
        if(failCopy)
        {
            Sql(settings,"UPDATE LogicalNodes SET X=110 WHERE Code='ABCDEFGHIJ'");
            Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));
            using var verifyConnection=new MySqlConnection(settings.ConnectionString());verifyConnection.Open();using var verify=verifyConnection.CreateCommand();
            verify.CommandText="SELECT X FROM LogicalNodes WHERE Code='ABCDEFGHIJ'";Assert.AreEqual(110m,Convert.ToDecimal(verify.ExecuteScalar()),"Failed shadow copy must retain the original live table.");
            verify.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(11,Convert.ToInt32(verify.ExecuteScalar()));
            Sql(settings,"UPDATE LogicalNodes SET X=110.5 WHERE Code='ABCDEFGHIJ'");
        }
        // Failure at the version publication occurs AFTER atomic table publication. Retry must recognize actual columns.
        Sql(settings,"CREATE TRIGGER TestPositionMigration BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=12 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected schema publication failure'; END IF; END");
        try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));}
        finally{Sql(settings,"DROP TRIGGER TestPositionMigration");}
        repository=new MySqlLineRepository(settings);var nodes=new LogicalNodeService(repository).GetAll().Data!;Assert.AreEqual(2,nodes.Length);
        var ln=nodes.Single(n=>n.Code=="ABCDEFGHIJ");Assert.AreEqual(3,ln.Positions.Length);Assert.AreEqual("中文 LN",ln.Properties.Value);Assert.AreEqual(8,ln.Version);
        Assert.AreEqual("LN_ABCDEFGHIJ",ln.Properties.LogicLanguage);
        Assert.AreEqual(0,nodes.Single(n=>n.Code=="ZYXWVUTSRQ").Positions.Length);
        CollectionAssert.AreEqual(new[]{a.Code,b.Code}.Order().ToArray(),ln.Positions.Select(p=>p.RoadCode).Distinct().Order().ToArray());
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var command=connection.CreateCommand();
        command.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(command.ExecuteScalar()));
        command.Parameters.AddWithValue("@db",settings.Database);command.CommandText="SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND COLUMN_NAME IN ('RoadCode','RoadOrder','RoadPercent','RoadVersion','IsChanged')";Assert.AreEqual(0,Convert.ToInt32(command.ExecuteScalar()));
        Assert.AreEqual(Fingerprint(ln),Fingerprint(Node(new MySqlLineRepository(settings),ln.Code)));
        return Task.CompletedTask;
    });
}
