using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
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
public sealed class LogicalNodeInstanceNavigationTests
{
    public TestContext TestContext {get;set;}=null!;
    private static T Offscreen<T>(T window) where T:Window
    {window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-10000;window.Top=-10000;window.ShowInTaskbar=false;window.ShowActivated=false;return window;}
    private static LogicalNodeEditor Editor(CoordinateMapView view)=>(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static LogicalNodeWindow? Dialog(MainWindow main)=>(LogicalNodeWindow?)typeof(MainWindow).GetField("logicalNodeWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main);
    private static LogicalNodeSnapshot Instance(string value="m(id=1)",int size=10)=>new("BBBBBBBBBB","owner",0,100.5m,50.5m,size,new(){Value=value,LogicLanguage=LogicalNodeInstanceRules.Expression(value)});
    private static LogicalNodeSnapshot Parent(string value="M",string code="AAAAAAAAAA",int size=12)=>new(code,"other",0,50.5m,50.5m,size,new(){Value=value,LogicLanguage="自定义逻辑，故意与值不同"});
    private static bool Complete(Task<bool> task)
    {LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}

    [STATestMethod,TestCategory("Rendering"),DataRow("M",false),DataRow("H人",false),DataRow("M",true)]
    public void ButtonLocatesFreshParentByValue_WithMovedCoordinates_AndEKeepsDraft(string parentValue,bool readOnly)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        var parent=service.Create(new("other",50.5m,50.5m,12,new(){Value=parentValue,LogicLanguage="任意不同表达式"})).Data!;
        var instance=service.Create(new(readOnly?"other":"owner",100.5m,50.5m,10,new(){Value=LogicalNodeInstanceRules.DefaultValue(parentValue,"1")})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var main=Offscreen(new MainWindow(new HttpLineClient(http,new("owner","test"))));
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Editor(view).Select(instance.Code);view.RequestLogicalNodeEdit();
            LogicalNodeWindowEntryTests.DrainUntil(()=>Dialog(main) is not null);var dialog=Dialog(main)!;var button=(Button)dialog.FindName("LocateInstanceParentButton");
            Assert.IsTrue(button.IsVisible);Assert.IsTrue(button.IsEnabled);Assert.AreEqual(!readOnly,((UIElement)dialog.FindName("MetadataPanel")).IsEnabled);
            if(parentValue=="M"&&!readOnly)SaveImage(dialog);
            if(!readOnly)((TextBox)dialog.FindName("InfoInput")).Text="未保存的实例说明";
            var moved=service.Move(new(parent.Code,"other",parent.Version,500.5m,300.5m));Assert.IsTrue(moved.Success,moved.Message);
            var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LogicalNodeWindowEntryTests.DrainUntil(()=>dialog.WindowState==WindowState.Minimized);
            Assert.AreEqual(new Point(500.5,300.5),view.Center);Assert.AreEqual(parent.Code,Editor(view).SelectedCode);Assert.AreEqual(500.5m,Editor(view).Selected!.X);
            Assert.AreEqual(parentValue,Editor(view).Selected!.Properties.Value);Assert.AreEqual("任意不同表达式",Editor(view).Selected!.Properties.LogicLanguage);
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(main)!,0,Key.E){RoutedEvent=Keyboard.PreviewKeyDownEvent};main.RaiseEvent(key);
            Assert.IsTrue(key.Handled);Assert.AreSame(dialog,Dialog(main));Assert.AreEqual(WindowState.Normal,dialog.WindowState);
            Assert.AreEqual(instance.Properties.Value,((TextBox)dialog.FindName("ValueInput")).Text);Assert.AreEqual(instance.Properties.LogicLanguage,((TextBox)dialog.FindName("LogicInput")).Text);
            if(!readOnly)Assert.AreEqual("未保存的实例说明",((TextBox)dialog.FindName("InfoInput")).Text);
        }
        finally{Dialog(main)?.Close();main.Close();}
    }

    [STATestMethod,DataRow("m(name=Alice)"),DataRow("h人(id=1)"),DataRow("people(id=42)"),DataRow("人(id=旧实例)")]
    public void MatchesOnlyLn12Value_IncludingLegacyInstances_AndUsesCurrentUnsavedValue(string value)
    {
        var expected=LogicalNodeInstanceRules.ParentValue(value);var target=Parent(expected);
        using var handler=new LookupHandler(_=>Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([
            Parent(expected,"CCCCCCCCCC",6),Parent("OTHER","DDDDDDDDDD") with {Properties=new(){Value="OTHER",LogicLanguage=expected}},target],"ok")));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Instance("original")));
        try
        {
            dialog.Show();((TextBox)dialog.FindName("ValueInput")).Text=value;((TextBox)dialog.FindName("InfoInput")).Text="草稿";
            LogicalNodeSnapshot? found=null;dialog.InstanceParentLocationRequested+=n=>found=n;
            Assert.IsTrue(Complete(dialog.LocateInstanceParentAsync()));Assert.IsNotNull(found);Assert.AreEqual(target.Code,found.Code);Assert.AreEqual(12,found.SideLength);
            Assert.AreEqual(target.Properties,found.Properties);Assert.AreEqual(target.X,found.X);Assert.AreEqual(target.Y,found.Y);Assert.AreEqual(1,handler.Calls);
            Assert.AreEqual(value,((TextBox)dialog.FindName("ValueInput")).Text);Assert.AreEqual(LogicalNodeInstanceRules.Expression(value),((TextBox)dialog.FindName("LogicInput")).Text);
            Assert.AreEqual("草稿",((TextBox)dialog.FindName("InfoInput")).Text);
        }
        finally{dialog.Close();}
    }

    [STATestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(12),DataRow(18)]
    public void OtherSizesHaveNoParentLookup(int size)
    {
        using var handler=new LookupHandler(_=>Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([Parent()],"ok")));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Instance(size:size)));
        try
        {
            dialog.Show();Assert.AreEqual(Visibility.Collapsed,((Button)dialog.FindName("LocateInstanceParentButton")).Visibility);
            Assert.IsFalse(Complete(dialog.LocateInstanceParentAsync()));Assert.AreEqual(0,handler.Calls);
        }
        finally{dialog.Close();}
    }

    [STATestMethod,DataRow(""),DataRow(" ")]
    public void EmptyValueDisablesLookupWithoutHttp_EnteringValueEnablesIt(string empty)
    {
        using var handler=new LookupHandler(_=>Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([Parent()],"ok")));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Instance()));
        try
        {
            dialog.Show();var button=(Button)dialog.FindName("LocateInstanceParentButton");((TextBox)dialog.FindName("ValueInput")).Text=empty;
            Assert.IsFalse(button.IsEnabled);Assert.IsFalse(Complete(dialog.LocateInstanceParentAsync()));Assert.AreEqual(0,handler.Calls);
            ((TextBox)dialog.FindName("ValueInput")).Text="m";Assert.IsTrue(button.IsEnabled);Assert.IsTrue(Complete(dialog.LocateInstanceParentAsync()));Assert.AreEqual(1,handler.Calls);
        }
        finally{dialog.Close();}
    }

    [STATestMethod,DataRow("missing"),DataRow("renamed"),DataRow("logicOnly"),DataRow("duplicate"),DataRow("expired")]
    public void MissingRenamedAmbiguousOrUnauthorizedParentNeverNavigates_AndKeepsDraft(string change)
    {
        var response=change=="expired"?ServiceResult<LogicalNodeSnapshot[]>.Fail("LoginExpired","登录已失效，请重新登录。"):
            ServiceResult<LogicalNodeSnapshot[]>.Ok(change=="missing"?[]:change=="renamed"?[Parent("N")]:change=="logicOnly"?[Parent("N") with {Properties=new(){Value="N",LogicLanguage="M"}}]:[Parent(),Parent(code:"CCCCCCCCCC")],"ok");
        using var handler=new LookupHandler(_=>Task.FromResult(response));using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Instance()));
        try
        {
            dialog.Show();var found=0;dialog.InstanceParentLocationRequested+=_=>found++;((TextBox)dialog.FindName("InfoInput")).Text="保留草稿";
            Assert.IsFalse(Complete(dialog.LocateInstanceParentAsync()));Assert.AreEqual(0,found);Assert.IsTrue(dialog.IsVisible);Assert.IsTrue(((Button)dialog.FindName("SaveButton")).IsEnabled);
            Assert.AreEqual("m(id=1)",((TextBox)dialog.FindName("ValueInput")).Text);Assert.AreEqual("m(id=1)∈M∧1∈M.ID",((TextBox)dialog.FindName("LogicInput")).Text);Assert.AreEqual("保留草稿",((TextBox)dialog.FindName("InfoInput")).Text);
            StringAssert.Contains(((TextBlock)dialog.FindName("Status")).Text,change=="expired"?"登录已失效":change=="duplicate"?"无法唯一定位":"找不到");
        }
        finally{dialog.Close();}
    }

    [STATestMethod]
    public void PendingReadBlocksDuplicateLookupAndSave_AndChangedDraftDiscardsResponse()
    {
        var gate=new TaskCompletionSource<ServiceResult<LogicalNodeSnapshot[]>>();using var handler=new LookupHandler(_=>gate.Task);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Instance()));
        try
        {
            dialog.Show();var found=0;dialog.InstanceParentLocationRequested+=_=>found++;var pending=dialog.LocateInstanceParentAsync();Assert.AreEqual(1,handler.Calls);
            Assert.IsFalse(((Button)dialog.FindName("LocateInstanceParentButton")).IsEnabled);Assert.IsFalse(((Button)dialog.FindName("SaveButton")).IsEnabled);
            Assert.IsFalse(Complete(dialog.LocateInstanceParentAsync()));dialog.Close();Assert.IsTrue(dialog.IsVisible);
            ((TextBox)dialog.FindName("ValueInput")).Text="n";gate.SetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([Parent()],"ok"));
            Assert.IsFalse(Complete(pending));Assert.AreEqual(0,found);Assert.AreEqual("n∈N",((TextBox)dialog.FindName("LogicInput")).Text);
            Assert.IsTrue(((Button)dialog.FindName("LocateInstanceParentButton")).IsEnabled);StringAssert.Contains(((TextBlock)dialog.FindName("Status")).Text,"实例值已改变");
        }
        finally{gate.TrySetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([],"ok"));dialog.Close();}
    }

    private sealed class LookupHandler(Func<int,Task<ServiceResult<LogicalNodeSnapshot[]>>> response):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Assert.AreEqual("/GetLogicalNodes",request.RequestUri!.AbsolutePath);return new(HttpStatusCode.OK){Content=JsonContent.Create(await response(++Calls))};}
    }
    private void SaveImage(Window window)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","ln10-locate-parent.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
    }
}
