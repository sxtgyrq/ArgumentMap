using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.IO;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LinePasteUiTests
{
    public TestContext TestContext {get;set;}=null!;
    private static LineEditor Editor(CoordinateMapView view)=>(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static MainWindow Window(ILineClient client)=>new(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
    [STATestMethod]
    public void ActualShortcuts_CopyHighlightedRoad_PreviewOnlyUntilMousePoint_RetryCancelAndRepeatPaste()
    {
        var map=new TestMap();var source=map.Add(100,100,130,100,140,110);source=map.Lines.SaveProperties(new(source.Code,"owner",source.Version,new(){RoadName="复制",Speed=357,PassInOpposeDirection=1})).Data!;
        var client=new LogicalNodeMoveUiTests.Client(map);var window=Window(client);
        try
        {
            window.Show();var view=(CoordinateMapView)window.FindName("MapView");Assert.IsTrue(view.RefreshLinesAsync().GetAwaiter().GetResult());Editor(view).SelectLine(source.Code);
            string? clipboard=null;window.WriteLn4Clipboard=text=>clipboard=text;window.ReadLn4Clipboard=()=>clipboard;
            Assert.IsTrue(window.HandleLn4ClipboardShortcut(Key.C,ModifierKeys.Control,false,view));Assert.IsNotNull(LineClipboard.Decode(clipboard));var before=map.Snapshot();
            Assert.IsTrue(window.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view));Assert.IsTrue(view.IsPastingLine);Assert.AreEqual(0,client.LinePastes);Assert.AreEqual(before,map.Snapshot());
            CollectionAssert.AreEqual(new[]{new GridPoint(300,300),new GridPoint(330,300),new GridPoint(340,310)},view.LinePasteNodes(new(300.2,300.1))!);
            Assert.IsFalse(view.PlacePastedLineAsync(new(100,100)).GetAwaiter().GetResult());Assert.IsTrue(view.IsPastingLine);Assert.AreEqual(before,map.Snapshot());
            Assert.IsTrue(view.PlacePastedLineAsync(new(300.2,300.1)).GetAwaiter().GetResult());Assert.IsFalse(view.IsPastingLine);var copy=map.Lines.GetAll().Data!.Single(l=>l.Code!=source.Code);Assert.AreEqual(source.Properties,copy.Properties);Assert.AreEqual(copy.Code,Editor(view).SelectedCode);
            window.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view);Assert.IsTrue(view.IsPastingLine);var after=map.Snapshot();view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsFalse(view.IsPastingLine);Assert.AreEqual(after,map.Snapshot());
            window.HandleLn4ClipboardShortcut(Key.V,ModifierKeys.Control,false,view);Assert.IsTrue(view.PlacePastedLineAsync(new(400,400)).GetAwaiter().GetResult());Assert.AreEqual(3,map.Lines.GetAll().Data!.Length);
            ((MenuItem)window.FindName("PasteSelectionMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.IsTrue(view.IsPastingLine);view.CancelEditing();Assert.IsFalse(view.IsPastingLine);
        }
        finally{window.Close();}
    }
    [STATestMethod]
    public void PendingAndSavingPasteBlocksOtherEdits_ProtectsDraftAndAllowsRetryAfterServerFailure()
    {
        var map=new TestMap();var source=map.Add(100,100,140,100);var client=new LogicalNodeMoveUiTests.Client(map);var view=new CoordinateMapView();view.ConfigureClient(client);view.RefreshLinesAsync().GetAwaiter().GetResult();Editor(view).SelectLine(source.Code);Assert.IsTrue(view.TryCopySelection(out var text));Assert.IsTrue(view.BeginPasteSelection(text));var before=map.Snapshot();
        foreach(var key in new[]{Key.C,Key.F6,Key.F7,Key.F9,Key.R,Key.D,Key.B,Key.J,Key.K})Assert.IsTrue(view.HandleEditorKeyAsync(key).GetAwaiter().GetResult());
        Assert.IsTrue(view.IsPastingLine);Assert.IsFalse(view.BeginFlowDiagramImport());Assert.IsFalse(view.BeginLogicalNodePlacement(4));Assert.AreEqual(before,map.Snapshot());
        client.PasteGate=new();var task=view.PlacePastedLineAsync(new(300,300));Assert.IsTrue(view.IsSaving);Assert.IsFalse(view.PlacePastedLineAsync(new(400,400)).GetAwaiter().GetResult());view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsTrue(view.IsPastingLine);Assert.IsFalse(view.TryCopySelection(out _));
        client.PasteGate.SetResult(ServiceResult<LinePasteResult>.Fail("DatabaseError","数据库写入失败"));LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);Assert.IsFalse(task.GetAwaiter().GetResult());Assert.IsFalse(view.IsSaving);Assert.IsTrue(view.IsPastingLine);Assert.AreEqual(before,map.Snapshot());
        client.PasteGate=null;Assert.IsTrue(view.PlacePastedLineAsync(new(300,300)).GetAwaiter().GetResult());Assert.AreEqual(2,client.LinePastes);
        Assert.IsTrue(view.NewLineAsync().GetAwaiter().GetResult());Assert.IsTrue(view.BeginDrawingAsync().GetAwaiter().GetResult());Editor(view).AddPoint(new(450,450));Assert.IsFalse(view.BeginPasteSelection(text));Assert.AreEqual(1,Editor(view).DraftNodes.Count);
    }
    [TestMethod]
    public void ClipboardRoundTripRejectsMalformedOversizedUnknownOrForgedShapeMetadata()
    {
        var source=new TestMap().Add(100,100,140,100);var text=LineClipboard.Encode(LinePasteTests.Copy(source));var copy=LineClipboard.Decode(text);Assert.IsNotNull(copy);Assert.AreEqual(source.Properties,copy.Properties);CollectionAssert.AreEqual(source.Nodes,copy.Nodes);
        foreach(var invalid in new[]{"", "{}","[]",text.Replace("argumentmap.road/1","unknown"),text.Replace("\"Version\":1","\"Version\":1,\"Version\":1"),text.Replace("\"Version\":1","\"Owner\":\"admin\",\"Version\":1"),text.Replace("\"Speed\":100","\"Speed\":0"),text.Replace("\"X\":100","\"X\":-1"),text.Replace("\"Order\":1","\"Order\":0"),new string(' ',8388609)})Assert.IsNull(LineClipboard.Decode(invalid));
        Assert.IsNull(Ln4Clipboard.Decode(text));Assert.IsNull(LineClipboard.Decode(Ln4Clipboard.Encode(new(){Value="True"})));
    }
    [STATestMethod,TestCategory("Rendering")]
    public void RoadPastePreviewRendersAtMouseTargetWithoutSaving()
    {
        var map=new TestMap();var source=map.Add(249950,250000,249980,250000,249990,250010,249990,250040);var client=new LogicalNodeMoveUiTests.Client(map);var window=Window(client);
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();Editor(view).SelectLine(source.Code);Assert.IsTrue(view.TryCopySelection(out var text));Assert.IsTrue(view.BeginPasteSelection(text));
            var at=(Point)typeof(CoordinateMapView).GetMethod("ToScreen",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,[new Point(250020,249980)])!;
            typeof(CoordinateMapView).GetField("lastPointer",BindingFlags.NonPublic|BindingFlags.Instance)!.SetValue(view,at);typeof(CoordinateMapView).GetMethod("Refresh",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(view,null);window.UpdateLayout();
            var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","road-paste-preview.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
            Assert.IsTrue(view.IsPastingLine);Assert.AreEqual(0,client.LinePastes);
        }
        finally{window.Close();}
    }
}
