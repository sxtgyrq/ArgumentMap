using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeOperandMySqlTests
{
    private static int Version(MySqlSettings s){using var c=new MySqlConnection(s.ConnectionString());c.Open();using var q=c.CreateCommand();q.CommandText="SELECT SchemaVersion FROM MapMetadata";return Convert.ToInt32(q.ExecuteScalar());}
    [TestMethod,DataRow("normal"),DataRow("pending"),DataRow("publish")]
    public Task Upgrade30PreservesOriginalCaseAndHistory_DefaultsCustom_RetryStaysClosed(string mode)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var upper=LogicalNodeOperandTests.Add(repo,"Custom","P");var lower=LogicalNodeOperandTests.Add(repo,"Custom","p",100.5m);MapUndoTests.Node(repo,4,"False","¬p",150.5m);
        Assert.IsTrue(MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Move(new(upper.Code,"owner",upper.Version,80.5m,80.5m))).Success);
        var before=Snapshot(repo);
        // Simulate the real pre-feature schema and journal JSON, which has no OperandKind.
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_OperandKind, DROP CHECK CK_LN_OperandCase, DROP COLUMN OperandKind; UPDATE MapMetadata SET SchemaVersion=30 WHERE Id=1; UPDATE MapUndoHistory SET Payload=REPLACE(Payload,',\"OperandKind\":\"Custom\"','')");
        if(mode=="pending")Sql(settings,"UPDATE MapMetadata SET SchemaVersion=3001 WHERE Id=1");
        if(mode=="publish")
        {
            Sql(settings,"CREATE TRIGGER FailOperandPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=31 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected operand publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.AreEqual(3001,Version(settings));Assert.ThrowsExactly<InvalidOperationException>(()=>repo.Transaction(s=>true));}
            finally{Sql(settings,"DROP TRIGGER FailOperandPublish");}
        }
        var upgraded=new MySqlLineRepository(settings);Assert.AreEqual(31,Version(settings));Assert.AreEqual(before,Snapshot(upgraded));Assert.IsTrue(upgraded.Transaction(s=>s.LogicalNodes.Values.All(n=>n.Properties.OperandKind=="Custom")));
        var undo=new MapUndoService(upgraded).Undo("owner");Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(50.5m,undo.Data!.Map.LogicalNodes.Single(n=>n.Code==upper.Code).X);
        Assert.AreEqual("P",undo.Data.Map.LogicalNodes.Single(n=>n.Code==upper.Code).Properties.LogicLanguage);Assert.AreEqual("p",undo.Data.Map.LogicalNodes.Single(n=>n.Code==lower.Code).Properties.LogicLanguage);
        new MySqlLineRepository(settings);await Task.CompletedTask;
    });
    [TestMethod,DataRow("Class","P"),DataRow("Instance","p"),DataRow("Custom","p")]
    public Task HttpCasingUniquenessReferenceSyncSqlGuardsAndUndoPersistAcrossRestart(string kind,string expected)=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var created=await client.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){Value="保持 Mixed",LogicLanguage="p",OperandKind=kind});Assert.IsTrue(created.Success,created.Message);var n=created.Data!;Assert.AreEqual(kind,n.Properties.OperandKind);Assert.AreEqual(expected,n.Properties.LogicLanguage);
        var branch=(await client.CreateLogicalNodeAsync(80.5m,50.5m,4,new(){Value="False",LogicLanguage="¬"+expected})).Data!;
        var expired=new HttpLineClient(http,new("owner","bad"));Assert.AreEqual("LoginExpired",(await expired.UpdateLogicalNodeAsync(n.Code,n.Version,n.Properties.Value,"","q","Class")).Code);
        var next=await client.UpdateLogicalNodeAsync(n.Code,n.Version,n.Properties.Value,"new","q","Class");Assert.IsTrue(next.Success,next.Message);Assert.AreEqual("Q",next.Data!.Properties.LogicLanguage);Assert.AreEqual("Class",next.Data.Properties.OperandKind);
        Assert.AreEqual("¬Q",repo.Transaction(s=>s.LogicalNodes[branch.Code].Properties.LogicLanguage));
        var prior=Snapshot(repo);
        foreach(var sql in new[]{"OperandKind='Class',LogicLanguage='lower'","OperandKind='Instance',LogicLanguage='UPPER'","OperandKind='class'","OperandKind='Custom '","OperandKind=NULL"})
            Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,$"UPDATE LogicalNodes SET {sql} WHERE Code='{n.Code}'"));
        Assert.AreEqual(prior,Snapshot(repo));
        var undo=await client.UndoAsync();Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(kind,undo.Data!.Map.LogicalNodes.Single(x=>x.Code==n.Code).Properties.OperandKind);Assert.AreEqual("¬"+expected,undo.Data.Map.LogicalNodes.Single(x=>x.Code==branch.Code).Properties.LogicLanguage);
        Assert.AreEqual(Snapshot(repo),Snapshot(new MySqlLineRepository(settings)));
    });
    [TestMethod]
    public Task TwoUsersReserveAfterConversion_EditingCollisionRollsBackAndKeepsBranches()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);await using var one=await LocalLineServer.StartAsync(repository:repo);await using var two=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=one.BaseAddress};using var h2=new HttpClient{BaseAddress=two.BaseAddress};var a=await TestLogin.ClientAsync(h1,one,"owner");var b=await TestLogin.ClientAsync(h2,two,"bob");
        var results=await Task.WhenAll(a.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){LogicLanguage="p",OperandKind="Class"}),b.CreateLogicalNodeAsync(100.5m,50.5m,6,new(){LogicLanguage="P",OperandKind="Class"}));
        foreach(var r in results)Assert.IsTrue(r.Success,r.Message);CollectionAssert.AreEquivalent(new[]{"P","P1"},results.Select(r=>r.Data!.Properties.LogicLanguage).ToArray());
        var node=results[0].Data!;var other=results[1].Data!;var before=Snapshot(repo);
        Assert.AreEqual("DuplicateLogicalNodeLogic",(await a.UpdateLogicalNodeAsync(node.Code,node.Version,node.Properties.Value,"",other.Properties.LogicLanguage.ToLowerInvariant(),"Class")).Code);Assert.AreEqual(before,Snapshot(repo));
        Sql(settings,"CREATE TRIGGER FailOperandUpdate BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected operand save failure'");
        try{Assert.AreEqual("DatabaseError",(await a.UpdateLogicalNodeAsync(node.Code,node.Version,node.Properties.Value,"","new","Instance")).Code);}
        finally{Sql(settings,"DROP TRIGGER FailOperandUpdate");}Assert.AreEqual(before,Snapshot(repo));
    });
}
