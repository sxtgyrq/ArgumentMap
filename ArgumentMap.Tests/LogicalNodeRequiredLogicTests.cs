using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeRequiredLogicTests
{
    internal static LogicalNodeSnapshot Add(ArgumentMap.Storage.ILineRepository repository,string logic,decimal x=50.5m,int size=6,string owner="owner",string value="可重复的值")
    {
        var result=new LogicalNodeService(repository).Create(new(owner,x,50.5m,size,new(){Value=size==4?(logic.StartsWith("¬",StringComparison.Ordinal)?"False":"True"):value,Info="原说明",LogicLanguage=logic}));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    [TestMethod,DataRow(""),DataRow(" \t\u3000"),DataRow("\u00a0\u2003")]
    public void DirectCreateBlankGetsUniquePlaceholder_ExplicitBlankEditIsRejected(string blank)
    {
        var map=new TestMap();var a=Add(map.Repository,blank);var b=Add(map.Repository,blank,100.5m);
        Assert.AreEqual("LN_"+a.Code,a.Properties.LogicLanguage);Assert.AreEqual("LN_"+b.Code,b.Properties.LogicLanguage);
        Assert.AreNotEqual(a.Properties.LogicLanguage,b.Properties.LogicLanguage);Assert.AreEqual(a.Properties.Value,b.Properties.Value);
        var before=map.Snapshot();Assert.AreEqual("LogicalNodeLogicRequired",new LogicalNodeService(map.Repository).Update(new(a.Code,a.Owner,a.Version,"不提交的值","",LogicLanguage:blank)).Code);
        Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void ExplicitLogicIsUniqueAcrossOwners_ExcludesSelfAndOtherSizes_AndPreservesCase()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);
        var a=Add(map.Repository,"P");var b=Add(map.Repository,"p",100.5m,owner:"another");
        Add(map.Repository,"P",150.5m,4);Add(map.Repository,"P",200.5m,12);Add(map.Repository,"P",250.5m,18);
        Assert.AreEqual("P",a.Properties.LogicLanguage);Assert.AreEqual("p",b.Properties.LogicLanguage);
        var duplicate=service.Create(new("other",300.5m,50.5m,6,new(){Value="任何值",LogicLanguage="P"}));
        Assert.IsTrue(duplicate.Success,duplicate.Message);Assert.AreEqual("P1",duplicate.Data!.Properties.LogicLanguage);
        var before=map.Snapshot();var rejected=service.Update(new(b.Code,b.Owner,b.Version,b.Properties.Value,"",LogicLanguage:"P"));
        Assert.AreEqual("DuplicateLogicalNodeLogic",rejected.Code);StringAssert.Contains(rejected.Message,a.Code);StringAssert.Contains(rejected.Message,"X=50.5, Y=50.5");Assert.AreEqual(before,map.Snapshot());
        var same=service.Update(new(a.Code,a.Owner,a.Version,"改值","改说明",LogicLanguage:"P"));Assert.IsTrue(same.Success);
        Assert.IsTrue(service.Update(new(a.Code,a.Owner,same.Data!.Version,"改值","旧客户端未传逻辑语言")).Success);
    }
    [TestMethod]
    public void DalBypassBlankOrDuplicateRollsBackWholeWorkingState()
    {
        var map=new TestMap();var a=Add(map.Repository,"P");var b=Add(map.Repository,"Q",100.5m);var before=map.Snapshot();
        Assert.ThrowsExactly<LogicalNodeLogicRequiredException>(()=>map.Repository.Transaction(s=>
        {s.LogicalNodes[b.Code]=b with{Properties=b.Properties with{LogicLanguage=" \t"}};s.DirtyLines.Add("ABCDEFGHIJ");return true;}));
        Assert.AreEqual(before,map.Snapshot());
        Assert.ThrowsExactly<DuplicateLogicalNodeLogicException>(()=>map.Repository.Transaction(s=>
        {s.LogicalNodes[b.Code]=b with{Properties=b.Properties with{LogicLanguage=a.Properties.LogicLanguage}};return true;}));
        Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void PlaceholderSkipsExistingExpressions_ImportAllocatesWithoutPreviewReservation_AndMovePreservesLogic()
    {
        var used=new HashSet<string>(StringComparer.Ordinal){"LN_ABCDEFGHIJ","LN_ABCDEFGHIJ1"};
        Assert.AreEqual("LN_ABCDEFGHIJ2",LogicalNodeLogicRules.ReservePlaceholder("ABCDEFGHIJ",used));
        var map=new TestMap();var d=FlowDiagramTests.Small();
        d["ln"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""{"id":"T","at":[40.5,0.5],"size":6,"value":"事实","logic":"S"}"""));
        var service=new FlowDiagramService(map.Repository);var request=new FlowDiagramRequest("owner",d.ToJsonString(),50.5m,50.5m);var before=map.Snapshot();
        var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
        CollectionAssert.AreEquivalent(new[]{"S","S1"},preview.Data!.Added.LogicalNodes.Select(n=>n.Properties.LogicLanguage).ToArray());
        StringAssert.Contains(string.Join(" ",preview.Data.Notes),"S1");
        var invalid=d.DeepClone();invalid["roads"]![0]!["points"]=System.Text.Json.Nodes.JsonNode.Parse("[[0,0],[10,0]]");
        Assert.IsFalse(service.Import(request with{Text=invalid.ToJsonString()}).Success);Assert.AreEqual(before,map.Snapshot());
        var result=service.Import(request);Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(2,result.Data!.Added.LogicalNodes.Length);Assert.IsTrue(result.Data.Added.LogicalNodes.All(n=>n.Properties.Value=="事实"));
        var addedCodes=result.Data.Added.LogicalNodes.Select(n=>n.Code).ToArray();
        var selection=new MapAreaMoveService(map.Repository).Select(new("owner",new(30,30,110,70)));Assert.IsTrue(selection.Success,selection.Message);
        var moved=new MapAreaMoveService(map.Repository).Move(new("owner",selection.Data!,100,100));Assert.IsTrue(moved.Success,moved.Message);
        CollectionAssert.AreEquivalent(new[]{"S","S1"},moved.Data!.LogicalNodes.Where(n=>addedCodes.Contains(n.Code)).Select(n=>n.Properties.LogicLanguage).ToArray());
    }
    [TestMethod]
    public void DuplicateCreationSkipsSuffixes_PreservesValueInfoAndCase_AndHandlesUnicodeLength()
    {
        var map=new TestMap();Add(map.Repository,"P");Add(map.Repository,"P1",100.5m);Add(map.Repository,"P2",150.5m);
        var next=Add(map.Repository,"P",200.5m);Assert.AreEqual("P3",next.Properties.LogicLanguage);
        Assert.AreEqual("可重复的值",next.Properties.Value);Assert.AreEqual("原说明",next.Properties.Info);
        Assert.AreEqual("p",Add(map.Repository,"p",250.5m).Properties.LogicLanguage);
        var root=new string('x',252)+"😀"+"Z";Assert.AreEqual(255,root.Length);
        var used=new HashSet<string>(StringComparer.Ordinal){root};
        for(var i=1;i<=9;i++)used.Add(new string('x',252)+"😀"+i);
        var allocated=LogicalNodeLogicRules.ReserveLogic(root,used);
        Assert.AreEqual(new string('x',252)+"10",allocated);Assert.IsTrue(allocated.Length<=255);
        Assert.IsFalse(allocated.Any(char.IsSurrogate));Assert.IsTrue(used.Contains(allocated));
    }
}
