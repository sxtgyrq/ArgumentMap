using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeEquivalenceMySqlTests
{
    private const string PreviousCheck="ALTER TABLE LogicalNodes DROP CHECK CK_LN_RelationValue, ADD CONSTRAINT CK_LN_RelationValue CHECK (SideLength<>8 OR CONVERT(`Value` USING binary) IN (X'3D',X'E28692',X'E28A87',X'E28A8B',X'E28A86',X'E28A8A'))";
    private static int Version(MySqlSettings settings)
    {using var c=new MySqlConnection(settings.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";return Convert.ToInt32(q.ExecuteScalar());}

    [TestMethod,DataRow("normal"),DataRow("pending-ddl"),DataRow("publish")]
    public Task Upgrade28To29PreservesAllNodeDataAndIsChanged_InterruptedUpgradeStaysClosedAndCanRetry(string phase)=>Isolated(settings=>
    {
        var old=new MySqlLineRepository(settings);Add(old,20,50,500,50);var service=new LogicalNodeService(old);
        LogicalNodeInstanceTests.Parent(old,"HUMAN",50.5m);LogicalNodeInstanceTests.Child(old,"human(id=1)",80.5m);
        Assert.IsTrue(service.Create(new("owner",110.5m,50.5m,6,new(){Value="前提",LogicLanguage="P ∧ Q"})).Success);
        Assert.IsTrue(service.Create(new("owner",140.5m,50.5m,4,new(){Value="True",LogicLanguage="P ∧ Q"})).Success);
        LogicalNodeRelationTests.Add(old,170.5m,50.5m,"⊆");
        Sql(settings,PreviousCheck+"; UPDATE MapMetadata SET SchemaVersion=28 WHERE Id=1; UPDATE LogicalNodePositions SET IsChanged=-1");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        if(phase=="pending-ddl")
        {
            Sql(settings,"UPDATE MapMetadata SET SchemaVersion=2801 WHERE Id=1");
            Assert.ThrowsExactly<InvalidOperationException>(()=>old.Transaction(s=>true));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        if(phase=="publish")
        {
            Sql(settings,"CREATE TRIGGER FailEquivalencePublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=29 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected equivalence publication failure'; END IF; END");
            try
            {
                Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(2801,Version(settings));
                Assert.ThrowsExactly<InvalidOperationException>(()=>old.Transaction(s=>true));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
            }
            finally{Sql(settings,"DROP TRIGGER FailEquivalencePublish");}
        }
        var upgraded=new MySqlLineRepository(settings);Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Version(settings));Assert.IsTrue(System.IO.File.Exists(upgraded.LogicalNodeMigrationBackupPath));
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var relation=LogicalNodeRelationTests.Add(upgraded,200.5m,50.5m,"↔","custom logic must be replaced");Assert.AreEqual("↔",relation.Properties.LogicLanguage);
        var after=LineTopologyMySqlTests.RawSnapshot(settings);new MySqlLineRepository(settings);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });

    [TestMethod]
    public Task NarrowCurrentConstraintFailsClosed_AndMissingPositionsRecoverWithoutRejectingEquivalence()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,20,50,200,50);var relation=LogicalNodeRelationTests.Add(repository,100.5m,50.5m,"↔");
        Sql(settings,"DROP TABLE LogicalNodePositions");var recovered=new MySqlLineRepository(settings);var node=new LogicalNodeService(recovered).GetAll().Data!.Single();
        Assert.AreEqual(relation.Code,node.Code);Assert.AreEqual(relation.Properties,node.Properties);Assert.AreEqual(relation.Version,node.Version);CollectionAssert.AreEqual(relation.Positions,node.Positions);
        // A version number alone must never make an older six-symbol CHECK ready.
        Sql(settings,$"UPDATE LogicalNodes SET `Value`='=',LogicLanguage='=' WHERE Code='{relation.Code}'; "+PreviousCheck);
        Assert.ThrowsExactly<InvalidOperationException>(()=>new MySqlLineRepository(settings));return Task.CompletedTask;
    });
}
