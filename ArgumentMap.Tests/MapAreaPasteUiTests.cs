using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class MapAreaPasteUiTests
{
    public TestContext TestContext{get;set;}=null!;
    private static MainWindow Window(HttpLineClient client)=>new(client){Width=1100,Height=800,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
    private static void Select(CoordinateMapView view)
    {Assert.IsTrue(view.BeginAreaMove());Assert.IsTrue(view.SelectMapAreaAsync(MapAreaPasteTests.Bounds,new(250,10)).GetAwaiter().GetResult());}

    [STATestMethod,DataRow("key"),DataRow("menu")]
    public void CopyPasteUsesF7Selection_MouseChoosesPoint_NewObjectsAndRepeatedPasteDoNotMoveOriginals(string entry)
    {
        using var handler=new DirectLogicalNodeHttpStub();MapAreaPasteTests.Fixture(handler.Repository);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");Select(view);string? clipboard=null;window.WriteLn4Clipboard=t=>clipboard=t;window.ReadLn4Clipboard=()=>clipboard;
            var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            if(entry=="key")Assert.IsTrue(window.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,view));else ((MenuItem)window.FindName("CopySelectionMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Assert.IsNotNull(MapAreaClipboard.Decode(clipboard));Assert.IsTrue(view.IsAreaMoveMode);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            Assert.IsFalse(window.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.None,false,view));Assert.IsFalse(window.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,new TextBox()));
            Assert.IsTrue(window.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view));Assert.IsTrue(view.IsPastingArea);Assert.IsFalse(view.IsAreaMoveMode);Assert.AreEqual(0,handler.AreaPastes);
            Assert.IsFalse(view.NewLineAsync().GetAwaiter().GetResult());Assert.IsFalse(view.BeginFlowDiagramImport());Assert.IsFalse(view.BeginAreaMove());
            Assert.IsTrue(view.PlacePastedAreaAsync(new(310.4,390.3)).GetAwaiter().GetResult());Assert.IsFalse(view.IsPastingArea);Assert.AreEqual(1,handler.AreaPastes);Assert.AreEqual(0,handler.AreaMoves);Assert.AreEqual(0,handler.AreaDeletes);
            Assert.AreEqual(4,view.LineCount);Assert.AreEqual(12,view.LogicalNodeCount);Assert.AreEqual(2,view.CrossCount);
            var nodes=new LogicalNodeService(handler.Repository).GetAll().Data!;Assert.AreEqual(350.5m,nodes.Single(n=>n.Properties.Value=="PEOPLE1").X);Assert.AreEqual(350.5m,nodes.Single(n=>n.Properties.Value=="PEOPLE1").Y);Assert.AreEqual(50.5m,nodes.Single(n=>n.Properties.Value=="PEOPLE").X);
            window.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view);Assert.IsTrue(view.PlacePastedAreaAsync(new(610,690)).GetAwaiter().GetResult());Assert.AreEqual(2,handler.AreaPastes);
            Assert.AreEqual("P2",new LogicalNodeService(handler.Repository).GetAll().Data!.Single(n=>n.Properties.Value=="True"&&n.Y>600).Properties.LogicLanguage);
        }
        finally{window.Close();}
    }

    [STATestMethod,TestCategory("Rendering")]
    public void FailedDropRetainsPreview_EscCancels_AndBusyPasteCannotDuplicateOrDelete()
    {
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        using var handler=new DirectLogicalNodeHttpStub();MapAreaPasteTests.Fixture(handler.Repository);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");
            var initialize=window.InitializeLinesAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>initialize.IsCompleted);Assert.IsTrue(initialize.GetAwaiter().GetResult());
            Assert.AreEqual(2,view.LineCount);Assert.AreEqual(6,view.LogicalNodeCount);Select(view);Assert.IsTrue(view.TryCopySelection(out var text));var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);Assert.IsTrue(view.BeginPasteSelection(text));
            Assert.IsFalse(view.PlacePastedAreaAsync(new(10,90)).GetAwaiter().GetResult());Assert.IsTrue(view.IsPastingArea);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            view.ResetView();
            var viewport=typeof(CoordinateMapView).GetField("viewport",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
            viewport.GetType().GetMethod("FitBounds",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)!.Invoke(viewport,[new Rect(0,0,650,500)]);
            var screen=(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,[new Point(310,390)])!;
            typeof(CoordinateMapView).GetField("lastPointer",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(view,screen);
            typeof(CoordinateMapView).GetMethod("Refresh",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(view,null);window.UpdateLayout();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(()=>{},System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            Assert.IsNotNull(view.PointerPosition);Assert.IsTrue(view.IsPastingArea);
            var visual=new DrawingVisual();using(var dc=visual.RenderOpen())dc.DrawRectangle(new VisualBrush(view),null,new Rect(0,0,view.ActualWidth,view.ActualHeight));
            var bitmap=new RenderTargetBitmap((int)view.ActualWidth,(int)view.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","area-paste-preview.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);TestContext.AddResultFile(path);
            Assert.IsTrue(view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult());Assert.IsFalse(view.IsPastingArea);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            Assert.IsTrue(view.BeginPasteSelection(text));handler.AreaPasteGate=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=view.PlacePastedAreaAsync(new(310,390));LogicalNodeWindowEntryTests.DrainUntil(()=>handler.AreaPastes==2);Assert.IsTrue(view.IsSaving);
            Assert.IsFalse(view.PlacePastedAreaAsync(new(610,690)).GetAwaiter().GetResult());view.HandleEditorKeyAsync(Key.R).GetAwaiter().GetResult();view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsTrue(view.IsPastingArea);Assert.AreEqual(0,handler.AreaDeletes);
            handler.AreaPasteGate.SetResult(ServiceResult<MapAreaPasteResult>.Fail("TestFailure","请更换位置重试"));LogicalNodeWindowEntryTests.DrainUntil(()=>pending.IsCompleted);Assert.IsFalse(pending.GetAwaiter().GetResult());Assert.IsTrue(view.IsPastingArea);
            handler.AreaPasteGate=null;Assert.IsTrue(view.PlacePastedAreaAsync(new(310,390)).GetAwaiter().GetResult());
        }
        finally{window.Close();SynchronizationContext.SetSynchronizationContext(previous);}
    }

    [STATestMethod]
    public void InvalidClipboardAndStartLeaveSelectionAndExistingClipboardUntouched()
    {
        using var handler=new DirectLogicalNodeHttpStub();MapAreaPasteTests.Fixture(handler.Repository);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new(http,new("owner","test")));
        try
        {
            window.Show();var view=(CoordinateMapView)window.FindName("MapView");Select(view);Assert.IsTrue(view.TryCopySelection(out var text));Assert.IsNotNull(MapAreaClipboard.Decode(text));
            foreach(var invalid in new[]{"{}",text.Replace("argumentmap.area/1","argumentmap.area/2"),text.Replace("\"format\":","\"extra\":1,\"format\":"),text.Replace("\"format\":","\"format\":\"duplicate\",\"format\":")})Assert.IsNull(MapAreaClipboard.Decode(invalid));
            Assert.IsTrue(view.IsAreaMoveMode);view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsTrue(new LogicalNodeService(handler.Repository).Create(new("owner",800.5m,800.5m,18,new())).Success);
            Assert.IsTrue(view.BeginAreaMove());Assert.IsTrue(view.SelectMapAreaAsync(new(780,780,820,820),new()).GetAwaiter().GetResult());var clipboard=text;window.WriteLn4Clipboard=t=>clipboard=t;
            window.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,view);Assert.AreEqual(text,clipboard);Assert.IsTrue(view.IsAreaMoveMode);StringAssert.Contains(view.OperationMessage,"LN18");
        }
        finally{window.Close();}
    }
}
