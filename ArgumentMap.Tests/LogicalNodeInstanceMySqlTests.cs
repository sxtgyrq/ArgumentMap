using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeInstanceMySqlTests
{
    [TestMethod]
    public Task EnglishEligibilityEnforcedOverHttpAndDal_LegacyChineseInstancesRemainUsable()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);var parent=LogicalNodeInstanceTests.Parent(repository,"H人");
        LogicalNodeInstanceTests.Child(repository,"h人(id=1)");var chinese=LogicalNodeInstanceTests.Parent(repository,"人",200.5m);
        var legacyCode=LineCode.Generate();
        // Reproduce a valid version-27 record created before the eligibility rule.
        Sql(settings,$"INSERT INTO LogicalNodes(Code,Owner,X,Y,SideLength,`Value`,LogicLanguage) VALUES('{legacyCode}','owner',250.5,50.5,10,'人(id=1)','人(id=1)∈人∧1∈人.ID')");
        repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        Assert.IsTrue((await client.GetLogicalNodesAsync()).Success);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        var rejected=await client.UpdateLogicalNodeAsync(parent.Code,parent.Version,"人","",parent.Properties.LogicLanguage);
        Assert.AreEqual("InvalidInstanceParentValue",rejected.Code,rejected.Message);StringAssert.Contains(rejected.Message,"英文字母");
        Assert.AreEqual("InvalidInstanceValue",(await client.CreateLogicalNodeAsync(300.5m,50.5m,10,new(){Value="人"})).Code);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.ThrowsExactly<InvalidLogicalNodeInstanceException>(()=>repository.Transaction(s=>
        {var code=LineCode.Generate();s.LogicalNodes[code]=new(code,"owner",0,300.5m,50.5m,10,new(){Value="人(id=1)",LogicLanguage="人(id=1)∈人∧1∈人.ID"});return true;}));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var edited=await client.UpdateLogicalNodeAsync(legacyCode,0,"人(id=1)","旧实例仍可编辑说明","人(id=1)∈人∧1∈人.ID");Assert.IsTrue(edited.Success,edited.Message);
        rejected=await client.UpdateLogicalNodeAsync(chinese.Code,chinese.Version,"无英文字母","",chinese.Properties.LogicLanguage);
        Assert.AreEqual("InvalidInstanceParentValue",rejected.Code,rejected.Message);
        Assert.IsTrue((await client.DeleteLogicalNodeAsync(legacyCode,edited.Data!.Version)).Success);
        var allowed=await client.UpdateLogicalNodeAsync(chinese.Code,chinese.Version,"无英文字母","",chinese.Properties.LogicLanguage);
        Assert.IsTrue(allowed.Success,allowed.Message);
        var renamed=await client.UpdateLogicalNodeAsync(parent.Code,parent.Version,"HUMAN","",parent.Properties.LogicLanguage);Assert.IsTrue(renamed.Success,renamed.Message);
        var child=(await client.GetLogicalNodesAsync()).Data!.Single(n=>n.SideLength==10);
        Assert.AreEqual("human(id=1)",child.Properties.Value);Assert.AreEqual("human(id=1)∈HUMAN∧1∈HUMAN.ID",child.Properties.LogicLanguage);
    });

    [TestMethod]
    public Task ConcurrentInstanceCreationAndParentRenameCannotCreateIneligibleRelationship()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);var parent=LogicalNodeInstanceTests.Parent(repository,"HUMAN");
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};
        var owner=await TestLogin.ClientAsync(http,server,"owner");var other=await TestLogin.ClientAsync(http,server,"other");
        var create=other.CreateLogicalNodeAsync(90.5m,50.5m,10,new(){Value="human(id=1)"});
        var rename=owner.UpdateLogicalNodeAsync(parent.Code,parent.Version,"人","",parent.Properties.LogicLanguage);
        await Task.WhenAll(create,rename);Assert.AreEqual(1,(create.Result.Success?1:0)+(rename.Result.Success?1:0));
        var nodes=(await owner.GetLogicalNodesAsync()).Data!;
        if(create.Result.Success)
        {Assert.AreEqual("InvalidInstanceParentValue",rename.Result.Code);Assert.IsTrue(nodes.Any(n=>n.SideLength==12&&n.Properties.Value=="HUMAN"));}
        else
        {Assert.AreEqual("InstanceParentNotFound",create.Result.Code);Assert.AreEqual(0,nodes.Count(n=>n.SideLength==10));}
    });

    [TestMethod]
    public Task ConcurrentRepeatedPaste_ParentRenameRollbackAndSync_DeletionProtection_ExpiredSession()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);var parent=LogicalNodeInstanceTests.Parent(repository);
        await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var a=new HttpClient{BaseAddress=first.BaseAddress};using var b=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(a,first,"owner");var bob=await TestLogin.ClientAsync(b,second,"other");
        var p=LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(10,new(){Value="people(id=1)",LogicLanguage="people(id=1)∈PEOPLE∧1∈PEOPLE.ID",Info="副本"}))!.Properties;
        var pending=new[]{alice.CreateLogicalNodeAsync(90.5m,50.5m,10,p),bob.CreateLogicalNodeAsync(130.5m,50.5m,10,p)};
        await Task.WhenAll(pending);foreach(var result in pending)Assert.IsTrue(result.Result.Success,result.Result.Message);
        Assert.AreNotEqual(pending[0].Result.Data!.Code,pending[1].Result.Data!.Code);
        var nodes=(await alice.GetLogicalNodesAsync()).Data!;Assert.AreEqual(2,nodes.Count(n=>n.SideLength==10&&n.Properties.Value=="people(id=1)"));
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailInstanceRename BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.SideLength=10 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected instance synchronization failure'; END IF; END");
        try
        {
            var failed=await alice.UpdateLogicalNodeAsync(parent.Code,parent.Version,"NEWPEOPLE","",parent.Properties.LogicLanguage);
            Assert.AreEqual("DatabaseError",failed.Code,failed.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        finally{Sql(settings,"DROP TRIGGER FailInstanceRename");}
        var rename=await alice.UpdateLogicalNodeAsync(parent.Code,parent.Version,"NEWPEOPLE","",parent.Properties.LogicLanguage);
        Assert.IsTrue(rename.Success,rename.Message);
        var restarted=new MySqlLineRepository(settings);var persisted=new LogicalNodeService(restarted).GetAll().Data!;
        foreach(var child in persisted.Where(n=>n.SideLength==10))
        {
            Assert.AreEqual("newpeople(id=1)",child.Properties.Value);Assert.AreEqual("newpeople(id=1)∈NEWPEOPLE∧1∈NEWPEOPLE.ID",child.Properties.LogicLanguage);
            var old=nodes.Single(n=>n.Code==child.Code);Assert.AreEqual(old.Owner,child.Owner);Assert.AreEqual(old.Version+1,child.Version);
        }
        before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LogicalNodeHasInstances",(await alice.DeleteLogicalNodeAsync(parent.Code,rename.Data!.Version)).Code);
        var selected=await alice.SelectMapAreaAsync(new(30,30,110,70));Assert.IsTrue(selected.Success,selected.Message);
        Assert.AreEqual("LogicalNodeHasInstances",(await alice.DeleteMapAreaAsync(selected.Data!)).Code);
        var expired=new HttpLineClient(a,new("owner","invalid"));
        Assert.AreEqual("LoginExpired",(await expired.CreateLogicalNodeAsync(170.5m,50.5m,10,p)).Code);
        Assert.AreEqual("InstanceParentNotFound",(await alice.CreateLogicalNodeAsync(170.5m,50.5m,10,p)).Code);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        foreach(var child in persisted.Where(n=>n.SideLength==10))
            Assert.IsTrue((await(child.Owner=="owner"?alice:bob).DeleteLogicalNodeAsync(child.Code,child.Version)).Success);
        Assert.IsTrue((await alice.DeleteLogicalNodeAsync(parent.Code,rename.Data.Version)).Success);
    });

    [TestMethod]
    public Task SqlLocalChecksAndDalMembershipGuard_AndInstanceMoveMarkLifecycle()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repository);
        Add(repository,70,100,150,100);var child=LogicalNodeInstanceTests.Child(repository,x:90.5m,y:100.5m);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var sql in new[]{"UPDATE LogicalNodes SET `Value`='PEOPLE' WHERE SideLength=10","UPDATE LogicalNodes SET LogicLanguage='wrong' WHERE SideLength=10"})
        {Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,sql)).Number);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        Assert.ThrowsExactly<InvalidLogicalNodeInstanceException>(()=>repository.Transaction(s=>{s.LogicalNodes.Remove(s.LogicalNodes.Values.Single(n=>n.SideLength==12).Code);return true;}));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Sql(settings,"CREATE TABLE InstancePositionAudit (OldFlag INT,NewFlag INT); CREATE TRIGGER AuditInstancePosition AFTER UPDATE ON LogicalNodePositions FOR EACH ROW INSERT INTO InstancePositionAudit VALUES(OLD.IsChanged,NEW.IsChanged)");
        var moved=new LogicalNodeService(repository).Move(new(child.Code,"owner",child.Version,110.5m,100.5m));Assert.IsTrue(moved.Success,moved.Message);
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var q=connection.CreateCommand();
        q.CommandText="SELECT COUNT(*) FROM InstancePositionAudit WHERE NewFlag=-1";Assert.IsTrue(Convert.ToInt32(q.ExecuteScalar())>0);
        q.CommandText="SELECT COUNT(*) FROM LogicalNodePositions WHERE LogicalNodeCode=@code AND IsChanged=1";q.Parameters.AddWithValue("@code",child.Code);
        Assert.AreEqual(1,Convert.ToInt32(q.ExecuteScalar()));
        return Task.CompletedTask;
    });

    [TestMethod,DataRow("normal"),DataRow("ddl"),DataRow("publish")]
    public Task Schema26UpgradeAndRetry_PreserveData_FailClosedUntilVersion27(string phase)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repository);
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_InstanceValue, DROP CHECK CK_LN_InstanceLogic; UPDATE MapMetadata SET SchemaVersion=26 WHERE Id=1");
        if(phase=="ddl")Sql(settings,"ALTER TABLE MapMetadata ADD CONSTRAINT CK_LN_InstanceValue CHECK (1=1)");
        if(phase=="publish")Sql(settings,"CREATE TRIGGER FailInstancePublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=27 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN10 publication failure'; END IF; END");
        if(phase!="normal")
        {
            try
            {
                Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));
                using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var q=connection.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";
                Assert.AreEqual(2601,Convert.ToInt32(q.ExecuteScalar()));Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>true));
            }
            finally{Sql(settings,phase=="ddl"?"ALTER TABLE MapMetadata DROP CHECK CK_LN_InstanceValue":"DROP TRIGGER FailInstancePublish");}
        }
        repository=new MySqlLineRepository(settings);
        using(var verified=new MySqlConnection(settings.ConnectionString())){verified.Open();using var q=verified.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(q.ExecuteScalar()));}
        Assert.AreEqual("PEOPLE",new LogicalNodeService(repository).GetAll().Data!.Single().Properties.Value);
        LogicalNodeInstanceTests.Child(repository);var before=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });

    [TestMethod]
    public Task ImportedInstancesRollbackTogether_ParentPrioritySurvivesNumbering_AndMissingGuardFailsClosed()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repository);
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var request=LogicalNodeInstanceTests.Document();var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailInstanceImport BEFORE INSERT ON LogicalNodes FOR EACH ROW BEGIN IF NEW.SideLength=10 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected instance import failure'; END IF; END");
        try{var failed=await client.ImportFlowDiagramAsync(request,1000.5m,1000.5m);Assert.AreEqual("DatabaseError",failed.Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailInstanceImport");}
        var imported=await client.ImportFlowDiagramAsync(request,1000.5m,1000.5m);Assert.IsTrue(imported.Success,imported.Message);
        Assert.IsTrue(imported.Data!.Added.LogicalNodes.Where(n=>n.SideLength==10).All(n=>n.Properties.Value=="people1(id=1)"&&n.Properties.LogicLanguage=="people1(id=1)∈PEOPLE1∧1∈PEOPLE1.ID"));
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_InstanceLogic");Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));
    });
}
