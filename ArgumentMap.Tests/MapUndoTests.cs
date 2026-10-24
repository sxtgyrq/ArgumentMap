using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class MapUndoTests
{
    internal static ServiceResult<T> Edit<T>(ILineRepository repository, Func<ILineRepository, ServiceResult<T>> edit, string owner = "owner") =>
        repository.Transaction(state =>
        {
            var scope = new Scope(state);
            return new MapUndoService(scope).Record(owner, "测试编辑", () => edit(scope));
        });
    private sealed class Scope(LineStoreState state) : ILineRepository
    { public T Transaction<T>(Func<LineStoreState, T> operation) => operation(state); }
    internal static string Content(ILineRepository repository) => repository.Transaction(s => JsonSerializer.Serialize(new
    {
        Lines = s.Lines.Values.OrderBy(l => l.Code).Select(l => new { l.Code, l.Owner, l.IsLocked, l.Nodes, l.Properties }),
        Nodes = s.LogicalNodes.Values.OrderBy(n => n.Code).Select(n => new { n.Code, n.Owner, n.X, n.Y, n.SideLength, n.Properties }),
        Crosses = s.Crosses.Values.OrderBy(c => c.Key).Select(c => new { c.Key, c.State })
    }));
    internal static LogicalNodeSnapshot Node(ILineRepository repo, int size = 6, string value = "命题", string logic = "P", decimal x = 50.5m, string owner = "owner")
    { var r = new LogicalNodeService(repo).Create(new(owner, x, 50.5m, size, new() { Value = value, LogicLanguage = logic })); Assert.IsTrue(r.Success, r.Message); return r.Data!; }
    private static void Ok(ServiceResult<MapUndoResult> result) => Assert.IsTrue(result.Success, result.Message);

    [TestMethod, DataRow(4), DataRow(6), DataRow(8), DataRow(10), DataRow(12), DataRow(18)]
    public void CreateUndoPreservesEachNodeRule_AndNeverRenumbersRestoredObjects(int size)
    {
        var repo = new InMemoryLineRepository(); Node(repo, 12, "PEOPLE", "PEOPLE", 300.5m);
        var before = Content(repo);
        var created = Edit(repo, r => new LogicalNodeService(r).Create(new("owner", 50.5m, 50.5m, size,
            new() { Value = size switch { 4 => "False", 8 => "↔", 10 => "people(id=1)", 12 => "PEOPLE", _ => "命题" }, LogicLanguage = size == 6 ? "P" : "" })));
        Assert.IsTrue(created.Success, created.Message); Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(before, Content(repo));
        Assert.AreEqual("NothingToUndo", new MapUndoService(repo).Undo("owner").Code);
    }

    [TestMethod, DataRow("move"), DataRow("delete"), DataRow("edit")]
    public void SingleNodeChangesUndoInDatabaseAndBumpVersion(string operation)
    {
        var repo = new InMemoryLineRepository(); var node = Node(repo); var before = Content(repo);
        if (operation == "move") Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Move(new(node.Code, "owner", node.Version, 90.5m, 90.5m))).Success);
        else if (operation == "delete") Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Delete(new(node.Code, "owner", node.Version))).Success);
        else Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Update(new(node.Code, "owner", node.Version, "新命题", "说明", LogicLanguage: "Q"))).Success);
        Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(before, Content(repo));
        Assert.IsTrue(repo.Transaction(s => s.LogicalNodes[node.Code].Version) > node.Version);
        Assert.AreEqual("VersionConflict", new LogicalNodeService(repo).Move(new(node.Code, "owner", node.Version, 150.5m, 150.5m)).Code);
    }

    [TestMethod]
    public void MultipleUndoUsesRestoredEditStamps_NotOldVersions_AndDoesNotConsumeFailedOrNoOpEdits()
    {
        var repo = new InMemoryLineRepository(); var n = Node(repo); var original = Content(repo);
        var a = Edit(repo, r => new LogicalNodeService(r).Move(new(n.Code, "owner", n.Version, 80.5m, 80.5m))).Data!;
        var first = Content(repo);
        Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Move(new(n.Code, "owner", a.Version, 110.5m, 110.5m))).Success);
        var now = repo.Transaction(s => s.LogicalNodes[n.Code]);
        Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Move(new(n.Code, "owner", now.Version, now.X, now.Y))).Success);
        Assert.IsFalse(Edit(repo, r => new LogicalNodeService(r).Update(new(n.Code, "owner", now.Version, "bad", "", LogicLanguage: ""))).Success);
        Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(first, Content(repo));
        Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(original, Content(repo));
        Assert.AreEqual("NothingToUndo", new MapUndoService(repo).Undo("owner").Code);
    }

    [TestMethod, DataRow("rename"), DataRow("delete")]
    public void Ln6UndoRestoresBothTrueAndFalseReferences_EvenWhenOtherUserOwnsBranches(string operation)
    {
        var repo = new InMemoryLineRepository(); var n = Node(repo);
        Node(repo, 4, "True", "P", 80.5m, "bob"); Node(repo, 4, "False", "¬P", 100.5m, "bob"); var before = Content(repo);
        if (operation == "rename") Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Update(new(n.Code, "owner", n.Version, n.Properties.Value, "", LogicLanguage: "Q"))).Success);
        else Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Delete(new(n.Code, "owner", n.Version))).Success);
        Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(before, Content(repo));
    }

    [TestMethod]
    public void Ln12RenameUndoSynchronizesInstances_ExternalNewInstanceBlocksUndo()
    {
        var repo = new InMemoryLineRepository(); var parent = Node(repo, 12, "PEOPLE", "HUMAN"); Node(repo, 10, "people(id=1)", "", 80.5m, "bob");
        var before = Content(repo);
        Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Update(new(parent.Code, "owner", parent.Version, "HUMAN", "", LogicLanguage: "HUMAN"))).Success);
        var extra = Node(repo, 10, "human(id=1)", "", 110.5m, "bob"); var changed = Content(repo);
        Assert.IsFalse(new MapUndoService(repo).Undo("owner").Success); Assert.AreEqual(changed, Content(repo));
        Assert.IsTrue(new LogicalNodeService(repo).Delete(new(extra.Code, "bob", extra.Version)).Success);
        Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(before, Content(repo));
    }

    [TestMethod, DataRow("edit"), DataRow("delete"), DataRow("aba")]
    public void AnotherUsersEditOfSameObjectBlocksUndo_IncludingEqualContentRestoredByManualEdit(string mode)
    {
        var repo = new InMemoryLineRepository(); new LoginService(repo).CreateUser(new("admin", "pass")); var n = Node(repo);
        n = Edit(repo, r => new LogicalNodeService(r).Move(new(n.Code, "owner", n.Version, 80.5m, 80.5m))).Data!;
        if (mode == "delete") Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Delete(new(n.Code, "admin", n.Version)), "admin").Success);
        else
        {
            var edited = Edit(repo, r => new LogicalNodeService(r).Update(new(n.Code, "admin", n.Version, n.Properties.Value, "changed", LogicLanguage: "P")), "admin").Data!;
            if (mode == "aba")
            {
                repo.Transaction(s => { s.LogicalNodes[n.Code] = s.LogicalNodes[n.Code] with { Owner = "owner" }; return true; });
                Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Update(new(n.Code, "owner", edited.Version, n.Properties.Value, "", LogicLanguage: "P")), "admin").Success);
            }
        }
        var before = LogicalNodeMySqlTests.Snapshot(repo); Assert.AreEqual("UndoConflict", new MapUndoService(repo).Undo("owner").Code);
        Assert.AreEqual(before, LogicalNodeMySqlTests.Snapshot(repo));
    }

    [TestMethod]
    public void OtherUsersUnrelatedEditSurvives_AndTheirHistoryIsSeparate()
    {
        var repo = new InMemoryLineRepository(); var a = Node(repo); var b = Node(repo, logic: "Q", x: 200.5m, owner: "bob");
        a = Edit(repo, r => new LogicalNodeService(r).Move(new(a.Code, "owner", a.Version, 80.5m, 80.5m))).Data!;
        b = Edit(repo, r => new LogicalNodeService(r).Move(new(b.Code, "bob", b.Version, 300.5m, 300.5m)), "bob").Data!;
        Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(50.5m, repo.Transaction(s => s.LogicalNodes[a.Code].X));
        Assert.AreEqual(300.5m, repo.Transaction(s => s.LogicalNodes[b.Code].X)); Ok(new MapUndoService(repo).Undo("bob"));
        Assert.AreEqual(200.5m, repo.Transaction(s => s.LogicalNodes[b.Code].X));
    }

    [TestMethod, DataRow("duplicate12"), DataRow("duplicate6"), DataRow("occupied"), DataRow("parentHasInstance")]
    public void RestoringDeletedOrCreatedObjectChecksLatestRules_AndFailureKeepsHistory(string mode)
    {
        var repo = new InMemoryLineRepository(); var size = mode is "duplicate12" or "parentHasInstance" ? 12 : 6;
        var n = Node(repo, size, size == 12 ? "PEOPLE" : "命题", "P");
        if (mode == "parentHasInstance")
        {
            n = Edit(repo, r => new LogicalNodeService(r).Create(new("owner", 200.5m, 200.5m, 12, new() { Value = "HUMAN" }))).Data!;
            Node(repo, 10, "human(id=1)", "", 300.5m, "bob");
        }
        else
        {
            Assert.IsTrue(Edit(repo, r => new LogicalNodeService(r).Delete(new(n.Code, "owner", n.Version))).Success);
            if (mode == "occupied") Node(repo, 8, "=", "=", n.X, "bob");
            else Node(repo, size, size == 12 ? "PEOPLE" : "命题", "P", 200.5m, "bob");
        }
        var before = LogicalNodeMySqlTests.Snapshot(repo); Assert.IsFalse(new MapUndoService(repo).Undo("owner").Success);
        Assert.AreEqual(before, LogicalNodeMySqlTests.Snapshot(repo)); Assert.IsNotNull(repo.Transaction(s => s.UndoHistory.Latest("owner")));
    }

    [TestMethod, DataRow("move"), DataRow("delete"), DataRow("paste")]
    public void WholeGroupUndoIsAtomic_PreservesCrossSettingsAndAllLnReferences(string operation)
    {
        var repo = new InMemoryLineRepository(); var selected = MapAreaPasteTests.Fixture(repo, CrossState.Road2ToRoad1); var before = Content(repo);
        if (operation == "move") Assert.IsTrue(Edit(repo, r => new MapAreaMoveService(r).Move(new("owner", selected, 300, 300))).Success);
        else if (operation == "paste") Assert.IsTrue(Edit(repo, r => new MapAreaPasteService(r).Paste(new("owner", selected, 300, 300))).Success);
        else
        {
            // Normal deletion still requires removing LN10 before deleting its LN12.
            var child = selected.LogicalNodes.Single(n => n.SideLength == 10); Assert.IsTrue(new LogicalNodeService(repo).Delete(new(child.Code, "owner", child.Version)).Success);
            selected = MapAreaPasteTests.Select(repo, MapAreaPasteTests.Bounds); before = Content(repo);
            Assert.IsTrue(Edit(repo, r => new MapAreaMoveService(r).Delete(new("owner", selected))).Success);
        }
        Ok(new MapUndoService(repo).Undo("owner")); Assert.AreEqual(before, Content(repo));
        Assert.IsTrue(new LogicalNodeService(repo).Calculate().Success);
    }

    [TestMethod]
    public void BackgroundKJDoesNotConflictOrEnterHistory_AndUnrelatedPendingFlagsSurvive()
    {
        var repo = new InMemoryLineRepository(); var selected = MapAreaPasteTests.Fixture(repo);
        var unrelated = LogicalNodeMySqlTests.Add(repo, 10000, 10000, 12000, 10000);
        var n = Node(repo, 6, "远端", "FAR", 10050.5m);
        var saved = repo.Transaction(s => new { Pending = s.PendingSegments.Where(p => p.LineCode == unrelated.Code).ToHashSet(), Dirty = s.DirtyLines.Contains(unrelated.Code) });
        Assert.IsTrue(Edit(repo, r => new MapAreaMoveService(r).Move(new("owner", selected, 300, 300))).Success);
        // J can legitimately advance projection versions without becoming a user edit.
        Assert.IsTrue(new LogicalNodeService(repo).Calculate().Success);
        Ok(new MapUndoService(repo).Undo("owner"));
        CollectionAssert.AreEquivalent(saved.Pending.ToArray(), repo.Transaction(s => s.PendingSegments.Where(p => p.LineCode == unrelated.Code).ToArray()));
        Assert.AreEqual(saved.Dirty, repo.Transaction(s => s.DirtyLines.Contains(unrelated.Code)));
        Assert.AreEqual("NothingToUndo", new MapUndoService(repo).Undo("owner").Code);
    }

    [TestMethod]
    public void PerUserHistoryIsBoundedToOneHundredCommittedEdits()
    {
        var repo = new InMemoryLineRepository(); var n = Node(repo);
        for (var i = 0; i < 103; i++) n = Edit(repo, r => new LogicalNodeService(r).Update(new(n.Code, "owner", n.Version, "V" + i, "", LogicLanguage: "P"))).Data!;
        for (var i = 0; i < 100; i++) Ok(new MapUndoService(repo).Undo("owner"));
        Assert.AreEqual("V2", repo.Transaction(s => s.LogicalNodes[n.Code].Properties.Value));
        Assert.AreEqual("NothingToUndo", new MapUndoService(repo).Undo("owner").Code);
    }

    [TestMethod]
    public void CrossStateUndoDoesNotUndoKProgressOrPendingMarkers()
    {
        var repo = new InMemoryLineRepository(); MapAreaPasteTests.Fixture(repo);
        var cross = repo.Transaction(s => s.Crosses.Values.Single());
        Assert.IsTrue(Edit(repo, r => new CrossService(r).Edit(new(cross.Key,"owner",cross.Version,CrossState.Disconnected))).Success);
        repo.Transaction(s => { s.StaleCrosses.Add(cross.Key); s.Crosses[cross.Key] = s.Crosses[cross.Key] with { Version = 100 }; return true; });
        Ok(new MapUndoService(repo).Undo("owner"));
        Assert.IsTrue(repo.Transaction(s => s.StaleCrosses.Contains(cross.Key)));
        Assert.AreEqual(CrossState.Connected,repo.Transaction(s => s.Crosses[cross.Key].State));
        Assert.IsTrue(repo.Transaction(s => s.Crosses[cross.Key].Version)>100);
    }

    [TestMethod]
    public void UndoLineMoveRejectsNewCrossCreatedBySomeoneElse_InsteadOfLosingItsSetting()
    {
        var repo = new InMemoryLineRepository(); var line = LogicalNodeMySqlTests.Add(repo,20,50,240,50);
        var selection = MapAreaPasteTests.Select(repo,new(10,40,250,60));
        Assert.IsTrue(Edit(repo,r=>new MapAreaMoveService(r).Move(new("owner",selection,300,300))).Success);
        LogicalNodeMySqlTests.Add(repo,400,320,400,380); var cross=new CrossService(repo);while(cross.Calculate().Data!.CanContinue){}
        var before = LogicalNodeMySqlTests.Snapshot(repo); Assert.AreEqual("UndoConflict",new MapUndoService(repo).Undo("owner").Code);
        Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));
    }

    [TestMethod]
    public void UndoLn6CreationCannotOrphanAnotherUsersNewLn4()
    {
        var repo = new InMemoryLineRepository();
        var source = Edit(repo,r=>new LogicalNodeService(r).Create(new("owner",50.5m,50.5m,6,new(){LogicLanguage="P"}))).Data!;
        Node(repo,4,"True","P",80.5m,"bob"); var before=LogicalNodeMySqlTests.Snapshot(repo);
        Assert.AreEqual("UndoRuleConflict",new MapUndoService(repo).Undo("owner").Code); Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));
    }
}
