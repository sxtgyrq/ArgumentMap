using System.Net.Http;
using System.Net.Http.Json;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql"), TestCategory("Http")]
public sealed class MapUndoMySqlTests
{
    private sealed class LoseUndoResponse : DelegatingHandler
    {
        internal string? LostRequestId;
        internal LoseUndoResponse() : base(new HttpClientHandler()) { }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var undo = request.RequestUri!.AbsolutePath == "/UndoMap";
            var id = undo ? System.Text.Json.JsonSerializer.Deserialize<MapUndoRequest>(
                await request.Content!.ReadAsStringAsync(token), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!.RequestId : null;
            var response = await base.SendAsync(request,token);
            if(undo && LostRequestId is null) { LostRequestId=id;response.Dispose();throw new System.IO.IOException("Simulated lost response after commit"); }
            return response;
        }
    }
    private static int Scalar(MySqlSettings settings, string sql)
    { using var c = new MySqlConnection(settings.ConnectionString()); c.Open(); using var q = c.CreateCommand(); q.CommandText = sql; return Convert.ToInt32(q.ExecuteScalar()); }
    private static string Journal(MySqlSettings settings)
    {
        using var c = new MySqlConnection(settings.ConnectionString()); c.Open(); using var q = c.CreateCommand();
        q.CommandText = "SELECT OperationId,Owner,Operation,Payload,IsUndone FROM MapUndoHistory ORDER BY Sequence; SELECT ObjectKey,OperationId FROM MapEditStamps ORDER BY ObjectKey";
        using var reader = q.ExecuteReader(); var rows = new List<string>();
        do { while (reader.Read()) rows.Add(string.Join("|", Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue))); } while (reader.NextResult());
        return string.Join("\n", rows);
    }

    [TestMethod]
    public Task Schema29UpgradeIsAdditive_RetrySafe_AndPreservesExistingMap()=>Isolated(settings=>
    {
        var repo = new MySqlLineRepository(settings); Add(repo, 20, 50, 240, 50); MapUndoTests.Node(repo);
        var before = LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings, "DROP TABLE MapUndoHistory; DROP TABLE MapEditStamps; UPDATE MapMetadata SET SchemaVersion=29 WHERE Id=1");
        Sql(settings, "CREATE TRIGGER FailUndoUpgrade BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=30 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected undo publication failure'; END IF; END");
        try { Assert.ThrowsExactly<MySqlException>(() => new MySqlLineRepository(settings)); Assert.AreEqual(29, Scalar(settings, "SELECT SchemaVersion FROM MapMetadata")); Assert.ThrowsExactly<InvalidOperationException>(() => repo.Transaction(s => true)); }
        finally { Sql(settings, "DROP TRIGGER FailUndoUpgrade"); }
        repo = new MySqlLineRepository(settings); Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion, Scalar(settings, "SELECT SchemaVersion FROM MapMetadata"));
        Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings)); Assert.AreEqual("NothingToUndo", new MapUndoService(repo).Undo("owner").Code);
        new MySqlLineRepository(settings); Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings)); return Task.CompletedTask;
    });

    [TestMethod]
    public Task JournalAndEditStampsCommitWithMap_FailuresRollBack_RestartAndReloginRetainUndo()=>Isolated(async settings=>
    {
        var repo = new MySqlLineRepository(settings); var n = MapUndoTests.Node(repo); await using var server = await LocalLineServer.StartAsync(repository: repo);
        using var http = new HttpClient { BaseAddress = server.BaseAddress }; var client = await TestLogin.ClientAsync(http, server, "owner"); var before = LineTopologyMySqlTests.RawSnapshot(settings);
        foreach (var table in new[] { "MapUndoHistory", "MapEditStamps" })
        {
            Sql(settings, $"CREATE TRIGGER FailUndoRecord BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected undo record failure'");
            try { Assert.AreEqual("DatabaseError", (await client.MoveLogicalNodeAsync(n.Code, n.Version, 90.5m, 90.5m)).Code); }
            finally { Sql(settings, "DROP TRIGGER FailUndoRecord"); }
            Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings)); Assert.AreEqual(0, Scalar(settings, "SELECT COUNT(*) FROM MapUndoHistory")); Assert.AreEqual(0, Scalar(settings, "SELECT COUNT(*) FROM MapEditStamps"));
        }
        Assert.IsTrue((await client.MoveLogicalNodeAsync(n.Code, n.Version, 90.5m, 90.5m)).Success);
        var moved = LineTopologyMySqlTests.RawSnapshot(settings); var journal = Journal(settings);
        Sql(settings, "CREATE TRIGGER FailConsumeUndo BEFORE UPDATE ON MapUndoHistory FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected undo consume failure'");
        try { Assert.AreEqual("DatabaseError", (await client.UndoAsync()).Code); }
        finally { Sql(settings, "DROP TRIGGER FailConsumeUndo"); }
        Assert.AreEqual(moved, LineTopologyMySqlTests.RawSnapshot(settings)); Assert.AreEqual(journal, Journal(settings));
        await using var restarted = await LocalLineServer.StartAsync(repository: new MySqlLineRepository(settings)); using var second = new HttpClient { BaseAddress = restarted.BaseAddress };
        var relogin = await TestLogin.ClientAsync(second, restarted, "owner"); Assert.AreEqual("LoginExpired", (await client.UndoAsync()).Code);
        var undo = await relogin.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual(50.5m, undo.Data!.Map.LogicalNodes.Single().X);
        Assert.AreEqual(1, Scalar(settings, "SELECT COUNT(*) FROM MapUndoHistory WHERE IsUndone=1")); Assert.AreEqual(0, Scalar(settings, "SELECT COUNT(*) FROM MapEditStamps"));
        Assert.AreEqual("NothingToUndo", (await relogin.UndoAsync()).Code);
        Assert.AreEqual(50.5m, new MySqlLineRepository(settings).Transaction(s => s.LogicalNodes[n.Code].X));
    });

    [TestMethod]
    public Task AreaUndoRollsBackAllSqlFailures_AndPreservesUnrelatedCrossAndPositionPendingFlags()=>Isolated(async settings=>
    {
        var repo = new MySqlLineRepository(settings); var selected = MapAreaPasteTests.Fixture(repo, CrossState.Road1ToRoad2);
        var far = Add(repo, 10000, 10000, 10200, 10000); Add(repo, 10100, 9900, 10100, 10100);
        Assert.IsTrue(new LogicalNodeService(repo).Create(new("owner",10050.5m,10000.5m,6,new(){LogicLanguage="FAR"})).Success);
        var calc = new CrossService(repo); while (calc.Calculate().Data!.CanContinue) { }
        Sql(settings, "UPDATE Crosses SET IsChanged=-1 WHERE X>1000; UPDATE LogicalNodePositions SET IsChanged=-1 WHERE X>1000");
        await using var server = await LocalLineServer.StartAsync(repository: repo); using var http = new HttpClient { BaseAddress = server.BaseAddress }; var client = await TestLogin.ClientAsync(http, server, "owner");
        var original = MapUndoTests.Content(repo); Assert.IsTrue((await client.MoveMapAreaAsync(selected, 300, 300)).Success);
        var before = LineTopologyMySqlTests.RawSnapshot(settings); var journal = Journal(settings);
        foreach (var (table, phase) in new[] { ("LogicalNodes", "UPDATE"), ("LineNodes", "INSERT"), ("Crosses", "INSERT"), ("LogicalNodePositions", "INSERT") })
        {
            Sql(settings, $"CREATE TRIGGER FailUndoRestore BEFORE {phase} ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected undo restore failure'");
            try { Assert.AreEqual("DatabaseError", (await client.UndoAsync()).Code, table); }
            finally { Sql(settings, "DROP TRIGGER FailUndoRestore"); }
            Assert.AreEqual(before, LineTopologyMySqlTests.RawSnapshot(settings), table); Assert.AreEqual(journal, Journal(settings));
        }
        var undo = await client.UndoAsync(); Assert.IsTrue(undo.Success, undo.Message); Assert.AreEqual(original, MapUndoTests.Content(repo));
        Assert.AreEqual(1, Scalar(settings, "SELECT COUNT(*) FROM Crosses WHERE X>1000 AND IsChanged=-1"));
        Assert.AreEqual(1, Scalar(settings, "SELECT COUNT(*) FROM LogicalNodePositions WHERE X>1000 AND IsChanged=-1"));
        Assert.AreEqual(0, Scalar(settings, "SELECT COUNT(*) FROM Crosses WHERE X<1000 AND IsChanged=-1"));
        Assert.AreEqual(0, Scalar(settings, "SELECT COUNT(*) FROM LogicalNodePositions WHERE X<1000 AND IsChanged=-1"));
        Assert.AreEqual(MapUndoTests.Content(repo), MapUndoTests.Content(new MySqlLineRepository(settings)));
    });

    [TestMethod]
    public Task TwoServersDoNotOverwriteOtherUsersEdits_IndependentEditsAndKJCanContinue()=>Isolated(async settings=>
    {
        var repo = new MySqlLineRepository(settings); var a = MapUndoTests.Node(repo); var b = MapUndoTests.Node(repo, logic:"Q", x:200.5m, owner:"bob");
        await using var one = await LocalLineServer.StartAsync(repository:repo); await using var two = await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1 = new HttpClient { BaseAddress = one.BaseAddress }; using var h2 = new HttpClient { BaseAddress = two.BaseAddress }; using var h3 = new HttpClient { BaseAddress = two.BaseAddress };
        var owner = await TestLogin.ClientAsync(h1,one,"owner"); var bob = await TestLogin.ClientAsync(h2,two,"bob"); var admin = await TestLogin.ClientAsync(h3,two,"admin");
        a = (await owner.MoveLogicalNodeAsync(a.Code,a.Version,90.5m,90.5m)).Data!;
        var ownerUndo=owner.UndoAsync();var bobMove=bob.MoveLogicalNodeAsync(b.Code,b.Version,300.5m,300.5m);
        await Task.WhenAll(ownerUndo,bobMove);Assert.IsTrue(ownerUndo.Result.Success,ownerUndo.Result.Message);Assert.IsTrue(bobMove.Result.Success,bobMove.Result.Message);
        Assert.AreEqual(300.5m,repo.Transaction(s=>s.LogicalNodes[b.Code].X));
        a = repo.Transaction(s=>s.LogicalNodes[a.Code]); a = (await owner.MoveLogicalNodeAsync(a.Code,a.Version,90.5m,90.5m)).Data!;
        Assert.IsTrue((await admin.UpdateLogicalNodeAsync(a.Code,a.Version,"别人编辑","","P")).Success);
        var before = LineTopologyMySqlTests.RawSnapshot(settings); Assert.AreEqual("UndoConflict",(await owner.UndoAsync()).Code); Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await admin.UndoAsync()).Success); // Undo restores the previous mutation stamp as well as data.
        Assert.IsTrue((await admin.CalculateCrossAsync()).Success); Assert.IsTrue((await admin.CalculateLogicalNodeAsync()).Success);
        var undo = await owner.UndoAsync(); Assert.IsTrue(undo.Success,undo.Message); Assert.AreEqual(50.5m,repo.Transaction(s=>s.LogicalNodes[a.Code].X));
        Assert.IsTrue((await bob.UndoAsync()).Success); Assert.AreEqual(200.5m,repo.Transaction(s=>s.LogicalNodes[b.Code].X));
    });

    [TestMethod]
    public Task PasteParentAndInstancesUndoAsOneCompensation_NormalParentDeletionRemainsForbidden()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);var selected=MapAreaPasteTests.Fixture(repo);var before=MapUndoTests.Content(repo);
        await using var server=await LocalLineServer.StartAsync(repository:repo);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var paste=await client.PasteMapAreaAsync(selected,300,300);Assert.IsTrue(paste.Success,paste.Message);
        var parent=paste.Data!.Added.LogicalNodes.Single(n=>n.SideLength==12);Assert.IsFalse((await client.DeleteLogicalNodeAsync(parent.Code,parent.Version)).Success);
        var undo=await client.UndoAsync();Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(before,MapUndoTests.Content(repo));
        var n=(await client.CreateLogicalNodeAsync(1000.5m,1000.5m,6,new(){LogicLanguage="bounded"})).Data!;
        for(var i=0;i<103;i++){var edited=await client.UpdateLogicalNodeAsync(n.Code,n.Version,"V"+i,"","bounded");Assert.IsTrue(edited.Success,edited.Message);n=edited.Data!;}
        Assert.AreEqual(100,Scalar(settings,"SELECT COUNT(*) FROM MapUndoHistory WHERE Owner='owner'"));
        for(var i=0;i<100;i++){var result=await client.UndoAsync();Assert.IsTrue(result.Success,result.Message);}
        Assert.AreEqual("V2",repo.Transaction(s=>s.LogicalNodes[n.Code].Properties.Value));Assert.AreEqual("NothingToUndo",(await client.UndoAsync()).Code);
    });

    [TestMethod]
    public Task LostResponseRetryAndRestartReplayUndoOnlyOnce()=>Isolated(async settings=>
    {
        var repo=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repo);
        using var handler=new LoseUndoResponse();using var http=new HttpClient(handler){BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var n=(await client.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){LogicLanguage="P"})).Data!;
        Assert.IsTrue((await client.MoveLogicalNodeAsync(n.Code,n.Version,90.5m,90.5m)).Success);
        var first=await client.UndoAsync();Assert.AreEqual("ConnectionFailure",first.Code);Assert.AreEqual(50.5m,repo.Transaction(s=>s.LogicalNodes[n.Code].X));
        var retry=await client.UndoAsync();Assert.IsTrue(retry.Success,retry.Message);StringAssert.Contains(retry.Message,"没有再次撤销");Assert.AreEqual(1,retry.Data!.Map.LogicalNodes.Length);
        await using var restarted=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));using var second=new HttpClient{BaseAddress=restarted.BaseAddress};
        var relogin=await TestLogin.ClientAsync(second,restarted,"owner");
        using var replay=await second.PostAsJsonAsync("UndoMap",new MapUndoRequest("owner",relogin.Session.LoginCode,handler.LostRequestId!));
        var response=await replay.Content.ReadFromJsonAsync<ServiceResult<MapUndoResult>>();Assert.IsTrue(response!.Success,response.Message);Assert.AreEqual(1,response.Data!.Map.LogicalNodes.Length);
        Assert.AreEqual(1,Scalar(settings,"SELECT COUNT(*) FROM MapUndoHistory WHERE IsUndone=1"));
        Assert.IsTrue((await relogin.UndoAsync()).Success);Assert.AreEqual(0,repo.Transaction(s=>s.LogicalNodes.Count));
    });
}
