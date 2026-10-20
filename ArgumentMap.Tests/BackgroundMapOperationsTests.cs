using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class BackgroundMapOperationsTests
{
    [TestMethod]
    public void PendingRoadsCanBeSelectedBeforeCrossExists_KCanCreateItBeforeDrop_WithoutChangingSelection()
    {
        var map=new TestMap();map.Add(20,50,80,50);map.Add(50,20,50,80);var before=map.Snapshot();var selected=MapAreaMoveTests.Select(map);
        Assert.AreEqual(before,map.Snapshot(),"Selection must not consume K work or persist projected Crosses.");Assert.AreEqual(0,map.Crosses.GetAll().Data!.Length);Assert.AreEqual(1,selected.Crosses.Length);
        Assert.IsTrue(map.Crosses.Calculate().Success);Assert.AreEqual(1,map.Crosses.GetAll().Data!.Length);
        var moved=MapAreaMoveTests.Move(map,selected);Assert.IsTrue(moved.Success,moved.Message);Assert.AreEqual(new CrossPosition(300,300),moved.Data!.Crosses.Single().Key.Position);Assert.AreEqual(CrossState.Unset,moved.Data.Crosses.Single().State);
        Assert.AreEqual(0,map.Repository.Transaction(s=>s.PendingSegments.Count));map.Calculate();Assert.AreEqual(1,map.Crosses.GetAll().Data!.Length);
    }

    [TestMethod]
    public void KChangingDerivedOrdersAndPercentsDoesNotInvalidateSelection_ButUserChangingCrossSettingDoes()
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,CrossState.Connected);
        Assert.IsTrue(map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(pair.A,20,50,40,50,80,50)).Success);var before=map.Snapshot();var selected=MapAreaMoveTests.Select(map);Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual(1,selected.Crosses.Single().LocationOn(pair.A.Code).RoadOrder);
        map.Crosses.Calculate();map.Crosses.Calculate();var actual=map.Crosses.GetAll().Data!.Single();Assert.AreEqual(selected.Crosses.Single().Version,actual.Version);
        var moved=MapAreaMoveTests.Move(map,selected);Assert.IsTrue(moved.Success,moved.Message);Assert.AreEqual(CrossState.Connected,moved.Data!.Crosses.Single().State);
        selected=MapAreaMoveTests.Select(map,new(110,110,190,190));actual=map.Crosses.GetAll().Data!.Single();Assert.IsTrue(map.Crosses.Edit(new(actual.Key,"owner",actual.Version,CrossState.Disconnected)).Success);
        before=map.Snapshot();Assert.AreEqual("SelectionChanged",MapAreaMoveTests.Move(map,selected).Code);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void PendingUncomputedCrossIsSkippedWithoutBothWholeRoads_AndDeleteCannotBeUndoneByLaterK()
    {
        var map=new TestMap();map.Add(20,50,80,50);map.Add(50,20,50,80);var before=map.Snapshot();
        var incomplete=new MapAreaMoveService(map.Repository).Select(new("owner",new(15,40,85,60)));
        Assert.IsTrue(incomplete.Success,incomplete.Message);Assert.AreEqual(1,incomplete.Data!.Lines.Length);
        Assert.AreEqual(0,incomplete.Data.Crosses.Length);StringAssert.Contains(incomplete.Message,"已跳过 1 个");Assert.AreEqual(before,map.Snapshot());
        var selection=MapAreaMoveTests.Select(map);var deleted=new MapAreaMoveService(map.Repository).Delete(new("owner",selection));Assert.IsTrue(deleted.Success,deleted.Message);map.Calculate();Assert.AreEqual(0,map.Lines.GetAll().Data!.Length);Assert.AreEqual(0,map.Crosses.GetAll().Data!.Length);
    }

    [STATestMethod,DataRow("area-preview",false),DataRow("area-drop",false),DataRow("flow-preview",false),DataRow("flow-import",false),
        DataRow("area-preview",true),DataRow("area-drop",true),DataRow("flow-preview",true),DataRow("flow-import",true)]
    public void BackgroundCompletionCannotOverwriteAreaSelectionOrFlowPreview_OrNewlyPublishedMap(string action,bool j)
    {
        var map=new TestMap();map.Add(20,50,80,50);map.Add(50,20,50,80);var node=MapAreaMoveTests.Node(map,30.5m,50.5m);
        var client=new BackgroundCrossTests.Client(map);var main=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        var readGate=new TaskCompletionSource<ServiceResult<CrossSnapshot[]>>(TaskCreationOptions.RunContinuationsAsynchronously);Task<bool>? task=null;FlowDiagramImportWindow? window=null;
        var lnReadGate=new TaskCompletionSource<ServiceResult<LogicalNodeSnapshot[]>>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();
            var oldCrosses=map.Crosses.GetAll().Data!;client.Step=()=>Task.FromResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,oldCrosses.Length),"计算完成"));client.Read=()=>readGate.Task;
            var oldNodes=new LogicalNodeService(map.Repository).GetAll().Data!;client.JStep=_=>Task.FromResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,oldNodes.Length),"计算完成"));client.JRead=()=>lnReadGate.Task;
            task=j?view.CalculateLogicalNodesAsync():view.CalculateCrossesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>j?view.IsCalculatingLogicalNodes&&client.JCalculations==1:view.IsCalculatingCrosses&&client.Calculations==1);Assert.IsFalse(view.IsSaving);
            object? preview=null;MapAreaSelection? selection=null;var original=map.Snapshot();
            if(action.StartsWith("area",StringComparison.Ordinal))
            {
                Assert.IsTrue(view.BeginAreaMove());Assert.IsTrue(view.SelectMapAreaAsync(MapAreaMoveTests.Bounds,new(50,50)).GetAwaiter().GetResult());
                var area=(MapAreaMoveEditor)typeof(CoordinateMapView).GetField("areaMove",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;selection=area.Selection;
                if(action=="area-drop"){Assert.IsTrue(view.CompleteAreaMoveAsync(new(150,150)).GetAwaiter().GetResult());Assert.AreEqual(1,view.CrossCount);Assert.AreEqual(130.5m,new LogicalNodeService(map.Repository).GetAll().Data!.Single().X);}
            }
            else
            {
                Assert.IsTrue(view.BeginFlowDiagramImport());window=new FlowDiagramImportWindow(client,view){Owner=main};
                ((TextBox)window.FindName("SourceText")).Text=FlowDiagramTests.Example;((TextBox)window.FindName("XInput")).Text="1200.5";((TextBox)window.FindName("YInput")).Text="5000.5";
                Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());preview=typeof(CoordinateMapView).GetField("flowPreview",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view);Assert.IsNotNull(preview);Assert.AreEqual(original,map.Snapshot());
                if(action=="flow-import"){Assert.IsTrue(window.ImportAsync().GetAwaiter().GetResult());Assert.AreEqual(2,view.CrossCount);Assert.AreEqual(11,view.LogicalNodeCount);}
            }
            var message=view.OperationMessage;var beforeCompletion=map.Snapshot();readGate.SetResult(ServiceResult<CrossSnapshot[]>.Ok(oldCrosses,"旧的后台读取"));lnReadGate.SetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok(oldNodes,"旧的后台读取"));LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);Assert.IsTrue(task.GetAwaiter().GetResult());Assert.AreEqual(beforeCompletion,map.Snapshot());Assert.AreEqual(message,view.OperationMessage);
            if(action=="area-preview")
            {
                Assert.IsTrue(view.IsAreaMoveMode);var area=(MapAreaMoveEditor)typeof(CoordinateMapView).GetField("areaMove",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;Assert.AreSame(selection,area.Selection);Assert.AreEqual(original,map.Snapshot());
            }
            if(action=="flow-preview")
            {Assert.IsTrue(view.IsFlowDiagramImportActive);Assert.AreSame(preview,typeof(CoordinateMapView).GetField("flowPreview",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view));Assert.IsTrue(((Button)window!.FindName("ImportButton")).IsEnabled);Assert.AreEqual(FlowDiagramTests.Example,((TextBox)window.FindName("SourceText")).Text);}
            if(action=="area-drop"){Assert.AreEqual(1,view.CrossCount);Assert.AreEqual(130.5m,((LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!).Nodes.Single().X);}
            if(action=="flow-import"){Assert.AreEqual(2,view.CrossCount);Assert.AreEqual(11,view.LogicalNodeCount);}
        }
        finally{window?.Close();main.Close();readGate.TrySetResult(ServiceResult<CrossSnapshot[]>.Ok([],"完成"));lnReadGate.TrySetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([],"完成"));if(task is not null)LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);}
    }
}
