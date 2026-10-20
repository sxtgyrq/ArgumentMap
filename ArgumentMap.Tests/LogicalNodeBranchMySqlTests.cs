using System.Net.Http;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeBranchMySqlTests
{
    private static LogicalNodeSnapshot Node(ILineRepository repository,int size,string value,string logic,decimal x,string owner="owner")
    {var r=new LogicalNodeService(repository).Create(new(owner,x,50.5m,size,new(){Value=value,LogicLanguage=logic,Info="保留说明"}));Assert.IsTrue(r.Success,r.Message);return r.Data!;}
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}
    [TestMethod,DataRow(false),DataRow(true)]
    public Task HttpRenameOrDeleteRollsBackEveryDependentWrite_OnLateFailure_AndRetryPersists(bool delete)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,10,50,400,50);var source=Node(repository,6,"判断","P",50.5m);var yes=Node(repository,4,"True","P",100.5m);var no=Node(repository,4,"False","¬P",150.5m);
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,$"CREATE TRIGGER TestBranchSync BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='{no.Code}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN4 sync failure'; END IF; END");
        try
        {var code=delete?(await client.DeleteLogicalNodeAsync(source.Code,source.Version)).Code:(await client.UpdateLogicalNodeAsync(source.Code,source.Version,"新判断","","Q")).Code;Assert.AreEqual("DatabaseError",code);}
        finally{Sql(settings,"DROP TRIGGER TestBranchSync");}
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        if(delete)Assert.IsTrue((await client.DeleteLogicalNodeAsync(source.Code,source.Version)).Success);
        else Assert.IsTrue((await client.UpdateLogicalNodeAsync(source.Code,source.Version,"新判断","","Q")).Success);
        var nodes=(await client.GetLogicalNodesAsync()).Data!;
        Assert.AreEqual(delete?"":"Q",nodes.Single(n=>n.Code==yes.Code).Properties.LogicLanguage);Assert.AreEqual(delete?"":"¬Q",nodes.Single(n=>n.Code==no.Code).Properties.LogicLanguage);
        foreach(var branch in new[]{yes,no}){var current=nodes.Single(n=>n.Code==branch.Code);Assert.AreEqual(branch.Properties.Value,current.Properties.Value);Assert.AreEqual(branch.Version+1,current.Version);CollectionAssert.AreEqual(branch.Positions,current.Positions);}
        Assert.AreEqual("VersionConflict",(await client.UpdateLogicalNodeAsync(no.Code,no.Version,"False","","")).Code);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });
    [TestMethod]
    public Task DatabaseValueCheckAndDalReferencesRejectBypass_AndTwoServersSerializeRenameVersusEdit()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);var source=Node(repository,6,"判断","P",50.5m,"alice");var branch=Node(repository,4,"False","¬P",100.5m,"bob");var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var value in new[]{"true","是","False ",""})Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET `Value`='{value}' WHERE Code='{branch.Code}'")).Number);
        Assert.ThrowsExactly<InvalidLogicalNodeBranchException>(()=>repository.Transaction(s=>{s.LogicalNodes[branch.Code]=branch with{Properties=branch.Properties with{LogicLanguage="¬missing"}};return true;}));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        await using var a=await LocalLineServer.StartAsync(repository:repository);await using var b=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));using var h1=new HttpClient{BaseAddress=a.BaseAddress};using var h2=new HttpClient{BaseAddress=b.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,a,"alice");var bob=await TestLogin.ClientAsync(h2,b,"bob");
        var rename=alice.UpdateLogicalNodeAsync(source.Code,source.Version,"新判断","","Q");var edit=bob.UpdateLogicalNodeAsync(branch.Code,branch.Version,"False","编辑说明","¬P");
        await Task.WhenAll(rename,edit);Assert.IsTrue(rename.Result.Success,rename.Result.Message);Assert.IsTrue(edit.Result.Success||edit.Result.Code=="VersionConflict",edit.Result.Message);
        var after=repository.Transaction(s=>s.LogicalNodes[branch.Code]);Assert.AreEqual("False",after.Properties.Value);Assert.AreEqual("¬Q",after.Properties.LogicLanguage);Assert.IsNull(LogicalNodeBranchRules.Validate(repository.Transaction(s=>s.LogicalNodes.Values.ToArray())));
    });
    [TestMethod,DataRow("none"),DataRow("dml"),DataRow("ddl"),DataRow("publish")]
    public Task Upgrade21BacksUpAndRepairsBooleansAndInvalidLogic_RecoversWithoutDoubleVersionIncrements(string failure)=>Isolated(settings=>
    {
        new MySqlLineRepository(settings);Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_BranchValue; UPDATE MapMetadata SET SchemaVersion=21 WHERE Id=1");
        Sql(settings,"""
            INSERT INTO LogicalNodes(Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage) VALUES
            ('AAAAAAAAAA','owner',1,50.5,50.5,6,'判断','原说明','P'),
            ('BBBBBBBBBB','owner',2,100.5,50.5,4,'是','原说明','P'),
            ('CCCCCCCCCC','other',3,150.5,50.5,4,'否','原说明','¬P'),
            ('DDDDDDDDDD','owner',4,200.5,50.5,4,'未知','原说明','missing'),
            ('EEEEEEEEEE','owner',5,250.5,50.5,4,'True','原说明','P'),
            ('FFFFFFFFFF','owner',6,300.5,50.5,4,'False','原说明','P');
            """);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(failure=="dml")Sql(settings,"CREATE TRIGGER TestBranchMigration BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='CCCCCCCCCC' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='LN4 DML failure'; END IF; END");
        if(failure=="ddl")Sql(settings,"ALTER TABLE LogicalNodes ADD CONSTRAINT CK_LN_BranchValue CHECK (1=1)");
        if(failure=="publish")Sql(settings,"CREATE TRIGGER TestBranchMigration BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=22 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='LN4 publish failure'; END IF; END");
        // Force DDL failure with the reserved constraint name on a different table.
        if(failure=="ddl"){Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_BranchValue; ALTER TABLE MapMetadata ADD CONSTRAINT CK_LN_BranchValue CHECK (1=1)");}
        if(failure!="none")
        {
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(failure=="dml"?21:2101,Version(settings));if(failure=="dml")Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
            finally{Sql(settings,failure=="ddl"?"ALTER TABLE MapMetadata DROP CHECK CK_LN_BranchValue":"DROP TRIGGER TestBranchMigration");}
        }
        var repository=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));var nodes=repository.Transaction(s=>s.LogicalNodes);
        Assert.AreEqual("True",nodes["BBBBBBBBBB"].Properties.Value);Assert.AreEqual("P",nodes["BBBBBBBBBB"].Properties.LogicLanguage);Assert.AreEqual(3L,nodes["BBBBBBBBBB"].Version);
        Assert.AreEqual("False",nodes["CCCCCCCCCC"].Properties.Value);Assert.AreEqual("¬P",nodes["CCCCCCCCCC"].Properties.LogicLanguage);Assert.AreEqual(4L,nodes["CCCCCCCCCC"].Version);
        foreach(var code in new[]{"DDDDDDDDDD","FFFFFFFFFF"}){Assert.AreEqual("False",nodes[code].Properties.Value);Assert.AreEqual("",nodes[code].Properties.LogicLanguage);}
        Assert.AreEqual(5L,nodes["EEEEEEEEEE"].Version);Assert.AreEqual(1L,nodes["AAAAAAAAAA"].Version);Assert.IsTrue(nodes.Values.All(n=>n.Properties.Info=="原说明"));
        using var plan=JsonDocument.Parse(System.IO.File.ReadAllText(repository.LogicalNodeBranchBackupPath!));Assert.AreEqual(22,plan.RootElement.GetProperty("TargetVersion").GetInt32());
        var after=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });
    [TestMethod]
    public Task TwoServers_DeleteSourceVersusCreateBranchNeverCommitsDanglingReference()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);var source=Node(repository,6,"判断","P",50.5m,"alice");
        await using var a=await LocalLineServer.StartAsync(repository:repository);await using var b=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));using var h1=new HttpClient{BaseAddress=a.BaseAddress};using var h2=new HttpClient{BaseAddress=b.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,a,"alice");var bob=await TestLogin.ClientAsync(h2,b,"bob");
        var add=bob.CreateLogicalNodeAsync(100.5m,50.5m,4,new(){Value="True",LogicLanguage="P"});var delete=alice.DeleteLogicalNodeAsync(source.Code,source.Version);await Task.WhenAll(add,delete);
        Assert.IsTrue(delete.Result.Success,delete.Result.Message);Assert.IsTrue(add.Result.Success||add.Result.Code=="BranchSourceNotFound",add.Result.Message);
        var nodes=repository.Transaction(s=>s.LogicalNodes.Values.ToArray());Assert.IsFalse(nodes.Any(n=>n.Code==source.Code));LogicalNodeBranchRules.EnsureValid(nodes);
        if(add.Result.Success){Assert.AreEqual("True",nodes.Single().Properties.Value);Assert.AreEqual("",nodes.Single().Properties.LogicLanguage);}
    });
}
