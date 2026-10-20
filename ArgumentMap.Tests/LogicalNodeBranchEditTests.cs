using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using ArgumentMap.Tools;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeBranchEditTests
{
    [STATestMethod]
    public void BooleanEditorOnlyOffersTrueFalse_RejectsMismatchedLogic_AndAllowsBlankAfterRepair()
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        Assert.IsTrue(service.Create(new("owner",50.5m,50.5m,6,new(){LogicLanguage="P"})).Success);
        var branch=service.Create(new("owner",100.5m,50.5m,4,new(){Value="False",LogicLanguage="¬P"})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(100.5,50.5),branch){Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            window.Show();var values=(ComboBox)window.FindName("BranchValueInput");Assert.AreEqual(2,values.Items.Count);Assert.IsFalse(values.IsEditable);Assert.AreEqual("False",values.SelectedValue);
            Assert.AreEqual(Visibility.Collapsed,((TextBox)window.FindName("ValueInput")).Visibility);var logic=(TextBox)window.FindName("LogicInput");var save=(Button)window.FindName("SaveButton");
            var before=Snapshot(handler.Repository);values.SelectedValue="True";Assert.AreEqual("P",logic.Text);logic.Text="¬P";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LogicalNodeWindowEntryTests.DrainUntil(()=>((TextBlock)window.FindName("Status")).Text.Contains("唯一 LN6",StringComparison.Ordinal));Assert.AreEqual(before,Snapshot(handler.Repository));Assert.IsTrue(window.IsVisible);
            logic.Text="";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));LogicalNodeWindowEntryTests.DrainUntil(()=>!window.IsVisible);
            var current=service.GetAll().Data!.Single(n=>n.Code==branch.Code);Assert.AreEqual("True",current.Properties.Value);Assert.AreEqual("",current.Properties.LogicLanguage);
        }
        finally{window.Close();}
    }
    [STATestMethod,DataRow("body"),DataRow("label"),DataRow("keyboard7"),DataRow("button")]
    public void BrowsingCanSelectRealBranchAndOpenOriginalEditor_SaveLogicAndValue(string entry)
    {
        using var handler=new DirectLogicalNodeHttpStub();var data=new EquipmentDiagramData(handler.Repository).Import("owner",250002,249920);Assert.IsTrue(data.Success,data.Message);
        var node=data.Data!.Single(n=>n.Properties.Info==EquipmentDiagramData.Marker+"PNo");
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var client=new HttpLineClient(http,new("owner","test"));
        var window=new MainWindow(client){Width=1400,Height=980,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();window.SetToolPanelExpanded(false);window.UpdateLayout();var load=window.InitializeLinesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>load.IsCompleted);Assert.IsTrue(load.Result);
            var view=(CoordinateMapView)window.FindName("MapView");view.FocusLogicalNodes(data.Data!.Select(n=>n.Code));window.UpdateLayout();Assert.IsFalse(view.IsLogicalNodeMode);
            Point Screen(Point world)=>(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[world])!;
            var screen=Screen(new((double)node.X,(double)node.Y));
            if(entry=="label")
            {
                var label=((FormattedText Text,Point At))typeof(CoordinateMapView).GetMethod("LogicalNodeLabel",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[node])!;
                screen=new(label.At.X+label.Text.Width-1,label.At.Y+label.Text.Height/2);
                Assert.IsTrue(view.SelectLogicalNodeAt(screen));
            }
            else if(entry=="keyboard7")
            {
                typeof(CoordinateMapView).GetField("lastPointer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,screen);
                Press(window,Key.D7);
            }
            else Assert.IsTrue(view.SelectLogicalNodeAt(screen));
            Assert.IsTrue(view.IsLogicalNodeMode);
            if(entry=="button")((Button)window.FindName("EditLogicalNodeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else Press(window,Key.E);
            LogicalNodeWindowEntryTests.DrainUntil(()=>Dialog(window) is not null);var dialog=Dialog(window)!;
            Assert.AreEqual("False",((ComboBox)dialog.FindName("BranchValueInput")).SelectedValue);Assert.AreEqual(Visibility.Collapsed,((TextBox)dialog.FindName("ValueInput")).Visibility);Assert.AreEqual("¬P",((TextBox)dialog.FindName("LogicInput")).Text);
            Assert.IsTrue(((Button)dialog.FindName("SaveButton")).IsEnabled);Assert.IsTrue(((Button)dialog.FindName("MoveButton")).IsEnabled);
            ((ComboBox)dialog.FindName("BranchValueInput")).SelectedValue="True";Assert.AreEqual("P",((TextBox)dialog.FindName("LogicInput")).Text);
            ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LogicalNodeWindowEntryTests.DrainUntil(()=>new LogicalNodeService(handler.Repository).GetAll().Data!.Single(n=>n.Code==node.Code).Version>node.Version);
            var stored=new LogicalNodeService(handler.Repository).GetAll().Data!.Single(n=>n.Code==node.Code);
            Assert.AreEqual("True",stored.Properties.Value);Assert.AreEqual("P",stored.Properties.LogicLanguage);Assert.AreEqual(4,stored.SideLength);CollectionAssert.AreEqual(node.Positions,stored.Positions);
        }
        finally{Dialog(window)?.Close();window.Close();}
    }
    [STATestMethod]
    public void PlainValueLabelWithoutLogicIsAlsoSelectable_AndRoadDraftIsProtected()
    {
        var map=new TestMap();var node=new LogicalNodeService(map.Repository).Create(new("owner",250000.5m,250000.5m,6,new(){Value="普通节点文字"})).Data!;
        var client=new LogicalNodeMoveUiTests.Client(map);var view=new CoordinateMapView();
        var host=new Window{Content=view,Width=900,Height=700,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            host.Show();host.UpdateLayout();view.ConfigureClient(client);view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();view.ZoomAtCenter(4);host.UpdateLayout();
            var label=((FormattedText Text,Point At))typeof(CoordinateMapView).GetMethod("LogicalNodeLabel",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[node])!;
            Assert.IsTrue(view.SelectLogicalNodeAt(new(label.At.X+label.Text.Width-1,label.At.Y+2)));
            view.NewLineAsync().GetAwaiter().GetResult();view.BeginDrawingAsync().GetAwaiter().GetResult();
            Assert.IsFalse(view.SelectLogicalNodeAt(new(label.At.X+label.Text.Width-1,label.At.Y+2)));
        }
        finally{host.Close();}
    }
    [TestMethod]
    public void FourBranchesAreOrdinaryNodes_SupportUpdateMoveDeleteAndRecalculation()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);var result=new EquipmentDiagramData(map.Repository).Import("owner",1000,1000);Assert.IsTrue(result.Success,result.Message);
        var branches=result.Data!.Where(n=>n.Properties.Value is "True" or "False").ToArray();Assert.AreEqual(4,branches.Length);
        foreach(var branch in branches)
        {
            Assert.AreEqual(4,branch.SideLength);Assert.AreEqual(1,branch.Positions.Length);
            var updated=service.Update(new(branch.Code,"owner",branch.Version,branch.Properties.Value,"说明",LogicLanguage:""));Assert.IsTrue(updated.Success);
            var moved=service.Move(new(branch.Code,"owner",updated.Data!.Version,branch.X+1,branch.Y));Assert.IsTrue(moved.Success,moved.Message);
            Assert.AreEqual("",moved.Data!.Properties.LogicLanguage);
            Assert.IsTrue(service.Delete(new(branch.Code,"owner",moved.Data.Version)).Success);
            Assert.IsFalse(map.Repository.Transaction(s=>s.LogicalNodePositions.Keys.Any(p=>p.LogicalNodeCode==branch.Code)));
        }
        Assert.IsTrue(service.Calculate().Success);Assert.AreEqual(6,service.GetAll().Data!.Length);
    }
    private static LogicalNodeWindow? Dialog(MainWindow window)=>(LogicalNodeWindow?)typeof(MainWindow).GetField("logicalNodeWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
    private static void Press(Window window,Key key)
    {var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);}
}

[TestClass,TestCategory("MySql")]
public sealed class LogicalNodeBranchRepairMySqlTests
{
    [TestMethod,DataRow("LogicalNodes"),DataRow("LogicalNodePositions")]
    public Task RepairOldDataRollsBackAllBranchesOnFailure_ThenSavesNormalEditableNodes(string table)=>Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var fixture=new EquipmentDiagramData(repository);var created=fixture.Import("owner",1000,1000);Assert.IsTrue(created.Success,created.Message);
        repository.Transaction(state=>
        {
            foreach(var code in created.Data!.Where(n=>n.Properties.Value is "True" or "False").Select(n=>n.Code))
            {state.LogicalNodes.Remove(code);foreach(var key in state.LogicalNodePositions.Keys.Where(k=>k.LogicalNodeCode==code).ToArray())state.LogicalNodePositions.Remove(key);}
            return true;
        });
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        Sql(settings,$"CREATE TRIGGER TestBranchRepair BEFORE INSERT ON {table} FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Branch repair failure'");
        try{Assert.ThrowsExactly<MySqlException>(()=>fixture.AddBranches("owner"));Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{Sql(settings,"DROP TRIGGER TestBranchRepair");}
        var repaired=fixture.AddBranches("owner");Assert.IsTrue(repaired.Success,repaired.Message);Assert.AreEqual(10,repaired.Data!.Length);Assert.AreEqual(12,repaired.Data.Sum(n=>n.Positions.Length));
        before=LineTopologyMySqlTests.RawSnapshot(settings);Assert.IsTrue(fixture.AddBranches("owner").Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.AreEqual(Snapshot(repository),Snapshot(new MySqlLineRepository(settings)));return Task.CompletedTask;
    });
}
