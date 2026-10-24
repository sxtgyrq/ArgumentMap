using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class CrossVisibilityTests
{
    private static readonly CrossState[] States=[CrossState.Connected,CrossState.Road1ToRoad2,CrossState.Road2ToRoad1,CrossState.Disconnected,CrossState.Unset];
    private static CrossSnapshot Cross(CrossState state,int index=0,bool samePosition=false)=>new(
        CrossKey.Create("AAAAAAAAAA",new string((char)('B'+index),10),new(500000+(samePosition?0:(index-2)*60),500000)),0,0,1,1,state,0,.5,.5);
    private static CrossEditor Editor(CoordinateMapView view)=>(CrossEditor)typeof(CoordinateMapView).GetField("crossEditor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static byte[] Pixels(CoordinateMapView view)
    {
        view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(view);
        var pixels=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(pixels,bitmap.PixelWidth*4,0);return pixels;
    }
    private static CoordinateMapView View(CrossSnapshot[] crosses)
    {
        var view=new CoordinateMapView();view.Measure(new Size(1000,600));view.Arrange(new Rect(0,0,1000,600));view.UpdateLayout();
        view.ResetView();view.ZoomAtCenter(4);Editor(view).ReplaceCrosses(crosses);return view;
    }
    [TestMethod]
    public async Task LegacyHalfCrossesAreExcludedFromCacheRefreshCalculationAndSelection()
    {
        var whole=Cross(CrossState.Connected,0,true);
        var half=whole with{Key=whole.Key with{Position=new(500001,500001)}};
        var client=new Client([half,whole]);var editor=new CrossEditor();editor.ConfigureClient(client);editor.ReplaceCrosses([half,whole]);
        CollectionAssert.AreEqual(new[]{whole},editor.Crosses.ToArray());StringAssert.Contains(editor.Message,"非整数 Cross");
        editor.ToggleSelection();editor.SelectClosest(new(250000.5,250000.5),.1);Assert.IsNull(editor.Selected);
        Assert.IsFalse(await editor.SetStateAsync(CrossState.Disconnected));Assert.AreEqual(0,client.Saves);
        Assert.IsTrue(await editor.RefreshAsync());CollectionAssert.AreEqual(new[]{whole},editor.Crosses.ToArray());
        Assert.IsTrue(await editor.CalculateAsync(()=>{}));CollectionAssert.AreEqual(new[]{whole},editor.Crosses.ToArray());
        editor.SelectClosest(new(250000,250000),.1);Assert.AreEqual(whole,editor.Selected);Assert.AreEqual(0,client.Saves);
    }
    [STATestMethod,TestCategory("Rendering")]
    public void LegacyHalfCrossesCreateNoMapOrImportPreviewMarkers()
    {
        var whole=Cross(CrossState.Connected,0,true);
        var half=whole with{Key=whole.Key with{Position=new(500001,500001)}};
        var view=new CoordinateMapView();var window=new Window{Content=view,Width=1000,Height=600,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        byte[] Capture()
        {
            // A live presentation source and completed render pass prevent the
            // bitmap from reusing the preceding frame of this retained visual.
            view.InvalidateVisual();view.Dispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ContextIdle);
            return Pixels(view);
        }
        try
        {
            window.Show();window.UpdateLayout();view.ResetView();view.ZoomAtCenter(4);
            var at=(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[new Point(whole.Key.Position.X,whole.Key.Position.Y)])!;
            Assert.IsTrue(whole.Key.IsValid);Assert.IsTrue(new Rect(0,0,view.ActualWidth,view.ActualHeight).Contains(at));
            var empty=Capture();Editor(view).ReplaceCrosses([half]);CollectionAssert.AreEqual(empty,Capture());
            Assert.IsTrue(view.BeginFlowDiagramImport());view.ShowFlowDiagramPreview(new([],[],[]));var emptyPreview=Capture();
            view.ShowFlowDiagramPreview(new([],[],[half]));CollectionAssert.AreEqual(emptyPreview,Capture());
            view.ShowFlowDiagramPreview(new([],[],[whole]));Assert.IsFalse(emptyPreview.SequenceEqual(Capture()),"An integer Cross must produce a visible preview marker.");
        }
        finally{window.Close();}
    }
    [TestMethod,DataRow(CrossState.Connected),DataRow(CrossState.Road1ToRoad2),DataRow(CrossState.Road2ToRoad1),DataRow(CrossState.Disconnected),DataRow(CrossState.Unset)]
    public void HideClearsSelectedRelation_AndVisibleFilterPreventsReselectionWithoutRemovingData(CrossState state)
    {
        var cross=Cross(state);var editor=new CrossEditor();editor.ReplaceCrosses([cross]);editor.ToggleSelection();
        var at=new Point(cross.Key.Position.X,cross.Key.Position.Y);editor.SelectClosest(at,.1);Assert.AreEqual(cross,editor.Selected);
        editor.SetVisibility(state is CrossState.Disconnected or CrossState.Unset,state!=CrossState.Disconnected,state!=CrossState.Unset);
        Assert.IsNull(editor.SelectedKey);Assert.IsNull(editor.Selected);Assert.IsFalse(editor.IsVisible(state));
        editor.SelectClosest(at,.1);Assert.IsNull(editor.SelectedKey);CollectionAssert.AreEqual(new[]{cross},editor.Crosses.ToArray());
        editor.SetVisibility(true,true,true);editor.SelectClosest(at,.1);Assert.AreEqual(cross,editor.Selected);
    }
    [TestMethod]
    public void NearestAndTabSelectionSkipHiddenTypes_AndCannotPreserveHiddenPairAtSamePosition()
    {
        var crosses=States.Select((s,i)=>Cross(s,i,true)).ToArray();var editor=new CrossEditor();editor.ReplaceCrosses(crosses);editor.ToggleSelection();
        var at=new Point(250000,250000);editor.SelectClosest(at,.1);Assert.IsNotNull(editor.Selected);
        editor.SetVisibility(false,true,true);editor.SelectClosest(at,.1);
        var seen=new HashSet<CrossState>();for(var i=0;i<8;i++){seen.Add(editor.Selected!.State);editor.CycleSelection(at,.1);}
        CollectionAssert.AreEquivalent(new[]{CrossState.Disconnected,CrossState.Unset},seen.ToArray());
        editor.SetVisibility(false,false,false);editor.SelectClosest(at,.1);editor.CycleSelection(at,.1);Assert.IsNull(editor.SelectedKey);Assert.AreEqual(5,editor.Crosses.Count);
        var hidden=Cross(CrossState.Connected,0,true);var visible=Cross(CrossState.Unset,1,true) with{Key=CrossKey.Create("AAAAAAAAAA","CCCCCCCCCC",new(500002,500000))};
        editor.ReplaceCrosses([hidden,visible]);editor.SetVisibility(false,true,true);editor.SelectClosest(at,2);Assert.AreEqual(visible.Key,editor.SelectedKey);
    }
    [TestMethod,DataRow(true),DataRow(false)]
    public async Task EditIntoHiddenTypeClearsSelection_RefreshAndKRetainFiltersAndSavedState(bool reload)
    {
        var cross=Cross(CrossState.Unset);var client=new Client([cross]);var editor=new CrossEditor();editor.ConfigureClient(client);
        editor.SetVisibility(true,false,true);Assert.IsTrue(await editor.RefreshAsync());editor.ToggleSelection();editor.SelectClosest(new(cross.Key.Position.X,cross.Key.Position.Y),.1);
        Assert.IsTrue(await editor.SetStateAsync(CrossState.Disconnected,reload));Assert.IsNull(editor.SelectedKey);Assert.IsNull(editor.Selected);
        Assert.AreEqual(CrossState.Disconnected,editor.Crosses.Single().State);Assert.AreEqual(1,client.Saves);
        Assert.IsFalse(await editor.SetStateAsync(CrossState.Connected));Assert.AreEqual(1,client.Saves);
        Assert.IsTrue(await editor.RefreshAsync());Assert.IsTrue(await editor.CalculateAsync(()=>{}));Assert.IsFalse(editor.ShowDisconnected);
        editor.SelectClosest(new(cross.Key.Position.X,cross.Key.Position.Y),.1);Assert.IsNull(editor.Selected);
        editor.SetVisibility(true,true,true);editor.SelectClosest(new(cross.Key.Position.X,cross.Key.Position.Y),.1);Assert.AreEqual(CrossState.Disconnected,editor.Selected!.State);
    }
    [STATestMethod,TestCategory("Rendering"),DataRow(false,true,true),DataRow(true,false,true),DataRow(true,true,false),DataRow(false,false,false)]
    public void RenderingUsesSameThreeGroups_AndHideRestorePreservesAllCrossRecords(bool connected,bool disconnected,bool unset)
    {
        var crosses=States.Select((s,i)=>Cross(s,i)).ToArray();var view=View(crosses);var shown=Pixels(view);
        view.SetCrossVisibility(false,false,false);var hidden=Pixels(view);view.SetCrossVisibility(connected,disconnected,unset);var filtered=Pixels(view);
        for(var i=0;i<crosses.Length;i++)
        {
            var at=(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[new Point(crosses[i].Key.Position.X,crosses[i].Key.Position.Y)])!;
            var expect=i<3?connected:i==3?disconnected:unset;var changed=0;
            for(var y=(int)at.Y-8;y<at.Y+8;y++)for(var x=(int)at.X-8;x<at.X+8;x++)
            {
                var offset=(y*1000+x)*4;
                CollectionAssert.AreEqual((expect?shown:hidden).AsSpan(offset,4).ToArray(),filtered.AsSpan(offset,4).ToArray());
                if(!shown.AsSpan(offset,4).SequenceEqual(hidden.AsSpan(offset,4)))changed++;
            }
            Assert.IsTrue(changed>10,"The fixture must contain a rendered marker for each Cross type.");
        }
        CollectionAssert.AreEqual(crosses,Editor(view).Crosses.ToArray());view.SetCrossVisibility(true,true,true);CollectionAssert.AreEqual(shown,Pixels(view));
    }
    [STATestMethod,DataRow(Key.D8),DataRow(Key.NumPad8),DataRow(Key.D9),DataRow(Key.NumPad9),DataRow(Key.D0),DataRow(Key.NumPad0)]
    public void WindowRoutesPreviouslyUnusedKeys_MenuToolbarSync_AndFilteringNeverWritesMap(Key key)
    {
        using var stub=new DirectLogicalNodeHttpStub();LogicalNodeMySqlTests.Add(stub.Repository,249980,250000,250020,250000);LogicalNodeMySqlTests.Add(stub.Repository,250000,249980,250000,250020);
        var crossService=new CrossService(stub.Repository);while(crossService.Calculate().Data!.CanContinue){}
        using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var window=new MainWindow(new HttpLineClient(http,new("owner","test"))){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();var load=window.InitializeLinesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>load.IsCompleted);Assert.IsTrue(load.Result);
            var view=(CoordinateMapView)window.FindName("MapView");var before=LogicalNodeMySqlTests.Snapshot(stub.Repository);Assert.IsTrue(CoordinateMapView.IsEditorKey(key));
            var prefix=key is Key.D8 or Key.NumPad8?"Connected":key is Key.D9 or Key.NumPad9?"Disconnected":"Unset";
            var toggle=(CheckBox)window.FindName(prefix+"CrossesToggle");var menu=(MenuItem)window.FindName(prefix+"CrossesMenu");
            Assert.AreEqual(true,toggle.IsChecked);Assert.IsTrue(menu.IsChecked);
            var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);
            Assert.AreEqual(false,toggle.IsChecked);Assert.IsFalse(menu.IsChecked);
            menu.IsChecked=true;menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.AreEqual(true,toggle.IsChecked);
            toggle.IsChecked=false;Assert.IsFalse(menu.IsChecked);
            view.SetCrossVisibility(false,false,false);foreach(var p in new[]{"Connected","Disconnected","Unset"})
            {Assert.AreEqual(false,((CheckBox)window.FindName(p+"CrossesToggle")).IsChecked);Assert.IsFalse(((MenuItem)window.FindName(p+"CrossesMenu")).IsChecked);}
            view.RefreshCrossesAsync().GetAwaiter().GetResult();view.ResetView();view.FitView();Assert.AreEqual(1,view.CrossCount);Assert.IsFalse(view.ShowConnectedCrosses||view.ShowDisconnectedCrosses||view.ShowUnsetCrosses);
            view.SetCrossVisibility(true,true,true);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(stub.Repository));Assert.AreEqual(0,stub.Creates+stub.CrossCalculations+stub.LogicalNodeCalculations+stub.AreaMoves+stub.AreaDeletes+stub.FlowImports);
        }
        finally{window.Close();}
    }
    [STATestMethod]
    public void FiltersWorkDuringImportAndAreaMove_WithoutCancellingOrLosingSelectionData()
    {
        var crosses=States.Select((s,i)=>Cross(s,i)).ToArray();var view=View([]);var snapshot=new MapAreaSnapshot([],[],crosses);
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};view.ConfigureClient(new HttpLineClient(http,new("owner","test")));
        view.FlowDiagramCancelRequested+=(_,_)=>view.EndFlowDiagramImport("");
        Assert.IsTrue(view.BeginFlowDiagramImport());view.ShowFlowDiagramPreview(snapshot);var shown=Pixels(view);
        foreach(var key in new[]{Key.D8,Key.NumPad9,Key.D0})Assert.IsTrue(view.HandleEditorKeyAsync(key).GetAwaiter().GetResult());
        var hidden=Pixels(view);Assert.IsFalse(shown.SequenceEqual(hidden));Assert.IsTrue(view.IsFlowDiagramImportActive);
        Assert.AreSame(snapshot,typeof(CoordinateMapView).GetField("flowPreview",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view));
        view.SetCrossVisibility(true,true,true);CollectionAssert.AreEqual(shown,Pixels(view));view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsTrue(view.BeginAreaMove());
        Assert.IsTrue(view.HandleEditorKeyAsync(Key.NumPad8).GetAwaiter().GetResult());Assert.IsFalse(view.ShowConnectedCrosses);Assert.IsTrue(view.IsAreaMoveMode);
    }
    [STATestMethod]
    public void BoxSelectionKeepsHiddenCrossRelations_AndViewFiltersDoNotChangeDraft()
    {
        using var stub=new DirectLogicalNodeHttpStub();LogicalNodeMySqlTests.Add(stub.Repository,249980,250000,250020,250000);LogicalNodeMySqlTests.Add(stub.Repository,250000,249980,250000,250020);
        var crosses=new CrossService(stub.Repository);while(crosses.Calculate().Data!.CanContinue){}
        using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));var view=View([]);view.ConfigureClient(client);
        view.RefreshLinesAsync().GetAwaiter().GetResult();view.RefreshCrossesAsync().GetAwaiter().GetResult();view.SetCrossVisibility(false,false,false);
        var before=LogicalNodeMySqlTests.Snapshot(stub.Repository);Assert.IsTrue(view.BeginAreaMove());Assert.IsTrue(view.SelectMapAreaAsync(new(249970,249970,250030,250030),new(250000,250000)).GetAwaiter().GetResult());
        var area=(MapAreaMoveEditor)typeof(CoordinateMapView).GetField("areaMove",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        var selection=area.Selection!;Assert.AreEqual(2,selection.Lines.Length);Assert.AreEqual(1,selection.Crosses.Length);
        view.HandleEditorKeyAsync(Key.D8).GetAwaiter().GetResult();Assert.AreSame(selection,area.Selection);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(stub.Repository));
        view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsFalse(view.IsAreaMoveMode);
        var draft=new TestMap();var editor=(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        editor.ConfigureClient(new NodeEditUiTests.Client(draft));Assert.IsTrue(editor.NewLineAsync().GetAwaiter().GetResult());Assert.IsTrue(editor.BeginDrawingAsync().GetAwaiter().GetResult());
        editor.AddPoint(new(100,100));editor.AddPoint(new(120,100));var nodes=editor.DraftNodes.ToArray();var code=editor.DraftCode;
        foreach(var key in new[]{Key.D8,Key.D9,Key.D0})Assert.IsTrue(view.HandleEditorKeyAsync(key).GetAwaiter().GetResult());
        Assert.AreEqual(code,editor.DraftCode);CollectionAssert.AreEqual(nodes,editor.DraftNodes.ToArray());Assert.AreEqual(LineEditorMode.Drawing,editor.Mode);
    }
    private sealed class Client(CrossSnapshot[] values):ICrossClient
    {
        private CrossSnapshot[] crosses=values;internal int Saves;
        public Task<ServiceResult<CrossSnapshot[]>> GetCrossesAsync()=>Task.FromResult(ServiceResult<CrossSnapshot[]>.Ok(crosses,""));
        public Task<ServiceResult<CrossCalculationProgress>> CalculateCrossAsync()=>Task.FromResult(ServiceResult<CrossCalculationProgress>.Ok(new(false,0,crosses.Length),""));
        public Task<ServiceResult<CrossSnapshot>> SetCrossStateAsync(CrossKey key,long version,CrossState state)
        {
            Saves++;var old=crosses.Single(c=>c.Key==key);var updated=old with{State=state,Version=old.Version+1};crosses=crosses.Select(c=>c.Key==key?updated:c).ToArray();
            return Task.FromResult(ServiceResult<CrossSnapshot>.Ok(updated,""));
        }
    }
}
