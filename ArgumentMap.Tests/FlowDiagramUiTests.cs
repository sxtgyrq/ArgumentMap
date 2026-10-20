using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Contracts;
using ArgumentMap.Client;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class FlowDiagramUiTests
{
    public TestContext TestContext { get; set; }=null!;
    private static MainWindow CreateMainWindow(HttpLineClient client)=>new(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
    private static FlowDiagramImportWindow Open(MainWindow main)
    {
        ((MenuItem)main.FindName("ImportFlowDiagramMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        var window=(FlowDiagramImportWindow)typeof(MainWindow).GetField("flowDiagramWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main)!;
        window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-10000;window.Top=-10000;return window;
    }
    private static void Press(Window window,Key key)
    {var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);}

    [STATestMethod]
    public void MenuPasteMapPickPreviewAndImport_UpdatesCachesAndCloses_SnapsAnchorToHalfInteger()
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var main=CreateMainWindow(new(http,new("owner","test")));
        try
        {
            main.Show();main.UpdateLayout();var map=(CoordinateMapView)main.FindName("MapView");var window=Open(main);((TextBox)window.FindName("SourceText")).Text=FlowDiagramTests.Example;
            Assert.IsTrue(map.IsFlowDiagramImportActive);Assert.IsFalse(map.NewLineAsync().GetAwaiter().GetResult());Assert.IsFalse(map.BeginAreaMove());
            Assert.IsFalse(((Button)window.FindName("ImportButton")).IsEnabled);Assert.IsFalse(window.ImportAsync().GetAwaiter().GetResult());Assert.AreEqual(0,stub.FlowImports);
            Assert.IsTrue(window.StartMapPick());Assert.IsFalse(window.IsVisible);Assert.IsTrue(map.IsPickingFlowDiagramOrigin);
            Assert.IsTrue(map.SelectFlowDiagramOrigin(new(1200.1,5000.9)));Assert.IsTrue(window.IsVisible);Assert.AreEqual("1200.5",((TextBox)window.FindName("XInput")).Text);Assert.AreEqual("5000.5",((TextBox)window.FindName("YInput")).Text);
            var before=stub.Repository.Transaction(s=>(s.Lines.Count,s.LogicalNodes.Count));Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());Assert.AreEqual(before,stub.Repository.Transaction(s=>(s.Lines.Count,s.LogicalNodes.Count)));
            Assert.IsTrue(((Button)window.FindName("ImportButton")).IsEnabled);
            ((Button)window.FindName("MapPreviewButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsFalse(window.IsVisible);Press(main,Key.Enter);Assert.IsTrue(window.IsVisible);
            Assert.IsTrue(window.ImportAsync().GetAwaiter().GetResult());Assert.IsFalse(map.IsFlowDiagramImportActive);Assert.IsFalse(window.IsVisible);
            Assert.AreEqual(3,map.LineCount);Assert.AreEqual(10,map.LogicalNodeCount);Assert.AreEqual(2,map.CrossCount);Assert.AreEqual(1,stub.FlowPreviews);Assert.AreEqual(1,stub.FlowImports);StringAssert.Contains(map.OperationMessage,"已导入");
            Assert.IsTrue(map.BeginAreaMove());StringAssert.Contains(map.OperationMessage,"拖出矩形");Press(main,Key.Escape);Assert.IsFalse(map.IsAreaMoveMode);
        }
        finally{main.Close();}
    }

    [STATestMethod]
    public void InvalidTextAndDestinationShowReasons_InputChangesInvalidatePreview_EscReturnsFromMapThenCancels()
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var main=CreateMainWindow(new(http,new("owner","test")));
        try
        {
            main.Show();main.UpdateLayout();var map=(CoordinateMapView)main.FindName("MapView");var window=Open(main);var source=(TextBox)window.FindName("SourceText");source.Text="invalid";Assert.IsFalse(window.StartMapPick());Assert.IsFalse(window.PreviewAsync().GetAwaiter().GetResult());StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"JSON");
            source.Text=FlowDiagramTests.Example;Assert.IsTrue(window.StartMapPick());Press(main,Key.Escape);Assert.IsTrue(window.IsVisible);Assert.IsFalse(map.IsPickingFlowDiagramOrigin);
            ((TextBox)window.FindName("XInput")).Text="0.5";((TextBox)window.FindName("YInput")).Text="0.5";Assert.IsFalse(window.PreviewAsync().GetAwaiter().GetResult());StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"LN S");
            ((TextBox)window.FindName("XInput")).Text="1200.5";((TextBox)window.FindName("YInput")).Text="5000.5";Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());source.Text+="\n";Assert.IsFalse(((Button)window.FindName("ImportButton")).IsEnabled);Assert.IsFalse(window.ImportAsync().GetAwaiter().GetResult());
            Press(window,Key.Escape);Assert.IsFalse(map.IsFlowDiagramImportActive);Assert.AreEqual(0,stub.FlowImports);Assert.AreEqual(0,stub.Repository.Transaction(s=>s.Lines.Count));
        }
        finally{main.Close();}
    }

    [STATestMethod]
    public void BusyImportPreventsCloseCancelOtherCommandsAndDuplicateSubmission_FailureCanRetryAfterPreview()
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var main=CreateMainWindow(new(http,new("owner","test")));
        try
        {
            main.Show();main.UpdateLayout();var map=(CoordinateMapView)main.FindName("MapView");var window=Open(main);((TextBox)window.FindName("SourceText")).Text=FlowDiagramTests.Example;Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());
            stub.FlowGate=new();var task=window.ImportAsync();Assert.IsTrue(map.IsSaving);window.Close();Assert.IsTrue(window.IsVisible);Press(main,Key.Escape);Assert.IsTrue(map.IsFlowDiagramImportActive);
            Assert.IsFalse(window.ImportAsync().GetAwaiter().GetResult());Assert.IsFalse(map.CalculateCrossesAsync().GetAwaiter().GetResult());Assert.AreEqual(1,stub.FlowImports);
            stub.FlowGate.SetResult(ServiceResult<FlowDiagramResult>.Fail("DatabaseError","写入失败，整次回滚"));LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);Assert.IsFalse(task.GetAwaiter().GetResult());Assert.IsFalse(map.IsSaving);Assert.IsFalse(((Button)window.FindName("ImportButton")).IsEnabled);
            stub.FlowGate=null;Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());Assert.IsTrue(window.ImportAsync().GetAwaiter().GetResult());Assert.AreEqual(2,stub.FlowImports);
        }
        finally{main.Close();}
    }

    [STATestMethod,TestCategory("Rendering")]
    public void ImportWindowAndPreviewRender_ExampleCanBeLoadedWithoutDatabaseWrites()
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var main=CreateMainWindow(new(http,new("owner","test")));
        try
        {
            main.Show();main.UpdateLayout();var window=Open(main);((Button)window.FindName("ExampleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());
            Save(window,"flow-diagram-import.png");Save(main,"flow-diagram-preview.png");Assert.AreEqual(0,stub.FlowImports);Assert.AreEqual(0,stub.Repository.Transaction(s=>s.LogicalNodes.Count));window.Close();
        }
        finally{main.Close();}
    }
    private void Save(Window window,string name)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);TestContext.AddResultFile(path);
    }
}
