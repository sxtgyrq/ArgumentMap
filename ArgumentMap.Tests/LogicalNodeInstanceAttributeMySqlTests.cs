using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeInstanceAttributeMySqlTests
{
    private static T Query<T>(MySqlSettings settings,string sql)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText=sql;return (T)Convert.ChangeType(q.ExecuteScalar()!,typeof(T));}
    private static void SetPayload(MySqlConnection c,long sequence,JsonNode payload)
    {using var q=c.CreateCommand();q.CommandText="UPDATE MapUndoHistory SET Payload=@payload WHERE Sequence=@seq";q.Parameters.AddWithValue("@payload",payload.ToJsonString());q.Parameters.AddWithValue("@seq",sequence);q.ExecuteNonQuery();}
    // Reproduce an actual v31 database, including its old CHECKs and old undo snapshots.
    private static void Legacy31(MySqlSettings settings)
    {
        Sql(settings,"DROP TRIGGER TR_LN10_Attributes_Insert; DROP TRIGGER TR_LN10_Attributes_Update; ALTER TABLE LogicalNodes DROP CHECK CK_LN_LogicLength, DROP CHECK CK_LN_InstanceValue, DROP CHECK CK_LN_InstanceLogic, DROP CHECK CK_LN_InstanceAttributes, DROP CHECK CK_LN_ValueLength");
        Sql(settings,"UPDATE LogicalNodes SET `Value`=SUBSTRING_INDEX(`Value`,'(',1),LogicLanguage=CONCAT(SUBSTRING_INDEX(`Value`,'(',1),' ∈ ',UPPER(SUBSTRING_INDEX(`Value`,'(',1))) WHERE SideLength=10");
        using(var c=new MySqlConnection(settings.ConnectionString()))
        {
            c.Open();var rows=new List<(long,JsonNode)>();using(var q=c.CreateCommand())
            {q.CommandText="SELECT Sequence,Payload FROM MapUndoHistory";using var r=q.ExecuteReader();while(r.Read())rows.Add((r.GetInt64(0),JsonNode.Parse(r.GetString(1))!));}
            foreach(var (seq,payload) in rows)
            {
                foreach(var change in payload["Delta"]!["Nodes"]!.AsArray())foreach(var key in new[]{"Before","After"})
                    if(change![key] is {} node&&node["SideLength"]!.GetValue<int>()==10)
                    {
                        var p=node["Properties"]!;var value=p["Value"]!.GetValue<string>().Split('(',2)[0];
                        p["Value"]=value;p["LogicLanguage"]=value+" ∈ "+LogicalNodeValueUniqueness.Normalize(value);
                    }
                SetPayload(c,seq,payload);
            }
        }
        Sql(settings,"ALTER TABLE LogicalNodes MODIFY COLUMN `Value` VARCHAR(32) NOT NULL DEFAULT '', ADD CONSTRAINT CK_LN_InstanceValue CHECK (SideLength<>10 OR CONVERT(`Value` USING binary)=CONVERT(LOWER(`Value`) USING binary)), ADD CONSTRAINT CK_LN_InstanceLogic CHECK (SideLength<>10 OR CONVERT(LogicLanguage USING binary)=CONVERT(CONCAT(`Value`,' ∈ ',UPPER(`Value`)) USING binary)); UPDATE MapMetadata SET SchemaVersion=31 WHERE Id=1");
    }

    [TestMethod,DataRow("rename"),DataRow("delete"),DataRow("create")]
    public Task UpgradeMigratesCurrentAndHistoricalInstances_AndDatabaseUndoStillWorks(string operation)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        Add(repo,70,50,150,50);
        var created=MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Create(new("owner",90.5m,50.5m,10,new(){Value="human(id=old)",Info="旧说明\n保持"})));
        Assert.IsTrue(created.Success,created.Message);var child=created.Data!;
        if(operation=="rename")Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(parent.Code,"owner",parent.Version,"PEOPLE","",LogicLanguage:parent.Properties.LogicLanguage))).Success);
        if(operation=="delete")Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Delete(new(child.Code,"owner",child.Version))).Success);
        Legacy31(settings);
        var version=operation=="delete"?0L:Query<long>(settings,"SELECT Version FROM LogicalNodes WHERE SideLength=10");
        var positionFlags=Query<string>(settings,"SELECT COALESCE(GROUP_CONCAT(CONCAT(LogicalNodeCode,':',IsChanged) ORDER BY LogicalNodeCode),'') FROM LogicalNodePositions");
        var stamps=Query<string>(settings,"SELECT GROUP_CONCAT(CONCAT(ObjectKey,':',OperationId) ORDER BY ObjectKey) FROM MapEditStamps");
        repo=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Query<int>(settings,"SELECT SchemaVersion FROM MapMetadata"));
        Assert.IsTrue(System.IO.File.Exists(repo.LogicalNodeInstanceAttributeBackupPath));
        Assert.AreEqual(stamps,Query<string>(settings,"SELECT GROUP_CONCAT(CONCAT(ObjectKey,':',OperationId) ORDER BY ObjectKey) FROM MapEditStamps"));
        Assert.AreEqual(positionFlags,Query<string>(settings,"SELECT COALESCE(GROUP_CONCAT(CONCAT(LogicalNodeCode,':',IsChanged) ORDER BY LogicalNodeCode),'') FROM LogicalNodePositions"));
        if(operation!="delete")
        {
            var current=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.SideLength==10);
            Assert.AreEqual($"{(operation=="rename"?"people":"human")}(id={child.Code})",current.Properties.Value);
            Assert.AreEqual(version+1,current.Version);Assert.AreEqual(child.Properties.Info,current.Properties.Info);
            var before=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        var undo=new MapUndoService(repo).Undo("owner");Assert.IsTrue(undo.Success,undo.Message);
        if(operation=="create")Assert.IsFalse(new LogicalNodeService(repo).GetAll().Data!.Any(n=>n.SideLength==10));
        else
        {
            var restored=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.SideLength==10);
            Assert.AreEqual($"human(id={child.Code})∈HUMAN∧{child.Code}∈HUMAN.ID",restored.Properties.LogicLanguage);Assert.AreEqual(child.Properties.Info,restored.Properties.Info);
        }
        return Task.CompletedTask;
    });

    [TestMethod,DataRow("map"),DataRow("history"),DataRow("ddl"),DataRow("publish")]
    public Task InterruptedUpgradeStaysClosedAndRetriesWithoutDoubleMigration(string phase)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var created=MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Create(new("owner",90.5m,50.5m,10,new(){Value="human(id=old)"})));
        Assert.IsTrue(created.Success);Legacy31(settings);
        var oldVersion=Query<long>(settings,"SELECT Version FROM LogicalNodes WHERE SideLength=10");
        var oldPayload=Query<string>(settings,"SELECT Payload FROM MapUndoHistory LIMIT 1");
        var setup=phase switch
        {
            "map"=>"CREATE TRIGGER FailAttributes BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected instance repair failure'",
            "history"=>"CREATE TRIGGER FailAttributes BEFORE UPDATE ON MapUndoHistory FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected undo repair failure'",
            "ddl"=>"ALTER TABLE MapMetadata ADD CONSTRAINT CK_LN_InstanceAttributes CHECK (1=1)",
            _=>"CREATE TRIGGER FailAttributes BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=35 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected schema publish failure'; END IF; END"
        };
        Sql(settings,setup);
        try
        {
            Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));
            Assert.AreEqual(3401,Query<int>(settings,"SELECT SchemaVersion FROM MapMetadata"));
            Assert.ThrowsExactly<InvalidOperationException>(()=>repo.Transaction(s=>true));
            if(phase is "map" or "history")
            {
                Assert.AreEqual("human",Query<string>(settings,"SELECT `Value` FROM LogicalNodes WHERE SideLength=10"));
                Assert.AreEqual(oldVersion,Query<long>(settings,"SELECT Version FROM LogicalNodes WHERE SideLength=10"));
                Assert.AreEqual(oldPayload,Query<string>(settings,"SELECT Payload FROM MapUndoHistory LIMIT 1"));
            }
        }
        finally{Sql(settings,phase=="ddl"?"ALTER TABLE MapMetadata DROP CHECK CK_LN_InstanceAttributes":"DROP TRIGGER FailAttributes");}
        repo=new MySqlLineRepository(settings);
        var current=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.SideLength==10);
        Assert.AreEqual($"human(id={created.Data!.Code})∈HUMAN∧{created.Data!.Code}∈HUMAN.ID",current.Properties.LogicLanguage);Assert.AreEqual(oldVersion+1,current.Version);
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Query<int>(settings,"SELECT SchemaVersion FROM MapMetadata"));
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);
        return Task.CompletedTask;
    });

    [TestMethod,DataRow("human"),DataRow("human(id=)"),DataRow("human( =Alice)"),DataRow("human(id= )"),
        DataRow("human(id=(1))"),DataRow("human(id=1)(id=2)"),DataRow("Human(name=Alice)")]
    public Task DatabaseChecksRejectMalformedValuesEvenWithMatchingLogic(string invalid)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");LogicalNodeInstanceTests.Child(repo,"human(name=Alice)");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();
        q.CommandText="UPDATE LogicalNodes SET `Value`=@value,LogicLanguage=@logic WHERE SideLength=10";
        q.Parameters.AddWithValue("@value",invalid);q.Parameters.AddWithValue("@logic",LogicalNodeInstanceRules.Expression(invalid));
        Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>q.ExecuteNonQuery()).Number);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });

    [TestMethod]
    public Task HttpCreationEditingConcurrentDefaultsAndDatabaseLengthsPreservePayloadCase()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};
        var owner=await TestLogin.ClientAsync(http,server,"owner");var other=await TestLogin.ClientAsync(http,server,"other");
        var pending=new[]{owner.CreateLogicalNodeInstanceAsync(90.5m,50.5m,parent.Code,new()),other.CreateLogicalNodeInstanceAsync(130.5m,50.5m,parent.Code,new())};
        await Task.WhenAll(pending);
        foreach(var task in pending)
        {Assert.IsTrue(task.Result.Success,task.Result.Message);var child=task.Result.Data!;Assert.AreEqual($"human(id={child.Code})∈HUMAN∧{child.Code}∈HUMAN.ID",child.Properties.LogicLanguage);}
        var first=pending[0].Result.Data!;var value="HUMAN(name="+new string('A',70)+")";
        var changed=await owner.UpdateLogicalNodeAsync(first.Code,first.Version,value,"Info\nTwo", "ignored");
        Assert.IsTrue(changed.Success,changed.Message);Assert.AreEqual(LogicalNodeInstanceRules.Expression("human"+value[5..]),changed.Data!.Properties.LogicLanguage);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE LogicalNodes SET `Value`=REPEAT('X',33) WHERE SideLength=12")).Number);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        new MySqlLineRepository(settings);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_InstanceAttributes");Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));
    });

    [TestMethod]
    public Task OldClassContainingParenthesesCannotSilentlyBecomeAnEmptyAttributeInstance()=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        LogicalNodeInstanceTests.Parent(repo,"HUMAN()",200.5m);LogicalNodeInstanceTests.Child(repo,"human(id=1)");Legacy31(settings);
        Sql(settings,"UPDATE LogicalNodes SET `Value`='human()',LogicLanguage='human() ∈ HUMAN()' WHERE SideLength=10");
        Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));
        Assert.AreEqual(31,Query<int>(settings,"SELECT SchemaVersion FROM MapMetadata"));
        Assert.AreEqual("human()",Query<string>(settings,"SELECT `Value` FROM LogicalNodes WHERE SideLength=10"));
        Assert.AreEqual("human() ∈ HUMAN()",Query<string>(settings,"SELECT LogicLanguage FROM LogicalNodes WHERE SideLength=10"));
        return Task.CompletedTask;
    });
}
