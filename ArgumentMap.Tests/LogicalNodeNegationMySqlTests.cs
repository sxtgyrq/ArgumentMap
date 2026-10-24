using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeNegationMySqlTests
{
    private const string PreviousCheck="ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationValue, ADD CONSTRAINT CK_LN_RelationValue CHECK (SideLength<>8 OR CONVERT(`Value` USING binary) IN (X'3D',X'E28692',X'E28694',X'E28A87',X'E28A8B',X'E28A86',X'E28A8A'))";
    internal const string LegacyLogicCheck="ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic, ADD CONSTRAINT CK_LN_RelationLogic CHECK (SideLength<>8 OR CONVERT(LogicLanguage USING binary)=CONVERT(`Value` USING binary))";
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}
    private static string UndoSnapshot(MySqlSettings settings)
    {
        using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();
        q.CommandText="SELECT Sequence,OperationId,Owner,Operation,Payload,IsUndone,UndoRequestId FROM MapUndoHistory ORDER BY Sequence";
        using var r=q.ExecuteReader();var rows=new List<object[]>();
        while(r.Read()){var row=new object[r.FieldCount];r.GetValues(row);rows.Add(row);}return JsonSerializer.Serialize(rows);
    }

    [TestMethod,DataRow("normal"),DataRow("marker"),DataRow("pending-ddl"),DataRow("ddl"),DataRow("publish")]
    public Task UpgradeRealSevenSymbolVersion35PreservesDataAndUndo_InterruptedUpgradeIsClosedAndRetryable(string phase)=>Isolated(settings=>
    {
        var old=new MySqlLineRepository(settings);Add(old,20,50,500,50);var service=new LogicalNodeService(old);
        LogicalNodeInstanceTests.Parent(old,"HUMAN",50.5m);LogicalNodeInstanceTests.Child(old,"human(id=1)",80.5m);
        Assert.IsTrue(service.Create(new("owner",110.5m,50.5m,6,new(){Value="前提",LogicLanguage="P ∧ Q"})).Success);
        Assert.IsTrue(service.Create(new("owner",140.5m,50.5m,4,new(){Value="False",LogicLanguage="¬P ∧ Q"})).Success);
        var relation=LogicalNodeRelationTests.Add(old,170.5m,50.5m,"↔");
        Assert.IsTrue(MapUndoTests.Edit(old,scope=>new LogicalNodeService(scope).Update(new(relation.Code,"owner",relation.Version,"=","preserved undo"))).Success);
        Sql(settings,PreviousCheck+"; "+LegacyLogicCheck+"; UPDATE MapMetadata SET SchemaVersion=35 WHERE Id=1; UPDATE LogicalNodePositions SET IsChanged=-1");
        // This fixture has the actual v35 seven-symbol SQL restriction, even though
        // it was created by the current test assembly.
        Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET `Value`='¬',LogicLanguage='¬' WHERE Code='{relation.Code}'")).Number);
        if(phase=="pending-ddl")Sql(settings,"UPDATE MapMetadata SET SchemaVersion=3501 WHERE Id=1");
        // Let an intentionally damaged legacy CHECK admit !; the new atomic
        // ALTER must reject that row and leave the database closed for repair.
        if(phase=="ddl")Sql(settings,PreviousCheck.Replace("X'E28A8A'))","X'E28A8A',X'21'))")+$"; UPDATE LogicalNodes SET `Value`='!',LogicLanguage='!' WHERE Code='{relation.Code}'");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var history=UndoSnapshot(settings);
        Assert.AreNotEqual("[]",history);
        Assert.ThrowsExactly<InvalidOperationException>(()=>old.Transaction(s=>true));
        if(phase is "marker" or "publish")Sql(settings,$"CREATE TRIGGER FailNegationPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion={(phase=="marker"?3501:36)} THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected negation migration failure'; END IF; END");
        if(phase is "marker" or "ddl" or "publish")
        {
            try
            {
                Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));
                Assert.AreEqual(phase=="marker"?35:3501,Version(settings));
                Assert.ThrowsExactly<InvalidOperationException>(()=>old.Transaction(s=>true));
                Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(history,UndoSnapshot(settings));
            }
            finally
            {
                if(phase=="ddl")Sql(settings,$"UPDATE LogicalNodes SET `Value`='=',LogicLanguage='=' WHERE Code='{relation.Code}'");
                else Sql(settings,"DROP TRIGGER FailNegationPublish");
            }
            before=LineTopologyMySqlTests.RawSnapshot(settings);
        }
        var upgraded=new MySqlLineRepository(settings);
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));Assert.IsTrue(System.IO.File.Exists(upgraded.LogicalNodeMigrationBackupPath));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(history,UndoSnapshot(settings));
        var negation=LogicalNodeRelationTests.Add(upgraded,200.5m,50.5m,"¬","custom logic must be replaced");
        Assert.AreEqual("¬(?)",negation.Properties.LogicLanguage);
        var after=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));
        return Task.CompletedTask;
    });

    [TestMethod,DataRow("missing-value"),DataRow("narrow-value"),DataRow("missing-logic"),DataRow("narrow-value-missing-positions")]
    public Task CurrentVersionRequiresEnforcedCompleteChecksBeforeRecovery(string damage)=>Isolated(settings=>
    {
        new MySqlLineRepository(settings);
        Sql(settings,damage switch
        {
            "missing-value"=>"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationValue",
            "missing-logic"=>"ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationLogic",
            "narrow-value-missing-positions"=>PreviousCheck+"; DROP TABLE LogicalNodePositions",
            _=>PreviousCheck
        });
        Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));return Task.CompletedTask;
    });

    [TestMethod]
    public Task RebuildMissingPositionsPreservesNegationThroughEveryHistoricalConstraint()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,20,50,200,50);
        var created=LogicalNodeRelationTests.Add(repository,100.5m,50.5m,"¬");
        var edit=MapUndoTests.Edit(repository,scope=>new LogicalNodeService(scope).Update(new(created.Code,"owner",created.Version,"¬","preserved history")));
        Assert.IsTrue(edit.Success,edit.Message);var node=edit.Data!;
        var history=UndoSnapshot(settings);
        Sql(settings,"DROP TABLE LogicalNodePositions");
        var recovered=new MySqlLineRepository(settings);var restored=new LogicalNodeService(recovered).GetAll().Data!.Single();
        Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));Assert.AreEqual(node.Code,restored.Code);Assert.AreEqual(node.Properties,restored.Properties);
        Assert.AreEqual(node.Version,restored.Version);CollectionAssert.AreEqual(node.Positions,restored.Positions);Assert.AreEqual(history,UndoSnapshot(settings));
        Assert.AreEqual(Snapshot(recovered),Snapshot(new MySqlLineRepository(settings)));return Task.CompletedTask;
    });

    [TestMethod]
    public Task SqlAndDalAcceptNegationAndRejectAliasesOrMismatchedLogicAtomically()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var node=LogicalNodeRelationTests.Add(repository);
        Sql(settings,$"UPDATE LogicalNodes SET `Value`='¬',LogicLanguage='¬(?)' WHERE Code='{node.Code}'");
        var current=new LogicalNodeService(repository).GetAll().Data!.Single();Assert.AreEqual("¬",current.Properties.Value);Assert.AreEqual("¬(?)",current.Properties.LogicLanguage);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var value in new[]{"!","NOT","not","非","¬P"," ¬","¬ ",""})
        {
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET `Value`='{value}',LogicLanguage='{value}' WHERE Code='{node.Code}'")).Number);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        foreach(var assignment in new[]{"LogicLanguage='¬'","LogicLanguage='¬(P)'","LogicLanguage='¬(?) '","LogicLanguage='!'","LogicLanguage='P'","LogicLanguage=''","`Value`='='"})
        {
            Assert.AreEqual(3819,Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET {assignment} WHERE Code='{node.Code}'")).Number);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        Assert.ThrowsExactly<InvalidLogicalNodeRelationException>(()=>repository.Transaction(state=>
        {var changed=state.LogicalNodes[node.Code];state.LogicalNodes[node.Code]=changed with{Properties=changed.Properties with{Info="must roll back",LogicLanguage="!"}};return true;}));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));return Task.CompletedTask;
    });
}
