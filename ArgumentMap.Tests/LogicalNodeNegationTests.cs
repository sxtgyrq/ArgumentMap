using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LogicalNodeNegationTests
{
    [TestMethod,DataRow("¬"),DataRow("¬(P)"),DataRow(""),DataRow("¬(?) ")]
    public void DalRejectsNoncanonicalNegationLogicWithoutPartialWrites(string logic)
    {
        var map=new TestMap();var node=LogicalNodeRelationTests.Add(map.Repository,value:"¬");
        Assert.AreEqual("¬",node.Properties.Value);Assert.AreEqual("¬(?)",node.Properties.LogicLanguage);
        var before=map.Snapshot();
        Assert.ThrowsExactly<InvalidLogicalNodeRelationException>(()=>map.Repository.Transaction(s=>
        {s.LogicalNodes[node.Code]=node with{Properties=node.Properties with{LogicLanguage=logic,Info="must roll back"}};return true;}));
        Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void AreaClipboardAndPastePreserveNegationWhileRemappingBranchReferences_AndUndoRestoresMap()
    {
        var map = new TestMap();
        var selection = MapAreaPasteTests.Fixture(map.Repository);
        var relation = selection.LogicalNodes.Single(n => n.SideLength == 8);
        var updated = new LogicalNodeService(map.Repository).Update(new(relation.Code, "owner", relation.Version, "¬", "非", LogicLanguage: "¬"));
        Assert.IsTrue(updated.Success, updated.Message);
        Assert.AreEqual("¬(?)", updated.Data!.Properties.LogicLanguage);
        selection = MapAreaPasteTests.Select(map.Repository, MapAreaPasteTests.Bounds);
        var original = MapUndoTests.Content(map.Repository);
        var copy = MapAreaClipboard.Decode(MapAreaClipboard.Encode(selection));
        Assert.IsNotNull(copy);
        var result = MapUndoTests.Edit(map.Repository, r => new MapAreaPasteService(r).Paste(new("owner", copy, 400, 400)));
        Assert.IsTrue(result.Success, result.Message);
        var pasted = result.Data!.Added.LogicalNodes.Single(n => n.SideLength == 8);
        Assert.AreEqual(updated.Data!.Properties, pasted.Properties);
        Assert.AreEqual("¬P1", result.Data.Added.LogicalNodes.Single(n => n.SideLength == 4 && n.Properties.Value == "False").Properties.LogicLanguage);
        Assert.IsTrue(new MapUndoService(map.Repository).Undo("owner").Success);
        Assert.AreEqual(original, MapUndoTests.Content(map.Repository));
    }

    [TestMethod]
    public void NegationCanBeCopiedEditedDeletedAndRestoredWithoutChangingRoadsOrBranches()
    {
        var map = new TestMap();
        map.Add(20, 50, 300, 50);
        var service = new LogicalNodeService(map.Repository);
        var source = service.Create(new("owner", 50.5m, 50.5m, 6, new() { Value = "命题", LogicLanguage = "P" }));
        Assert.IsTrue(source.Success, source.Message);
        var branch = service.Create(new("owner", 90.5m, 50.5m, 4, new() { Value = "False", LogicLanguage = "¬P" }));
        Assert.IsTrue(branch.Success, branch.Message);
        var negation = LogicalNodeRelationTests.Add(map.Repository, 130.5m, 50.5m, LogicalNodeRelationRules.Negation);
        var original = MapUndoTests.Content(map.Repository);
        var copy = LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(8, negation.Properties));
        Assert.IsNotNull(copy);
        Assert.AreEqual(negation.Properties, copy.Properties);

        var pasted = MapUndoTests.Edit(map.Repository, r => new LogicalNodeService(r).Create(new("owner", 180.5m, 50.5m, copy.SideLength, copy.Properties)));
        Assert.IsTrue(pasted.Success, pasted.Message);
        Assert.AreNotEqual(negation.Code, pasted.Data!.Code);
        Assert.AreEqual("¬(?)", pasted.Data.Properties.LogicLanguage);
        Assert.IsTrue(new MapUndoService(map.Repository).Undo("owner").Success);
        Assert.AreEqual(original, MapUndoTests.Content(map.Repository));

        var edited = MapUndoTests.Edit(map.Repository, r => new LogicalNodeService(r).Update(new(negation.Code, "owner", negation.Version, "↔", "changed", LogicLanguage: "ignored")));
        Assert.IsTrue(edited.Success, edited.Message);
        Assert.IsTrue(new MapUndoService(map.Repository).Undo("owner").Success);
        Assert.AreEqual(original, MapUndoTests.Content(map.Repository));
        var restored = service.GetAll().Data!.Single(n => n.Code == negation.Code);
        Assert.AreEqual("¬", restored.Properties.Value);
        Assert.AreEqual("¬(?)", restored.Properties.LogicLanguage);
        CollectionAssert.AreEqual(negation.Positions, restored.Positions);
        Assert.AreEqual(branch.Data!.Properties, service.GetAll().Data!.Single(n => n.Code == branch.Data.Code).Properties);

        var deleted = MapUndoTests.Edit(map.Repository, r => new LogicalNodeService(r).Delete(new(restored.Code, "owner", restored.Version)));
        Assert.IsTrue(deleted.Success, deleted.Message);
        Assert.IsTrue(new MapUndoService(map.Repository).Undo("owner").Success);
        Assert.AreEqual(original, MapUndoTests.Content(map.Repository));
    }
}
