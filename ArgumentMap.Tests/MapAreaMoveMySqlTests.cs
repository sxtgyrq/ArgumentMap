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
public sealed class MapAreaMoveMySqlTests
{
    private static async Task Calculate(HttpLineClient client)
    {while((await client.CalculateCrossAsync()).Data!.CanContinue){}}
    private static async Task<MapAreaSelection> Fixture(HttpLineClient client)
    {
        await Add(client,20,50,80,50);await Add(client,50,20,50,80);await Calculate(client);
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        Assert.IsTrue((await client.CreateLogicalNodeAsync(50.5m,50.5m,18,new(){Value="开始",Info="事务测试",LogicLanguage="S ∧ P"})).Success);
        var selection=await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds);Assert.IsTrue(selection.Success,selection.Message);return selection.Data!;
    }

    [TestMethod]
    public Task HttpSelectionRequiresFullSquaresAndAllPolylineVertices_AndRemainsReadOnly()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var index=0;
        foreach(var size in new[]{4,6,8,12,18})
        {
            var created=await client.CreateLogicalNodeAsync(250.5m+40*index++,250.5m,size,
                new(){Value=size==4?"False":size==8?"=":"完整框选",LogicLanguage=size==4?"":size==8?"=":"Node_"+size});
            Assert.IsTrue(created.Success,created.Message);var node=created.Data!;
            var bounds=new MapAreaBounds(node.Left,node.Bottom,node.Right,node.Top);
            var before=LineTopologyMySqlTests.RawSnapshot(settings);
            var full=await client.SelectMapAreaAsync(bounds);Assert.IsTrue(full.Success,full.Message);
            Assert.AreEqual(node.Code,full.Data!.LogicalNodes.Single().Code);
            foreach(var partial in new[]{bounds with{Left=bounds.Left+0.01m},bounds with{Right=bounds.Right-0.01m},
                bounds with{Bottom=bounds.Bottom+0.01m},bounds with{Top=bounds.Top-0.01m}})
                Assert.AreEqual("EmptySelection",(await client.SelectMapAreaAsync(partial)).Code);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings),"HTTP preview must not write to MySQL.");
        }
        var complete=await Add(client,20,20,80,20);
        await Add(client,20,70,0,70,0,60,20,60);
        await Add(client,20,80,100,80);
        await Add(client,0,35,100,35);
        var partialNode=await client.CreateLogicalNodeAsync(88.5m,50.5m,12,new(){Value="部分在框外",LogicLanguage="PARTIAL"});
        Assert.IsTrue(partialNode.Success,partialNode.Message);
        var initial=LineTopologyMySqlTests.RawSnapshot(settings);
        var selection=await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds);
        Assert.IsTrue(selection.Success,selection.Message);
        Assert.AreEqual(complete.Code,selection.Data!.Lines.Single().Code);
        Assert.AreEqual(0,selection.Data.LogicalNodes.Length);Assert.AreEqual(0,selection.Data.Crosses.Length);
        Assert.AreEqual(initial,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    [DataRow("moveX"),DataRow("moveY"),DataRow("delete")]
    public Task HttpSkippedCrossAllowsWholeRoadOperation_FailureRollsBackRelations_ThenRetry(string action)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,20,50,80,50);var external=await Add(client,50,20,50,80);await Calculate(client);
        var cross=(await client.GetCrossesAsync()).Data!.Single();
        Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Road1ToRoad2)).Success);
        var node=await client.CreateLogicalNodeAsync(70.5m,50.5m,6,new(){Value="完整 LN",LogicLanguage="P"});
        Assert.IsTrue(node.Success,node.Message);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        var selected=await client.SelectMapAreaAsync(new(15,40,85,60));Assert.IsTrue(selected.Success,selected.Message);
        Assert.AreEqual(road.Code,selected.Data!.Lines.Single().Code);Assert.AreEqual(1,selected.Data.LogicalNodes.Length);
        Assert.AreEqual(0,selected.Data.Crosses.Length);StringAssert.Contains(selected.Message,"已跳过 1 个");
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var staleSession=new HttpLineClient(http,new("owner","invalid"));
        Assert.AreEqual("LoginExpired",(await staleSession.MoveMapAreaAsync(selected.Data,10,0)).Code);
        Assert.AreEqual("LoginExpired",(await staleSession.DeleteMapAreaAsync(selected.Data)).Code);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var sqlAction=action=="moveX"?"UPDATE":"DELETE";
        Sql(settings,$"CREATE TRIGGER TestSkippedCross BEFORE {sqlAction} ON Crosses FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected skipped Cross write failure'");
        async Task<ServiceResult<MapAreaSnapshot>> Commit()=>action=="delete"?await client.DeleteMapAreaAsync(selected.Data):
            await client.MoveMapAreaAsync(selected.Data,action=="moveX"?10:0,action=="moveY"?10:0);
        try
        {
            var failed=await Commit();Assert.AreEqual("DatabaseError",failed.Code,failed.Message);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings),"All objects and Cross/LN markers must roll back together.");
        }
        finally{Sql(settings,"DROP TRIGGER TestSkippedCross");}
        var result=await Commit();Assert.IsTrue(result.Success,result.Message);
        var kept=result.Data!.Lines.Single(l=>l.Code==external.Code);
        Assert.AreEqual(external.Version,kept.Version);CollectionAssert.AreEqual(external.Nodes,kept.Nodes);
        if(action=="delete")
        {
            Assert.AreEqual(1,result.Data.Lines.Length);Assert.AreEqual(0,result.Data.LogicalNodes.Length);Assert.AreEqual(0,result.Data.Crosses.Length);
        }
        else
        {
            cross=result.Data.Crosses.Single();Assert.AreEqual(action=="moveX"?CrossState.Road1ToRoad2:CrossState.Unset,cross.State);
            Assert.AreEqual(action=="moveX"?new CrossPosition(100,100):new CrossPosition(100,120),cross.Key.Position);
            Assert.AreEqual(1,result.Data.LogicalNodes.Single().Positions.Length);
        }
        before=LineTopologyMySqlTests.RawSnapshot(settings);await Calculate(client);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    public Task HttpMovePersistsEverythingAcrossRestart_KDoesNotResetState_NoOpDoesNotWrite()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var selection=await Fixture(client);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.IsTrue((await client.MoveMapAreaAsync(selection,0,0)).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var move=await client.MoveMapAreaAsync(selection,100,100);Assert.IsTrue(move.Success,move.Message);
        var after=LineTopologyMySqlTests.RawSnapshot(settings);await Calculate(client);Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));
        var restarted=new MySqlLineRepository(settings);var crosses=new CrossService(restarted).GetAll().Data!;
        Assert.AreEqual(1,crosses.Length);Assert.AreEqual(CrossState.Disconnected,crosses.Single().State);Assert.AreEqual(new CrossPosition(300,300),crosses.Single().Key.Position);
        var node=new LogicalNodeService(restarted).GetAll("owner").Data!.Single();Assert.IsTrue(node.IsStart);Assert.AreEqual(150.5m,node.X);Assert.AreEqual(2,node.Positions.Length);
        Assert.AreEqual("S ∧ P",node.Properties.LogicLanguage);Assert.IsTrue(node.Positions.All(p=>!p.IsChanged&&p.RoadVersion==move.Data!.Lines.Single(l=>l.Code==p.RoadCode).Version));
        Assert.AreEqual(after,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod,DataRow("Lines","UPDATE"),DataRow("LineNodes","DELETE"),DataRow("LineNodes","INSERT"),
     DataRow("Crosses","DELETE"),DataRow("Crosses","INSERT"),DataRow("Crosses","UPDATE"),DataRow("LogicalNodes","UPDATE"),
     DataRow("LogicalNodePositions","DELETE"),DataRow("LogicalNodePositions","INSERT")]
    public Task InjectedWriteFailureRollsBackAllFiveTablesFlagsVersionsTimestamps_ThenRetry(string table,string action)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");var selection=await Fixture(client);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,$"CREATE TRIGGER TestAreaMove BEFORE {action} ON `{table}` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected group move failure'");
        try
        {
            var failed=await client.MoveMapAreaAsync(selection,100,100);Assert.AreEqual("DatabaseError",failed.Code,failed.Message);
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings),"Every row, relationship, flag and timestamp must roll back.");
        }
        finally{Sql(settings,"DROP TRIGGER TestAreaMove");}
        var retry=await client.MoveMapAreaAsync(selection,100,100);Assert.IsTrue(retry.Success,retry.Message);Assert.AreEqual(2,retry.Data!.LogicalNodes.Single().Positions.Length);
    });

    [TestMethod]
    public Task TwoServersCompetingGroupMoves_OnlyOneVersionCommits_StaleCrossEditRejected()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var client=await TestLogin.ClientAsync(http1,first,"owner");var sameUser=new HttpLineClient(http2,client.Session);var selection=await Fixture(client);
        var a=client.MoveMapAreaAsync(selection,100,100);var b=sameUser.MoveMapAreaAsync(selection,200,200);await Task.WhenAll(a,b);
        Assert.AreEqual(1,(a.Result.Success?1:0)+(b.Result.Success?1:0));Assert.IsFalse(a.Result.Success?b.Result.Success:a.Result.Success);
        var delta=a.Result.Success?100:200;var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.AreEqual(new CrossPosition(100+2*delta,100+2*delta),cross.Key.Position);
        Assert.AreEqual(CrossState.Disconnected,cross.State);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("CrossNotFound",(await sameUser.SetCrossStateAsync(selection.Crosses.Single().Key,selection.Crosses.Single().Version,CrossState.Connected)).Code);
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
    });

    [TestMethod,DataRow("road"),DataRow("ln"),DataRow("cross")]
    public Task ConcurrentDestinationConflict_OneOperationCommits_RegardlessOfOrder(string kind)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(http1,first,"alice");var bob=await TestLogin.ClientAsync(http2,second,"bob");
        Assert.IsTrue((await alice.CreateLogicalNodeAsync(50.5m,50.5m,6,new())).Success);
        CrossSnapshot? cross=null;LineSnapshot? road=null;
        if(kind=="road")road=(await bob.CreateAsync()).Data!;
        if(kind=="cross")
        {
            await Add(bob,120,150,180,150);await Add(bob,150,120,150,180);await Calculate(bob);
            cross=(await bob.GetCrossesAsync()).Data!.Single();cross=(await bob.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Data!;
        }
        var selection=(await alice.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;
        var move=alice.MoveMapAreaAsync(selection,100,100);
        var other=kind switch
        {
            "road"=>Success(bob.SaveAsync(road!.Code,road.Version,TestMap.Shape(road,150,150,150,180).Nodes)),
            "ln"=>Success(bob.CreateLogicalNodeAsync(150.5m,150.5m,6,new())),
            _=>Success(bob.SetCrossStateAsync(cross!.Key,cross.Version,CrossState.Connected))
        };
        await Task.WhenAll(move,other);Assert.AreEqual(1,(move.Result.Success?1:0)+(other.Result?1:0));Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
    });

    [TestMethod]
    public Task SessionPermissionsAndSourceStateChangedAfterSelection_AreCheckedInMoveTransaction()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");var other=await TestLogin.ClientAsync(http,server,"other");
        await Add(client,20,50,80,50);await Add(client,50,20,50,80);await Calculate(client);var selected=(await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;
        var badSession=new HttpLineClient(http,new("owner","invalid"));var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("LoginExpired",(await badSession.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Code);
        Assert.AreEqual("LoginExpired",(await badSession.MoveMapAreaAsync(selected,100,100)).Code);
        Assert.AreEqual("NotOwner",(await other.MoveMapAreaAsync(selected,100,100)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var cross=selected.Crosses.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Road1ToRoad2)).Success);
        before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual("SelectionChanged",(await client.MoveMapAreaAsync(selected,100,100)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        selected=(await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;var result=await client.MoveMapAreaAsync(selected,100,100);Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(CrossState.Road1ToRoad2,result.Data!.Crosses.Single().State);
    });
    private static async Task<bool> Success<T>(Task<ServiceResult<T>> task)=>(await task).Success;

    [TestMethod,DataRow(100),DataRow(20)]
    public Task SqlCrossLifecycle_MarksNegativeBeforeRestoreOrDelete_RecoversStateEvenWhenNewKeyReusesOldKey(int delta)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        await Add(client,20,50,80,50);await Add(client,40,20,40,80,60,80,60,20);await Calculate(client);
        foreach(var c in (await client.GetCrossesAsync()).Data!)Assert.IsTrue((await client.SetCrossStateAsync(c.Key,c.Version,c.Key.Position.X==40?CrossState.Connected:CrossState.Road2ToRoad1)).Success);
        var selected=(await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;CreateAudit(settings);
        Assert.IsTrue((await client.MoveMapAreaAsync(selected,0,0)).Success);Assert.AreEqual(0,Audit(settings).Count);
        var result=await client.MoveMapAreaAsync(selected,delta,0);Assert.IsTrue(result.Success,result.Message);
        var events=Audit(settings);Assert.AreEqual(2,events.Count(e=>e.Kind=="U"&&e.Old==0&&e.New==-1));
        Assert.IsTrue(events.Take(2).All(e=>e.Kind=="U"&&e.Old==0&&e.New==-1),"All original Crosses must be marked before any replacement or deletion.");
        Assert.IsTrue(events.Where(e=>e.Kind=="D").All(e=>e.Old==-1),"Only unrecovered negative originals are deleted.");
        Assert.AreEqual(delta==20?1:2,events.Count(e=>e.Kind=="D"));
        if(delta==20)Assert.IsTrue(events.Any(e=>e.Kind=="U"&&e.X==60&&e.Old==-1&&e.New==0),"A reused original primary key must be explicitly restored to 0.");
        var crosses=result.Data!.Crosses;Assert.AreEqual(CrossState.Connected,crosses.Single(c=>c.Key.Position.X==40+delta).State);Assert.AreEqual(CrossState.Road2ToRoad1,crosses.Single(c=>c.Key.Position.X==60+delta).State);
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT COUNT(*) FROM Crosses WHERE IsChanged<>0";Assert.AreEqual(0,Convert.ToInt32(command.ExecuteScalar()));
        var before=LineTopologyMySqlTests.RawSnapshot(settings);await Calculate(client);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    public Task RestoreFailureRollsBackMarksAndAllData_NoTemporaryNegativeMarkerLeaks()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        await Add(client,20,50,80,50);await Add(client,40,20,40,80,60,80,60,20);await Calculate(client);
        var selected=(await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;CreateAudit(settings);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailCrossRestore BEFORE UPDATE ON Crosses FOR EACH ROW BEGIN IF OLD.IsChanged=-1 AND NEW.IsChanged=0 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected restore failure'; END IF; END");
        try
        {Assert.AreEqual("DatabaseError",(await client.MoveMapAreaAsync(selected,20,0)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(0,Audit(settings).Count,"Audit inserts and pending marks must roll back with the map.");}
        finally{Sql(settings,"DROP TRIGGER FailCrossRestore");}
        Assert.IsTrue((await client.MoveMapAreaAsync(selected,20,0)).Success);
    });

    private static void CreateAudit(MySqlSettings settings)
    {
        Sql(settings,"CREATE TABLE CrossLifecycleAudit (Seq INT AUTO_INCREMENT PRIMARY KEY,Kind CHAR(1),X DECIMAL(7,1),OldFlag INT NULL,NewFlag INT NULL) ENGINE=InnoDB");
        Sql(settings,"CREATE TRIGGER AuditCrossUpdate AFTER UPDATE ON Crosses FOR EACH ROW INSERT INTO CrossLifecycleAudit(Kind,X,OldFlag,NewFlag) VALUES ('U',NEW.X,OLD.IsChanged,NEW.IsChanged)");
        Sql(settings,"CREATE TRIGGER AuditCrossDelete AFTER DELETE ON Crosses FOR EACH ROW INSERT INTO CrossLifecycleAudit(Kind,X,OldFlag) VALUES ('D',OLD.X,OLD.IsChanged)");
        Sql(settings,"CREATE TRIGGER AuditCrossInsert AFTER INSERT ON Crosses FOR EACH ROW INSERT INTO CrossLifecycleAudit(Kind,X,NewFlag) VALUES ('I',NEW.X,NEW.IsChanged)");
    }
    private static List<(string Kind,decimal X,int? Old,int? New)> Audit(MySqlSettings settings)
    {
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var command=connection.CreateCommand();command.CommandText="SELECT Kind,X,OldFlag,NewFlag FROM CrossLifecycleAudit ORDER BY Seq";
        using var reader=command.ExecuteReader();var rows=new List<(string,decimal,int?,int?)>();while(reader.Read())rows.Add((reader.GetString(0),reader.GetDecimal(1),reader.IsDBNull(2)?null:reader.GetInt32(2),reader.IsDBNull(3)?null:reader.GetInt32(3)));return rows;
    }

    [TestMethod]
    public Task RigidTranslationKeepsExact18DigitCrossAndLnPercentages_AfterKeyAndForeignKeyReplacement()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var a=await Add(client,20,50,80,50);await Add(client,40,20,40,80);await Calculate(client);
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        var node=(await client.CreateLogicalNodeAsync(40.5m,50.5m,6,new())).Data!;
        var percentColumn=cross.Key.RoadCode1==a.Code?"Percent1":"Percent2";
        Sql(settings,$"UPDATE Crosses SET {percentColumn}=0.333333333333333333; UPDATE LogicalNodePositions SET RoadPercent=0.341666666666666667 WHERE RoadCode='{a.Code}'");
        var selection=(await client.SelectMapAreaAsync(MapAreaMoveTests.Bounds)).Data!;var move=await client.MoveMapAreaAsync(selection,100,100);Assert.IsTrue(move.Success,move.Message);
        using var connection=new MySqlConnection(settings.ConnectionString());await connection.OpenAsync();
        using var command=connection.CreateCommand();command.CommandText=$"SELECT {percentColumn} FROM Crosses";
        Assert.AreEqual(0.333333333333333333m,(decimal)(await command.ExecuteScalarAsync())!);
        command.CommandText="SELECT RoadPercent FROM LogicalNodePositions WHERE LogicalNodeCode=@ln AND RoadCode=@road";command.Parameters.AddWithValue("@ln",node.Code);command.Parameters.AddWithValue("@road",a.Code);
        Assert.AreEqual(0.341666666666666667m,(decimal)(await command.ExecuteScalarAsync())!);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);await Calculate(client);Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });
}
