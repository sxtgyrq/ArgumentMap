using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
public sealed class LogicalNodeLocationLinkTests
{
    public TestContext TestContext {get;set;}=null!;
    private static LogicalNodeWindow? Dialog(MainWindow window)=>(LogicalNodeWindow?)typeof(MainWindow)
        .GetField("logicalNodeWindow",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window);
    private static LogicalNodeEditor Editor(CoordinateMapView view)=>(LogicalNodeEditor)typeof(CoordinateMapView)
        .GetField("logicalNodes",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
    private static void Save(LogicalNodeWindow dialog)=>((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    [STATestMethod,DataRow(12),DataRow(6),TestCategory("Rendering")]
    public void DuplicateSave_CoordinateIsBlueUnderlined_ClickCentersAndSelectsTarget_AndEPreservesDraft(int size)
    {
        using var handler=new DirectLogicalNodeHttpStub();
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var client=new HttpLineClient(http,new("owner","test"));var service=new LogicalNodeService(handler.Repository);
        var target=service.Create(new("owner",1224.5m,484502.5m,size,new(){Value="S",LogicLanguage="S"})).Data!;
        var source=service.Create(new("owner",1326.5m,484492.5m,size,new(){Value="原值",LogicLanguage="T"})).Data!;
        var window=new MainWindow(client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");
            view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Editor(view).Select(source.Code);view.RequestLogicalNodeEdit();
            var dialog=Dialog(window);Assert.IsNotNull(dialog);
            ((TextBox)dialog.FindName("ValueInput")).Text="S";((TextBox)dialog.FindName("InfoInput")).Text="尚未保存的说明";
            if(size==6)((TextBox)dialog.FindName("LogicInput")).Text="S";
            var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);Save(dialog);
            var status=(TextBlock)dialog.FindName("Status");
            LogicalNodeWindowEntryTests.DrainUntil(()=>status.Inlines.OfType<Hyperlink>().Any()&&((Button)dialog.FindName("SaveButton")).IsEnabled);
            var link=status.Inlines.OfType<Hyperlink>().Single();
            Assert.AreEqual("（中心坐标 X=1224.5, Y=484502.5）",new TextRange(link.ContentStart,link.ContentEnd).Text);
            Assert.AreEqual(Brushes.RoyalBlue.Color,((SolidColorBrush)link.Foreground).Color);
            Assert.IsTrue(link.TextDecorations.Any(d=>d.Location==TextDecorationLocation.Underline));
            Assert.AreEqual(Cursors.Hand,link.Cursor);StringAssert.Contains(status.Text,target.Code);
            SaveImage(dialog,$"ln{size}-conflict-location-link.png");
            link.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));window.UpdateLayout();
            Assert.AreEqual(new Point(1224.5,484502.5),view.Center);Assert.IsTrue(view.Scale>1);
            Assert.AreEqual(target.Code,Editor(view).SelectedCode);Assert.AreEqual(WindowState.Minimized,dialog.WindowState);
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            SaveImage(window,$"ln{size}-conflict-location-map.png");
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.E){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(key);
            Assert.IsTrue(key.Handled);Assert.AreSame(dialog,Dialog(window));Assert.AreEqual(WindowState.Normal,dialog.WindowState);
            Assert.AreEqual("S",((TextBox)dialog.FindName("ValueInput")).Text);
            Assert.AreEqual("尚未保存的说明",((TextBox)dialog.FindName("InfoInput")).Text);
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
        }
        finally{Dialog(window)?.Close();window.Close();}
    }

    [STATestMethod]
    public void LaterFailureClearsPreviousLink_AndFrenchLocaleDoesNotChangeCoordinates()
    {
        var previous=CultureInfo.CurrentCulture;CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("fr-FR");
        using var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var service=new LogicalNodeService(handler.Repository);
        var target=service.Create(new("owner",100.5m,200.5m,12,new(){Value="S"})).Data!;
        var source=service.Create(new("owner",300.5m,400.5m,12,new(){Value="T"})).Data!;
        var dialog=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),source);
        try
        {
            ((TextBox)dialog.FindName("ValueInput")).Text="S";Save(dialog);
            var status=(TextBlock)dialog.FindName("Status");
            LogicalNodeWindowEntryTests.DrainUntil(()=>status.Inlines.OfType<Hyperlink>().Any());
            var locations=new List<Point>();dialog.LocationRequested+=(_,point)=>locations.Add(point);
            status.Inlines.OfType<Hyperlink>().Single().RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            Assert.AreEqual(new Point(100.5,200.5),locations.Single());
            Assert.IsTrue(service.Update(new(source.Code,"owner",source.Version,"U","")).Success);
            Save(dialog);LogicalNodeWindowEntryTests.DrainUntil(()=>!status.Inlines.OfType<Hyperlink>().Any());
            StringAssert.Contains(status.Text,"逻辑节点已发生变化");
        }
        finally{dialog.Close();CultureInfo.CurrentCulture=previous;}
    }

    [STATestMethod,DataRow("legacy"),DataRow("outOfBounds"),DataRow("wrongCode")]
    public void PlainOrInvalidErrorMessagesRemainText_WithoutClickableCoordinates(string kind)
    {
        var message=kind=="legacy"?"边长为 12 的 LN 值“S”已被 LN ABCDEFGHIJ 使用，请使用不同的值。":
            $"边长为 12 的 LN 值“S”已被 LN ABCDEFGHIJ（中心坐标 X={(kind=="outOfBounds"?"500001.5":"100.5")}, Y=200.5）使用，请使用不同的值。";
        using var http=new HttpClient(new ErrorHandler(kind=="wrongCode"?"VersionConflict":"DuplicateLogicalNodeValue",message)){BaseAddress=new Uri("http://localhost/")};
        var node=new LogicalNodeSnapshot("ZYXWVUTSRQ","owner",0,300.5m,400.5m,12,new(){Value="T"});
        var dialog=new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),node);
        try
        {
            Save(dialog);var status=(TextBlock)dialog.FindName("Status");LogicalNodeWindowEntryTests.DrainUntil(()=>status.Text==message);
            Assert.AreEqual(0,status.Inlines.OfType<Hyperlink>().Count());
        }
        finally{dialog.Close();}
    }

    private sealed class ErrorHandler(string code,string message):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(ServiceResult<LogicalNodeSnapshot>.Fail(code,message))});
    }
    private void SaveImage(Window window,string name)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
    }
}
