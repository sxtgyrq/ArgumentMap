using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeInstanceConjunctionTests
{
    [TestMethod,DataRow("human()","human()∈HUMAN"),
        DataRow("human(id=1,name=张三) 张三","张三∈HUMAN∧1∈HUMAN.ID∧张三∈HUMAN.NAME"),
        DataRow("human(name=甲∧乙⋂丙) 张⋂三","张⋂三∈HUMAN∧甲∧乙⋂丙∈HUMAN.NAME")]
    public void AndConnectsGeneratedTerms_ParameterAndAliasSymbolsRemainLiteral(string value,string expected)
    {
        var repo=new InMemoryLineRepository();LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(repo,value);Assert.AreEqual(expected,child.Properties.LogicLanguage);Assert.AreEqual(value,child.Properties.Value);
        Assert.AreEqual(child.Properties,LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(10,child.Properties))!.Properties);
        var imported=new FlowDiagramService(repo).Import(new("owner",LogicalNodeInstanceTests.Document(value,"HUMAN"),1000.5m,1000.5m));
        Assert.IsTrue(imported.Success,imported.Message);
        Assert.IsTrue(imported.Data!.Added.LogicalNodes.Where(n=>n.SideLength==10).All(n=>n.Properties.LogicLanguage==
            expected.Replace("human(","human1(").Replace("HUMAN","HUMAN1")));
    }
}

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeInstanceConjunctionMySqlTests
{
    private static T Scalar<T>(MySqlSettings settings,string sql)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText=sql;return (T)Convert.ChangeType(q.ExecuteScalar()!,typeof(T));}

    private static string OldExpression(string value)
    {
        // Reconstruct v34 from its grammar, keeping literal ∧/⋂ inside values.
        var open=value.IndexOf('(');var close=value.IndexOf(')');var alias=value[(close+1)..].Trim();
        var parent=LogicalNodeInstanceRules.ParentValue(value);
        var terms=new List<string>{(alias.Length==0?value:alias)+"∈"+parent};
        var body=value[(open+1)..close];
        if(body.Length>0)foreach(var pair in body.Split(','))
        {var at=pair.IndexOf('=');terms.Add(pair[(at+1)..]+"∈"+parent+"."+LogicalNodeValueUniqueness.Normalize(pair[..at]));}
        return string.Join("⋂",terms);
    }

    private static void Version34(MySqlSettings settings)
    {
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();
        var triggers=new List<(string Name,string Action,string Body)>();
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT TRIGGER_NAME,EVENT_MANIPULATION,ACTION_STATEMENT FROM information_schema.TRIGGERS WHERE TRIGGER_SCHEMA=DATABASE() AND TRIGGER_NAME IN ('TR_LN10_Attributes_Insert','TR_LN10_Attributes_Update')";
            using var reader=q.ExecuteReader();while(reader.Read())triggers.Add((reader.GetString(0),reader.GetString(1),reader.GetString(2).Replace("'∧',attributeValue","'⋂',attributeValue")));
        }
        Assert.AreEqual(2,triggers.Count);Sql(settings,"DROP TRIGGER TR_LN10_Attributes_Insert; DROP TRIGGER TR_LN10_Attributes_Update");
        var nodes=new List<(string Code,string Value)>();
        using(var q=c.CreateCommand())
        {q.CommandText="SELECT Code,`Value` FROM LogicalNodes WHERE SideLength=10";using var reader=q.ExecuteReader();while(reader.Read())nodes.Add((reader.GetString(0),reader.GetString(1)));}
        foreach(var (code,value) in nodes)
        {using var q=c.CreateCommand();q.CommandText="UPDATE LogicalNodes SET LogicLanguage=@logic WHERE Code=@code";q.Parameters.AddWithValue("@logic",OldExpression(value));q.Parameters.AddWithValue("@code",code);q.ExecuteNonQuery();}
        var history=new List<(long Sequence,JsonNode Payload)>();
        using(var q=c.CreateCommand())
        {q.CommandText="SELECT Sequence,Payload FROM MapUndoHistory";using var reader=q.ExecuteReader();while(reader.Read())history.Add((reader.GetInt64(0),JsonNode.Parse(reader.GetString(1))!));}
        foreach(var (sequence,payload) in history)
        {
            foreach(var change in payload["Delta"]!["Nodes"]!.AsArray())foreach(var key in new[]{"Before","After"})
                if(change![key] is {} node&&node["SideLength"]!.GetValue<int>()==10)
                {var p=node["Properties"]!;p["LogicLanguage"]=OldExpression(p["Value"]!.GetValue<string>());}
            using var q=c.CreateCommand();q.CommandText="UPDATE MapUndoHistory SET Payload=@payload WHERE Sequence=@seq";
            q.Parameters.AddWithValue("@payload",payload.ToJsonString());q.Parameters.AddWithValue("@seq",sequence);q.ExecuteNonQuery();
        }
        foreach(var trigger in triggers)Sql(settings,$"CREATE TRIGGER {trigger.Name} BEFORE {trigger.Action} ON LogicalNodes FOR EACH ROW {trigger.Body}");
        Sql(settings,"UPDATE MapMetadata SET SchemaVersion=34 WHERE Id=1");
    }

    private static string Journal(MySqlSettings settings)
    {
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT OperationId,Owner,Operation,Sequence,IsUndone,UndoRequestId,Payload FROM MapUndoHistory ORDER BY Sequence";
        using var r=q.ExecuteReader();var rows=new List<object[]>();while(r.Read()){var values=new object[r.FieldCount];r.GetValues(values);rows.Add(values);}return JsonSerializer.Serialize(rows);
    }

    [TestMethod,DataRow("move"),DataRow("delete"),DataRow("rename")]
    public Task Real34UpgradeRegeneratesOnlyLn10AndUndo_PreservesLiteralSymbolsAndOtherMapData(string operation)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(repo,"human(id=1,name=甲∧乙⋂丙) 张⋂三");
        LogicalNodeInstanceTests.Child(repo,"human()",130.5m);LogicalNodeInstanceTests.Child(repo,"human(id=2)",170.5m);
        var ln6=new LogicalNodeService(repo).Create(new("owner",300.5m,50.5m,6,new(){Value="其他命题",LogicLanguage="P⋂Q"})).Data!;
        Assert.IsTrue(new LogicalNodeService(repo).Create(new("owner",330.5m,50.5m,4,new(){Value="True",LogicLanguage="P⋂Q"})).Success);
        Add(repo,600,600,680,600);Add(repo,650,550,650,650);Assert.IsTrue(new CrossService(repo).Recalculate().Success);
        repo.Transaction(s=>{var key=s.Crosses.Keys.Single();s.StaleCrosses.Add(key);s.DirtyLines.Add(key.RoadCode1);s.PendingSegments.Add((key.RoadCode1,0));return true;});
        var beforeEdit=MapUndoTests.Content(repo);
        if(operation=="move")Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Move(new(child.Code,"owner",child.Version,90.5m,90.5m))).Success);
        if(operation=="delete")Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Delete(new(child.Code,"owner",child.Version))).Success);
        if(operation=="rename")Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(parent.Code,"owner",parent.Version,"PERSON",""))).Success);
        var expected=MapUndoTests.Content(repo);
        var beforeNodes=repo.Transaction(s=>s.LogicalNodes.Values.ToDictionary(n=>n.Code));
        Version34(settings);
        var beforeRaw=JsonNode.Parse(LineTopologyMySqlTests.RawSnapshot(settings))!;
        repo=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Scalar<int>(settings,"SELECT SchemaVersion FROM MapMetadata"));
        Assert.AreEqual(expected,MapUndoTests.Content(repo));
        var afterRaw=JsonNode.Parse(LineTopologyMySqlTests.RawSnapshot(settings))!;
        foreach(var table in new[]{"Lines","LineNodes","Crosses","LogicalNodePositions"})Assert.IsTrue(JsonNode.DeepEquals(beforeRaw[table],afterRaw[table]),table);
        repo.Transaction(s=>
        {
            foreach(var n in s.LogicalNodes.Values)
            {
                var old=beforeNodes[n.Code];Assert.AreEqual(old.Properties,n.Properties);Assert.AreEqual(old.X,n.X);Assert.AreEqual(old.Y,n.Y);
                Assert.AreEqual(old.Version+(n.SideLength==10&&n.Properties.Value.Contains('=')?1:0),n.Version);
            }
            Assert.AreEqual("P⋂Q",s.LogicalNodes[ln6.Code].Properties.LogicLanguage);Assert.AreEqual(1,s.StaleCrosses.Count);Assert.AreEqual(1,s.PendingSegments.Count);
            return true;
        });
        using(var plan=JsonDocument.Parse(System.IO.File.ReadAllText(repo.LogicalNodeInstanceAttributeBackupPath!)))
        {Assert.AreEqual(35,plan.RootElement.GetProperty("TargetVersion").GetInt32());Assert.IsTrue(System.IO.File.Exists(plan.RootElement.GetProperty("FullBackup").GetString()));}
        var stable=LineTopologyMySqlTests.RawSnapshot(settings);var journal=Journal(settings);new MySqlLineRepository(settings);
        Assert.AreEqual(stable,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(journal,Journal(settings));
        var undone=new MapUndoService(repo).Undo("owner");Assert.IsTrue(undone.Success,undone.Message);Assert.AreEqual(beforeEdit,MapUndoTests.Content(repo));
        var restored=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code);
        Assert.AreEqual("张⋂三∈HUMAN∧1∈HUMAN.ID∧甲∧乙⋂丙∈HUMAN.NAME",restored.Properties.LogicLanguage);
        return Task.CompletedTask;
    });

    [TestMethod,DataRow("map"),DataRow("history"),DataRow("marker"),DataRow("publication")]
    public Task Interrupted35UpgradeIsClosedAndRetriesWithoutDuplicateVersionBumps(string phase)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");var child=LogicalNodeInstanceTests.Child(repo,"human(id=1,name=张三) 张三");
        Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(child.Code,"owner",child.Version,child.Properties.Value,"改说明"))).Success);
        var version=repo.Transaction(s=>s.LogicalNodes[child.Code].Version);Version34(settings);
        var oldPayload=Journal(settings);
        var fault=phase switch
        {
            "map"=>"CREATE TRIGGER FailConjunction BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected conjunction map failure'",
            "history"=>"CREATE TRIGGER FailConjunction BEFORE UPDATE ON MapUndoHistory FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected conjunction history failure'",
            "marker"=>"CREATE TRIGGER FailConjunction BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=3401 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected conjunction marker failure'; END IF; END",
            _=>"CREATE TRIGGER FailConjunction BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=35 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected conjunction publish failure'; END IF; END"
        };
        Sql(settings,fault);
        try
        {
            Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(phase=="marker"?34:3401,Scalar<int>(settings,"SELECT SchemaVersion FROM MapMetadata"));
            Assert.ThrowsExactly<InvalidOperationException>(()=>repo.Transaction(s=>true));
            if(phase is "map" or "history" or "marker")
            {Assert.AreEqual(version,Scalar<long>(settings,"SELECT Version FROM LogicalNodes WHERE SideLength=10"));Assert.AreEqual(oldPayload,Journal(settings));}
        }
        finally{Sql(settings,"DROP TRIGGER FailConjunction");}
        repo=new MySqlLineRepository(settings);var now=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code);
        Assert.AreEqual(version+1,now.Version);Assert.AreEqual("张三∈HUMAN∧1∈HUMAN.ID∧张三∈HUMAN.NAME",now.Properties.LogicLanguage);
        var stable=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(stable,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);
        Assert.AreEqual("实例说明",new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code).Properties.Info);
        return Task.CompletedTask;
    });

    [TestMethod]
    public Task BothSqlGuardsAndHttpRejectOldGeneratedConnectorAfterUpgrade()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");var child=LogicalNodeInstanceTests.Child(repo,"human(id=1,name=张三) 张三");
        Version34(settings);repo=new MySqlLineRepository(settings);
        using(var c=new MySqlConnection(settings.ConnectionString()))
        {
            c.Open();using var q=c.CreateCommand();q.CommandText="UPDATE LogicalNodes SET LogicLanguage=@logic WHERE Code=@code";
            q.Parameters.AddWithValue("@logic",OldExpression(child.Properties.Value));q.Parameters.AddWithValue("@code",child.Code);
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>q.ExecuteNonQuery()).Number);
            // Explicit columns below also make this independent of historical column ordering.
            q.CommandText="INSERT INTO LogicalNodes (Code,Owner,Version,X,Y,SideLength,`Value`,Info,LogicLanguage,OperandKind,UpdatedAt) SELECT 'ZZZZZZZZZZ',Owner,Version,500.5,500.5,SideLength,`Value`,Info,@logic,OperandKind,UpdatedAt FROM LogicalNodes WHERE Code=@code";
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>q.ExecuteNonQuery()).Number);
        }
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var saved=await client.CreateLogicalNodeAsync(210.5m,50.5m,10,new(){Value="human(id=2,name=李四) 李四"});Assert.IsTrue(saved.Success,saved.Message);
        Assert.AreEqual("李四∈HUMAN∧2∈HUMAN.ID∧李四∈HUMAN.NAME",saved.Data!.Properties.LogicLanguage);
    });

    [TestMethod]
    public Task RestrictedBinlogAccountGetsActionableErrorBeforeMigrationWrites()=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");LogicalNodeInstanceTests.Child(repo,"human(id=1)");Version34(settings);
        if(Scalar<int>(settings,"SELECT @@log_bin")==0||Scalar<int>(settings,"SELECT @@log_bin_trust_function_creators")!=0)
        {Assert.Inconclusive("This permission regression requires binlog ON and function creator trust OFF; it does not alter global server settings.");return Task.CompletedTask;}
        var user="am_ln10_"+Guid.NewGuid().ToString("N")[..16];var password=Guid.NewGuid().ToString("N");
        using var admin=new MySqlConnection(settings.ConnectionString(false));admin.Open();
        try
        {
            foreach(var host in new[]{"localhost","127.0.0.1"})
            {
                using var q=admin.CreateCommand();q.CommandText=$"CREATE USER '{user}'@'{host}' IDENTIFIED BY @password";q.Parameters.AddWithValue("@password",password);q.ExecuteNonQuery();
                q.CommandText=$"GRANT ALL PRIVILEGES ON `{settings.Database}`.* TO '{user}'@'{host}'";q.ExecuteNonQuery();
            }
            var before=LineTopologyMySqlTests.RawSnapshot(settings);
            var error=Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings with{User=user,Password=password}));
            StringAssert.Contains(error.Message,"log_bin_trust_function_creators");Assert.AreEqual(34,Scalar<int>(settings,"SELECT SchemaVersion FROM MapMetadata"));
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
            // An administrator can complete the one-time upgrade. Subsequent normal
            // starts must work for the restricted account while trust stays OFF.
            _=new MySqlLineRepository(settings);
            var limited=new MySqlLineRepository(settings with{User=user,Password=password});
            Assert.AreEqual("human(id=1)∈HUMAN∧1∈HUMAN.ID",new LogicalNodeService(limited).GetAll().Data!.Single(n=>n.SideLength==10).Properties.LogicLanguage);
        }
        finally
        {
            foreach(var host in new[]{"localhost","127.0.0.1"}){using var q=admin.CreateCommand();q.CommandText=$"DROP USER IF EXISTS '{user}'@'{host}'";q.ExecuteNonQuery();}
            using var pooled=new MySqlConnection((settings with{User=user,Password=password}).ConnectionString());MySqlConnection.ClearPool(pooled);
        }
        return Task.CompletedTask;
    });

    [TestMethod,DataRow(false),DataRow(true)]
    public Task Schema35RefusesMissingOrLegacyConjunctionTrigger(bool missing)=>Isolated(settings=>
    {
        var repo=new MySqlLineRepository(settings);LogicalNodeInstanceTests.Parent(repo,"HUMAN");LogicalNodeInstanceTests.Child(repo,"human(id=1)");
        Version34(settings);Sql(settings,"UPDATE MapMetadata SET SchemaVersion=35 WHERE Id=1");
        if(missing)Sql(settings,"DROP TRIGGER TR_LN10_Attributes_Update");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        return Task.CompletedTask;
    });
}
