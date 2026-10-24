using System.Text.Json;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeRelationLogicTests
{
    [TestMethod,DataRow("="),DataRow("→"),DataRow("⊇"),DataRow("⊋"),DataRow("⊆"),DataRow("⊊"),DataRow("↔"),DataRow("¬")]
    public void ServerAlwaysDerivesLogicFromSelectedValue_IncludingOldClientsWithoutLogic(string value)
    {
        var map=new TestMap();var node=LogicalNodeRelationTests.Add(map.Repository,value:value,logic:"old custom logic");
        var expected=value=="¬"?"¬(?)":value;
        Assert.AreEqual(expected,node.Properties.LogicLanguage);
        var service=new LogicalNodeService(map.Repository);var opposite=value=="="?"→":"=";
        var changed=service.Update(new(node.Code,"owner",node.Version,opposite,"preserved info"));
        Assert.IsTrue(changed.Success,changed.Message);Assert.AreEqual(opposite,changed.Data!.Properties.LogicLanguage);
        var again=service.Update(new(node.Code,"owner",changed.Data.Version,value,"preserved info",LogicLanguage:"outdated expression"));
        Assert.IsTrue(again.Success,again.Message);Assert.AreEqual(expected,again.Data!.Properties.LogicLanguage);Assert.AreEqual("preserved info",again.Data.Properties.Info);
    }

    [TestMethod,DataRow(""),DataRow("P"),DataRow("→"),DataRow(" = "),DataRow("= ")]
    public void DalRejectsMismatchedLogic_AndRollsBackUnrelatedChanges(string logic)
    {
        var map=new TestMap();var n=LogicalNodeRelationTests.Add(map.Repository);var other=LogicalNodeRelationTests.Add(map.Repository,200.5m);
        var before=map.Snapshot();
        Assert.ThrowsExactly<InvalidLogicalNodeRelationException>(()=>map.Repository.Transaction(s=>
        {
            s.LogicalNodes[other.Code]=other with{Properties=other.Properties with{Info="must roll back"}};
            s.LogicalNodes[n.Code]=n with{Properties=n.Properties with{LogicLanguage=logic}};return true;
        }));
        Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow("="),DataRow("→"),DataRow("⊇"),DataRow("⊋"),DataRow("⊆"),DataRow("⊊"),DataRow("↔"),DataRow("¬")]
    public void ImportFillsMissingOrEmptyLogic_ButRejectsAnExplicitMismatchBeforeAnyWrite(string value)
    {
        var document=LogicalNodeRulesAuditTests.MixedDocument();document["ln"]![5]!["value"]=value;
        var map=new TestMap();var service=new FlowDiagramService(map.Repository);
        var expected=value=="¬"?"¬(?)":value;
        foreach(var logic in new[]{"","P",value=="="?"→":"="," ",value,expected,"¬(P)"}.Distinct())
        {
            document["ln"]![5]!["logic"]=logic;var before=map.Snapshot();
            var request=new FlowDiagramRequest("owner",document.ToJsonString(),500.5m,500.5m);
            var preview=service.Preview(request);Assert.AreEqual(logic==""||logic==value||logic==expected,preview.Success,logic+": "+preview.Message);
            Assert.AreEqual(before,map.Snapshot());
            if(preview.Success)
            {Assert.AreEqual(expected,FlowDiagramLnAuditTests.Node(preview.Data!,"Equal").Properties.LogicLanguage);}
            else
            {StringAssert.Contains(preview.Message,"Equal");Assert.IsFalse(service.Import(request).Success);Assert.AreEqual(before,map.Snapshot());}
        }
        document["ln"]![5]!.AsObject().Remove("logic");
        var parsed=FlowDiagramLanguage.Parse(document.ToJsonString());Assert.IsTrue(parsed.Success,parsed.Message);
        Assert.AreEqual(expected,parsed.Data!.Ln.Single(n=>n.Id=="Equal").Logic);
        var imported=service.Import(new("owner",document.ToJsonString(),500.5m,500.5m));Assert.IsTrue(imported.Success,imported.Message);
        Assert.IsTrue(imported.Data!.Added.LogicalNodes.Where(n=>n.SideLength==8).All(n=>LogicalNodeRelationRules.Expression(n.Properties.Value)==n.Properties.LogicLanguage));
    }
}

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeRelationLogicMySqlTests
{
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}

    [TestMethod,DataRow("none"),DataRow("dml"),DataRow("ddl"),DataRow("publish"),DataRow("existing-check")]
    public Task Upgrade24RepairsOnlyMismatchedLn8_AndRetriesEveryStageWithoutDoubleVersionBumps(string phase)=>Isolated(settings=>
    {
        var old=new MySqlLineRepository(settings);Add(old,20,100,400,100);
        var a=LogicalNodeRelationTests.Add(old,100.5m,100.5m,"=");var b=LogicalNodeRelationTests.Add(old,150.5m,100.5m,"→");var stable=LogicalNodeRelationTests.Add(old,200.5m,100.5m,"=");
        var service=new LogicalNodeService(old);var six=service.Create(new("owner",250.5m,100.5m,6,new(){Value="判断",LogicLanguage="P"})).Data!;
        var four=service.Create(new("owner",300.5m,100.5m,4,new(){Value="False",LogicLanguage="¬P"})).Data!;var twelve=service.Create(new("owner",350.5m,100.5m,12,new(){Value="S",LogicLanguage="custom"})).Data!;
        var unchanged=service.GetAll().Data!.Where(n=>n.Code!=a.Code&&n.Code!=b.Code).ToDictionary(n=>n.Code);
        Sql(settings,$"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic; UPDATE MapMetadata SET SchemaVersion=24 WHERE Id=1; UPDATE LogicalNodes SET LogicLanguage='' WHERE Code='{a.Code}'; UPDATE LogicalNodes SET LogicLanguage='old custom' WHERE Code='{b.Code}'");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(phase=="dml")Sql(settings,$"CREATE TRIGGER TestLn8LogicMigration BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='{b.Code}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN8 update failure'; END IF; END");
        if(phase=="ddl")Sql(settings,"ALTER TABLE MapMetadata ADD CONSTRAINT CK_LN_RelationLogic CHECK (1=1)");
        if(phase=="publish")Sql(settings,"CREATE TRIGGER TestLn8LogicMigration BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=25 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN8 publish failure'; END IF; END");
        if(phase=="existing-check")Sql(settings,$"UPDATE LogicalNodes SET LogicLanguage=`Value` WHERE SideLength=8; ALTER TABLE LogicalNodes ADD CONSTRAINT CK_LN_RelationLogic CHECK (SideLength<>8 OR CONVERT(LogicLanguage USING binary)=CONVERT(`Value` USING binary))");
        if(phase is "dml" or "ddl" or "publish")
        {
            try
            {
                Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(phase=="dml"?24:2401,Version(settings));
                if(phase=="dml")Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
                Assert.ThrowsExactly<InvalidOperationException>(()=>old.Transaction(s=>true));
            }
            finally{Sql(settings,phase=="ddl"?"ALTER TABLE MapMetadata DROP CHECK CK_LN_RelationLogic":"DROP TRIGGER TestLn8LogicMigration");}
        }
        var repository=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));var nodes=new LogicalNodeService(repository).GetAll().Data!.ToDictionary(n=>n.Code);
        foreach(var n in new[]{a,b})
        {
            var repaired=nodes[n.Code];Assert.AreEqual(n.Properties.Value,repaired.Properties.LogicLanguage);Assert.AreEqual(n.Version+(phase=="existing-check"?0:1),repaired.Version);
            Assert.AreEqual(n.Properties.Info,repaired.Properties.Info);Assert.AreEqual(n.X,repaired.X);Assert.AreEqual(n.Y,repaired.Y);Assert.AreEqual(n.Owner,repaired.Owner);CollectionAssert.AreEqual(n.Positions,repaired.Positions);
        }
        foreach(var n in unchanged.Values)Assert.AreEqual(JsonSerializer.Serialize(n),JsonSerializer.Serialize(nodes[n.Code]));
        using var plan=JsonDocument.Parse(System.IO.File.ReadAllText(repository.LogicalNodeRelationLogicBackupPath!));Assert.AreEqual(25,plan.RootElement.GetProperty("TargetVersion").GetInt32());Assert.IsTrue(System.IO.File.Exists(plan.RootElement.GetProperty("FullBackup").GetString()));
        var after=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });

    [TestMethod]
    public Task DatabaseAndDalRejectBypass_AndAtomicSqlCanChangeBothFieldsTogether()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var n=LogicalNodeRelationTests.Add(repository);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var set in new[]{"LogicLanguage=''","LogicLanguage='P'","LogicLanguage='→'","LogicLanguage='= '","`Value`='→'","SideLength=8,LogicLanguage='x'"})
        {Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET {set} WHERE Code='{n.Code}'")).Number);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        Assert.ThrowsExactly<InvalidLogicalNodeRelationException>(()=>repository.Transaction(s=>{s.LogicalNodes[n.Code]=n with{Properties=n.Properties with{LogicLanguage="P"}};return true;}));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Sql(settings,$"UPDATE LogicalNodes SET `Value`='→',LogicLanguage='→' WHERE Code='{n.Code}'");var changed=new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single();Assert.AreEqual("→",changed.Properties.Value);Assert.AreEqual("→",changed.Properties.LogicLanguage);return Task.CompletedTask;
    });
}
