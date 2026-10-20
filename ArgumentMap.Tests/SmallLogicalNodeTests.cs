using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using ArgumentMap.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class SmallLogicalNodeTests
{
    internal static void AssertRoadPassesThrough(LogicalNodeSnapshot node,LineSnapshot road)
    {
        var position=node.Positions.Single();var a=road.Nodes[position.RoadOrder];var b=road.Nodes[position.RoadOrder+1];
        Assert.AreEqual(4,node.SideLength);
        if(a.Y==b.Y)
        {Assert.IsTrue(Math.Min(a.X,b.X)<node.Left&&Math.Max(a.X,b.X)>node.Right);Assert.IsTrue(a.Y>node.Bottom&&a.Y<node.Top);}
        else
        {Assert.AreEqual(a.X,b.X);Assert.IsTrue(Math.Min(a.Y,b.Y)<node.Bottom&&Math.Max(a.Y,b.Y)>node.Top);Assert.IsTrue(a.X>node.Left&&a.X<node.Right);}
        Assert.AreEqual(node.Code,position.LogicalNodeCode);Assert.AreEqual(road.Version,position.RoadVersion);
    }
    [TestMethod]
    public void FourBranchesAreSizeFourAndPathsPassThroughInteriors_HorizontalAndVertical()
    {
        var map=new TestMap();var result=new EquipmentDiagramData(map.Repository).Import("owner",1000,1000);Assert.IsTrue(result.Success,result.Message);
        var branches=result.Data!.Where(n=>n.Properties.Value is "True" or "False").ToArray();Assert.AreEqual(4,branches.Length);
        foreach(var node in branches)AssertRoadPassesThrough(node,map.Repository.Transaction(s=>s.Lines[node.Positions.Single().RoadCode]));
        var before=map.Snapshot();Assert.IsTrue(new EquipmentDiagramData(map.Repository).ResizeBranchesToFour("owner").Success);Assert.AreEqual(before,map.Snapshot());
    }
    [STATestMethod]
    public void F5IncludesFour_ExistingFourEditorDisplaysCorrectReadonlySize()
    {
        var chooser=new LogicalNodeSizeWindow(true);int? chosen=null;chooser.SizeSelected+=size=>chosen=size;
        Assert.IsTrue(((Button)chooser.FindName("Size4Button")).IsEnabled);Assert.IsFalse(((Button)chooser.FindName("Size18Button")).IsEnabled);
        ((Button)chooser.FindName("Size4Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(4,chosen);
        var map=new TestMap();var node=new LogicalNodeService(map.Repository).Create(new("owner",100.5m,100.5m,4,new(){Value="True",LogicLanguage=""})).Data!;
        var editor=new LogicalNodeWindow(new LogicalNodeMoveUiTests.Client(map),"owner",new(),node);
        try{Assert.AreEqual("4",((ComboBoxItem)((ComboBox)editor.FindName("SizeInput")).SelectedItem).Tag);Assert.IsFalse(((UIElement)editor.FindName("GeometryPanel")).IsEnabled);}
        finally{editor.Close();}
    }
}

[TestClass,TestCategory("MySql")]
public sealed class SmallLogicalNodeMySqlTests
{
    [TestMethod,DataRow(false),DataRow(true)]
    public Task Schema17UpgradeAcceptsFour_PreservesOldDataAndRetriesPublication(bool interrupt)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var service=new LogicalNodeService(repository);service.Create(new("owner",100.5m,100.5m,6,new(){LogicLanguage="P"}));
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,"ALTER TABLE LogicalNodes DROP CHECK CK_LN_V14, ADD CONSTRAINT CK_LN_V14 CHECK (REGEXP_LIKE(Code, '^[A-Z]{10}$', 'c') AND Version>=0 AND MOD(X,1)=0.5 AND MOD(Y,1)=0.5 AND SideLength IN (6,12,18) AND X-SideLength/2>=0 AND Y-SideLength/2>=0 AND X+SideLength/2<=500000 AND Y+SideLength/2<=500000); UPDATE MapMetadata SET SchemaVersion=17 WHERE Id=1");
        if(interrupt)
        {
            Sql(settings,"CREATE TRIGGER TestSmallPublish BEFORE UPDATE ON MapMetadata FOR EACH ROW BEGIN IF NEW.SchemaVersion=18 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Small LN publication failure'; END IF; END");
            try{Assert.ThrowsExactly<MySqlException>(()=>new MySqlLineRepository(settings));Assert.ThrowsExactly<InvalidOperationException>(()=>repository.Transaction(s=>true));}
            finally{Sql(settings,"DROP TRIGGER TestSmallPublish");}
        }
        repository=new MySqlLineRepository(settings);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var created=new LogicalNodeService(repository).Create(new("owner",200.5m,200.5m,4,new(){Value="False",LogicLanguage="¬P"}));Assert.IsTrue(created.Success,created.Message);
        Assert.ThrowsExactly<MySqlException>(()=>Sql(settings,"UPDATE LogicalNodes SET SideLength=5"));
        Assert.AreEqual(4,new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single(n=>n.Code==created.Data!.Code).SideLength);
        return Task.CompletedTask;
    });
    [TestMethod]
    public Task ResizeFourExistingBranches_SecondWriteFailureRollsBackThenRetryPreservesIdentityAndPositions()=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var data=new EquipmentDiagramData(repository);var imported=data.Import("owner",1000,1000);Assert.IsTrue(imported.Success,imported.Message);
        repository.Transaction(s=>{foreach(var node in s.LogicalNodes.Values.Where(n=>n.Properties.Value is "True" or "False").ToArray())s.LogicalNodes[node.Code]=node with{SideLength=6,Properties=node.Properties with{LogicLanguage="Legacy_"+node.Code}};return true;});
        var original=new LogicalNodeService(repository).GetAll().Data!;var before=LineTopologyMySqlTests.RawSnapshot(settings);
        var secondCode=original.Where(n=>n.Properties.Value is "True" or "False").OrderBy(n=>n.Code,StringComparer.Ordinal).Skip(1).First().Code;
        Assert.IsTrue(LineCode.IsValid(secondCode));
        Sql(settings,$"CREATE TRIGGER TestSmallWrite BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='{secondCode}' AND OLD.SideLength=6 AND NEW.SideLength=4 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Second branch update failure'; END IF; END");
        try{Assert.ThrowsExactly<MySqlException>(()=>data.ResizeBranchesToFour("owner"));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER TestSmallWrite");}
        var resized=data.ResizeBranchesToFour("owner");Assert.IsTrue(resized.Success,resized.Message);
        foreach(var node in resized.Data!.Where(n=>n.Properties.Value is "True" or "False"))
        {
            var old=original.Single(n=>n.Code==node.Code);Assert.AreEqual(old.Version+1,node.Version);Assert.AreEqual(old.X,node.X);Assert.AreEqual(old.Y,node.Y);Assert.AreEqual(old.Properties.Value,node.Properties.Value);Assert.AreEqual(old.Properties.Info,node.Properties.Info);Assert.IsTrue(node.Properties.LogicLanguage is "P" or "¬P" or "Q" or "¬Q");CollectionAssert.AreEqual(old.Positions,node.Positions);
            SmallLogicalNodeTests.AssertRoadPassesThrough(node,repository.Transaction(s=>s.Lines[node.Positions.Single().RoadCode]));
        }
        before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.IsTrue(data.ResizeBranchesToFour("owner").Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));return Task.CompletedTask;
    });
}
