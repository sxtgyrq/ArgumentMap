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

[TestClass,TestCategory("Unit")]
public sealed class LogicLanguageTests
{
    [TestMethod]
    public void MetadataLegacyUpdateMoveAndReverseKeepLogic_Size6RejectsExplicitEmpty()
    {
        var map=new TestMap();var road=map.Add(90,110,130,110);var service=new LogicalNodeService(map.Repository);
        var node=service.Create(new("owner",110.5m,110.5m,6,new(){Value="检查电源",LogicLanguage="(S ∧ ¬P) → A"})).Data!;
        node=service.Update(new(node.Code,node.Owner,node.Version,"新值","说明")).Data!;
        Assert.AreEqual("(S ∧ ¬P) → A",node.Properties.LogicLanguage);
        node=service.Move(new(node.Code,node.Owner,node.Version,111.5m,110.5m)).Data!;
        Assert.AreEqual("(S ∧ ¬P) → A",node.Properties.LogicLanguage);
        Assert.IsTrue(map.Lines.Reverse(new(road.Code,road.Owner,road.Version)).Success);
        node=service.GetAll().Data!.Single();Assert.AreEqual("(S ∧ ¬P) → A",node.Properties.LogicLanguage);
        var before=map.Snapshot();Assert.AreEqual("LogicalNodeLogicRequired",service.Update(new(node.Code,node.Owner,node.Version,node.Properties.Value,node.Properties.Info,LogicLanguage:"")).Code);
        Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow(0),DataRow(255),DataRow(256)]
    public void LogicLanguageLengthBoundaries_CreateAndUpdate(int length)
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);var logic=new string('命',length);var before=map.Snapshot();
        var create=service.Create(new("owner",100.5m,100.5m,12,new(){LogicLanguage=logic}));Assert.AreEqual(length<=255,create.Success);
        if(!create.Success)Assert.AreEqual(before,map.Snapshot());
        var node=create.Data??service.Create(new("owner",100.5m,100.5m,12,new())).Data!;before=map.Snapshot();
        var update=service.Update(new(node.Code,"owner",node.Version,"值","说明",LogicLanguage:logic));Assert.AreEqual(length is >0 and <=255,update.Success);
        if(!update.Success)Assert.AreEqual(before,map.Snapshot());else Assert.AreEqual(logic,update.Data!.Properties.LogicLanguage);
    }
    [TestMethod]
    public void NullCreateLogicAndUnauthorizedOrStaleUpdateRejectWithoutMutation()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);var before=map.Snapshot();
        Assert.IsFalse(service.Create(new("owner",100.5m,100.5m,6,new(){LogicLanguage=null!})).Success);Assert.AreEqual(before,map.Snapshot());
        var node=service.Create(new("owner",100.5m,100.5m,6,new(){LogicLanguage="S"})).Data!;before=map.Snapshot();
        Assert.AreEqual("NotOwner",service.Update(new(node.Code,"other",node.Version,"值","",LogicLanguage:"P")).Code);
        Assert.AreEqual("VersionConflict",service.Update(new(node.Code,"owner",node.Version+1,"值","",LogicLanguage:"P")).Code);Assert.AreEqual(before,map.Snapshot());
    }
}

[TestClass,TestCategory("MySql")]
public sealed class LogicLanguageMySqlTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public Task Schema16Upgrade_PreservesDataAndAllowsRetryAfterFailedPublication(bool interrupt)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);Add(repository,90,110,130,110);var service=new LogicalNodeService(repository);
        var node=service.Create(new("owner",110.5m,110.5m,6,new(){Value="中文",Info="说明"})).Data!;var before=Snapshot(repository);
        Sql(settings,"DROP TRIGGER TR_LN10_Attributes_Insert; DROP TRIGGER TR_LN10_Attributes_Update; ALTER TABLE LogicalNodes DROP CHECK CK_LN_LogicLength, DROP CHECK CK_LN_OperandKind, DROP CHECK CK_LN_OperandCase, DROP COLUMN OperandKind, DROP CHECK CK_LN_InstanceAttributes, DROP CHECK CK_LN_ValueLength, DROP CHECK CK_LN_InstanceValue, DROP CHECK CK_LN_InstanceLogic, DROP CHECK CK_LN_Logic6Required, DROP CHECK CK_LN_Logic12Required, DROP CHECK CK_LN_RelationLogic, DROP INDEX UQ_LN_Logic6, DROP COLUMN Logic6Key, DROP COLUMN LogicLanguage; UPDATE MapMetadata SET SchemaVersion=16 WHERE Id=1");
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER TestLogicPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=17 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Logic publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>true));}
            finally{Sql(settings,"DROP TRIGGER TestLogicPublish");}
        }
        repository=new MySqlLineRepository(settings);
        var repaired=new LogicalNodeService(repository).GetAll().Data!.Single();
        Assert.AreEqual("LN_"+node.Code,repaired.Properties.LogicLanguage);Assert.AreEqual(node.Version+1,repaired.Version);
        Assert.AreEqual(node.Code,repaired.Code);Assert.AreEqual(node.X,repaired.X);CollectionAssert.AreEqual(node.Positions,repaired.Positions);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));return Task.CompletedTask;
    });
    [TestMethod,TestCategory("Http")]
    public Task HttpUnicodeAndFormulaRoundTrip_FaultRollbackAndConcurrentVersionCheck()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        await Add(client,90,110,130,110);var node=(await client.CreateLogicalNodeAsync(110.5m,110.5m,6,new(){Value="动作",LogicLanguage="S"})).Data!;
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER TestLogicWrite BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Logic update failure'");
        try{Assert.AreEqual("DatabaseError",(await client.UpdateLogicalNodeAsync(node.Code,node.Version,"检查电源","说明","(S ∧ ¬P) → A")).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER TestLogicWrite");}
        var moved=(await client.UpdateLogicalNodeAsync(node.Code,node.Version,"检查电源","说明","(S ∧ ¬P) → A")).Data!;
        Assert.AreEqual("(S ∧ ¬P) → A",moved.Properties.LogicLanguage);CollectionAssert.AreEqual(node.Positions,moved.Positions);
        Assert.AreEqual("VersionConflict",(await client.UpdateLogicalNodeAsync(node.Code,node.Version,"失效","","Q")).Code);
        node=(await client.UpdateLogicalNodeAsync(moved.Code,moved.Version,"保留公式","旧调用")).Data!;Assert.AreEqual(moved.Properties.LogicLanguage,node.Properties.LogicLanguage);
        Assert.AreEqual(node.Properties,new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single().Properties);
        Assert.IsFalse((await client.UpdateLogicalNodeAsync(node.Code,node.Version,"值","",new string('中',256))).Success);
    });
}
