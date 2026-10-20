using System.IO;
using System.Net;
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
public sealed class LogicalNodeBranchNavigationTests
{
    public TestContext TestContext {get;set;}=null!;
    private static T Offscreen<T>(T window) where T:Window
    {window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-10000;window.Top=-10000;window.ShowInTaskbar=false;window.ShowActivated=false;return window;}
    private static LogicalNodeEditor Editor(CoordinateMapView view)=>(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(view)!;
    private static LogicalNodeWindow? Dialog(MainWindow window)=>(LogicalNodeWindow?)typeof(MainWindow).GetField("logicalNodeWindow",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(window);
    private static LogicalNodeSnapshot Branch(string value="True",string logic="P",int size=4)=>new("BBBBBBBBBB","owner",0,100.5m,50.5m,size,new(){Value=value,LogicLanguage=logic});
    private static LogicalNodeSnapshot Source(string logic="P",string code="AAAAAAAAAA",int size=6)=>new(code,"other",1,50.5m,50.5m,size,new(){LogicLanguage=logic});

    [STATestMethod,TestCategory("Rendering")]
    [DataRow("True",false)][DataRow("False",false)][DataRow("True",true)][DataRow("False",true)]
    public void ActualButtonFindsFreshMovedLn6_UpdatesCachedGeometry_AndEReturnsUnchangedDraft(string value,bool readOnly)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);const string sourceLogic="dateSuccess(S,M)";
        var target=service.Create(new("other",1200.5m,484500.5m,6,new(){LogicLanguage=sourceLogic})).Data!;
        var branch=service.Create(new(readOnly?"other":"owner",1325.5m,484430.5m,4,new(){Value=value,LogicLanguage=LogicalNodeBranchRules.Expression(value,sourceLogic)})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var main=Offscreen(new MainWindow(new HttpLineClient(http,new("owner","test"))));
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Editor(view).Select(branch.Code);view.RequestLogicalNodeEdit();
            LogicalNodeWindowEntryTests.DrainUntil(()=>Dialog(main) is not null);var dialog=Dialog(main)!;var button=(Button)dialog.FindName("LocateBranchSourceButton");
            Assert.IsTrue(button.IsVisible);Assert.IsTrue(button.IsEnabled);Assert.AreEqual(!readOnly,((UIElement)dialog.FindName("MetadataPanel")).IsEnabled);
            if(!readOnly)((TextBox)dialog.FindName("InfoInput")).Text="尚未保存的说明";
            var moved=service.Move(new(target.Code,"other",target.Version,1550.5m,484600.5m));Assert.IsTrue(moved.Success,moved.Message);
            Assert.AreEqual(target.X,Editor(view).Nodes.Single(n=>n.Code==target.Code).X);var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));LogicalNodeWindowEntryTests.DrainUntil(()=>dialog.WindowState==WindowState.Minimized);
            Assert.AreEqual(new Point(1550.5,484600.5),view.Center);Assert.AreEqual(target.Code,Editor(view).SelectedCode);Assert.AreEqual(1550.5m,Editor(view).Selected!.X);
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(main)!,0,Key.E){RoutedEvent=Keyboard.PreviewKeyDownEvent};main.RaiseEvent(key);
            Assert.IsTrue(key.Handled);Assert.AreEqual(WindowState.Normal,dialog.WindowState);Assert.AreSame(dialog,Dialog(main));
            Assert.AreEqual(value,((ComboBox)dialog.FindName("BranchValueInput")).SelectedValue);Assert.AreEqual(LogicalNodeBranchRules.Expression(value,sourceLogic),((TextBox)dialog.FindName("LogicInput")).Text);
            if(!readOnly)Assert.AreEqual("尚未保存的说明",((TextBox)dialog.FindName("InfoInput")).Text);
            if(value=="False"&&!readOnly)SaveImage(dialog,"ln4-locate-related-ln6.png");
        }
        finally{Dialog(main)?.Close();main.Close();}
    }

    [STATestMethod]
    [DataRow("True","",4)][DataRow("False"," \t",4)][DataRow("False","P",4)][DataRow("False","¬",4)]
    [DataRow("True","P",6)][DataRow("True","P",12)]
    public void EmptyOrInvalidBranchAndOtherSizesCannotLookUp(string value,string logic,int size)
    {
        using var handler=new LookupHandler(_=>Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([Source()],"ok")));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Branch(value,logic,size)));
        try
        {
            dialog.Show();var button=(Button)dialog.FindName("LocateBranchSourceButton");Assert.AreEqual(size==4?Visibility.Visible:Visibility.Collapsed,button.Visibility);Assert.IsFalse(button.IsEnabled);
            Assert.IsFalse(dialog.LocateBranchSourceAsync().GetAwaiter().GetResult());Assert.AreEqual(0,handler.Calls);
        }
        finally{dialog.Close();}
    }

    [STATestMethod,DataRow("True","¬P","¬P"),DataRow("False","¬¬P","¬P"),DataRow("True","p","p"),DataRow("False","¬p","p")]
    public void ExactSourceMatchingHonorsCaseAndRemovesOnlyOneNegation(string value,string logic,string expected)
    {
        var target=Source(expected);
        using var handler=new LookupHandler(_=>Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([Source(expected,size:12),Source("P","CCCCCCCCCC"),target],"ok")));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Branch(value,logic)));
        try
        {
            dialog.Show();LogicalNodeSnapshot? found=null;dialog.BranchSourceLocationRequested+=n=>found=n;
            Assert.IsTrue(dialog.LocateBranchSourceAsync().GetAwaiter().GetResult());Assert.IsNotNull(found);Assert.AreEqual(target.Code,found.Code);Assert.AreEqual(6,found.SideLength);Assert.AreEqual(expected,found.Properties.LogicLanguage);Assert.AreEqual(target.X,found.X);Assert.AreEqual(target.Y,found.Y);Assert.AreEqual(logic,((TextBox)dialog.FindName("LogicInput")).Text);Assert.AreEqual(1,handler.Calls);
            ((TextBox)dialog.FindName("LogicInput")).Text="";Assert.IsFalse(((Button)dialog.FindName("LocateBranchSourceButton")).IsEnabled);
        }
        finally{dialog.Close();}
    }

    [STATestMethod,DataRow("deleted"),DataRow("renamed"),DataRow("duplicate"),DataRow("expired")]
    public void MissingChangedAmbiguousOrUnauthorizedTargetsNeverNavigate_AndKeepDraft(string kind)
    {
        var response=kind=="expired"?ServiceResult<LogicalNodeSnapshot[]>.Fail("LoginExpired","登录已失效，请重新登录。"):
            ServiceResult<LogicalNodeSnapshot[]>.Ok(kind=="deleted"?[]:kind=="renamed"?[Source("Q")]:[Source(),Source("P","CCCCCCCCCC")],"ok");
        using var handler=new LookupHandler(_=>Task.FromResult(response));using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Branch()));
        try
        {
            dialog.Show();var navigations=0;dialog.BranchSourceLocationRequested+=_=>navigations++;((TextBox)dialog.FindName("InfoInput")).Text="保留草稿";
            Assert.IsFalse(dialog.LocateBranchSourceAsync().GetAwaiter().GetResult());Assert.AreEqual(0,navigations);Assert.IsTrue(dialog.IsVisible);Assert.IsTrue(((Button)dialog.FindName("SaveButton")).IsEnabled);
            Assert.AreEqual("P",((TextBox)dialog.FindName("LogicInput")).Text);Assert.AreEqual("保留草稿",((TextBox)dialog.FindName("InfoInput")).Text);
            StringAssert.Contains(((TextBlock)dialog.FindName("Status")).Text,kind=="expired"?"登录已失效":kind=="duplicate"?"无法唯一定位":"找不到");
        }
        finally{dialog.Close();}
    }

    [STATestMethod]
    public void LookupIsSingleFlightAndBlocksCloseOrSave_ChangedDraftCannotNavigateUsingOldResponse()
    {
        var gate=new TaskCompletionSource<ServiceResult<LogicalNodeSnapshot[]>>();using var handler=new LookupHandler(_=>gate.Task);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),Branch()));
        try
        {
            dialog.Show();var calls=0;dialog.BranchSourceLocationRequested+=_=>calls++;var pending=dialog.LocateBranchSourceAsync();Assert.AreEqual(1,handler.Calls);
            Assert.IsFalse(((Button)dialog.FindName("LocateBranchSourceButton")).IsEnabled);Assert.IsFalse(((Button)dialog.FindName("SaveButton")).IsEnabled);
            Assert.IsFalse(dialog.LocateBranchSourceAsync().GetAwaiter().GetResult());dialog.Close();Assert.IsTrue(dialog.IsVisible);
            ((TextBox)dialog.FindName("LogicInput")).Text="Q";gate.SetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([Source()],"ok"));LogicalNodeWindowEntryTests.DrainUntil(()=>pending.IsCompleted);
            Assert.IsFalse(pending.Result);Assert.AreEqual(0,calls);Assert.AreEqual(1,handler.Calls);Assert.IsTrue(((Button)dialog.FindName("LocateBranchSourceButton")).IsEnabled);
        }
        finally{gate.TrySetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([],"ok"));dialog.Close();}
    }

    private sealed class LookupHandler(Func<HttpRequestMessage,Task<ServiceResult<LogicalNodeSnapshot[]>>> respond):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Assert.AreEqual("/GetLogicalNodes",request.RequestUri!.AbsolutePath);Calls++;return new(HttpStatusCode.OK){Content=JsonContent.Create(await respond(request))};}
    }
    private void SaveImage(Window window,string name)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
    }
}
