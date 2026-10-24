using System.Text.Json.Nodes;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeInstanceAttributeTests
{
    [TestMethod,DataRow("HUMAN(id=1)","human(id=1)"),DataRow("Human(name=要瑞卿)","human(name=要瑞卿)"),
        DataRow("HUMAN(name=Alice)","human(name=Alice)"),DataRow("HUMAN(id=ABCDEFGHIJ)","human(id=ABCDEFGHIJ)"),
        DataRow("HUMAN(note=A=B)","human(note=A=B)")]
    public void SaveNormalizesOnlyClassPrefix_AndUsesClassValueInsteadOfClassLogic(string draft,string expected)
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var first=LogicalNodeInstanceTests.Child(repo,draft);var second=LogicalNodeInstanceTests.Child(repo,draft,130.5m);
        Assert.AreEqual(expected,first.Properties.Value);Assert.AreEqual(LogicalNodeInstanceRules.Expression(expected),first.Properties.LogicLanguage);
        Assert.AreEqual(first.Properties,second.Properties);Assert.AreNotEqual(first.Code,second.Code);
        Assert.AreEqual("HUMAN",LogicalNodeInstanceRules.ParentValue(first.Properties.Value));
        Assert.AreNotEqual(parent.Properties.LogicLanguage,LogicalNodeInstanceRules.ParentValue(first.Properties.Value));
        Assert.AreEqual(first.Properties,LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(10,first.Properties))!.Properties);
    }

    [TestMethod,DataRow("human"),DataRow("human1"),DataRow("human(id)"),DataRow("human(=1)"),
        DataRow("human(id=)"),DataRow("human( =1)"),DataRow("human(id= )"),DataRow("human(id=1)(name=A)"),
        DataRow("human(id=(1))"),DataRow("human(id=1)tail"),DataRow("(id=1)")]
    public void MalformedValuesAreRejectedByCreateEditClipboardAndImport_WithoutMutation(string value)
    {
        var repo=new InMemoryLineRepository();LogicalNodeInstanceTests.Parent(repo,"HUMAN");var child=LogicalNodeInstanceTests.Child(repo,"human(id=1)");
        var before=MapUndoTests.Content(repo);var service=new LogicalNodeService(repo);
        Assert.AreEqual("InvalidInstanceValue",service.Create(new("owner",130.5m,50.5m,10,new(){Value=value})).Code,value);
        Assert.AreEqual("InvalidInstanceValue",service.Update(new(child.Code,"owner",child.Version,value,"")).Code,value);
        Assert.IsFalse(LogicalNodeClipboard.Valid(10,new(){Value=value,LogicLanguage=LogicalNodeInstanceRules.Expression(value)}));
        Assert.IsFalse(new FlowDiagramService(repo).Import(new("owner",LogicalNodeInstanceTests.Document(value,"HUMAN"),1000.5m,1000.5m)).Success,value);
        Assert.AreEqual(before,MapUndoTests.Content(repo));
    }

    [TestMethod]
    public void ParentRenameAndUndoPreserveAttributePayloadAcrossOwners_AndReassignmentUsesPrefix()
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var a=LogicalNodeInstanceTests.Child(repo,"human(name=Alice)");var b=LogicalNodeInstanceTests.Child(repo,"human(id=AAZZ)",130.5m,owner:"other");
        var before=MapUndoTests.Content(repo);
        var renamed=MapUndoTests.Edit(repo,r=>new LogicalNodeService(r).Update(new(parent.Code,"owner",parent.Version,"PEOPLE","",LogicLanguage:"custom")));
        Assert.IsTrue(renamed.Success,renamed.Message);
        foreach(var original in new[]{a,b})
        {
            var current=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==original.Code);
            Assert.AreEqual(original.Properties.Value.Replace("human(","people("),current.Properties.Value);
            Assert.AreEqual(LogicalNodeInstanceRules.Expression(current.Properties.Value),current.Properties.LogicLanguage);
            Assert.AreEqual(original.Owner,current.Owner);Assert.AreEqual(original.Version+1,current.Version);
        }
        var undo=new MapUndoService(repo).Undo("owner");Assert.IsTrue(undo.Success,undo.Message);Assert.AreEqual(before,MapUndoTests.Content(repo));
        LogicalNodeInstanceTests.Parent(repo,"PEOPLE",200.5m);
        var latest=new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==a.Code);
        var changed=new LogicalNodeService(repo).Update(new(a.Code,"owner",latest.Version,"PEOPLE(name=Alice)",""));
        Assert.IsTrue(changed.Success,changed.Message);Assert.AreEqual("people(name=Alice)∈PEOPLE∧Alice∈PEOPLE.NAME",changed.Data!.Properties.LogicLanguage);
    }

    [TestMethod]
    public void QuickPlacementUsesOwnNodeCode_AndValidatesLatestParent()
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");var service=new LogicalNodeService(repo);
        for(var i=0;i<2;i++)
        {
            var created=service.Create(new("owner",90.5m+i*40,50.5m,10,new(){Info="保留"},InstanceParentCode:parent.Code));
            Assert.IsTrue(created.Success,created.Message);Assert.AreEqual($"human(id={created.Data!.Code})",created.Data.Properties.Value);
            Assert.AreEqual($"human(id={created.Data.Code})∈HUMAN∧{created.Data.Code}∈HUMAN.ID",created.Data.Properties.LogicLanguage);Assert.AreEqual("保留",created.Data.Properties.Info);
        }
        Assert.AreEqual("InstanceParentNotFound",service.Create(new("owner",200.5m,50.5m,10,new(),InstanceParentCode:"ZZZZZZZZZZ")).Code);
        Assert.AreEqual("InvalidInstanceValue",service.Create(new("owner",200.5m,50.5m,10,new(){Value="human"})).Code);
    }

    [TestMethod,DataRow(true),DataRow(false)]
    public void LocalClassRenumberingPreservesAttributesDuringImportAndGroupPaste(bool paste)
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(repo,"human(name=Alice)");
        if(paste)
        {
            var selected=MapAreaPasteTests.Select(repo,new(30,30,110,70));
            var result=new MapAreaPasteService(repo).Paste(new("owner",selected,300,300));Assert.IsTrue(result.Success,result.Message);
            Assert.AreEqual("human1(name=Alice)∈HUMAN1∧Alice∈HUMAN1.NAME",result.Data!.Added.LogicalNodes.Single(n=>n.SideLength==10).Properties.LogicLanguage);
        }
        else
        {
            var result=new FlowDiagramService(repo).Import(new("owner",LogicalNodeInstanceTests.Document("human(name=Alice)","HUMAN"),1000.5m,1000.5m));
            Assert.IsTrue(result.Success,result.Message);
            Assert.IsTrue(result.Data!.Added.LogicalNodes.Where(n=>n.SideLength==10).All(n=>n.Properties.Value=="human1(name=Alice)"&&n.Properties.LogicLanguage=="human1(name=Alice)∈HUMAN1∧Alice∈HUMAN1.NAME"));
        }
        Assert.AreEqual(child.Properties,new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==child.Code).Properties);
        Assert.AreEqual("HUMAN",new LogicalNodeService(repo).GetAll().Data!.Single(n=>n.Code==parent.Code).Properties.Value);
    }

    [TestMethod]
    public void LongInstanceValuesAreSupported_AndOverlongParentRenameIsAtomic()
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"A");
        var value="a(name="+new string('X',212)+")";Assert.AreEqual(220,value.Length);
        var child=LogicalNodeInstanceTests.Child(repo,value);Assert.AreEqual(value,child.Properties.Value);
        Assert.IsTrue(LogicalNodeClipboard.Valid(10,child.Properties));
        var before=MapUndoTests.Content(repo);
        var failed=new LogicalNodeService(repo).Update(new(parent.Code,"owner",parent.Version,"LONGER","",LogicLanguage:"class"));
        Assert.AreEqual("InvalidInstanceValue",failed.Code);Assert.AreEqual(before,MapUndoTests.Content(repo));
        Assert.IsFalse(new LogicalNodeService(repo).Create(new("owner",130.5m,50.5m,10,new(){Value=value[..^1]+"X)"})).Success);
        Assert.IsFalse(new LogicalNodeService(repo).Create(new("owner",130.5m,50.5m,12,new(){Value=new string('A',33)})).Success);
    }

    [TestMethod,DataRow("人(id=English)"),DataRow("123(name=Alice)")]
    public void EnglishInAttributeDoesNotQualifyClass(string value)
    {
        var repo=new InMemoryLineRepository();LogicalNodeInstanceTests.Parent(repo,LogicalNodeInstanceRules.ParentValue(value));
        Assert.AreEqual("InvalidInstanceValue",new LogicalNodeService(repo).Create(new("owner",90.5m,50.5m,10,new(){Value=value})).Code);
        Assert.IsFalse(FlowDiagramLanguage.Parse(LogicalNodeInstanceTests.Document(value,LogicalNodeInstanceRules.ParentValue(value))).Success);
        Assert.IsFalse(LogicalNodeClipboard.Valid(10,new(){Value=value,LogicLanguage=LogicalNodeInstanceRules.Expression(value)}));
    }

    [TestMethod]
    public void ParentWithInstancesCannotGainParentheses_ButClassWithoutInstancesCan()
    {
        var repo=new InMemoryLineRepository();var parent=LogicalNodeInstanceTests.Parent(repo,"HUMAN");var child=LogicalNodeInstanceTests.Child(repo,"human(name=Alice)");
        var before=MapUndoTests.Content(repo);var service=new LogicalNodeService(repo);
        var refused=service.Update(new(parent.Code,"owner",parent.Version,"HUMAN(ID=1)",""));
        Assert.AreEqual("InvalidInstanceParentValue",refused.Code);StringAssert.Contains(refused.Message,"括号");Assert.AreEqual(before,MapUndoTests.Content(repo));
        Assert.IsTrue(service.Delete(new(child.Code,"owner",child.Version)).Success);
        Assert.IsTrue(service.Update(new(parent.Code,"owner",parent.Version,"HUMAN(ID=1)","")).Success);
        Assert.IsFalse(LogicalNodeInstanceRules.CanInstantiate(service.GetAll().Data!.Single()));
    }

    [STATestMethod]
    public void EditorPreservesPayloadCaseAndLongValues_AndLocatesByPrefixOnly()
    {
        using var handler=new DirectLogicalNodeHttpStub();var parent=LogicalNodeInstanceTests.Parent(handler.Repository,"HUMAN");
        var child=LogicalNodeInstanceTests.Child(handler.Repository,"human(name="+new string('X',70)+")");
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var window=new LogicalNodeWindow(new ArgumentMap.Client.HttpLineClient(http,new("owner","test")),"owner",new(),child);
        try
        {
            var value=(TextBox)window.FindName("ValueInput");var logic=(TextBox)window.FindName("LogicInput");
            Assert.AreEqual(child.Properties.Value,value.Text);Assert.AreEqual(220,value.MaxLength);
            value.Text="HUMAN(name=Alice)";Assert.AreEqual("human(name=Alice)",value.Text);Assert.AreEqual("human(name=Alice)∈HUMAN∧Alice∈HUMAN.NAME",logic.Text);
            LogicalNodeSnapshot? found=null;window.InstanceParentLocationRequested+=n=>found=n;
            Assert.IsTrue(window.LocateInstanceParentAsync().GetAwaiter().GetResult());Assert.AreEqual(parent.Code,found!.Code);
        }
        finally{window.Close();}
    }
}
