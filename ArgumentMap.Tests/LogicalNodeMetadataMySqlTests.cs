using System.Net.Http;
using System.Net.Http.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeMetadataMySqlTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public Task Upgrade13RenamesNameAndDropsTypeWeight_PreservesGeometryPositionsAndRetriesPublication(bool interrupt)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,90,110,130,110);var service=new LogicalNodeService(repository);
        var node=service.Create(new("owner",110.5m,110.5m,18,new(){Value="中文旧名称",Info="保留说明"})).Data!;var before=Snapshot(repository);
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_InstanceValue, DROP CHECK CK_LN_InstanceLogic, DROP CHECK CK_LN_BranchValue, DROP CHECK CK_LN_RelationValue, DROP CHECK CK_LN_RelationLogic, DROP CHECK CK_LN_Value12Upper, DROP INDEX UQ_LN_Value12, DROP COLUMN Value12Key, DROP CHECK CK_LN_V14, CHANGE COLUMN `Value` Name VARCHAR(32) NOT NULL, ADD COLUMN NodeType INT NOT NULL DEFAULT 99, ADD COLUMN Weight INT NOT NULL DEFAULT 10, ADD CONSTRAINT CK_LN_V13 CHECK (MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (6,12,18) AND Weight BETWEEN 0 AND 99 AND NodeType>=0)");
        Sql(settings,"UPDATE LogicalNodes SET NodeType=17,Weight=35; UPDATE MapMetadata SET SchemaVersion=13 WHERE Id=1");
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER TestMetadataPublication BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=14 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected metadata publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));}
            finally{Sql(settings,"DROP TRIGGER TestMetadataPublication");}
            using var failedConnection=new MySqlConnection(settings.ConnectionString());failedConnection.Open();using var query=failedConnection.CreateCommand();query.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(13,Convert.ToInt32(query.ExecuteScalar()));
            Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>true),"Unpublished migration must not accept a map transaction.");
        }
        repository=new MySqlLineRepository(settings);Assert.AreEqual(before,Snapshot(repository));
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var check=connection.CreateCommand();check.Parameters.AddWithValue("@db",settings.Database);
        check.CommandText="SELECT COUNT(*) FROM information_schema.COLUMNS WHERE TABLE_SCHEMA=@db AND TABLE_NAME='LogicalNodes' AND COLUMN_NAME IN ('Name','NodeType','Weight')";Assert.AreEqual(0,Convert.ToInt32(check.ExecuteScalar()));
        check.CommandText="SELECT `Value`,Info FROM LogicalNodes";using(var reader=check.ExecuteReader()){Assert.IsTrue(reader.Read());Assert.AreEqual("中文旧名称",reader.GetString(0));Assert.AreEqual("保留说明",reader.GetString(1));}
        Assert.AreEqual(before,Snapshot(new MySqlLineRepository(settings)));
        return Task.CompletedTask;
    });
    [TestMethod]
    public Task ConcurrentMetadataEditsUseVersions_AndGeometrySpoofingIsIgnored()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");
        repository.Transaction(s=>{s.Accounts["bob"]=s.Accounts["bob"] with{EditAllLogicalNodes=true};return true;});
        await Add(alice,90,110,130,110);var node=(await alice.CreateLogicalNodeAsync(110.5m,110.5m,18,new())).Data!;
        var results=await Task.WhenAll(alice.UpdateLogicalNodeAsync(node.Code,node.Version,"甲值","甲说明"),bob.UpdateLogicalNodeAsync(node.Code,node.Version,"乙值","乙说明"));
        Assert.AreEqual(1,results.Count(r=>r.Success));Assert.AreEqual("VersionConflict",results.Single(r=>!r.Success).Code);var current=results.Single(r=>r.Success).Data!;
        CollectionAssert.AreEqual(node.Positions,current.Positions);Assert.AreEqual(node.X,current.X);Assert.AreEqual(node.Y,current.Y);Assert.AreEqual(18,current.SideLength);
        var winner=current.Owner=="alice"?alice:bob;var http=current.Owner=="alice"?h1:h2;
        using var response=await http.PostAsJsonAsync("UpdateLogicalNode",new{Code=node.Code,Owner=winner.Session.UserName,Version=current.Version,Value="最终值",Info="最终说明",X=1.5,Y=2.5,SideLength=6,Type=1,Weight=99,LoginCode=winner.Session.LoginCode});
        var result=await response.Content.ReadFromJsonAsync<ServiceResult<LogicalNodeSnapshot>>();Assert.IsTrue(result!.Success);Assert.AreEqual(node.X,result.Data!.X);Assert.AreEqual(node.Y,result.Data.Y);Assert.AreEqual(18,result.Data.SideLength);Assert.AreEqual("最终值",result.Data.Properties.Value);
        CollectionAssert.AreEqual(node.Positions,result.Data.Positions);
    });
    [TestMethod]
    public Task MetadataSqlFailureRollsBackOwnerVersionValueInfoAndPositions_ThenRetryPersists()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        await Add(client,90,110,130,110);var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Data!;var before=Snapshot(repository);
        Sql(settings,"CREATE TRIGGER TestMetadataSave BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN metadata failure'");
        try{Assert.AreEqual("DatabaseError",(await client.UpdateLogicalNodeAsync(node.Code,node.Version,"新值","新说明")).Code);Assert.AreEqual(before,Snapshot(repository));}
        finally{Sql(settings,"DROP TRIGGER TestMetadataSave");}
        var updated=await client.UpdateLogicalNodeAsync(node.Code,node.Version,"新值","新说明");Assert.IsTrue(updated.Success);CollectionAssert.AreEqual(node.Positions,updated.Data!.Positions);
        var restarted=new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single();Assert.AreEqual("新值",restarted.Properties.Value);Assert.AreEqual("新说明",restarted.Properties.Info);Assert.AreEqual(node.Version+1,restarted.Version);
    });
}
