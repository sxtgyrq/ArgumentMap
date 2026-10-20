using System.Net.Http;
using System.Threading.Channels;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class MultiUserBackgroundMySqlTests
{
    // Pause delivery AFTER the real Server has committed a unit. The calculation
    // remains running while a different account submits real HTTP transactions.
    private sealed class BetweenUnits(string path):DelegatingHandler(new HttpClientHandler())
    {
        internal readonly Channel<TaskCompletionSource> Commits=Channel.CreateUnbounded<TaskCompletionSource>();
        internal volatile bool Pause=true;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            var response=await base.SendAsync(request,token);
            if(Pause&&request.RequestUri!.AbsolutePath==path)
            {
                var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await Commits.Writer.WriteAsync(release,token);await release.Task.WaitAsync(token);
            }
            return response;
        }
        internal async Task<TaskCompletionSource> Next()=>await Commits.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task Finish(HttpLineClient client,bool j)
    {
        string? cursor=null;
        for(var i=0;i<200;i++)
        {
            if(j)
            {
                var result=await client.CalculateLogicalNodeStepAsync(cursor);Assert.IsTrue(result.Success,result.Message);
                if(!result.Data!.CanContinue)return;
                Assert.IsNotNull(result.Data.NextCode);cursor=result.Data.NextCode;
            }
            else
            {
                var result=await client.CalculateCrossAsync();Assert.IsTrue(result.Success,result.Message);
                if(!result.Data!.CanContinue)return;
            }
            await Task.Yield();
        }
        Assert.Fail("Background calculation did not finish.");
    }

    [TestMethod,DataRow(false,false),DataRow(false,true),DataRow(true,false),DataRow(true,true)]
    public Task OtherAccountCreatesEditsMovesAndImportsBeforeKOrJCompletes(bool j,bool injectFailure)=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var gate=new BetweenUnits(j?"/CalculateLogicalNode":"/CalculateCross");
        using var aliceHttp=new HttpClient(gate){BaseAddress=server.BaseAddress};using var bobHttp=new HttpClient{BaseAddress=server.BaseAddress};
        var alice=await TestLogin.ClientAsync(aliceHttp,server,"alice");var bob=await TestLogin.ClientAsync(bobHttp,server,"bob");
        var a=await Add(bob,20,50,80,50);await Add(bob,50,20,50,80);await Finish(bob,false);
        var oldCross=(await bob.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await bob.SetCrossStateAsync(oldCross.Key,oldCross.Version,CrossState.Connected)).Success);
        var ln=(await bob.CreateLogicalNodeAsync(30.5m,50.5m,6,new(){LogicLanguage="P"})).Data!;
        var longRoad=await Add(bob,300,300,700,300);
        for(var i=0;i<12;i++)Assert.IsTrue((await bob.CreateLogicalNodeAsync(320.5m+25*i,300.5m,6,new(){LogicLanguage="Q"+i})).Success);
        await Finish(bob,false);
        Assert.IsTrue((await bob.SaveAsync(a.Code,a.Version,TestMap.Shape(a,20,50,40,50,80,50).Nodes)).Success);
        var xy=Enumerable.Range(0,17).SelectMany(i=>new[]{300+25*i,300}).ToArray();
        Assert.IsTrue((await bob.SaveAsync(longRoad.Code,longRoad.Version,TestMap.Shape(longRoad,xy).Nodes)).Success);
        // Force J to perform real mark/restore work, not only read already-valid rows.
        if(j)Sql(settings,"UPDATE LogicalNodePositions SET IsChanged=-1");
        TaskCompletionSource? release=null;var calculation=Finish(alice,j);
        try
        {
            for(var phase=0;phase<4;phase++)
            {
                release=await gate.Next();Assert.IsFalse(calculation.IsCompleted);
                if(phase==0)
                {
                    await Add(bob,800,800,900,800);
                    Assert.IsTrue((await bob.CreateLogicalNodeAsync(850.5m,800.5m,6,new(){LogicLanguage="New"})).Success);
                }
                else if(phase==1)
                {
                    var latest=(await bob.GetLineDetailAsync(a.Code)).Data!;
                    var saved=await bob.SaveAsync(latest.Code,latest.Version,TestMap.Shape(latest,20,50,40,50,60,50,80,50).Nodes);Assert.IsTrue(saved.Success,saved.Message);
                    var current=(await bob.GetLogicalNodesAsync()).Data!.Single(n=>n.Code==ln.Code);
                    var edited=await bob.UpdateLogicalNodeAsync(current.Code,current.Version,"Bob edit","during calculation","P");Assert.IsTrue(edited.Success,edited.Message);
                    var moved=await bob.MoveLogicalNodeAsync(edited.Data!.Code,edited.Data.Version,25.5m,50.5m);Assert.IsTrue(moved.Success,moved.Message);
                }
                else if(phase==2)
                {
                    var selected=await bob.SelectMapAreaAsync(MapAreaMoveTests.Bounds);Assert.IsTrue(selected.Success,selected.Message);
                    var moved=await bob.MoveMapAreaAsync(selected.Data!,100,100);Assert.IsTrue(moved.Success,moved.Message);
                    Assert.AreEqual(CrossState.Connected,moved.Data!.Crosses.Single().State);
                }
                else
                {
                    var before=LineTopologyMySqlTests.RawSnapshot(settings);
                    var preview=await bob.PreviewFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
                    if(injectFailure)
                    {
                        Sql(settings,"CREATE TRIGGER FailMultiUserImport BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected multiuser import failure'");
                        try{Assert.AreEqual("DatabaseError",(await bob.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
                        finally{Sql(settings,"DROP TRIGGER FailMultiUserImport");}
                    }
                    var imported=await bob.ImportFlowDiagramAsync(FlowDiagramTests.Example,1200.5m,5000.5m);Assert.IsTrue(imported.Success,imported.Message);
                }
                Assert.IsFalse(calculation.IsCompleted,"Bob's transaction must finish before Alice's calculation does.");
                if(phase==3)gate.Pause=false;
                release.SetResult();release=null;
            }
            await calculation.WaitAsync(TimeSpan.FromSeconds(20));
        }
        finally
        {
            gate.Pause=false;release?.TrySetResult();
            while(gate.Commits.Reader.TryRead(out var pending))pending.TrySetResult();
            await calculation.WaitAsync(TimeSpan.FromSeconds(20));
        }
        // Each type retains its own markers: J never consumes pending K work.
        await Finish(bob,false);await Finish(bob,true);
        var all=(await bob.GetLogicalNodesAsync()).Data!;var roads=(await bob.GetAllAsync()).Data!;
        foreach(var node in all)
        {
            Assert.IsTrue(node.Positions.All(p=>!p.IsChanged));
            CollectionAssert.AreEqual(LogicalNodePositionCalculator.Calculate(node,roads),node.Positions);
        }
        var final=all.Single(n=>n.Code==ln.Code);Assert.AreEqual(125.5m,final.X);Assert.AreEqual("Bob edit",final.Properties.Value);
        var owned=(await bob.GetLineDetailAsync(a.Code)).Data!;
        Assert.AreEqual("NotOwner",(await alice.SaveAsync(owned.Code,owned.Version,owned.Nodes)).Code);
        var crosses=(await bob.GetCrossesAsync()).Data!;Assert.IsFalse(crosses.Any(c=>c.Key==oldCross.Key));Assert.AreEqual(CrossState.Connected,crosses.Single(c=>c.Key.Position==new CrossPosition(300,300)).State);
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));
        var beforeExpired=LineTopologyMySqlTests.RawSnapshot(settings);await TestLogin.ClientAsync(aliceHttp,server,"alice");
        var expired=j?(await alice.CalculateLogicalNodeStepAsync(null)).Code:(await alice.CalculateCrossAsync()).Code;
        Assert.AreEqual("LoginExpired",expired);Assert.AreEqual(beforeExpired,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    public Task JRestoringDerivedPositionsDoesNotInvalidateAnotherUsersGroupPreview_ButPropertyEditDoes()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var h1=new HttpClient{BaseAddress=server.BaseAddress};using var h2=new HttpClient{BaseAddress=server.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,server,"alice");var bob=await TestLogin.ClientAsync(h2,server,"bob");
        await Add(bob,20,50,80,50);await Add(bob,50,20,50,80);await Finish(bob,false);
        var cross=(await bob.GetCrossesAsync()).Data!.Single();Assert.IsTrue((await bob.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Disconnected)).Success);
        Assert.IsTrue((await bob.CreateLogicalNodeAsync(50.5m,50.5m,6,new(){LogicLanguage="P"})).Success);
        Sql(settings,"UPDATE LogicalNodePositions SET IsChanged=-1");var before=LineTopologyMySqlTests.RawSnapshot(settings);
        var selection=await bob.SelectMapAreaAsync(MapAreaMoveTests.Bounds);Assert.IsTrue(selection.Success,selection.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.IsTrue((await alice.CalculateLogicalNodeStepAsync(null)).Success);
        var move=await bob.MoveMapAreaAsync(selection.Data!,100,100);Assert.IsTrue(move.Success,move.Message);Assert.AreEqual(CrossState.Disconnected,move.Data!.Crosses.Single().State);Assert.IsTrue(move.Data.LogicalNodes.Single().Positions.All(p=>!p.IsChanged));
        selection=await bob.SelectMapAreaAsync(new(110,110,190,190));Assert.IsTrue(selection.Success,selection.Message);var current=(await bob.GetLogicalNodesAsync()).Data!.Single();
        Assert.IsTrue((await bob.UpdateLogicalNodeAsync(current.Code,current.Version,"changed","info","P")).Success);before=LineTopologyMySqlTests.RawSnapshot(settings);
        Assert.AreEqual("SelectionChanged",(await bob.MoveMapAreaAsync(selection.Data!,100,100)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
    });

    [TestMethod]
    public Task JCommitsOneNode_RollsBackFailedUnit_AndRereadsMovedDeletedAndNewNodes()=>Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var h1=new HttpClient{BaseAddress=server.BaseAddress};using var h2=new HttpClient{BaseAddress=server.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,server,"alice");var bob=await TestLogin.ClientAsync(h2,server,"bob");
        await Add(bob,20,50,280,50);
        for(var i=0;i<4;i++)Assert.IsTrue((await bob.CreateLogicalNodeAsync(40.5m+50*i,50.5m,6,new(){LogicLanguage="P"+i})).Success);
        Sql(settings,"UPDATE LogicalNodePositions SET IsChanged=-1");var old=(await bob.GetLogicalNodesAsync()).Data!.OrderBy(n=>n.Code,StringComparer.Ordinal).ToArray();
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"CREATE TRIGGER FailJUnit BEFORE UPDATE ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected J unit failure'");
        try{Assert.AreEqual("DatabaseError",(await alice.CalculateLogicalNodeStepAsync(null)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER FailJUnit");}
        var step=await alice.CalculateLogicalNodeStepAsync(null);Assert.IsTrue(step.Success,step.Message);Assert.IsTrue(step.Data!.CanContinue);Assert.AreEqual(old[0].Code,step.Data.NextCode);
        var after=(await bob.GetLogicalNodesAsync()).Data!.OrderBy(n=>n.Code,StringComparer.Ordinal).ToArray();Assert.IsFalse(after[0].Positions.Single().IsChanged);Assert.IsTrue(after.Skip(1).All(n=>n.Positions.Single().IsChanged));
        Assert.IsTrue((await bob.MoveLogicalNodeAsync(after[0].Code,after[0].Version,after[0].X,100.5m)).Success);
        Assert.IsTrue((await bob.DeleteLogicalNodeAsync(after[1].Code,after[1].Version)).Success);
        var created=await bob.CreateLogicalNodeAsync(260.5m,50.5m,6,new(){LogicLanguage="New"});Assert.IsTrue(created.Success,created.Message);
        var cursor=step.Data.NextCode;
        do{step=await alice.CalculateLogicalNodeStepAsync(cursor);Assert.IsTrue(step.Success,step.Message);cursor=step.Data!.NextCode;}while(step.Data.CanContinue);
        var final=(await bob.GetLogicalNodesAsync()).Data!;Assert.AreEqual(4,final.Length);Assert.IsFalse(final.Any(n=>n.Code==after[1].Code));Assert.AreEqual(0,final.Single(n=>n.Code==after[0].Code).Positions.Length);Assert.IsTrue(final.All(n=>n.Positions.All(p=>!p.IsChanged)));
    });
}
