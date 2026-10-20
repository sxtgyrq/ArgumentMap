using System.Globalization;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LogicalNodeOperandTests
{
    internal static LogicalNodeSnapshot Add(ILineRepository repo,string kind,string logic,decimal x=50.5m) =>
        New(repo,kind,logic,x).Data!;
    internal static ServiceResult<LogicalNodeSnapshot> New(ILineRepository repo,string kind,string logic,decimal x=50.5m) =>
        new LogicalNodeService(repo).Create(new("owner",x,50.5m,6,new(){Value="显示 Mixed",LogicLanguage=logic,Info="说明\n原文",OperandKind=kind}));
    [TestMethod, DataRow("Class","SENDMSG(S,M)"), DataRow("Instance","sendmsg(s,m)"), DataRow("Custom","SendMsg(s,m)")]
    public void CreateNormalizesOnlyLogic_ReserveUsesFinalText_GlobalUniquenessAcrossKinds(string kind,string expected)
    {
        var repo=new InMemoryLineRepository();var a=Add(repo,kind,"SendMsg(s,m)");Assert.AreEqual(expected,a.Properties.LogicLanguage);Assert.AreEqual(kind,a.Properties.OperandKind);
        Assert.AreEqual("显示 Mixed",a.Properties.Value);Assert.AreEqual("说明\n原文",a.Properties.Info);
        var b=Add(repo,kind,"SendMsg(s,m)",100.5m);Assert.AreEqual(expected+"1",b.Properties.LogicLanguage);
        var custom=Add(repo,"Custom",expected,150.5m);Assert.AreEqual(expected+"2",custom.Properties.LogicLanguage);
        var blank=Add(repo,kind,"",200.5m);Assert.AreEqual(LogicalNodeOperandRules.Normalize("LN_"+blank.Code,kind),blank.Properties.LogicLanguage);
    }
    [TestMethod,DataRow("Class","Q"),DataRow("Instance","q"),DataRow("Custom","q")]
    public void EditNormalizesAndSyncsBothBranches_AndOmittedKindPreservesStoredMode(string kind,string expected)
    {
        var repo=new InMemoryLineRepository();var a=Add(repo,"Custom","P");MapUndoTests.Node(repo,4,"True","P",80.5m,"bob");MapUndoTests.Node(repo,4,"False","¬P",100.5m,"bob");
        var result=new LogicalNodeService(repo).Update(new(a.Code,"owner",a.Version,a.Properties.Value,a.Properties.Info,LogicLanguage:"q",OperandKind:kind));Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(expected,result.Data!.Properties.LogicLanguage);Assert.AreEqual(kind,result.Data.Properties.OperandKind);
        var branches=new LogicalNodeService(repo).GetAll().Data!.Where(n=>n.SideLength==4).ToArray();Assert.AreEqual(expected,branches.Single(n=>n.Properties.Value=="True").Properties.LogicLanguage);Assert.AreEqual("¬"+expected,branches.Single(n=>n.Properties.Value=="False").Properties.LogicLanguage);
        var updated=new LogicalNodeService(repo).Update(new(a.Code,"owner",result.Data.Version,a.Properties.Value,"info",LogicLanguage:"MiXeD"));Assert.IsTrue(updated.Success,updated.Message);Assert.AreEqual(kind,updated.Data!.Properties.OperandKind);Assert.AreEqual(LogicalNodeOperandRules.Normalize("MiXeD",kind),updated.Data.Properties.LogicLanguage);
    }
    [TestMethod,DataRow("Class","P"),DataRow("Instance","p")]
    public void EditCaseCollisionIsRejectedWithoutChangingKindSourceOrBranches(string kind,string conflict)
    {
        var repo=new InMemoryLineRepository();Add(repo,"Custom",conflict);var a=Add(repo,"Custom","Other",100.5m);MapUndoTests.Node(repo,4,"False","¬Other",130.5m);
        var before=LogicalNodeMySqlTests.Snapshot(repo);var result=new LogicalNodeService(repo).Update(new(a.Code,"owner",a.Version,a.Properties.Value,a.Properties.Info,LogicLanguage:"p",OperandKind:kind));Assert.AreEqual("DuplicateLogicalNodeLogic",result.Code);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));
    }
    [TestMethod,DataRow("class"),DataRow("Mixed"),DataRow(""),DataRow(null)]
    public void InvalidModesFailServiceAndDal(string? kind)
    {
        var repo=new InMemoryLineRepository();Assert.AreEqual("InvalidOperandKind",New(repo,kind!,"P").Code);var n=Add(repo,"Custom","P");
        Assert.ThrowsExactly<InvalidLogicalNodeOperandException>(()=>repo.Transaction(s=>{s.LogicalNodes[n.Code]=n with{Properties=n.Properties with{OperandKind=kind!}};return true;}));
        Assert.AreEqual("Custom",repo.Transaction(s=>s.LogicalNodes[n.Code].Properties.OperandKind));
    }
    [TestMethod,DataRow("Class","lower"),DataRow("Instance","UPPER")]
    public void DalRejectsWrongCaseRatherThanSilentlyRewritingRawWrites(string kind,string logic)
    {
        var repo=new InMemoryLineRepository();var n=Add(repo,"Custom","P");var before=LogicalNodeMySqlTests.Snapshot(repo);
        Assert.ThrowsExactly<InvalidLogicalNodeOperandException>(()=>repo.Transaction(s=>{s.LogicalNodes[n.Code]=n with{Properties=n.Properties with{OperandKind=kind,LogicLanguage=logic}};return true;}));Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repo));
        Assert.AreEqual("InvalidOperandKind",new LogicalNodeService(repo).Create(new("owner",150.5m,50.5m,12,new(){Value="HUMAN",OperandKind=kind})).Code);
    }
    [TestMethod]
    public void CasingUsesFixedEnglishCulture_AndCustomKeepsNonemptyUniqueSingleLineRules()
    {
        var previous=CultureInfo.CurrentCulture;try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("tr-TR");var repo=new InMemoryLineRepository();Assert.AreEqual("ID(I)",Add(repo,"Class","id(i)").Properties.LogicLanguage);Assert.AreEqual("id(i)",Add(repo,"Instance","ID(I)",100.5m).Properties.LogicLanguage);
            var n=Add(repo,"Custom","MiXeD",150.5m);Assert.AreEqual("LogicalNodeLogicRequired",new LogicalNodeService(repo).Update(new(n.Code,"owner",n.Version,n.Properties.Value,"",LogicLanguage:" ",OperandKind:"Custom")).Code);
            Assert.AreEqual(LogicalNodeTextRules.ErrorCode,New(repo,"Custom","A\nB",200.5m).Code);
        }finally{CultureInfo.CurrentCulture=previous;}
    }
    [TestMethod,DataRow("Class","p","P"),DataRow("Instance","P","p")]
    public void ImportClipboardGroupPasteAndUndoKeepKindAndRemapBranches(string kind,string input,string expected)
    {
        var repo=new InMemoryLineRepository();Add(repo,"Custom",expected,300.5m);
        var doc=FlowDiagramTests.Small();doc["ln"]![0]!["operandKind"]=kind;doc["ln"]![0]!["logic"]=input;
        doc["ln"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"T","at":[20.5,0.5],"size":4,"value":"True","logic":"{{input}}"}"""));
        doc["ln"]!.AsArray().Add(JsonNode.Parse($$"""{"id":"F","at":[40.5,0.5],"size":4,"value":"False","logic":"¬{{expected}}"}"""));
        var imported=new FlowDiagramService(repo).Import(new("owner",doc.ToJsonString(),50.5m,50.5m));Assert.IsTrue(imported.Success,imported.Message);
        var node=imported.Data!.Added.LogicalNodes.Single(n=>n.SideLength==6);Assert.AreEqual(kind,node.Properties.OperandKind);Assert.AreEqual(expected+"1",node.Properties.LogicLanguage);
        Assert.AreEqual(expected+"1",imported.Data.Added.LogicalNodes.Single(n=>n.Properties.Value=="True").Properties.LogicLanguage);Assert.AreEqual("¬"+expected+"1",imported.Data.Added.LogicalNodes.Single(n=>n.Properties.Value=="False").Properties.LogicLanguage);
        var single=LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(6,node.Properties));Assert.AreEqual(node.Properties,single!.Properties);
        var selected=MapAreaPasteTests.Select(repo,new(35,40,105,60));var copy=MapAreaClipboard.Decode(MapAreaClipboard.Encode(selected))!;Assert.AreEqual(kind,copy.LogicalNodes.Single(n=>n.SideLength==6).Properties.OperandKind);
        var pasted=MapUndoTests.Edit(repo,r=>new MapAreaPasteService(r).Paste(new("owner",copy,500,500)));Assert.IsTrue(pasted.Success,pasted.Message);Assert.AreEqual(expected+"11",pasted.Data!.Added.LogicalNodes.Single(n=>n.SideLength==6).Properties.LogicLanguage);Assert.AreEqual(kind,pasted.Data.Added.LogicalNodes.Single(n=>n.SideLength==6).Properties.OperandKind);
        Assert.IsTrue(new MapUndoService(repo).Undo("owner").Success);
        var changed=MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(node.Code,"owner",node.Version,node.Properties.Value,node.Properties.Info,LogicLanguage:"Z",OperandKind:"Custom")));Assert.IsTrue(changed.Success,changed.Message);
        var undo=new MapUndoService(repo).Undo("owner");Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(node.Properties,undo.Data!.Map.LogicalNodes.Single(n=>n.Code==node.Code).Properties);
    }
    [TestMethod]
    public void LegacyClipboardAndFlowDefaultCustom_InvalidTypeOnOtherSizesIsRejected()
    {
        Assert.AreEqual("Custom",LogicalNodeClipboard.Decode("""{"format":"argumentmap.ln/1","size":6,"value":"P","logic":"MiXeD","info":""}""")!.Properties.OperandKind);
        Assert.AreEqual("Custom",FlowDiagramLanguage.Parse(FlowDiagramTests.Small().ToJsonString()).Data!.Ln.Single().OperandKind);
        var doc=FlowDiagramTests.Small();doc["ln"]![0]!["operandKind"]="Mixed";Assert.AreEqual("InvalidFlowText",FlowDiagramLanguage.Parse(doc.ToJsonString()).Code);
        doc["ln"]![0]!["operandKind"]="Class";doc["ln"]![0]!["size"]=12;Assert.AreEqual("InvalidFlowText",FlowDiagramLanguage.Parse(doc.ToJsonString()).Code);
    }
}
