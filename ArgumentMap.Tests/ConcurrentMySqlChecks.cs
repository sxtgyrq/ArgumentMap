using System.Net.Http;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;

internal static class ConcurrentMySqlChecks
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
    private static LineNode[] Nodes(LineSnapshot line, params (int X, int Y)[] points) =>
        points.Select((point, i) => new LineNode(line.Code, i, point.X, point.Y)).ToArray();

    internal static async Task<int> RunAsync(MySqlSettings settings)
    {
        var repository1 = new MySqlLineRepository(settings);
        var repository2 = new MySqlLineRepository(settings);
        await using var server1 = await LocalLineServer.StartAsync(repository: repository1);
        await using var server2 = await LocalLineServer.StartAsync(repository: repository2);
        using var http1 = new HttpClient { BaseAddress = server1.BaseAddress };
        using var http2 = new HttpClient { BaseAddress = server2.BaseAddress };
        var alice = await TestLogin.ClientAsync(http1, server1, "用户甲");
        var bob = await TestLogin.ClientAsync(http2, server2, "用户乙");
        var aliceOtherEditor = new HttpLineClient(http2, alice.Session);
        var outsider = await TestLogin.ClientAsync(http2, server2, "无操作权用户");
        var created = await Task.WhenAll(alice.CreateAsync(), bob.CreateAsync());
        Check(created.All(result => result.Success) && created[0].Data!.Code != created[1].Data!.Code,
            "Separate servers allocate distinct IDs under concurrent creation");
        var a = created[0].Data!;
        var b = created[1].Data!;
        var nextCross = repository1.Transaction(state => state.Crosses.Count);
        var saves = await Task.WhenAll(
            alice.SaveAsync(a.Code, a.Version, Nodes(a, (7000, 7000), (7020, 7000))),
            bob.SaveAsync(b.Code, b.Version, Nodes(b, (7010, 6990), (7010, 7010))));
        Check(saves.All(result => result.Success), "Different users can save intersecting lines concurrently");
        a = saves[0].Data!;
        b = saves[1].Data!;
        async Task<int> Calculate(HttpLineClient client)
        {
            var processed = 0;
            while (true)
            {
                var step = await client.CalculateCrossAsync();
                if (!step.Success || step.Data is null) throw new InvalidOperationException(step.Message);
                if (!step.Data.CanContinue) return processed;
                processed++;
            }
        }
        var workers = await Task.WhenAll(Calculate(alice), Calculate(bob));
        Check(workers.Sum() == 2, "Concurrent K workers process each pending segment exactly once");
        var cross = (await alice.GetCrossesAsync()).Data!.Single(item =>
            item.Key.Contains(a.Code) && item.Key.Contains(b.Code) || item.Key.Contains(b.Code) && item.Key.Contains(a.Code));
        Check(repository1.Transaction(state => state.Crosses.Count) == nextCross + 1, "Concurrent calculation creates exactly one shared Cross ID");
        var edits = await Task.WhenAll(
            alice.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Connected),
            bob.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Disconnected));
        Check(edits.Count(result => result.Success) == 1 && edits.Single(result => !result.Success).Code == "VersionConflict",
            "Either road owner can edit Cross; concurrent stale edit cannot overwrite the winner");
        cross = edits.Single(result => result.Success).Data!;
        Check(!(await outsider.SetCrossStateAsync(cross.Key, cross.Version, CrossState.Connected)).Success,
            "Unrelated user cannot edit shared Cross");
        Check(!(await bob.SaveAsync(a.Code, a.Version, Nodes(a, (7000, 7000), (7030, 7000)))).Success,
            "Cross permissions do not grant editing another user's line");

        var sameLine = await Task.WhenAll(
            alice.SaveAsync(a.Code, a.Version, Nodes(a, (7000, 7000), (7020, 7000), (7030, 7000))),
            aliceOtherEditor.SaveAsync(a.Code, a.Version, Nodes(a, (7000, 7000), (7020, 7000), (7040, 7000))));
        Check(sameLine.Count(result => result.Success) == 1 && sameLine.Single(result => !result.Success).Code == "VersionConflict",
            "Two editors of the same line cannot overwrite each other's changed geometry");
        a = sameLine.Single(result => result.Success).Data!;
        var requestedState = cross.State == CrossState.Connected ? CrossState.Disconnected : CrossState.Connected;
        var crossEditTask = bob.SetCrossStateAsync(cross.Key, cross.Version, requestedState);
        var stepTask = alice.CalculateCrossAsync();
        await Task.WhenAll(crossEditTask, stepTask);
        Check(stepTask.Result.Success && (crossEditTask.Result.Success || crossEditTask.Result.Code == "VersionConflict"),
            "Concurrent Cross setting and geometry calculation commit safely or report a stale version");
        await Task.WhenAll(Calculate(alice), Calculate(bob));
        var recalculated = (await alice.GetCrossesAsync()).Data!.Single(item => item.Key == cross.Key);
        var expectedState = crossEditTask.Result.Success ? requestedState : cross.State;
        Check(recalculated.State == expectedState, "Recalculation cannot silently replace a user's Cross setting");

        a = (await alice.SaveAsync(a.Code, a.Version, Nodes(a, (7000, 7001), (7020, 7001)))).Data!;
        var step = await alice.CalculateCrossAsync();
        Check(step.Success && step.Data!.RemainingSegments == 0, "Changed line processed before cleanup race");
        // A new save may arrive just as another user requests the final cleanup.
        var restorationTask = alice.SaveAsync(a.Code, a.Version, Nodes(a, (7000, 7000), (7020, 7000)));
        var cleanupTask = bob.CalculateCrossAsync();
        await Task.WhenAll(restorationTask, cleanupTask);
        Check(restorationTask.Result.Success && cleanupTask.Result.Success, "Concurrent restoration and cleanup both preserve transactional consistency");
        a = restorationTask.Result.Data!;
        await Task.WhenAll(Calculate(alice), Calculate(bob));
        var restored = (await alice.GetCrossesAsync()).Data!.Single(item =>
            item.Key.Contains(a.Code) && item.Key.Contains(b.Code) || item.Key.Contains(b.Code) && item.Key.Contains(a.Code));
        Check(restored.Key.Position == new CrossPosition(14020, 14000), "Cleanup race retains the Cross belonging to final saved geometry");
        Check(restored.Key == cross.Key && restored.State == (cleanupTask.Result.Data!.CanContinue ? expectedState : CrossState.Unset),
            "Existing coordinate identity retains settings; an intersection already deleted by cleanup is recreated unset");
        Check(repository2.Transaction(state => state.PendingSegments.Count == 0 && state.StaleCrosses.Count == 0),
            "Concurrent K and saving leave no stranded pending segments or stale Cross markers");
        Check((await bob.DeleteAsync(b.Code, b.Version)).Success, "Road owner can delete own line after concurrent work");
        Check(!(await alice.GetCrossesAsync()).Data!.Any(item => item.Key.RoadCode1 == b.Code || item.Key.RoadCode2 == b.Code),
            "Deletion removes shared Cross records for all clients");

        // New third-line restriction: independent servers and database connections must share the same lock.
        var baseLine = (await alice.CreateAsync()).Data!;
        baseLine = (await alice.SaveAsync(baseLine.Code, baseLine.Version, Nodes(baseLine, (80000, 80010), (80020, 80010)))).Data!;
        var vertical = (await alice.CreateAsync()).Data!;
        var diagonal = (await bob.CreateAsync()).Data!;
        var nextCrossBeforeRace = repository1.Transaction(state => state.Crosses.Count);
        var crossingSaves = await Task.WhenAll(
            alice.SaveAsync(vertical.Code, vertical.Version, Nodes(vertical, (80010, 80000), (80010, 80020))),
            bob.SaveAsync(diagonal.Code, diagonal.Version, Nodes(diagonal, (80001, 80001), (80019, 80019))));
        Check(crossingSaves[0].Success && !crossingSaves[1].Success && crossingSaves[1].Code is "LinePassesCross" or "NonOrthogonalIntersection",
            "Separate MySQL servers serialize crossing saves: perpendicular road commits and oblique road is rejected by the applicable angle or third-party rule");
        var winnerIndex = crossingSaves[0].Success ? 0 : 1;
        var winner = crossingSaves[winnerIndex].Data!;
        var loser = winnerIndex == 0 ? diagonal : vertical;
        var loserClient = winnerIndex == 0 ? bob : alice;
        var loserNodes = winnerIndex == 0 ? Nodes(diagonal, (80001, 80001), (80019, 80019)) : Nodes(vertical, (80010, 80000), (80010, 80020));
        var loserStored = (await loserClient.GetLineDetailAsync(loser.Code)).Data!;
        Check(loserStored.Version == 0 && loserStored.Nodes.Length == 0,
            "MySQL rollback leaves rejected concurrent line empty with its original version");
        Check(repository2.Transaction(state => state.Crosses.Count == nextCrossBeforeRace &&
            state.Lines[winner.Code].Nodes.SequenceEqual(winner.Nodes) && state.Lines[winner.Code].Version == winner.Version &&
            state.PendingSegments.Contains((winner.Code, 0)) && !state.PendingSegments.Any(p => p.LineCode == loser.Code) &&
            !state.DirtyLines.Contains(loser.Code)),
            "Winner commits and loser rolls back geometry, recalculation markers and Cross ID counter across repository instances");
        await Task.WhenAll(Calculate(alice), Calculate(bob));
        var pairCross = (await alice.GetCrossesAsync()).Data!.Single(item => item.Key.Position == new CrossPosition(160020, 160020));
        Check(new[] { pairCross.Key.RoadCode1, pairCross.Key.RoadCode2 }.Order().SequenceEqual(new[] { baseLine.Code, winner.Code }.Order()),
            "Concurrent K commits exactly the valid winning road pair");
        pairCross = (await alice.SetCrossStateAsync(pairCross.Key, pairCross.Version, CrossState.Connected)).Data!;
        var nextCrossAfterCalculation = repository2.Transaction(state => state.Crosses.Count);
        var invalidSaveTask = loserClient.SaveAsync(loser.Code, loser.Version, loserNodes);
        var concurrentCalculation = bob.CalculateCrossAsync();
        var concurrentCrossEdit = alice.SetCrossStateAsync(pairCross.Key, pairCross.Version, CrossState.Disconnected);
        await Task.WhenAll(invalidSaveTask, concurrentCalculation, concurrentCrossEdit);
        Check(!invalidSaveTask.Result.Success && invalidSaveTask.Result.Code == "LinePassesCross" &&
            concurrentCalculation.Result.Success && concurrentCrossEdit.Result.Success,
            "A rejected third-line save, another user's K and a Cross setting edit coordinate transactions safely");
        var finalCross = (await bob.GetCrossesAsync()).Data!.Single(item => item.Key == pairCross.Key);
        Check(finalCross.State == CrossState.Disconnected && finalCross.Version == concurrentCrossEdit.Result.Data!.Version,
            "Rollback of invalid geometry does not undo another committed user's Cross setting");
        Check(repository1.Transaction(state => state.Crosses.Count == nextCrossAfterCalculation &&
            state.Lines[loser.Code].Version == 0 && state.Lines[loser.Code].Nodes.Length == 0 &&
            state.PendingSegments.Count == 0 && state.StaleCrosses.Count == 0 && state.DirtyLines.Count == 0 &&
            CrossPassageValidator.FindInCurrentGeometry(state) is null),
            "After concurrent operations, independent MySQL reads find no third passage or stranded transaction markers");
        Check((await loserClient.DeleteAsync(loser.Code, loser.Version)).Success, "Rejected concurrent registration can be deleted by its owner");
        var winnerClient = winnerIndex == 0 ? alice : bob;
        Check((await winnerClient.DeleteAsync(winner.Code, winner.Version)).Success &&
            (await alice.DeleteAsync(baseLine.Code, baseLine.Version)).Success, "Concurrent Cross restriction fixture cleans up only its own lines");
        return checks;
    }
}

