using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LogicalNodeUniqueValueTests
{
    internal static LogicalNodeSnapshot Add(ILineRepository repository, string value, decimal x = 50.5m, int size = 12, string owner = "owner")
    {
        var result = new LogicalNodeService(repository).Create(new(owner, x, 50.5m, size,
            new() { Value = size==4?"False":value, Info = "说明", LogicLanguage = size==4?"":size==6?"Fixture_"+LineCode.Generate():"S" }));
        Assert.IsTrue(result.Success, result.Message);
        return result.Data!;
    }

    [TestMethod, DataRow("S"), DataRow(""), DataRow("中文值"), DataRow("S ")]
    public void Create_GlobalAcrossOwners_AppendsSequenceAndPreservesOriginal(string value)
    {
        var map = new TestMap(); var node = Add(map.Repository, value);
        var duplicate = new LogicalNodeService(map.Repository).Create(new("another", 100.5m, 50.5m, 12, new() { Value = value }));
        Assert.IsTrue(duplicate.Success,duplicate.Message); Assert.AreEqual(value+"1",duplicate.Data!.Properties.Value);
        StringAssert.Contains(duplicate.Message,value+"1");
        Assert.AreEqual(node,map.Repository.Transaction(s=>s.LogicalNodes[node.Code]) with {CanEdit=true});
        Assert.AreEqual(value+"2",Add(map.Repository,value,150.5m).Properties.Value);
    }

    [TestMethod]
    public void Create_SkipsOccupiedSuffixes_UsesOriginalAsPrefix_AndDoesNotRenameOtherSizes()
    {
        var map=new TestMap(); Add(map.Repository,"S");Add(map.Repository,"S1",100.5m);Add(map.Repository,"S2",150.5m);
        Add(map.Repository,"S3",200.5m,6);
        Assert.AreEqual("S3",Add(map.Repository,"S",250.5m).Properties.Value);
        Assert.AreEqual("S11",Add(map.Repository,"S1",300.5m).Properties.Value);
        Assert.AreEqual("False",Add(map.Repository,"S",350.5m,4).Properties.Value);
    }

    [TestMethod]
    public void Create_LongValuesReserveSpaceForSuffix_WithoutSplittingUnicode()
    {
        foreach(var value in new[]{new string('中',32),string.Concat(Enumerable.Repeat("😀",16))})
        {
            var map=new TestMap();Add(map.Repository,value);
            var numbered=Add(map.Repository,value,100.5m).Properties.Value;
            Assert.IsTrue(numbered.Length<=32);Assert.IsTrue(numbered.EndsWith("1",StringComparison.Ordinal));
            Assert.IsFalse(numbered.Contains('\uFFFD'));
            Assert.AreEqual(numbered,System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(numbered)));
            for(var suffix=2;suffix<=12;suffix++)
            {
                var next=Add(map.Repository,value,100.5m+30*suffix).Properties.Value;
                Assert.IsTrue(next.Length<=32);Assert.IsTrue(next.EndsWith(suffix.ToString(System.Globalization.CultureInfo.InvariantCulture),StringComparison.Ordinal));
                Assert.AreEqual(next,System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(next)));
            }
            Assert.IsNull(map.Repository.Transaction(s=>LogicalNodeValueUniqueness.FindConflict(s.LogicalNodes.Values)));
        }
    }

    [TestMethod, DataRow(4), DataRow(6), DataRow(18)]
    public void OtherSizesMayShareWithSize12_AndSize12MayUseTheirExistingValue(int size)
    {
        var map = new TestMap(); Add(map.Repository, "S", size: size); Add(map.Repository, "S", 100.5m);
        if (size != 18) Add(map.Repository, "S", 150.5m, size);
        Add(map.Repository, "S", 200.5m, 4);
    }

    [TestMethod]
    public void ValuesAreUppercase_CaseCollides_WhitespaceAndUnicodeFormsRemainDistinct()
    {
        var map = new TestMap();
        foreach (var (value, i) in new[] { "S", "s", "S ", " S", "é", "e\u0301", "😀" }.Select((v, i) => (v, i)))
            Add(map.Repository, value, 50.5m + 30 * i);
        CollectionAssert.AreEquivalent(new[]{"S","S1","S "," S","É","E\u0301","😀"},
            map.Repository.Transaction(s=>s.LogicalNodes.Values.Select(n=>n.Properties.Value).ToArray()));
    }

    [TestMethod]
    public void Update_ExcludesSelf_PreservesVersionLogicAndAssociationsOnDuplicate_AllowsReuseAfterRenameOrDelete()
    {
        var map = new TestMap(); map.Add(10, 50, 220, 50);
        var service = new LogicalNodeService(map.Repository);
        var a = Add(map.Repository, "S"); var b = Add(map.Repository, "T", 100.5m);
        var same = service.Update(new(a.Code, a.Owner, a.Version, "S", "新说明", LogicLanguage: "P"));
        Assert.IsTrue(same.Success, same.Message); a = same.Data!;
        var before = map.Snapshot();
        var duplicate=service.Update(new(b.Code, b.Owner, b.Version, "S", "不能提交", LogicLanguage: "Q"));
        Assert.AreEqual("DuplicateLogicalNodeValue", duplicate.Code);
        StringAssert.Contains(duplicate.Message,a.Code);
        StringAssert.Contains(duplicate.Message,"中心坐标 X=50.5, Y=50.5");
        Assert.AreEqual(before, map.Snapshot());
        Assert.IsTrue(service.Update(new(a.Code, a.Owner, a.Version, "U", "说明")).Success);
        var updated = service.Update(new(b.Code, b.Owner, b.Version, "S", "说明")); Assert.IsTrue(updated.Success);
        Assert.IsTrue(service.Delete(new(b.Code, b.Owner, updated.Data!.Version)).Success);
        Add(map.Repository, "S", 150.5m);
    }

    [TestMethod]
    public void DalBypass_ThrowsAndRollsBackEntireWorkingState()
    {
        var map = new TestMap(); var a = Add(map.Repository, "S"); var b = Add(map.Repository, "T", 100.5m);
        var before = map.Snapshot();
        var error=Assert.ThrowsExactly<DuplicateLogicalNodeValueException>(() => map.Repository.Transaction(state =>
        {
            state.LogicalNodes[b.Code] = b with { Properties = b.Properties with { Value = a.Properties.Value } };
            state.DirtyLines.Add("ABCDEFGHIJ"); return true;
        }));
        var existing=map.Repository.Transaction(s=>s.LogicalNodes[error.Conflict.ExistingCode]);
        Assert.AreEqual(existing.X,error.Conflict.ExistingX);Assert.AreEqual(existing.Y,error.Conflict.ExistingY);
        StringAssert.Contains(error.Message,FormattableString.Invariant($"中心坐标 X={existing.X:0.0}, Y={existing.Y:0.0}"));
        Assert.AreEqual(before, map.Snapshot());
    }

    [TestMethod]
    public void ImportPreviewAndCommit_NumberBatchDuplicatesAndExistingValues_AndReportFinalValues()
    {
        var map = new TestMap(); var d = FlowDiagramTests.Small(); d["ln"]![0]!["size"] = 12;
        d["ln"]!.AsArray().Add(JsonNode.Parse("""{"id":"T","at":[40.5,0.5],"size":12,"value":"事实","logic":"T"}"""));
        Add(map.Repository,"事实",200.5m);var service = new FlowDiagramService(map.Repository); var before = map.Snapshot();
        var request = new FlowDiagramRequest("another",d.ToJsonString(),50.5m,50.5m);
        var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
        CollectionAssert.AreEqual(new[]{"事实1","事实2"},preview.Data!.Added.LogicalNodes.Select(n=>n.Properties.Value).ToArray());
        Assert.AreEqual(2,preview.Data.Notes.Length);StringAssert.Contains(preview.Message,"LN T");
        var invalid=d.DeepClone().AsObject();invalid["roads"]![0]!["points"]![1]![1]=2;
        Assert.IsFalse(service.Import(request with{Text=invalid.ToJsonString()}).Success);Assert.AreEqual(before,map.Snapshot());
        var result=service.Import(request);Assert.IsTrue(result.Success,result.Message);
        CollectionAssert.AreEqual(new[]{"事实1","事实2"},result.Data!.Added.LogicalNodes.Select(n=>n.Properties.Value).ToArray());
        Assert.AreEqual(3,new LogicalNodeService(map.Repository).GetAll().Data!.Length);
    }
}
