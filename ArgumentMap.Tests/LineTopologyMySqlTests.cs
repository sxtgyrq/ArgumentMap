using System.Net.Http;
using System.Text.Json;
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
public sealed class LineTopologyMySqlTests
{
    internal static string RawSnapshot(MySqlSettings settings)
    {
        using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();var tables=new SortedDictionary<string,string[]>();
        foreach(var table in new[]{"Lines","LineNodes","Crosses","LogicalNodes","LogicalNodePositions"})
        {
            using var command=connection.CreateCommand();command.CommandText=$"SELECT * FROM `{table}`";using var reader=command.ExecuteReader();var rows=new List<string>();
            while(reader.Read())
            {
                var row=new SortedDictionary<string,object?>();for(var i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);
                rows.Add(JsonSerializer.Serialize(row));
            }
            tables.Add(table,rows.Order(StringComparer.Ordinal).ToArray());
        }
        return JsonSerializer.Serialize(tables);
    }

    [TestMethod]
    public Task SplitThenMerge_HttpReassignsCrossAndLnPositionsAndSurvivesRestart()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,0,100,10,100,20,100,30,100,40,100,50,100);
        foreach(var x in new[]{5.5m,25.5m,35.5m})Assert.IsTrue((await client.CreateLogicalNodeAsync(x,100.5m,6,new())).Success);
        await Add(client,15,90,15,110);await Add(client,45,90,45,110);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var original=(await client.GetCrossesAsync()).Data!;
        foreach(var cross in original)Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Road1ToRoad2)).Success);
        original=(await client.GetCrossesAsync()).Data!;
        var beforeNodes=(await client.GetLogicalNodesAsync()).Data!.ToDictionary(n=>n.Code);
        var split=await client.SplitLineAsync(road.Code,road.Version,3);Assert.IsTrue(split.Success,split.Message);var first=split.Data!.Lines[0];var second=split.Data.Lines[1];
        Assert.AreEqual(4,(await client.GetAllAsync()).Data!.Length);Assert.AreEqual(2,(await client.GetCrossesAsync()).Data!.Length);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        var middle=(await client.GetLogicalNodesAsync()).Data!.Single(n=>n.X==25.5m);Assert.AreEqual(0,middle.Positions.Length);
        foreach(var node in (await client.GetLogicalNodesAsync()).Data!)
        {
            Assert.AreEqual(beforeNodes[node.Code].Version+1,node.Version);Assert.AreEqual(beforeNodes[node.Code].Properties,node.Properties);
            if(node.X==25.5m)continue;
            var position=node.Positions.Single();Assert.AreEqual(node.X==5.5m?first.Code:second.Code,position.RoadCode);
            Assert.AreEqual((0,.55d),(position.RoadOrder,position.RoadPercent));Assert.AreEqual((node.X,100m),(position.X,position.Y));
            Assert.AreEqual(node.X==5.5m?first.Version:second.Version,position.RoadVersion);Assert.IsFalse(position.IsChanged);
        }
        foreach(var cross in (await client.GetCrossesAsync()).Data!)
        {
            var code=cross.Key.Contains(first.Code)?first.Code:second.Code;Assert.AreEqual((1,.5d),cross.LocationOn(code));
            var old=original.Single(c=>c.Key.Position==cross.Key.Position);Assert.IsTrue(cross.Version>old.Version);
            var otherCode=old.Key.RoadCode1==road.Code?old.Key.RoadCode2:old.Key.RoadCode1;
            Assert.AreEqual(old.AllowsTransfer(road.Code,otherCode),cross.AllowsTransfer(code,otherCode));
        }
        var before=RawSnapshot(settings);var preview=await client.GetMergePlansAsync(first.Code,first.Version,second.Code,second.Version);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,RawSnapshot(settings));
        var merge=await client.ApplyMergePlanAsync(preview.Data!,preview.Data!.Plans.Single());Assert.IsTrue(merge.Success,merge.Message);var merged=merge.Data!.Lines.Single();
        CollectionAssert.AreEqual(road.Nodes,merged.Nodes);Assert.AreEqual(road.Code,merged.Code);Assert.AreEqual(3,(await client.GetAllAsync()).Data!.Length);
        foreach(var cross in (await client.GetCrossesAsync()).Data!)Assert.AreEqual(original.Single(o=>o.Key==cross.Key).State,cross.State);
        Assert.IsTrue((await client.GetLogicalNodesAsync()).Data!.All(n=>n.Positions.Length==1&&n.Positions[0].RoadCode==merged.Code));
        before=RawSnapshot(settings);while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,RawSnapshot(settings));
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod]
    public Task Merge_AdjustmentsPersistDeletedNewAndRetainedCrossesAndLnLocations_RestartAndRecalculateAreStable()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var a=await Add(client,100,100,140,100);var b=await Add(client,160,120,160,160);
        var lostA=await Add(client,135,99,135,101);var lostB=await Add(client,159,125,161,125);
        var retained=await Add(client,150,150,170,150);var added=await Add(client,140,120,150,110);
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var old=(await client.GetCrossesAsync()).Data!.Single(c=>c.Key.Contains(b.Code)&&c.Key.Contains(retained.Code));
        Assert.IsTrue((await client.SetCrossStateAsync(old.Key,old.Version,CrossState.Disconnected)).Success);
        var left=(await client.CreateLogicalNodeAsync(110.5m,100.5m,6,new(){Value="左",Info="保留"})).Data!;
        var right=(await client.CreateLogicalNodeAsync(160.5m,150.5m,6,new(){Value="右",Info="保留"})).Data!;
        var preview=(await client.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Data!;
        // Exercise a valid non-minimal endpoint adjustment; commit must rebuild its actual geometry.
        var plan=new MergePlan(130,100,160,130,1,1,135,135,20);
        var result=await client.ApplyMergePlanAsync(preview,plan);Assert.IsTrue(result.Success,result.Message);var merged=result.Data!.Lines.Single();
        var crosses=(await client.GetCrossesAsync()).Data!;Assert.AreEqual(2,crosses.Length);
        Assert.IsFalse(crosses.Any(c=>c.Key.Contains(b.Code)||c.Key.Contains(lostA.Code)||c.Key.Contains(lostB.Code)));
        var surviving=crosses.Single(c=>c.Key.Contains(retained.Code));Assert.AreEqual(CrossState.Disconnected,surviving.State);Assert.IsTrue(surviving.Version>old.Version);
        Assert.AreEqual((2,PercentPrecision.Normalize(20d/30)),surviving.LocationOn(a.Code));
        var fresh=crosses.Single(c=>c.Key.Contains(added.Code));Assert.AreEqual(CrossState.Unset,fresh.State);Assert.AreEqual(new CrossPosition(290,230),fresh.Key.Position);
        var nodes=(await client.GetLogicalNodesAsync()).Data!;Assert.AreEqual(2,nodes.Length);
        foreach(var previous in new[]{left,right})
        {
            var current=nodes.Single(n=>n.Code==previous.Code);Assert.AreEqual(previous.Version+1,current.Version);Assert.AreEqual(previous.Properties,current.Properties);
            Assert.AreEqual((previous.X,previous.Y,previous.SideLength),(current.X,current.Y,current.SideLength));
            var position=current.Positions.Single(p=>p.RoadCode==a.Code);Assert.AreEqual(merged.Version,position.RoadVersion);Assert.IsFalse(position.IsChanged);
            Assert.IsFalse(current.Positions.Any(p=>p.RoadCode==b.Code));
            if(current.Code==left.Code){Assert.AreEqual((0,.35d),(position.RoadOrder,position.RoadPercent));Assert.AreEqual((110.5m,100m),(position.X,position.Y));}
            else{Assert.AreEqual((2,PercentPrecision.Normalize(20.5/30)),(position.RoadOrder,position.RoadPercent));Assert.AreEqual((160m,150.5m),(position.X,position.Y));}
        }
        Assert.IsTrue(repository.Transaction(s=>s.PendingSegments.Count==0&&s.StaleCrosses.Count==0&&s.DirtyLines.Count==0&&s.Crosses.Values.All(c=>c.RoadVersion1==s.Lines[c.Key.RoadCode1].Version&&c.RoadVersion2==s.Lines[c.Key.RoadCode2].Version)));
        var before=RawSnapshot(settings);var restarted=new MySqlLineRepository(settings);Assert.AreEqual(Snapshot(repository),Snapshot(restarted));
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(before,RawSnapshot(settings));
    });

    [TestMethod,DataRow("LineNodes"),DataRow("Crosses"),DataRow("LogicalNodePositions")]
    public Task SplitFault_RollsBackBothRoadsAndEveryCrossPositionFlagAndTimestamp(string table)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,0,100,10,100,20,100,30,100);Assert.IsTrue((await client.CreateLogicalNodeAsync(25.5m,100.5m,6,new())).Success);
        await Add(client,25,90,25,110);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var before=RawSnapshot(settings);
        var body=table=="LineNodes"?$"BEGIN IF NEW.LineCode<>'{road.Code}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected split second road failure'; END IF; END":"SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected split dependent row failure'";
        Sql(settings,$"CREATE TRIGGER TestSplitAtomic BEFORE INSERT ON `{table}` FOR EACH ROW {body}");
        try{Assert.AreEqual("DatabaseError",(await client.SplitLineAsync(road.Code,road.Version,2)).Code);}finally{Sql(settings,"DROP TRIGGER TestSplitAtomic");}
        Assert.AreEqual(before,RawSnapshot(settings));Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        var retry=await client.SplitLineAsync(road.Code,road.Version,2);Assert.IsTrue(retry.Success,retry.Message);
        var second=retry.Data!.Lines[1];Assert.IsTrue((await client.GetCrossesAsync()).Data!.Single().Key.Contains(second.Code));
        Assert.IsTrue((await client.GetLogicalNodesAsync()).Data!.Single().Positions.Any(p=>p.RoadCode==second.Code));
    });

    [TestMethod,DataRow("LineNodes"),DataRow("Crosses"),DataRow("LogicalNodePositions")]
    public Task MergeFault_RollsBackDeletedRoadAndRekeyedDependents_ChosenPlanCanRetry(string table)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var a=await Add(client,0,100,10,100);var b=await Add(client,20,100,30,100);Assert.IsTrue((await client.CreateLogicalNodeAsync(25.5m,100.5m,6,new())).Success);
        await Add(client,25,90,25,110);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var preview=(await client.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Data!;var plan=preview.Plans.Single();var before=RawSnapshot(settings);
        Sql(settings,$"CREATE TRIGGER TestMergeAtomic BEFORE INSERT ON `{table}` FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected merge atomic failure'");
        try{Assert.AreEqual("DatabaseError",(await client.ApplyMergePlanAsync(preview,plan)).Code);}finally{Sql(settings,"DROP TRIGGER TestMergeAtomic");}
        Assert.AreEqual(before,RawSnapshot(settings));Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        var retry=await client.ApplyMergePlanAsync(preview,plan);Assert.IsTrue(retry.Success,retry.Message);Assert.IsFalse((await client.GetAllAsync()).Data!.Any(l=>l.Code==b.Code));
        Assert.IsTrue((await client.GetCrossesAsync()).Data!.Single().Key.Contains(a.Code));
        var positions=(await client.GetLogicalNodesAsync()).Data!.Single().Positions;Assert.AreEqual(2,positions.Length);Assert.AreEqual(a.Code,positions.Single(p=>p.RoadCode==a.Code).RoadCode);
    });

    [TestMethod]
    public Task TwoServers_MergeVersusSecondRoadSave_CannotDeleteConcurrentCommittedChanges()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var client=await TestLogin.ClientAsync(http1,first,"owner");var otherEditor=new HttpLineClient(http2,client.Session);
        for(var i=0;i<4;i++)
        {
            var y=100+i*100;var a=await Add(client,0,y,10,y);var b=await Add(client,20,y,30,y);
            var preview=(await client.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Data!;
            var merge=client.ApplyMergePlanAsync(preview,preview.Plans.Single());var save=otherEditor.SaveAsync(b.Code,b.Version,TestMap.Shape(b,20,y,30,y,40,y).Nodes);
            await Task.WhenAll(merge,save);Assert.AreEqual(1,(merge.Result.Success?1:0)+(save.Result.Success?1:0));
            if(merge.Result.Success)Assert.AreEqual("LineNotFound",save.Result.Code);
            else{Assert.AreEqual("VersionConflict",merge.Result.Code);Assert.AreEqual(3,(await client.GetLineDetailAsync(b.Code)).Data!.Nodes.Length);Assert.AreEqual(a.Version,(await client.GetLineDetailAsync(a.Code)).Data!.Version);}
            Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        }
    });

    [TestMethod]
    public Task TwoUsers_LnCreationVersusChosenDiagonalMerge_OnlyOneConstraintCompatibleCommit()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(http1,first,"alice");var bob=await TestLogin.ClientAsync(http2,second,"bob");
        for(var i=0;i<4;i++)
        {
            var x=1000+i*100;var y=1000+i*100;var a=await Add(alice,x,y,x+10,y);var b=await Add(alice,x+30,y+20,x+30,y+30);
            var preview=(await alice.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Data!;var plan=preview.Plans.Single(p=>p.DirectionX==1&&p.DirectionY==1);
            var merge=alice.ApplyMergePlanAsync(preview,plan);var create=bob.CreateLogicalNodeAsync(x+20.5m,y+10.5m,6,new());await Task.WhenAll(merge,create);
            Assert.AreEqual(1,(merge.Result.Success?1:0)+(create.Result.Success?1:0));
            Assert.AreEqual("DiagonalInsideLogicalNode",merge.Result.Success?create.Result.Code:merge.Result.Code);
            Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        }
    });

    [TestMethod]
    public Task TwoServers_SplitVersusMergeOfSameRoad_OnlyOnePublishesAndOtherReportsVersionConflict()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http1=new HttpClient{BaseAddress=first.BaseAddress};using var http2=new HttpClient{BaseAddress=second.BaseAddress};
        var client=await TestLogin.ClientAsync(http1,first,"owner");var another=new HttpLineClient(http2,client.Session);
        for(var i=0;i<4;i++)
        {
            var y=1000+i*100;var a=await Add(client,100,y,110,y,120,y,130,y);var b=await Add(client,140,y,150,y);
            var preview=(await client.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Data!;
            var split=client.SplitLineAsync(a.Code,a.Version,2);var merge=another.ApplyMergePlanAsync(preview,preview.Plans.Single());await Task.WhenAll(split,merge);
            Assert.AreEqual(1,(split.Result.Success?1:0)+(merge.Result.Success?1:0));Assert.AreEqual("VersionConflict",split.Result.Success?merge.Result.Code:split.Result.Code);
            var current=(await client.GetLineDetailAsync(a.Code)).Data!;Assert.AreEqual(a.Version+1,current.Version);
            if(split.Result.Success){Assert.AreEqual(2,current.Nodes.Length);Assert.AreEqual(b.Version,(await client.GetLineDetailAsync(b.Code)).Data!.Version);}
            else{Assert.AreEqual(6,current.Nodes.Length);Assert.AreEqual("LineNotFound",(await client.GetLineDetailAsync(b.Code)).Code);}
            while((await client.CalculateCrossAsync()).Data!.CanContinue){}
            Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
            Assert.IsTrue(repository.Transaction(s=>s.PendingSegments.Count==0&&s.StaleCrosses.Count==0&&s.DirtyLines.Count==0));
        }
    });

    [TestMethod]
    public Task Merge_HttpChecksBothOwnersWithoutAdminBypass_AndRollsBackEveryDeniedRequest()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var alice=await TestLogin.ClientAsync(http,server,"alice");
        var bob=await TestLogin.ClientAsync(http,server,"bob");var admin=await TestLogin.ClientAsync(http,server,"admin");
        Assert.IsTrue(repository.Transaction(s=>s.Accounts["admin"].EditAllRoad));
        var a=await Add(alice,100,100,110,100);var b=await Add(alice,130,100,140,100);
        var c=await Add(bob,100,200,110,200);var d=await Add(admin,130,200,140,200);
        foreach(var (client,first,second) in new[]{(alice,a,c),(alice,c,a),(bob,c,a),(admin,a,b),(admin,d,c),(admin,c,d)})
        {
            var before=RawSnapshot(settings);
            Assert.AreEqual("NotOwner",(await client.GetMergePlansAsync(first.Code,first.Version,second.Code,second.Version)).Code);
            // Bypass preview in the request to prove the commit route independently enforces ownership.
            var forged=new MergePreview(first,second,[]);
            var plan=new MergePlan(first.Nodes[^1].X,first.Nodes[^1].Y,second.Nodes[0].X,second.Nodes[0].Y,1,0,180,180,0);
            Assert.AreEqual("NotOwner",(await client.ApplyMergePlanAsync(forged,plan)).Code);
            Assert.AreEqual(before,RawSnapshot(settings));Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        }
        var allowed=await alice.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version);Assert.IsTrue(allowed.Success,allowed.Message);
        Assert.IsTrue((await alice.ApplyMergePlanAsync(allowed.Data!,allowed.Data!.Plans.Single())).Success);
        Assert.AreEqual("alice",(await alice.GetLineDetailAsync(a.Code)).Data!.Owner);
        var e=await Add(admin,150,200,160,200);var own=await admin.GetMergePlansAsync(d.Code,d.Version,e.Code,e.Version);Assert.IsTrue(own.Success,own.Message);
        Assert.IsTrue((await admin.ApplyMergePlanAsync(own.Data!,own.Data!.Plans.Single())).Success);
    });

    [TestMethod,DataRow(true),DataRow(false)]
    public Task Merge_HttpRechecksOwnershipAfterPreview_InCommitTransaction(bool changeFirst)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var a=await Add(client,100,100,110,100);var b=await Add(client,130,100,140,100);
        Assert.IsTrue((await client.CreateLogicalNodeAsync(105.5m,100.5m,6,new())).Success);
        await Add(client,105,90,105,110);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var preview=(await client.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Data!;
        var code=changeFirst?a.Code:b.Code;
        repository.Transaction(s=>{s.Lines[code]=s.Lines[code] with{Owner="other"};return true;});var before=RawSnapshot(settings);
        Assert.AreEqual("NotOwner",(await client.ApplyMergePlanAsync(preview,preview.Plans.Single())).Code);
        Assert.AreEqual(before,RawSnapshot(settings));Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });

    [TestMethod,DataRow("ln-diagonal"),DataRow("ln-node"),DataRow("cross")]
    public Task Merge_CommitValidatesLatestLnAndCross_RejectsAndRollsBackWholeMap(string kind)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        await using var otherServer=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http=new HttpClient{BaseAddress=server.BaseAddress};using var otherHttp=new HttpClient{BaseAddress=otherServer.BaseAddress};
        var owner=await TestLogin.ClientAsync(http,server,"owner");var other=await TestLogin.ClientAsync(otherHttp,otherServer,"other");
        var a=await Add(owner,100,100,110,100);
        var b=kind=="ln-node"?await Add(owner,130,100,140,100):await Add(owner,130,120,130,130);
        var previewResult=await owner.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version);Assert.IsTrue(previewResult.Success,previewResult.Message);
        var preview=previewResult.Data!;var plan=kind=="ln-node"?preview.Plans.Single():preview.Plans.Single(p=>p.DirectionX==1&&p.DirectionY==1);
        if(kind=="cross")
        {
            await Add(other,115,110,125,110);await Add(other,120,105,120,115);
            while((await other.CalculateCrossAsync()).Data!.CanContinue){}
            Assert.IsTrue((await other.GetCrossesAsync()).Data!.Any(c=>c.Key.Position==new CrossPosition(240,220)));
        }
        else
        {
            Assert.IsTrue((await other.CreateLogicalNodeAsync(120.5m,kind=="ln-node"?100.5m:110.5m,6,new())).Success);
            // A submitted endpoint adjustment must also be checked, even if it did not come from preview.
            if(kind=="ln-node")plan=plan with{FirstX=120};
        }
        var before=RawSnapshot(settings);
        var result=await owner.ApplyMergePlanAsync(preview,plan);
        Assert.AreEqual(kind switch{"ln-diagonal"=>"DiagonalInsideLogicalNode","ln-node"=>"LineNodeInsideLogicalNode",_=>"LinePassesCross"},result.Code,result.Message);
        Assert.AreEqual(before,RawSnapshot(settings));
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        Assert.AreEqual(a.Version,(await owner.GetLineDetailAsync(a.Code)).Data!.Version);
        Assert.AreEqual(b.Version,(await owner.GetLineDetailAsync(b.Code)).Data!.Version);
    });

    [TestMethod]
    public Task AuthenticationOwnerLockAndVersionChecks_ProtectNewRoutesAndLeaveDatabaseUnchanged()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var owner=await TestLogin.ClientAsync(http,server,"owner");var outsider=await TestLogin.ClientAsync(http,server,"other");
        var a=await Add(owner,0,100,10,100,20,100,30,100);var b=await Add(owner,40,100,50,100);
        var preview=(await owner.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Data!;var before=RawSnapshot(settings);
        Assert.AreEqual("NotOwner",(await outsider.SplitLineAsync(a.Code,a.Version,2)).Code);Assert.AreEqual("NotOwner",(await outsider.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Code);
        Assert.AreEqual("NotOwner",(await outsider.ApplyMergePlanAsync(preview,preview.Plans.Single())).Code);
        var invalid=new HttpLineClient(http,new("owner","invalid"));Assert.AreEqual("LoginExpired",(await invalid.GetMergePlansAsync(a.Code,a.Version,b.Code,b.Version)).Code);Assert.AreEqual("LoginExpired",(await invalid.SplitLineAsync(a.Code,a.Version,2)).Code);Assert.AreEqual("LoginExpired",(await invalid.ApplyMergePlanAsync(preview,preview.Plans.Single())).Code);
        Assert.AreEqual("VersionConflict",(await owner.SplitLineAsync(a.Code,a.Version-1,2)).Code);Assert.AreEqual(before,RawSnapshot(settings));
        repository.Transaction(s=>{s.Lines[a.Code]=s.Lines[a.Code] with{IsLocked=true};return true;});before=RawSnapshot(settings);
        Assert.AreEqual("LineIsLocked",(await owner.SplitLineAsync(a.Code,a.Version,2)).Code);Assert.AreEqual("LineIsLocked",(await owner.ApplyMergePlanAsync(preview,preview.Plans.Single())).Code);Assert.AreEqual(before,RawSnapshot(settings));
    });
}
