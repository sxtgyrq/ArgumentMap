using System.Text.Json;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeNegationLogicMySqlTests
{
    private sealed record HistoryPayload(MapUndoDelta Delta,UndoStamp[] Stamps);
    private sealed record LegacyFixture(LogicalNodeSnapshot[] Nodes,string CanonicalHistory,string DeletedCode,string ChangedCode);
    private static int Version(MySqlSettings settings)=>Scalar<int>(settings,"SELECT SchemaVersion FROM MapMetadata WHERE Id=1");
    private static T Scalar<T>(MySqlSettings settings,string sql)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText=sql;return (T)Convert.ChangeType(q.ExecuteScalar()!,typeof(T));}
    private static string History(MySqlSettings settings)
    {
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT Sequence,OperationId,Owner,Operation,Payload,IsUndone,UndoRequestId,CreatedAt FROM MapUndoHistory ORDER BY Sequence";
        using var r=q.ExecuteReader();var rows=new List<object[]>();
        while(r.Read()){var row=new object[r.FieldCount];r.GetValues(row);rows.Add(row);}return JsonSerializer.Serialize(rows);
    }
    private static void RewriteHistory(MySqlSettings settings,string expression)
    {
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();var rows=new List<(long Sequence,string Payload)>();
        using(var q=c.CreateCommand())
        {q.CommandText="SELECT Sequence,Payload FROM MapUndoHistory ORDER BY Sequence";using var r=q.ExecuteReader();while(r.Read())rows.Add((r.GetInt64(0),r.GetString(1)));}
        LogicalNodeSnapshot? Rewrite(LogicalNodeSnapshot? n)=>n is {SideLength:8}&&n.Properties.Value=="¬"?n with{Properties=n.Properties with{LogicLanguage=expression}}:n;
        foreach(var row in rows)
        {
            var payload=JsonSerializer.Deserialize<HistoryPayload>(row.Payload)!;
            var nodes=payload.Delta.Nodes.Select(change=>new UndoNodeChange(Rewrite(change.Before),Rewrite(change.After))).ToArray();
            using var q=c.CreateCommand();q.CommandText="UPDATE MapUndoHistory SET Payload=@payload WHERE Sequence=@sequence";
            q.Parameters.AddWithValue("@payload",JsonSerializer.Serialize(payload with{Delta=payload.Delta with{Nodes=nodes}}));q.Parameters.AddWithValue("@sequence",row.Sequence);q.ExecuteNonQuery();
        }
    }
    private static LegacyFixture PrepareVersion36(MySqlSettings settings)
    {
        var repository=new MySqlLineRepository(settings);Add(repository,20,50,400,50);
        var first=LogicalNodeRelationTests.Add(repository,50.5m,50.5m,"¬");
        LogicalNodeRelationTests.Add(repository,80.5m,50.5m,"¬");
        var changed=LogicalNodeRelationTests.Add(repository,110.5m,50.5m,"¬");
        var deleted=LogicalNodeRelationTests.Add(repository,140.5m,50.5m,"¬");
        LogicalNodeRelationTests.Add(repository,170.5m,50.5m,"→");
        var service=new LogicalNodeService(repository);
        Assert.IsTrue(service.Create(new("owner",200.5m,50.5m,6,new(){Value="命题",LogicLanguage="P"})).Success);
        Assert.IsTrue(service.Create(new("owner",230.5m,50.5m,4,new(){Value="False",LogicLanguage="¬P"})).Success);
        Assert.IsTrue(MapUndoTests.Edit(repository,scope=>new LogicalNodeService(scope).Update(new(first.Code,"owner",first.Version,"¬","edited information"))).Success);
        Assert.IsTrue(MapUndoTests.Edit(repository,scope=>new LogicalNodeService(scope).Update(new(changed.Code,"owner",changed.Version,"=","changed from NOT"))).Success);
        Assert.IsTrue(MapUndoTests.Edit(repository,scope=>new LogicalNodeService(scope).Delete(new(deleted.Code,"owner",deleted.Version))).Success);
        var nodes=service.GetAll().Data!;var canonicalHistory=History(settings);
        Assert.IsTrue(canonicalHistory.Length>2);
        // Recreate the real v36 equality CHECK and old NOT text in both live rows
        // and history; changing only the schema marker would not test migration.
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic; UPDATE LogicalNodes SET LogicLanguage='¬',UpdatedAt='2000-01-01 00:00:00' WHERE SideLength=8 AND CONVERT(`Value` USING binary)=X'C2AC'; ALTER TABLE LogicalNodes ADD CONSTRAINT CK_LN_RelationLogic CHECK (SideLength<>8 OR CONVERT(LogicLanguage USING binary)=CONVERT(`Value` USING binary)); UPDATE LogicalNodePositions SET IsChanged=-1; UPDATE MapMetadata SET SchemaVersion=36 WHERE Id=1");
        RewriteHistory(settings,"¬");
        Assert.AreEqual(2,Scalar<int>(settings,"SELECT COUNT(*) FROM LogicalNodes WHERE SideLength=8 AND CONVERT(`Value` USING binary)=X'C2AC' AND CONVERT(LogicLanguage USING binary)=X'C2AC'"));
        Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET LogicLanguage='¬(?)' WHERE Code='{first.Code}'")).Number);
        return new(nodes,canonicalHistory,deleted.Code,changed.Code);
    }
    private static string ExceptNegationMetadata(MySqlSettings settings)
    {
        var raw=JsonNode.Parse(LineTopologyMySqlTests.RawSnapshot(settings))!;var nodes=raw["LogicalNodes"]!.AsArray();
        for(var i=0;i<nodes.Count;i++)
        {
            var node=JsonNode.Parse(nodes[i]!.GetValue<string>())!;
            if(node["SideLength"]!.GetValue<int>()!=8||node["Value"]!.GetValue<string>()!="¬")continue;
            node["LogicLanguage"]="";node["Version"]=0;node["UpdatedAt"]="";nodes[i]=node.ToJsonString();
        }
        return raw.ToJsonString();
    }
    private static void AssertUpgraded(MySqlSettings settings,LegacyFixture fixture,MySqlLineRepository repository)
    {
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));
        var nodes=new LogicalNodeService(repository).GetAll().Data!.ToDictionary(n=>n.Code);
        foreach(var old in fixture.Nodes)
        {
            var node=nodes[old.Code];var isNot=old.SideLength==8&&old.Properties.Value=="¬";
            Assert.AreEqual(old.Properties,node.Properties);Assert.AreEqual(old.Version+(isNot?1:0),node.Version);
            Assert.AreEqual(old.X,node.X);Assert.AreEqual(old.Y,node.Y);Assert.AreEqual(old.Owner,node.Owner);Assert.AreEqual(old.SideLength,node.SideLength);
        }
        Assert.AreEqual(fixture.CanonicalHistory,History(settings));
        Assert.AreEqual(2,Scalar<int>(settings,"SELECT COUNT(*) FROM LogicalNodes WHERE SideLength=8 AND CONVERT(`Value` USING binary)=X'C2AC' AND UpdatedAt>'2000-01-01 00:00:00'"));
        Assert.AreEqual(2,Scalar<int>(settings,"SELECT COUNT(*) FROM LogicalNodePositions p JOIN LogicalNodes n ON n.Code=p.LogicalNodeCode WHERE n.SideLength=8 AND CONVERT(n.`Value` USING binary)=X'C2AC' AND p.IsChanged=-1"));
    }

    [TestMethod,DataRow("normal"),DataRow("pending"),DataRow("marker"),DataRow("ddl"),DataRow("map"),DataRow("history"),DataRow("publish")]
    public Task Version36UpgradeRepairsOnlyNotAndAllUndoSnapshots_AndRetriesWithoutDoubleVersionBumps(string phase)=>Isolated(settings=>
    {
        var fixture=PrepareVersion36(settings);var preserved=ExceptNegationMetadata(settings);
        var lastNot=fixture.Nodes.Where(n=>n.Properties.Value=="¬"&&n.SideLength==8).MaxBy(n=>n.Code)!.Code;
        if(phase=="pending")Sql(settings,"UPDATE MapMetadata SET SchemaVersion=3601 WHERE Id=1");
        if(phase=="ddl")Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic, ADD CONSTRAINT CK_LN_RelationLogic CHECK (SideLength<>8 OR CONVERT(LogicLanguage USING binary)=CONVERT(`Value` USING binary) OR LogicLanguage='broken'); "+$"UPDATE LogicalNodes SET LogicLanguage='broken' WHERE Code='{lastNot}'");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var beforeHistory=History(settings);
        var trigger=phase switch
        {
            "marker"=>"CREATE TRIGGER FailNotPlaceholder BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=3601 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected NOT marker failure'; END IF; END",
            "map"=>$"CREATE TRIGGER FailNotPlaceholder BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='{lastNot}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected NOT data failure'; END IF; END",
            "history"=>"CREATE TRIGGER FailNotPlaceholder BEFORE UPDATE ON MapUndoHistory FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected NOT history failure'",
            "publish"=>"CREATE TRIGGER FailNotPlaceholder BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=37 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected NOT publication failure'; END IF; END",
            _=>null
        };
        if(trigger is not null)Sql(settings,trigger);
        if(phase is "marker" or "ddl" or "map" or "history" or "publish")
        {
            try
            {
                Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));
                Assert.AreEqual(phase=="marker"?36:3601,Version(settings));
                if(phase!="publish")
                {Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(beforeHistory,History(settings));}
                else
                {
                    Assert.AreEqual(fixture.CanonicalHistory,History(settings));
                    foreach(var node in fixture.Nodes.Where(n=>n.SideLength==8&&n.Properties.Value=="¬"))
                        Assert.AreEqual(node.Version+1,Scalar<long>(settings,$"SELECT Version FROM LogicalNodes WHERE Code='{node.Code}'"));
                }
            }
            finally
            {
                if(trigger is not null)Sql(settings,"DROP TRIGGER FailNotPlaceholder");
                if(phase=="ddl")Sql(settings,$"UPDATE LogicalNodes SET LogicLanguage='¬' WHERE Code='{lastNot}'");
            }
        }
        var repository=new MySqlLineRepository(settings);AssertUpgraded(settings,fixture,repository);
        Assert.IsTrue(System.IO.File.Exists(repository.LogicalNodeNegationLogicBackupPath));
        Assert.AreEqual(preserved,ExceptNegationMetadata(settings));
        using(var plan=JsonDocument.Parse(System.IO.File.ReadAllText(repository.LogicalNodeNegationLogicBackupPath!)))
        {Assert.AreEqual(37,plan.RootElement.GetProperty("TargetVersion").GetInt32());Assert.IsTrue(System.IO.File.Exists(plan.RootElement.GetProperty("FullBackup").GetString()));}
        var stable=LineTopologyMySqlTests.RawSnapshot(settings);var stableHistory=History(settings);
        new MySqlLineRepository(settings);Assert.AreEqual(stable,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(stableHistory,History(settings));
        // These NOTs existed only in deletion/before-change history. Both must
        // restore as the new expression after restarting the server.
        Assert.IsTrue(new MapUndoService(repository).Undo("owner").Success);
        var restored=new LogicalNodeService(repository).GetAll().Data!.Single(n=>n.Code==fixture.DeletedCode);Assert.AreEqual("¬(?)",restored.Properties.LogicLanguage);
        Assert.IsTrue(new MapUndoService(repository).Undo("owner").Success);
        var changed=new LogicalNodeService(repository).GetAll().Data!.Single(n=>n.Code==fixture.ChangedCode);Assert.AreEqual("¬",changed.Properties.Value);Assert.AreEqual("¬(?)",changed.Properties.LogicLanguage);
        return Task.CompletedTask;
    });

    [TestMethod,DataRow("missing"),DataRow("legacy"),DataRow("transition"),DataRow("legacy-missing-positions")]
    public Task CurrentSchemaRequiresFinalPlaceholderCheckBeforeAnyRecovery(string damage)=>Isolated(settings=>
    {
        new MySqlLineRepository(settings);
        Sql(settings,damage switch
        {
            "missing"=>"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic",
            "transition"=>"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic, ADD CONSTRAINT CK_LN_RelationLogic CHECK (SideLength<>8 OR CONVERT(LogicLanguage USING binary)=CONVERT(`Value` USING binary) OR (CONVERT(`Value` USING binary)=X'C2AC' AND CONVERT(LogicLanguage USING binary)=X'C2AC283F29'))",
            "legacy-missing-positions"=>LogicalNodeNegationMySqlTests.LegacyLogicCheck+"; DROP TABLE LogicalNodePositions",
            _=>LogicalNodeNegationMySqlTests.LegacyLogicCheck
        });
        Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));return Task.CompletedTask;
    });

    [TestMethod,DataRow("normal"),DataRow("publish")]
    public Task OldNotWithMissingPositionsUpgradesThenRebuildsWithoutRevertingPlaceholder(string phase)=>Isolated(settings=>
    {
        var fixture=PrepareVersion36(settings);Sql(settings,"DROP TABLE LogicalNodePositions");
        if(phase=="publish")
        {
            Sql(settings,"CREATE TRIGGER FailNotRecovery BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=37 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected NOT recovery publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(3601,Version(settings));}
            finally{Sql(settings,"DROP TRIGGER FailNotRecovery");}
        }
        var repository=new MySqlLineRepository(settings);var nodes=new LogicalNodeService(repository).GetAll().Data!.ToDictionary(n=>n.Code);
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));
        foreach(var old in fixture.Nodes)
        {
            var node=nodes[old.Code];Assert.AreEqual(old.Properties,node.Properties);Assert.AreEqual(old.Version+(old.Properties.Value=="¬"?1:0),node.Version);
            CollectionAssert.AreEqual(old.Positions,node.Positions);
        }
        Assert.AreEqual(fixture.CanonicalHistory,History(settings));
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));return Task.CompletedTask;
    });
}
