using System.Net.Http;
using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;

namespace ArgumentMap.Tests;

internal static class DirectionMySqlChecks
{
    private static string Snapshot(ILineRepository repository) => repository.Transaction(s => JsonSerializer.Serialize(new
    {
        LineCount = s.Lines.Count, CrossCount = s.Crosses.Count, Lines = s.Lines.Values.OrderBy(l => l.Code).ToArray(), Crosses = s.Crosses.Values.OrderBy(c => c.Key).ToArray(),
        Pending = s.PendingSegments.OrderBy(p => p.LineCode).ThenBy(p => p.Segment).Select(p => new { p.LineCode, p.Segment }).ToArray(),
        Stale = s.StaleCrosses.Order().ToArray(), Dirty = s.DirtyLines.Order().ToArray()
    }));
    private static async Task Calculate(HttpLineClient client)
    {
        for (var i = 0; i < 100; i++)
        {
            var result = await client.CalculateCrossAsync(); Assert.IsTrue(result.Success, result.Message);
            if (!result.Data!.CanContinue) return;
        }
        Assert.Fail("Calculation did not finish.");
    }
    internal static async Task RunCrossLocationsAsync(MySqlSettings settings)
    {
        if (!settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal)) throw new InvalidOperationException("Owned test database required.");
        var repository = new MySqlLineRepository(settings);
        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var client = await TestLogin.ClientAsync(http, server, "反转位置测试");
        async Task<LineSnapshot> Save(params int[] xy)
        {
            var line = (await client.CreateAsync()).Data!;
            var result = await client.SaveAsync(line.Code, line.Version, TestMap.Shape(line, xy).Nodes);
            Assert.IsTrue(result.Success, result.Message); return result.Data!;
        }
        var a = await Save(460000, 460010, 460010, 460010, 460020, 460010, 460040, 460010);
        var b = await Save(460010, 460000, 460010, 460020); var c = await Save(460025, 460000, 460025, 460020);
        await Calculate(client); var initial = (await client.GetCrossesAsync()).Data!;
        var node = initial.Single(cross => cross.Key.Contains(b.Code)); var interior = initial.Single(cross => cross.Key.Contains(c.Code));
        node = (await client.SetCrossStateAsync(node.Key, node.Version, CrossState.Connected)).Data!;
        interior = (await client.SetCrossStateAsync(interior.Key, interior.Version, CrossState.Disconnected)).Data!;
        var nextCross = repository.Transaction(s => s.Crosses.Count);
        var result = await client.ReverseLineAsync(a.Code, a.Version); Assert.IsTrue(result.Success, result.Message); var reversed = result.Data!;
        var reopened = new MySqlLineRepository(settings);
        CollectionAssert.AreEqual(reversed.Nodes, new LineSaveService(reopened).GetDetail(new(a.Code, a.Owner)).Data!.Nodes);
        Assert.IsTrue(reopened.Transaction(s => s.PendingSegments.Count == 3 && s.StaleCrosses.Count == 2 && s.DirtyLines.Contains(a.Code)),
            "Interrupted reversal preserves all pending Cross work across MySQL repository restart.");
        Assert.AreEqual(node, new CrossService(reopened).GetAll().Data!.Single(cross => cross.Key == node.Key));
        await Calculate(client);
        var reloaded = new CrossService(new MySqlLineRepository(settings)).GetAll().Data!;
        var shiftedNode = reloaded.Single(cross => cross.Key == node.Key); var shiftedInterior = reloaded.Single(cross => cross.Key == interior.Key);
        Assert.AreEqual(2, shiftedNode.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.0, shiftedNode.LocationOn(a.Code).Percent); Assert.AreEqual(CrossState.Connected, shiftedNode.State);
        Assert.AreEqual(0, shiftedInterior.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.75, shiftedInterior.LocationOn(a.Code).Percent); Assert.AreEqual(CrossState.Disconnected, shiftedInterior.State);
        Assert.AreEqual(nextCross, reopened.Transaction(s => s.Crosses.Count));
        var second = await client.ReverseLineAsync(reversed.Code, reversed.Version); Assert.IsTrue(second.Success); await Calculate(client);
        CollectionAssert.AreEqual(a.Nodes, (await client.GetLineDetailAsync(a.Code)).Data!.Nodes);
        reloaded = new CrossService(new MySqlLineRepository(settings)).GetAll().Data!;
        Assert.AreEqual(1, reloaded.Single(cross => cross.Key == node.Key).LocationOn(a.Code).RoadOrder);
        Assert.AreEqual(0.0, reloaded.Single(cross => cross.Key == node.Key).LocationOn(a.Code).Percent);
        Assert.AreEqual(2, reloaded.Single(cross => cross.Key == interior.Key).LocationOn(a.Code).RoadOrder);
        Assert.AreEqual(0.25, reloaded.Single(cross => cross.Key == interior.Key).LocationOn(a.Code).Percent);
        Assert.AreEqual(CrossState.Connected, reloaded.Single(cross => cross.Key == node.Key).State);
        Assert.AreEqual(CrossState.Disconnected, reloaded.Single(cross => cross.Key == interior.Key).State);
    }

    internal static async Task RunAsync(MySqlSettings settings)
    {
        if (!settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal)) throw new InvalidOperationException("Owned test database required.");
        var repository1 = new MySqlLineRepository(settings); var repository2 = new MySqlLineRepository(settings);
        await using var server1 = await LocalLineServer.StartAsync(repository: repository1);
        await using var server2 = await LocalLineServer.StartAsync(repository: repository2);
        using var http1 = new HttpClient { BaseAddress = server1.BaseAddress }; using var http2 = new HttpClient { BaseAddress = server2.BaseAddress };
        var client1 = await TestLogin.ClientAsync(http1, server1, "方向测试"); var client2 = new HttpLineClient(http2, client1.Session);
        var a = (await client1.CreateAsync()).Data!;
        a = (await client1.SaveAsync(a.Code, a.Version, TestMap.Shape(a, 450000, 450010, 450010, 450010, 450030, 450010).Nodes)).Data!;
        var b = (await client1.CreateAsync()).Data!;
        b = (await client1.SaveAsync(b.Code, b.Version, TestMap.Shape(b, 450010, 450000, 450010, 450020).Nodes)).Data!;
        await Calculate(client1);
        a = (await client1.SaveLinePropertiesAsync(a.Code, a.Version, new LineProperties { RoadName = "双向测试", Speed = 500, PassInOpposeDirection = 1 })).Data!;
        var cross = (await client1.GetCrossesAsync()).Data!.Single();
        cross = (await client1.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Connected)).Data!;
        Assert.AreEqual((short)1, (await client2.GetLineDetailAsync(a.Code)).Data!.Properties.PassInOpposeDirection);
        var beforeFailure = Snapshot(repository2);
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open(); using var trigger = connection.CreateCommand();
            trigger.CommandText = "CREATE TRIGGER FailReverseNodeInsert BEFORE INSERT ON LineNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected reverse transaction failure'";
            trigger.ExecuteNonQuery();
            try { Assert.AreEqual("DatabaseError", (await client1.ReverseLineAsync(a.Code, a.Version)).Code); }
            finally { trigger.CommandText = "DROP TRIGGER FailReverseNodeInsert"; trigger.ExecuteNonQuery(); }
        }
        Assert.AreEqual(beforeFailure, Snapshot(new MySqlLineRepository(settings)), "Partial reversal SQL failure must roll back original nodes, version, Cross and markers.");
        var race = await Task.WhenAll(client1.ReverseLineAsync(a.Code, a.Version), client2.ReverseLineAsync(a.Code, a.Version));
        Assert.AreEqual(1, race.Count(r => r.Success)); Assert.AreEqual("VersionConflict", race.Single(r => !r.Success).Code);
        a = race.Single(r => r.Success).Data!;
        CollectionAssert.AreEqual(new[] { 450030, 450010, 450000 }, a.Nodes.Select(n => n.X).ToArray());
        Assert.AreEqual(500, a.Properties.Speed); Assert.AreEqual((short)1, a.Properties.PassInDirection); Assert.AreEqual((short)1, a.Properties.PassInOpposeDirection);
        await Task.WhenAll(Calculate(client1), Calculate(client2));
        var recalculated = (await client2.GetCrossesAsync()).Data!.Single();
        Assert.AreEqual(cross.Key, recalculated.Key); Assert.AreEqual(CrossState.Connected, recalculated.State);
        Assert.AreEqual(1, recalculated.LocationOn(a.Code).RoadOrder); Assert.AreEqual(0.0, recalculated.LocationOn(a.Code).Percent);
        var reverseTask = client1.ReverseLineAsync(a.Code, a.Version);
        var editTask = client2.SaveLinePropertiesAsync(a.Code, a.Version, a.Properties with { Speed = 125 });
        await Task.WhenAll(reverseTask, editTask);
        Assert.AreEqual(1, new[] { reverseTask.Result, editTask.Result }.Count(r => r.Success));
        Assert.AreEqual("VersionConflict", new[] { reverseTask.Result, editTask.Result }.Single(r => !r.Success).Code);
        await Calculate(client1);
        Assert.AreEqual(CrossState.Connected, (await client2.GetCrossesAsync()).Data!.Single().State);
        Assert.IsTrue(repository2.Transaction(s => s.PendingSegments.Count == 0 && s.StaleCrosses.Count == 0 && s.DirtyLines.Count == 0));

        // Independent generic fields migrate from schema 7 without bringing any transport-mode fields back.
        var current = (await client1.GetLineDetailAsync(a.Code)).Data!;
        var beforeCross = (await client1.GetCrossesAsync()).Data!.Single();
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open(); using var downgrade = connection.CreateCommand();
            downgrade.CommandText = "ALTER TABLE `Lines` DROP CHECK AM9_CK_Lines_PassDirection,DROP COLUMN PassInDirection,DROP COLUMN PassInOpposeDirection"; downgrade.ExecuteNonQuery();
            downgrade.CommandText = "UPDATE MapMetadata SET SchemaVersion=7 WHERE Id=1"; downgrade.ExecuteNonQuery();
        }
        var migrated = new MySqlLineRepository(settings); var resultLine = new LineSaveService(migrated).GetDetail(new(a.Code, a.Owner)).Data!;
        Assert.AreEqual((short)1, resultLine.Properties.PassInDirection); Assert.AreEqual((short)0, resultLine.Properties.PassInOpposeDirection);
        Assert.AreEqual(current.Properties.Speed, resultLine.Properties.Speed); Assert.AreEqual(current.Properties.RoadName, resultLine.Properties.RoadName);
        CollectionAssert.AreEqual(current.Nodes, resultLine.Nodes); Assert.AreEqual(beforeCross, new CrossService(migrated).GetAll().Data!.Single());
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open(); using var query = connection.CreateCommand();
            query.CommandText = "SELECT SchemaVersion FROM MapMetadata WHERE Id=1"; Assert.AreEqual(MySqlLineRepository.CurrentSchemaVersion,Convert.ToInt32(query.ExecuteScalar()));
            using var transaction = connection.BeginTransaction(); query.Transaction = transaction; query.Parameters.AddWithValue("@id", a.Code);
            try
            {
                foreach (var invalid in new[] { "PassInDirection=0", "PassInOpposeDirection=2" })
                {
                    query.CommandText = "UPDATE `Lines` SET " + invalid + " WHERE LineCode=@id";
                    try { query.ExecuteNonQuery(); Assert.Fail("Database direction CHECK should reject invalid flags."); }
                    catch (MySqlException error) when (error.Number == 3819) { }
                }
            }
            finally { transaction.Rollback(); }
            query.Transaction = null;
            query.CommandText = "UPDATE MapMetadata SET SchemaVersion=7 WHERE Id=1"; query.ExecuteNonQuery();
        }
        var retried = new MySqlLineRepository(settings);
        Assert.AreEqual(resultLine.Properties, new LineSaveService(retried).GetDetail(new(a.Code, a.Owner)).Data!.Properties);
        Assert.AreEqual(beforeCross, new CrossService(retried).GetAll().Data!.Single());
    }
}
