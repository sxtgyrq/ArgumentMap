using System.Net.Http;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Client;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;

namespace ArgumentMap.Tests;

internal static class CrossDirectionMySqlChecks
{
    private static string Snapshot(ILineRepository repository)=>repository.Transaction(s=>JsonSerializer.Serialize(new
    {
        Lines=s.Lines.Values.OrderBy(l=>l.Code).ToArray(),Crosses=s.Crosses.Values.OrderBy(c=>c.Key).ToArray(),
        Pending=s.PendingSegments.OrderBy(p=>p.LineCode).ThenBy(p=>p.Segment).Select(p=>new{p.LineCode,p.Segment}).ToArray(),
        Stale=s.StaleCrosses.Order().ToArray(),Dirty=s.DirtyLines.Order().ToArray()
    }));
    private static async Task Calculate(HttpLineClient client)
    {
        for(var i=0;i<100;i++) { var step=await client.CalculateCrossAsync();Assert.IsTrue(step.Success,step.Message);if(!step.Data!.CanContinue)return; }
        Assert.Fail("计算未结束。");
    }

    internal static async Task RunAsync(MySqlSettings settings)
    {
        Assert.IsTrue(settings.Database.StartsWith("ArgumentMap_Integration_",StringComparison.Ordinal));
        var repository=new MySqlLineRepository(settings);
        await using var first=await LocalLineServer.StartAsync(repository:repository);
        using var http1=new HttpClient{BaseAddress=first.BaseAddress}; var alice=await TestLogin.ClientAsync(http1,first,"alice");
        var a=(await alice.CreateAsync()).Data!; a=(await alice.SaveAsync(a.Code,a.Version,TestMap.Shape(a,100,110,110,110,130,110).Nodes)).Data!;
        await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var http2=new HttpClient{BaseAddress=second.BaseAddress}; var bob=await TestLogin.ClientAsync(http2,second,"bob");
        var b=(await bob.CreateAsync()).Data!; b=(await bob.SaveAsync(b.Code,b.Version,TestMap.Shape(b,110,100,110,140).Nodes)).Data!;
        await Calculate(alice); var cross=(await alice.GetCrossesAsync()).Data!.Single();

        // Simulate the actual schema-9 state restriction and verify upgrading changes no map data.
        var before=Snapshot(repository);
        using(var connection=new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();using var downgrade=connection.CreateCommand();
            downgrade.CommandText="ALTER TABLE Crosses DROP CHECK AM9_CK_Cross, ADD CONSTRAINT AM9_CK_Cross CHECK (CrossState IN (-1,0,1))";downgrade.ExecuteNonQuery();
            downgrade.CommandText="UPDATE MapMetadata SET SchemaVersion=9 WHERE Id=1";downgrade.ExecuteNonQuery();
        }
        var migrated=new MySqlLineRepository(settings); Assert.AreEqual(before,Snapshot(migrated));
        var race=await Task.WhenAll(alice.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Road1ToRoad2),bob.SetCrossStateAsync(cross.Key,cross.Version,CrossState.Road2ToRoad1));
        Assert.AreEqual(1,race.Count(r=>r.Success)); Assert.AreEqual("VersionConflict",race.Single(r=>!r.Success).Code);
        cross=race.Single(r=>r.Success).Data!; var state=cross.State;
        Assert.AreEqual(cross,(await bob.GetCrossesAsync()).Data!.Single());
        a=(await alice.SaveAsync(a.Code,a.Version,TestMap.Shape(a,100,110,105,110,110,110,130,110).Nodes)).Data!;
        await Calculate(bob); var changed=(await alice.GetCrossesAsync()).Data!.Single();
        Assert.AreEqual(cross.Key,changed.Key);Assert.AreEqual(state,changed.State);Assert.AreEqual((2,0.0),changed.LocationOn(a.Code));
        a=(await alice.ReverseLineAsync(a.Code,a.Version)).Data!;await Calculate(alice);
        changed=new CrossService(new MySqlLineRepository(settings)).GetAll().Data!.Single();
        Assert.AreEqual(cross.Key,changed.Key);Assert.AreEqual(state,changed.State);Assert.AreEqual((1,0.0),changed.LocationOn(a.Code));

        // Fault injection must roll back the one-way state and version together across both servers.
        before=Snapshot(migrated);
        using(var connection=new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();using var trigger=connection.CreateCommand();
            trigger.CommandText="CREATE TRIGGER TestOneWayFailure BEFORE UPDATE ON Crosses FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected Cross direction failure'";trigger.ExecuteNonQuery();
            try { Assert.AreEqual("DatabaseError",(await alice.SetCrossStateAsync(changed.Key,changed.Version,CrossState.Connected)).Code); }
            finally { trigger.CommandText="DROP TRIGGER TestOneWayFailure";trigger.ExecuteNonQuery(); }
        }
        Assert.AreEqual(before,Snapshot(new MySqlLineRepository(settings)));
        cross=(await bob.SetCrossStateAsync(changed.Key,changed.Version,CrossState.Road2ToRoad1)).Data!;
        Assert.AreEqual(CrossState.Road2ToRoad1,new CrossService(new MySqlLineRepository(settings)).GetAll().Data!.Single().State);

        // A completed ALTER followed by an interrupted schema-version write is safe to retry with state 3 already stored.
        before=Snapshot(migrated);
        using(var connection=new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();using var version=connection.CreateCommand();version.CommandText="UPDATE MapMetadata SET SchemaVersion=9 WHERE Id=1";version.ExecuteNonQuery();
        }
        Assert.AreEqual(before,Snapshot(new MySqlLineRepository(settings)));
        using(var connection=new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();using var query=connection.CreateCommand();
            query.CommandText="SELECT SchemaVersion FROM MapMetadata WHERE Id=1";Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(query.ExecuteScalar()));
            foreach(var invalid in new[]{"CrossState=4","CrossState=-2","X=500001"})
            {
                using var transaction=connection.BeginTransaction(); query.Transaction=transaction;
                try
                {
                    query.CommandText="UPDATE Crosses SET "+invalid;
                    try {query.ExecuteNonQuery();Assert.Fail("数据库必须拒绝无效状态和越界整数坐标。");}
                    catch(MySqlException error) when(error.Number==3819) { }
                }
                finally {transaction.Rollback();query.Transaction=null;}
            }
        }
        Assert.AreEqual(before,Snapshot(new MySqlLineRepository(settings)));

        var halfA=(await alice.CreateAsync()).Data!; halfA=(await alice.SaveAsync(halfA.Code,halfA.Version,TestMap.Shape(halfA,200,200,201,201).Nodes)).Data!;
        var halfB=(await bob.CreateAsync()).Data!; halfB=(await bob.SaveAsync(halfB.Code,halfB.Version,TestMap.Shape(halfB,200,201,201,200).Nodes)).Data!;
        await Calculate(alice);var half=CrossKey.Create(halfA.Code,halfB.Code,new CrossPosition(401,401));
        Assert.IsFalse((await bob.GetCrossesAsync()).Data!.Any(c=>c.Key==half));
        Assert.IsFalse((await bob.SetCrossStateAsync(half,0,CrossState.Road1ToRoad2)).Success);
        Assert.IsFalse(new CrossService(new MySqlLineRepository(settings)).GetAll().Data!.Any(c=>c.Key==half));
        Assert.IsTrue((await alice.DeleteAsync(a.Code,a.Version)).Success);
        Assert.IsFalse((await bob.GetCrossesAsync()).Data!.Any(c=>c.Key==cross.Key));
    }
}
