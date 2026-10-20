using System.Net.Http;
using System.Windows;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class FrontAdditionMySqlTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public Task FrontAndTailAdditionF6_PreservesDirectionAndCrossState_UpdatesLnLocations_AndRollsBackWriteFailure(bool injectFailure)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var road=await Add(client,100,100,110,100,120,100);var vertical=await Add(client,105,90,105,110);
        Assert.IsTrue((await client.SaveLinePropertiesAsync(road.Code,road.Version,road.Properties with{PassInOpposeDirection=0,Speed=77,RoadName="前补点"})).Success);
        road=(await client.GetLineDetailAsync(road.Code)).Data!;
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var cross=(await client.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await client.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        var ln=(await client.CreateLogicalNodeAsync(105.5m,100.5m,6,new(){Value="保留值",Info="说明"})).Data!;
        var otherPosition=ln.Positions.Single(p=>p.RoadCode==vertical.Code);
        var editor=new LineEditor(client);await FrontAdditionTests.Edit(editor,new(115,100));Assert.IsTrue(editor.ToggleAdditionEnd());
        Assert.IsTrue(editor.AddPoint(new(90.2,100.1)));Assert.IsTrue(editor.AddPoint(new(80.2,100.1)));Assert.IsTrue(editor.ToggleAdditionEnd());Assert.IsTrue(editor.AddPoint(new(130.2,100.1)));
        if(injectFailure)
        {
            var before=LineTopologyMySqlTests.RawSnapshot(settings);
            Sql(settings,"CREATE TRIGGER TestPrependAtomic BEFORE INSERT ON LineNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected prepend failure'");
            try{Assert.IsFalse(await editor.CompleteAsync());}finally{Sql(settings,"DROP TRIGGER TestPrependAtomic");}
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(LineEditorMode.Drawing,editor.Mode);Assert.AreEqual(6,editor.DraftNodes.Count);
        }
        Assert.IsTrue(await editor.CompleteAsync(),editor.Message);var saved=(await client.GetLineDetailAsync(road.Code)).Data!;
        Assert.AreEqual(road.Code,saved.Code);Assert.AreEqual(road.Version+1,saved.Version);Assert.AreEqual(road.Properties,saved.Properties);
        CollectionAssert.AreEqual(new[]{80,90,100,110,120,130},saved.Nodes.Select(n=>n.X).ToArray());CollectionAssert.AreEqual(Enumerable.Range(0,6).ToArray(),saved.Nodes.Select(n=>n.Order).ToArray());
        Assert.IsTrue(saved.Nodes.All(n=>n.Y==100&&n.LineCode==road.Code));
        var current=(await client.GetLogicalNodesAsync()).Data!.Single();Assert.AreEqual(ln.Version+1,current.Version);Assert.AreEqual(ln.Properties,current.Properties);
        var position=current.Positions.Single(p=>p.RoadCode==road.Code);Assert.AreEqual((2,.55d),(position.RoadOrder,position.RoadPercent));Assert.AreEqual((105.5m,100m),(position.X,position.Y));Assert.AreEqual(saved.Version,position.RoadVersion);
        Assert.AreEqual(otherPosition,current.Positions.Single(p=>p.RoadCode==vertical.Code));
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}
        var recalculated=(await client.GetCrossesAsync()).Data!.Single();Assert.AreEqual(cross.Key,recalculated.Key);Assert.AreEqual(CrossState.Disconnected,recalculated.State);Assert.AreEqual((2,.5d),recalculated.LocationOn(road.Code));
        Assert.AreEqual(saved.Version,recalculated.Key.RoadCode1==road.Code?recalculated.RoadVersion1:recalculated.RoadVersion2);
        var settled=LineTopologyMySqlTests.RawSnapshot(settings);Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        while((await client.CalculateCrossAsync()).Data!.CanContinue){}Assert.IsTrue((await client.CalculateLogicalNodeAsync()).Success);Assert.AreEqual(settled,LineTopologyMySqlTests.RawSnapshot(settings));
    });
}
