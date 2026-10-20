using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LinePasteMySqlTests
{
    [TestMethod]
    public Task HttpPasteRollsBackSqlFaults_RestoresAllPropertiesAndRelations_AndPersistsAcrossRestart()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        repository.Transaction(s=>{s.Accounts["owner"]=s.Accounts["owner"] with{EditAllRoad=true};return true;});
        var source=await Add(client,100,100,200,100);var properties=new LineProperties{RoadName="副本来源",RoadDetail="说明\n同样保留",Speed=321,PassInOpposeDirection=1};
        var edited=await client.SaveLinePropertiesAsync(source.Code,source.Version,properties);Assert.IsTrue(edited.Success,edited.Message);source=edited.Data!;
        var vertical=await Add(client,350,280,350,320);while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var locked=await client.ToggleLineLockAsync(source.Code,source.Version);Assert.IsTrue(locked.Success,locked.Message);source=locked.Data!;
        var ln=(await client.CreateLogicalNodeAsync(350.5m,300.5m,6,new())).Data!;var token=LinePasteTests.Copy(source);
        // Copying read-only geometry is a new road owned by the authenticated user;
        // no operation changes the original owner's line or lock state.
        var alice=await TestLogin.ClientAsync(http,server,"alice");
        foreach(var table in new[]{"LineNodes","Crosses","LogicalNodePositions"})
        {
            var before=LineTopologyMySqlTests.RawSnapshot(settings);var semantic=Snapshot(repository);
            Sql(settings,$"CREATE TRIGGER TestRoadPasteFailure BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected road paste failure'");
            try{var failed=await alice.PasteLineAsync(token,300,300);Assert.IsFalse(failed.Success);Assert.AreEqual("DatabaseError",failed.Code);}
            finally{Sql(settings,"DROP TRIGGER TestRoadPasteFailure");}
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings),table);Assert.AreEqual(semantic,Snapshot(repository),table);
        }
        var result=await alice.PasteLineAsync(token,300,300);Assert.IsTrue(result.Success,result.Message);var copy=result.Data!.Line;
        Assert.AreEqual("alice",copy.Owner);Assert.AreNotEqual(source.Code,copy.Code);Assert.IsTrue(copy.IsLocked);Assert.AreEqual(properties,copy.Properties);Assert.AreEqual((300,300),(copy.Nodes[0].X,copy.Nodes[0].Y));
        Assert.AreEqual(3,(await client.GetAllAsync()).Data!.Length);Assert.AreEqual(source.Version,(await client.GetLineDetailAsync(source.Code)).Data!.Version);
        var cross=result.Data.Map.Crosses.Single();Assert.IsTrue(cross.Key.Contains(copy.Code)&&cross.Key.Contains(vertical.Code));Assert.AreEqual(CrossState.Disconnected,cross.State);
        var position=result.Data.Map.LogicalNodes.Single(n=>n.Code==ln.Code).Positions.Single(p=>p.RoadCode==copy.Code);Assert.AreEqual((350.5m,300m),(position.X,position.Y));Assert.IsFalse(position.IsChanged);
        var persisted=Snapshot(repository);Assert.AreEqual(persisted,Snapshot(new MySqlLineRepository(settings)));
        // K and J remain safe after the atomic copy has already calculated its relations.
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}while((await client.CalculateLogicalNodeAsync()).Data!.CanContinue){}
        Assert.AreEqual(persisted,Snapshot(repository));
    });

    [TestMethod]
    public Task TwoUsersPasteSamePosition_OnlyOneCommits_AndSourceChangedAfterCopyIsRejected()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var first=await LocalLineServer.StartAsync(repository:repository);
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var firstHttp=new HttpClient{BaseAddress=first.BaseAddress};using var secondHttp=new HttpClient{BaseAddress=second.BaseAddress};
        var alice=await TestLogin.ClientAsync(firstHttp,first,"alice");var bob=await TestLogin.ClientAsync(secondHttp,second,"bob");var source=await Add(alice,100,100,140,100);var token=LinePasteTests.Copy(source);
        var attempts=await Task.WhenAll(alice.PasteLineAsync(token,300,300),bob.PasteLineAsync(token,300,300));Assert.AreEqual(1,attempts.Count(r=>r.Success));Assert.AreEqual(2,(await alice.GetAllAsync()).Data!.Length);
        Assert.AreEqual(0,repository.Transaction(s=>s.Lines.Values.Count(l=>l.Nodes.Length<2)),"No abandoned skeleton road from the losing transaction.");
        var changed=await alice.SaveLinePropertiesAsync(source.Code,source.Version,new(){Speed=240});Assert.IsTrue(changed.Success,changed.Message);
        var before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual("VersionConflict",(await bob.PasteLineAsync(token,400,400)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await bob.PasteLineAsync(LinePasteTests.Copy(changed.Data!),400,400)).Success);Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
    });
}
