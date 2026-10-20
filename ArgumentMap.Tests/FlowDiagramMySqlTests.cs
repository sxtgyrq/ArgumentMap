using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class FlowDiagramMySqlTests
{
    [TestMethod]
    public Task ExistingLnProjectionAndParentVersionRollBackIfLastPositionInsertFails()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var existing=(await client.CreateLogicalNodeAsync(80.5m,50.5m,6,new(){Value="已有 LN"})).Data!;var d=FlowDiagramTests.Small();d["roads"]![0]!["points"]=System.Text.Json.Nodes.JsonNode.Parse("[[-10,0],[50,0]]");var text=d.ToJsonString();var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailFlowExistingLn BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected final position failure'");
        try{Assert.AreEqual("DatabaseError",(await client.ImportFlowDiagramAsync(text,50.5m,50.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailFlowExistingLn");}
        var result=await client.ImportFlowDiagramAsync(text,50.5m,50.5m);Assert.IsTrue(result.Success,result.Message);var node=result.Data!.Map!.LogicalNodes.Single(n=>n.Code==existing.Code);
        // Geometry and imported properties may each refresh the position within the same transaction.
        Assert.IsTrue(node.Version>existing.Version);Assert.AreEqual(1,node.Positions.Length);Assert.AreEqual(100,result.Data.Added.Lines.Single().Properties.Speed);
        Assert.AreEqual(result.Data.Added.Lines.Single().Version,node.Positions.Single().RoadVersion);
        var committed=LineTopologyMySqlTests.RawSnapshot(settings);Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(committed,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    public Task PreviewDoesNotWrite_ImportPersistsOrdinaryObjects_RestartAndKJKeepMetadataAndSettings()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var preview=await client.PreviewFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m);Assert.IsTrue(preview.Success,preview.Message);Assert.IsNull(preview.Data!.Map);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var result=await client.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m);Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(10,result.Data!.Added.LogicalNodes.Length);Assert.AreEqual(10,result.Data.Added.LogicalNodes.Sum(n=>n.Positions.Length));Assert.AreEqual(2,result.Data.Added.Crosses.Length);
        var restart=new MySqlLineRepository(settings);var nodes=new LogicalNodeService(restart).GetAll().Data!;Assert.AreEqual(10,nodes.Length);Assert.AreEqual("设备停止运行",nodes.Single(n=>n.Code==result.Data.LogicalNodeCodes["S"]).Properties.Value);
        Assert.IsTrue(new CrossService(restart).GetAll().Data!.All(c=>c.State==CrossState.Connected));before=LineTopologyMySqlTests.RawSnapshot(settings);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsFalse((await client.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m)).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod,DataRow("Lines"),DataRow("LineNodes"),DataRow("Crosses"),DataRow("LogicalNodes"),DataRow("LogicalNodePositions")]
    public Task AnyMapTableInsertFailureRollsBackWholeDiagramAndFlags_RetryCanImport(string table)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        await Add(client,300,300,330,300);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,$"CREATE TRIGGER FailFlowImport BEFORE INSERT ON `{table}` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected import failure'");
        try{Assert.AreEqual("DatabaseError",(await client.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailFlowImport");}
        var result=await client.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m);Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(10,result.Data!.Added.LogicalNodes.Length);
        Assert.AreEqual(1,repository.Transaction(s=>s.DirtyLines.Count),"Do not clear preexisting K tasks.");
    });

    [TestMethod]
    public Task PreviewThenAnotherUserCreatesConflict_CommitRevalidates_NoPartialWrites()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};var alice=await TestLogin.ClientAsync(http1,first,"alice");var bob=await TestLogin.ClientAsync(http2,second,"bob");
        var text=FlowDiagramTests.Small().ToJsonString();Assert.IsTrue((await alice.PreviewFlowDiagramAsync(text,50.5m,50.5m)).Success);
        Assert.IsTrue((await bob.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){Value="其他用户"})).Success);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LogicalNodesTouchOrOverlap",(await alice.ImportFlowDiagramAsync(text,50.5m,50.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await alice.ImportFlowDiagramAsync(text,150.5m,150.5m)).Success);
    });

    [TestMethod]
    public Task TwoServersImportAtSamePlace_ExactlyOneCommits_LoginRequiredAndNoOwnerInjection()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};var alice=await TestLogin.ClientAsync(http1,first,"alice");var bob=await TestLogin.ClientAsync(http2,second,"bob");
        var text=FlowDiagramTests.Small().ToJsonString();var before=LineTopologyMySqlTests.RawSnapshot(settings);var invalid=new HttpLineClient(http1,new("alice","invalid"));
        Assert.AreEqual("LoginExpired",(await invalid.PreviewFlowDiagramAsync(text,50.5m,50.5m)).Code);Assert.AreEqual("LoginExpired",(await invalid.ImportFlowDiagramAsync(text,50.5m,50.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var a=alice.ImportFlowDiagramAsync(text,50.5m,50.5m);var b=bob.ImportFlowDiagramAsync(text,50.5m,50.5m);await Task.WhenAll(a,b);Assert.AreEqual(1,(a.Result.Success?1:0)+(b.Result.Success?1:0));
        var owner=a.Result.Success?"alice":"bob";Assert.IsTrue(new LineSaveService(repository).GetAll().Data!.All(l=>l.Owner==owner));Assert.IsTrue(new LogicalNodeService(repository).GetAll().Data!.All(n=>n.Owner==owner));
        var injected=FlowDiagramTests.Small();injected["ln"]![0]!["owner"]="admin";before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual("InvalidFlowText",(await alice.ImportFlowDiagramAsync(injected.ToJsonString(),250.5m,250.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });
}
