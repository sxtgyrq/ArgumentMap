using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeInstanceEligibilityTests
{
    [TestMethod,DataRow("人",false),DataRow("H人",true),DataRow("HUMAN",true),DataRow("人A",true),
        DataRow("123",false),DataRow("停止活动",false),DataRow("é",false),DataRow("Ａ",false),DataRow("H123",true)]
    public void ParentEligibilityRequiresAsciiEnglishLetter(string value,bool expected)
    {
        var parent=new LogicalNodeSnapshot("AAAAAAAAAA","owner",0,50.5m,50.5m,12,new(){Value=LogicalNodeValueUniqueness.Normalize(value)});
        Assert.AreEqual(expected,LogicalNodeInstanceRules.CanInstantiate(parent));
    }

    [TestMethod,DataRow(true,"人"),DataRow(false,"人"),DataRow(true,"123"),DataRow(false,"123"),DataRow(true,"停止活动"),DataRow(false,"停止活动")]
    public void RenameWithoutEnglishLetterIsBlockedOnlyWhenParentHasInstances(bool hasChildren,string target)
    {
        var map=new TestMap();var parent=LogicalNodeInstanceTests.Parent(map.Repository);var service=new LogicalNodeService(map.Repository);
        if(hasChildren){LogicalNodeInstanceTests.Child(map.Repository);LogicalNodeInstanceTests.Child(map.Repository,x:130.5m);}
        var before=map.Snapshot();var edited=service.Update(new(parent.Code,"owner",parent.Version,target,"new info",LogicLanguage:parent.Properties.LogicLanguage));
        if(hasChildren)
        {
            Assert.AreEqual("InvalidInstanceParentValue",edited.Code,edited.Message);StringAssert.Contains(edited.Message,"英文字母");
            Assert.AreEqual(before,map.Snapshot());
            foreach(var child in service.GetAll().Data!.Where(n=>n.SideLength==10))Assert.IsTrue(service.Delete(new(child.Code,"owner",child.Version)).Success);
            edited=service.Update(new(parent.Code,"owner",parent.Version,target,"new info",LogicLanguage:parent.Properties.LogicLanguage));
        }
        Assert.IsTrue(edited.Success,edited.Message);Assert.AreEqual(target,edited.Data!.Properties.Value);
    }

    [TestMethod]
    public void EnglishChineseRenameStillSynchronizesInstances_AndAssignmentToChineseParentFails()
    {
        var map=new TestMap();var parent=LogicalNodeInstanceTests.Parent(map.Repository,"H人");var child=LogicalNodeInstanceTests.Child(map.Repository,"h人(id=1)");
        var chinese=LogicalNodeInstanceTests.Parent(map.Repository,"人",200.5m);var service=new LogicalNodeService(map.Repository);
        var before=map.Snapshot();var rejected=service.Update(new(child.Code,"owner",child.Version,"人","",LogicLanguage:""));
        Assert.AreEqual("InvalidInstanceValue",rejected.Code,rejected.Message);Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual("InvalidInstanceValue",service.Create(new("owner",250.5m,50.5m,10,new(){Value="人"})).Code);Assert.AreEqual(before,map.Snapshot());
        var renamed=service.Update(new(parent.Code,"owner",parent.Version,"M人","",LogicLanguage:parent.Properties.LogicLanguage));
        Assert.IsTrue(renamed.Success,renamed.Message);var current=service.GetAll().Data!.Single(n=>n.Code==child.Code);
        Assert.AreEqual("m人(id=1)",current.Properties.Value);Assert.AreEqual("m人(id=1)∈M人∧1∈M人.ID",current.Properties.LogicLanguage);
        Assert.AreEqual(chinese.Properties,service.GetAll().Data!.Single(n=>n.Code==chinese.Code).Properties);
    }

    [TestMethod]
    public void DalGuardsNewChineseInstanceAndSynchronizedParentRename_AndImportClipboardRejectThem()
    {
        var map=new TestMap();LogicalNodeInstanceTests.Parent(map.Repository,"人");var before=map.Snapshot();
        Assert.ThrowsExactly<InvalidLogicalNodeInstanceException>(()=>map.Repository.Transaction(s=>
        {var code=LineCode.Generate();s.LogicalNodes[code]=new(code,"owner",0,90.5m,50.5m,10,new(){Value="人",LogicLanguage="人 ∈ 人"});return true;}));
        Assert.AreEqual(before,map.Snapshot());
        Assert.IsFalse(new FlowDiagramService(map.Repository).Import(new("owner",LogicalNodeInstanceTests.Document("人","人"),1000.5m,1000.5m)).Success);
        Assert.IsFalse(LogicalNodeClipboard.Valid(10,new(){Value="人",LogicLanguage="人 ∈ 人"}));Assert.AreEqual(before,map.Snapshot());
        map=new();var parent=LogicalNodeInstanceTests.Parent(map.Repository);var child=LogicalNodeInstanceTests.Child(map.Repository);before=map.Snapshot();
        Assert.ThrowsExactly<InvalidLogicalNodeInstanceException>(()=>map.Repository.Transaction(s=>
        {
            s.LogicalNodes[parent.Code]=parent with{Properties=parent.Properties with{Value="人"}};
            s.LogicalNodes[child.Code]=child with{Properties=child.Properties with{Value="人",LogicLanguage="人 ∈ 人"}};return true;
        }));Assert.AreEqual(before,map.Snapshot());
    }

    [STATestMethod]
    public void PickerAndMapListExcludeChineseOnlyAndNumericParents()
    {
        using var handler=new DirectLogicalNodeHttpStub();var names=new[]{"人","H人","HUMAN","123"};var x=50.5m;
        foreach(var name in names){LogicalNodeInstanceTests.Parent(handler.Repository,name,x);x+=40;}
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var client=new ArgumentMap.Client.HttpLineClient(http,new("owner","test"));
        var main=new MainWindow(client){Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            CollectionAssert.AreEquivalent(new[]{"H人","HUMAN"},view.InstanceParents.Select(n=>n.Properties.Value).ToArray());
            var all=new LogicalNodeService(handler.Repository).GetAll().Data!;
            var picker=new LogicalNodeSizeWindow(false,all){Owner=main};
            try
            {
                picker.Show();var combo=(ComboBox)picker.FindName("InstanceParentInput");
                CollectionAssert.AreEquivalent(new[]{"H人","HUMAN"},combo.Items.Cast<LogicalNodeSnapshot>().Select(n=>n.Properties.Value).ToArray());
            }
            finally{picker.Close();}
            Assert.IsFalse(view.BeginInstancePlacement(all.Single(n=>n.Properties.Value=="人")));
        }
        finally{main.Close();}
    }
}
