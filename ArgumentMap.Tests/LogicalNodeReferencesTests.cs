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
public sealed class LogicalNodeReferencesTests
{
    public TestContext TestContext {get;set;}=null!;
    private static T Offscreen<T>(T window) where T:Window
    {window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-10000;window.Top=-10000;window.ShowInTaskbar=false;window.ShowActivated=false;return window;}
    private static LogicalNodeEditor Editor(CoordinateMapView view)=>(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static LogicalNodeWindow? Dialog(MainWindow main)=>(LogicalNodeWindow?)typeof(MainWindow).GetField("logicalNodeWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(main);
    private static LogicalNodeSnapshot Add(LogicalNodeService service,int size,string value,string logic,decimal x,string owner="owner")
    {var result=service.Create(new(owner,x,50.5m,size,new(){Value=value,LogicLanguage=logic}));Assert.IsTrue(result.Success,result.Message);return result.Data!;}
    private static LogicalNodeSnapshot[] Rows(LogicalNodeWindow window)=>((ListBox)window.FindName("ReferencingBranchesList")).Items.Cast<LogicalNodeSnapshot>().ToArray();
    private static bool Complete(Task<bool> task)
    {LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
    {for(var i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);if(child is T result)yield return result;foreach(var nested in Descendants<T>(child))yield return nested;}}

    [STATestMethod,DataRow("True",false),DataRow("False",false),DataRow("False",true),TestCategory("Rendering")]
    public void ListsAllOwnersAndBothValues_ClickRevalidatesMovedBranchAndRenamedSource_EKeepsLn6Draft(string chosenValue,bool readOnly)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);var source=Add(service,6,"判断","P",50.5m,readOnly?"other":"owner");
        Add(service,6,"其他判断","Q",500.5m);Add(service,6,"大小写不同","p",550.5m);
        var branches=new[]{Add(service,4,"True","P",100.5m),Add(service,4,"True","P",150.5m,"other"),Add(service,4,"False","¬P",200.5m),Add(service,4,"False","¬P",250.5m,"other")};
        Add(service,4,"False","",300.5m);Add(service,4,"True","Q",350.5m);Add(service,12,"同名文字不算引用","P",400.5m);Add(service,4,"True","p",600.5m);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var main=Offscreen(new MainWindow(new HttpLineClient(http,new("owner","test"))));
        try
        {
            main.Show();var view=(CoordinateMapView)main.FindName("MapView");view.RefreshLogicalNodesAsync().GetAwaiter().GetResult();Editor(view).Select(source.Code);view.RequestLogicalNodeEdit();
            LogicalNodeWindowEntryTests.DrainUntil(()=>Dialog(main) is not null);var dialog=Dialog(main)!;var show=(Button)dialog.FindName("ShowReferencingBranchesButton");Assert.IsTrue(show.IsVisible);Assert.IsTrue(show.IsEnabled);
            Assert.AreEqual(!readOnly,((UIElement)dialog.FindName("MetadataPanel")).IsEnabled);
            if(!readOnly){((TextBox)dialog.FindName("LogicInput")).Text="Q";((TextBox)dialog.FindName("InfoInput")).Text="尚未保存的 LN6 说明";}
            var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);show.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));LogicalNodeWindowEntryTests.DrainUntil(()=>Rows(dialog).Length==4);
            CollectionAssert.AreEquivalent(branches.Select(n=>n.Code).ToArray(),Rows(dialog).Select(n=>n.Code).ToArray());
            var status=((TextBlock)dialog.FindName("ReferencingBranchesStatus")).Text;StringAssert.Contains(status,"True 2");StringAssert.Contains(status,"False 2");if(!readOnly)StringAssert.Contains(status,"草稿未保存");
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            if(chosenValue=="False"&&!readOnly)SaveImage(dialog,"ln6-referencing-ln4-list.png");
            var target=branches.First(n=>n.Properties.Value==chosenValue);Assert.IsTrue(service.Move(new(target.Code,target.Owner,target.Version,1000.5m,500.5m)).Success);
            Assert.IsTrue(service.Update(new(source.Code,source.Owner,source.Version,source.Properties.Value,"",LogicLanguage:"R")).Success);before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            var list=(ListBox)dialog.FindName("ReferencingBranchesList");dialog.UpdateLayout();list.ScrollIntoView(list.Items.Cast<LogicalNodeSnapshot>().Single(n=>n.Code==target.Code));dialog.UpdateLayout();
            LogicalNodeWindowEntryTests.DrainUntil(()=>Descendants<Button>(list).Any(b=>b.Tag is LogicalNodeSnapshot n&&n.Code==target.Code));
            var locate=Descendants<Button>(list).Single(b=>b.Tag is LogicalNodeSnapshot n&&n.Code==target.Code);locate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LogicalNodeWindowEntryTests.DrainUntil(()=>dialog.WindowState==WindowState.Minimized);
            Assert.AreEqual(new Point(1000.5,500.5),view.Center);Assert.AreEqual(target.Code,Editor(view).SelectedCode);Assert.AreEqual(1000.5m,Editor(view).Selected!.X);
            Assert.AreEqual(LogicalNodeBranchRules.Expression(chosenValue,"R"),Editor(view).Selected!.Properties.LogicLanguage);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(main)!,0,Key.E){RoutedEvent=Keyboard.PreviewKeyDownEvent};main.RaiseEvent(key);Assert.IsTrue(key.Handled);
            Assert.AreSame(dialog,Dialog(main));Assert.AreEqual(WindowState.Normal,dialog.WindowState);Assert.AreEqual(readOnly?"P":"Q",((TextBox)dialog.FindName("LogicInput")).Text);
            if(!readOnly)Assert.AreEqual("尚未保存的 LN6 说明",((TextBox)dialog.FindName("InfoInput")).Text);
            Assert.IsTrue(Rows(dialog).All(n=>LogicalNodeBranchRules.SourceLogic(n)=="R"));
        }
        finally{Dialog(main)?.Close();main.Close();}
    }

    [STATestMethod,DataRow("deleted"),DataRow("reassigned"),DataRow("cleared"),DataRow("sourceDeleted"),DataRow("sourceReplaced"),DataRow("booleanChanged")]
    public void ClickRechecksMembershipBySourceIdentity_AndNeverNavigatesAStaleRow(string change)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);var source=Add(service,6,"判断","P",50.5m);Add(service,6,"其他","Q",200.5m);var branch=Add(service,4,"True","P",100.5m);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),source));
        try
        {
            dialog.Show();Assert.IsTrue(Complete(dialog.LoadReferencingBranchesAsync()));Assert.AreEqual(1,Rows(dialog).Length);
            switch(change)
            {
                case "deleted":Assert.IsTrue(service.Delete(new(branch.Code,branch.Owner,branch.Version)).Success);break;
                case "reassigned":Assert.IsTrue(service.Update(new(branch.Code,branch.Owner,branch.Version,"True","",LogicLanguage:"Q")).Success);break;
                case "cleared":Assert.IsTrue(service.Update(new(branch.Code,branch.Owner,branch.Version,"True","",LogicLanguage:"")).Success);break;
                case "booleanChanged":Assert.IsTrue(service.Update(new(branch.Code,branch.Owner,branch.Version,"False","",LogicLanguage:"¬P")).Success);break;
                default:Assert.IsTrue(service.Delete(new(source.Code,source.Owner,source.Version)).Success);if(change=="sourceReplaced")Add(service,6,"新判断","P",300.5m);break;
            }
            var found=new List<LogicalNodeSnapshot>();dialog.ReferencingBranchLocationRequested+=n=>found.Add(n);var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            Assert.AreEqual(change=="booleanChanged",Complete(dialog.LocateReferencingBranchAsync(branch.Code)));Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            if(change=="booleanChanged"){Assert.AreEqual("False",found.Single().Properties.Value);Assert.AreEqual("¬P",found.Single().Properties.LogicLanguage);}
            else{Assert.AreEqual(0,found.Count);Assert.AreEqual(0,Rows(dialog).Length);}
        }
        finally{dialog.Close();}
    }

    [STATestMethod,DataRow("P"),DataRow("¬P"),DataRow(" (A ∧ B) ")]
    public void ExactFullExpressionSupportsMultipleTrueAndFalseReferences(string expression)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);var source=Add(service,6,"判断",expression,50.5m);
        var branches=new[]{Add(service,4,"True",expression,100.5m),Add(service,4,"True",expression,150.5m),Add(service,4,"False","¬"+expression,200.5m),Add(service,4,"False","¬"+expression,250.5m)};
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),source));
        try
        {
            dialog.Show();Assert.IsTrue(Complete(dialog.LoadReferencingBranchesAsync()));CollectionAssert.AreEquivalent(branches.Select(n=>n.Code).ToArray(),Rows(dialog).Select(n=>n.Code).ToArray());
        }
        finally{dialog.Close();}
    }

    [STATestMethod]
    public void EmptyListShowsZero_RefreshIncludesNewReferences_AndClearingTheSourceDraftStillUsesSavedLogic()
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);var source=Add(service,6,"判断","P",50.5m);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),source));
        try
        {
            dialog.Show();Assert.IsTrue(Complete(dialog.LoadReferencingBranchesAsync()));Assert.AreEqual(0,Rows(dialog).Length);StringAssert.Contains(((TextBlock)dialog.FindName("ReferencingBranchesStatus")).Text,"暂无 LN4 引用");
            Add(service,4,"False","¬P",100.5m);((TextBox)dialog.FindName("LogicInput")).Text="";
            Assert.IsTrue(((Button)dialog.FindName("ShowReferencingBranchesButton")).IsEnabled);Assert.IsTrue(Complete(dialog.LoadReferencingBranchesAsync()));Assert.AreEqual(1,Rows(dialog).Length);
            Assert.AreEqual("",((TextBox)dialog.FindName("LogicInput")).Text);StringAssert.Contains(((TextBlock)dialog.FindName("ReferencingBranchesStatus")).Text,"草稿未保存");
        }
        finally{dialog.Close();}
    }

    [STATestMethod,DataRow(4),DataRow(6),DataRow(12)]
    public void UnsavedNodesAndOtherSizesCannotReadReferences(int size)
    {
        using var handler=new ResponseHandler(_=>Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([],"ok")));using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(100.5,100.5),sideLength:size));
        try
        {
            dialog.Show();var button=(Button)dialog.FindName("ShowReferencingBranchesButton");Assert.AreEqual(size==6?Visibility.Visible:Visibility.Collapsed,button.Visibility);Assert.IsFalse(button.IsEnabled);
            Assert.IsFalse(Complete(dialog.LoadReferencingBranchesAsync()));Assert.AreEqual(0,handler.Calls);
        }
        finally{dialog.Close();}
    }

    [STATestMethod]
    public void SingleFlightReadBlocksSaveAndClose_AuthenticationFailureClearsStaleList()
    {
        var source=new LogicalNodeSnapshot("AAAAAAAAAA","owner",0,50.5m,50.5m,6,new(){LogicLanguage="P"});var branch=new LogicalNodeSnapshot("BBBBBBBBBB","other",0,100.5m,50.5m,4,new(){Value="False",LogicLanguage="¬P"});
        var gate=new TaskCompletionSource<ServiceResult<LogicalNodeSnapshot[]>>();using var handler=new ResponseHandler(call=>call==1?gate.Task:Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Fail("LoginExpired","登录已失效，请重新登录。")));
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var dialog=Offscreen(new LogicalNodeWindow(new HttpLineClient(http,new("owner","test")),"owner",new(),source));
        try
        {
            dialog.Show();var pending=dialog.LoadReferencingBranchesAsync();Assert.AreEqual(1,handler.Calls);Assert.IsFalse(((Button)dialog.FindName("SaveButton")).IsEnabled);Assert.IsFalse(Complete(dialog.LoadReferencingBranchesAsync()));dialog.Close();Assert.IsTrue(dialog.IsVisible);
            gate.SetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([source,branch],"ok"));Assert.IsTrue(Complete(pending));Assert.AreEqual(1,Rows(dialog).Length);
            var navigations=0;dialog.ReferencingBranchLocationRequested+=_=>navigations++;Assert.IsFalse(Complete(dialog.LocateReferencingBranchAsync(branch.Code)));Assert.AreEqual(0,navigations);Assert.AreEqual(0,Rows(dialog).Length);
            StringAssert.Contains(((TextBlock)dialog.FindName("ReferencingBranchesStatus")).Text,"登录已失效");Assert.IsTrue(((Button)dialog.FindName("SaveButton")).IsEnabled);
        }
        finally{gate.TrySetResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([],"ok"));dialog.Close();}
    }

    private sealed class ResponseHandler(Func<int,Task<ServiceResult<LogicalNodeSnapshot[]>>> response):HttpMessageHandler
    {
        internal int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {Assert.AreEqual("/GetLogicalNodes",request.RequestUri!.AbsolutePath);return new(HttpStatusCode.OK){Content=JsonContent.Create(await response(++Calls))};}
    }
    private void SaveImage(Window window,string name)
    {
        window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))png.Save(stream);TestContext.AddResultFile(path);
    }
}
