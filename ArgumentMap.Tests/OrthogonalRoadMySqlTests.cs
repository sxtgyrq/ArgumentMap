using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql")]
public sealed class OrthogonalRoadMySqlTests
{
    [TestMethod, TestCategory("Http")]
    public Task TwoServers_ConcurrentObliqueRoadsReadLatestCommittedGeometryAndOnlyOneCommits() => LogicalNodeMySqlTests.Isolated(async settings =>
    {
        var repository = new MySqlLineRepository(settings); var otherRepository = new MySqlLineRepository(settings);
        await using var first = await LocalLineServer.StartAsync(repository: repository);
        await using var second = await LocalLineServer.StartAsync(repository: otherRepository);
        using var http1 = new HttpClient { BaseAddress = first.BaseAddress }; using var http2 = new HttpClient { BaseAddress = second.BaseAddress };
        var alice = await TestLogin.ClientAsync(http1, first, "alice"); var bob = await TestLogin.ClientAsync(http2, second, "bob");
        for (var i = 0; i < 8; i++)
        {
            var x = 1000 + i * 100; const int y = 1000;
            var a = (await alice.CreateAsync()).Data!; var b = (await bob.CreateAsync()).Data!;
            var axis = TestMap.Shape(a, x - 20, y, x + 20, y); var diagonal = TestMap.Shape(b, x - 10, y - 10, x + 10, y + 10);
            var race = i % 2 == 0
                ? new[] { alice.SaveAsync(a.Code, a.Version, axis.Nodes), bob.SaveAsync(b.Code, b.Version, diagonal.Nodes) }
                : new[] { bob.SaveAsync(b.Code, b.Version, diagonal.Nodes), alice.SaveAsync(a.Code, a.Version, axis.Nodes) };
            var results = await Task.WhenAll(race);
            Assert.AreEqual(1, results.Count(r => r.Success)); Assert.AreEqual("NonOrthogonalIntersection", results.Single(r => !r.Success).Code);
            var saved = results.Single(r => r.Success).Data!; var loser = saved.Code == a.Code ? b : a; var client = saved.Code == a.Code ? bob : alice;
            var stored = (await client.GetLineDetailAsync(loser.Code)).Data!; Assert.AreEqual(0L, stored.Version); Assert.AreEqual(0, stored.Nodes.Length);
            Assert.IsTrue(otherRepository.Transaction(s => s.Lines[saved.Code].Nodes.SequenceEqual(saved.Nodes) &&
                !s.DirtyLines.Contains(loser.Code) && !s.PendingSegments.Any(p => p.LineCode == loser.Code) &&
                !s.StaleCrosses.Any(c => c.Contains(loser.Code)) && !s.Crosses.Keys.Any(c => c.Contains(loser.Code))));
            var before = LogicalNodeMySqlTests.Snapshot(repository);
            var again = await client.SaveAsync(loser.Code, loser.Version, saved.Code == a.Code ? diagonal.Nodes : axis.Nodes);
            Assert.AreEqual("NonOrthogonalIntersection", again.Code); Assert.AreEqual(before, LogicalNodeMySqlTests.Snapshot(otherRepository));
            var corrected = saved.Code == a.Code
                ? TestMap.Shape(loser, x + 7, y - 20, x + 7, y + 20)
                : TestMap.Shape(loser, x - 10, y + 10, x + 10, y - 10);
            Assert.IsTrue((await client.SaveAsync(loser.Code, loser.Version, corrected.Nodes)).Success);
            while ((await alice.CalculateCrossAsync()).Data!.CanContinue) { }
            var cross = (await bob.GetCrossesAsync()).Data!.Single(c => c.Key.Contains(a.Code) && c.Key.Contains(b.Code));
            Assert.AreEqual(saved.Code == a.Code ? new CrossPosition(2 * (x + 7), 2 * y) : new CrossPosition(2 * x, 2 * y), cross.Key.Position);
            Assert.IsTrue(repository.Transaction(s => s.PendingSegments.Count == 0 && s.StaleCrosses.Count == 0 && s.DirtyLines.Count == 0));
        }
    });

    [TestMethod, TestCategory("Http")]
    public Task InvalidEditAndDatabaseFault_RollBackRoadCrossAndLnRowsAndAllowCorrectedRetry() => LogicalNodeMySqlTests.Isolated(async settings =>
    {
        var repository = new MySqlLineRepository(settings); await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress }; var client = await TestLogin.ClientAsync(http, server, "owner");
        var road = await LogicalNodeMySqlTests.Add(client, 90, 110, 130, 110); await LogicalNodeMySqlTests.Add(client, 120, 90, 120, 130);
        while ((await client.CalculateCrossAsync()).Data!.CanContinue) { }
        var cross = (await client.GetCrossesAsync()).Data!.Single(); Assert.IsTrue((await client.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Connected)).Success);
        Assert.IsTrue((await client.CreateLogicalNodeAsync(100.5m, 110.5m, 6, new())).Success);
        var before = LogicalNodeMySqlTests.Snapshot(repository);
        Assert.AreEqual("NonOrthogonalIntersection", (await client.SaveAsync(road.Code, road.Version, TestMap.Shape(road, 90, 110, 110, 110, 130, 130).Nodes)).Code);
        Assert.AreEqual(before, LogicalNodeMySqlTests.Snapshot(new MySqlLineRepository(settings)));
        var valid = TestMap.Shape(road, 90, 110, 110, 110, 130, 110);
        // Fail after geometry is accepted and persistence has started, verifying the outer transaction
        // rolls back road replacement together with the later LN position/version writes.
        LogicalNodeMySqlTests.Sql(settings, "CREATE TRIGGER TestOrthogonalRollback BEFORE UPDATE ON LogicalNodes FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected orthogonal road persistence failure'");
        try
        {
            Assert.AreEqual("DatabaseError", (await client.SaveAsync(road.Code, road.Version, valid.Nodes)).Code);
            Assert.AreEqual(before, LogicalNodeMySqlTests.Snapshot(new MySqlLineRepository(settings)));
        }
        finally { LogicalNodeMySqlTests.Sql(settings, "DROP TRIGGER TestOrthogonalRollback"); }
        var corrected = await client.SaveAsync(road.Code, road.Version, valid.Nodes); Assert.IsTrue(corrected.Success, corrected.Message);
        while ((await client.CalculateCrossAsync()).Data!.CanContinue) { }
        Assert.AreEqual(CrossState.Connected, (await client.GetCrossesAsync()).Data!.Single().State);
        var position = (await client.GetLogicalNodesAsync()).Data!.Single().Positions.Single(); Assert.AreEqual(corrected.Data!.Version, position.RoadVersion);
        Assert.AreEqual(0, position.RoadOrder); Assert.AreEqual(0.525, position.RoadPercent, 1e-12);
    });
}
