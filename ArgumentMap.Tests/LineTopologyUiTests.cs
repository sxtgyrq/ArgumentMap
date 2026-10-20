using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LineTopologyUiTests
{
    private sealed class Client(TestMap map):ILineClient,ILineTopologyClient
    {
        internal int Previews,Applies,Splits;internal TaskCompletionSource<ServiceResult<LineTopologyResult>>? Gate;
        public Task<ServiceResult<LineSnapshot>> CreateAsync()=>Task.FromResult(ServiceResult<LineSnapshot>.Ok(map.Create(),""));
        public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string code)=>Task.FromResult(map.Lines.StartDrawing(new(code,"owner")));
        public Task<ServiceResult<LineSnapshot>> SaveAsync(string code,long version,LineNode[] nodes)=>Task.FromResult(map.Lines.CheckShapeIsOKThenSave(new(code,"owner",version,nodes)));
        public Task<ServiceResult<LineSnapshot[]>> GetAllAsync()=>Task.FromResult(map.Lines.GetAll());
        public Task<ServiceResult<bool>> DeleteAsync(string code,long version)=>Task.FromResult(map.Lines.Delete(new(code,"owner",version)));
        public Task<ServiceResult<LineTopologyResult>> SplitLineAsync(string code,long version,int cut){Splits++;return Task.FromResult(new LineTopologyService(map.Repository).Split(new(code,"owner",version,cut)));}
        public Task<ServiceResult<MergePreview>> GetMergePlansAsync(string a,long av,string b,long bv){Previews++;return Task.FromResult(new LineTopologyService(map.Repository).GetMergePlans(new(a,av,b,bv,"owner")));}
        public Task<ServiceResult<LineTopologyResult>> ApplyMergePlanAsync(MergePreview preview,MergePlan plan){Applies++;return Gate?.Task??Task.FromResult(new LineTopologyService(map.Repository).Merge(LineTopologyTests.Apply(preview,plan)));}
    }
    public TestContext TestContext{get;set;}=null!;
    private static (TestMap Map,LineSnapshot First,LineSnapshot Second,Client Client,LineEditor Editor) Pair()
    {
        var map=new TestMap();var first=map.Add(100,100,110,100);var second=map.Add(110,110,110,120);var client=new Client(map);var editor=new LineEditor(client);
        editor.RefreshLinesAsync().GetAwaiter().GetResult();editor.ToggleSelection();return(map,first,second,client,editor);
    }
    [TestMethod]
    public async Task BSequence_OnlyPreviewsUntilUserChoosesPlan_UpdatesCacheAfterCommit()
    {
        var pair=Pair();pair.Editor.SelectClosest(new Point(105,100),1);var before=pair.Map.Snapshot();
        Assert.IsNull(await pair.Editor.GetMergePlansAsync());Assert.AreEqual(pair.First.Code,pair.Editor.MergeFirstCode);Assert.AreEqual(0,pair.Client.Previews);
        pair.Editor.SelectClosest(new Point(110,115),1);var result=await pair.Editor.GetMergePlansAsync();Assert.IsTrue(result!.Success);Assert.AreEqual(before,pair.Map.Snapshot());Assert.AreEqual(0,pair.Client.Applies);
        Assert.IsTrue(await pair.Editor.ApplyMergeAsync(result.Data!,result.Data!.Plans[0]));Assert.AreEqual(1,pair.Client.Applies);Assert.AreEqual(1,pair.Editor.Lines.Count);
        Assert.AreEqual(pair.First.Code,pair.Editor.SelectedCode);Assert.IsNull(pair.Editor.MergeFirstCode);Assert.IsFalse(pair.Editor.IsBusy);
    }
    [TestMethod]
    public async Task BusyAndFailedCommit_KeepOriginalCacheAndReleaseControlsForRetry()
    {
        var pair=Pair();pair.Editor.SelectClosest(new Point(105,100),1);await pair.Editor.GetMergePlansAsync();pair.Editor.SelectClosest(new Point(110,115),1);
        var preview=(await pair.Editor.GetMergePlansAsync())!.Data!;pair.Client.Gate=new();var before=pair.Map.Snapshot();
        var apply=pair.Editor.ApplyMergeAsync(preview,preview.Plans[0]);Assert.IsTrue(pair.Editor.IsBusy);Assert.IsFalse(await pair.Editor.ApplyMergeAsync(preview,preview.Plans[0]));Assert.AreEqual(1,pair.Client.Applies);
        pair.Client.Gate.SetResult(ServiceResult<LineTopologyResult>.Fail("VersionConflict","其他用户已修改，请重新选择方案"));Assert.IsFalse(await apply);
        Assert.AreEqual(before,pair.Map.Snapshot());Assert.AreEqual(2,pair.Editor.Lines.Count);Assert.IsFalse(pair.Editor.IsBusy);StringAssert.Contains(pair.Editor.Message,"重新选择方案");
        pair.Client.Gate=null;Assert.IsTrue(await pair.Editor.ApplyMergeAsync(preview,preview.Plans[0]));
    }
    [TestMethod]
    public async Task DSelection_SplitsAtNearestNodeAndRejectsEndpoints()
    {
        var map=new TestMap();map.Add(100,100,110,100,120,100,130,100);var client=new Client(map);var editor=new LineEditor(client);
        await editor.RefreshLinesAsync();editor.ToggleSelection();editor.SelectClosest(new Point(119,100),1);
        var request=editor.SplitSelection(new Point(119,100));Assert.IsNotNull(request);Assert.AreEqual(2,request.CutBeforeOrder);Assert.IsTrue(await editor.SplitAsync(request));Assert.AreEqual(2,editor.Lines.Count);
        editor.SelectClosest(new Point(105,100),1);Assert.IsNull(editor.SplitSelection(new Point(100,100)));Assert.AreEqual(1,client.Splits);
    }
    [STATestMethod,DataRow("keyboard"),DataRow("button"),DataRow("menu"),TestCategory("Rendering")]
    public void ActualMainWindowBEntry_RemembersFirstRoadWithoutWritingOrOpeningConfirmation(string entry)
    {
        var pair=Pair();var window=new MainWindow(pair.Client){WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");view.RefreshLinesAsync().GetAwaiter().GetResult();view.ToggleLineSelection();
            var editor=(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;editor.SelectClosest(new Point(105,100),1);
            if(entry=="button")((Button)window.FindName("MergeLinesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if(entry=="menu")((MenuItem)window.FindName("MergeLinesMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,Key.B){RoutedEvent=Keyboard.PreviewKeyDownEvent});
            Assert.AreEqual(pair.First.Code,editor.MergeFirstCode);Assert.AreEqual(0,pair.Client.Applies);Assert.AreEqual(0,pair.Client.Previews);
            Assert.IsNotNull(window.FindName("SplitLineButton"));Assert.IsNotNull(window.FindName("SplitLineMenu"));
            Assert.IsTrue(CoordinateMapView.IsEditorKey(Key.B));Assert.IsTrue(CoordinateMapView.IsEditorKey(Key.D));
        }
        finally{window.Close();}
    }
    [STATestMethod,TestCategory("Rendering")]
    public void PlansWindow_SelectionChangesRealPreview_FailedSaveRetainsChoicesAndReenablesButtons()
    {
        var pair=Pair();var preview=new LineTopologyService(pair.Map.Repository).GetMergePlans(LineTopologyTests.Request(pair.First,pair.Second)).Data!;
        var calls=0;var window=new MergePlansWindow(preview,_=>{calls++;return Task.FromResult((false,"其他用户修改了 LN，请刷新方案"));})
            {WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
        try
        {
            window.Show();window.UpdateLayout();var list=(ListBox)window.FindName("PlanList");Assert.AreEqual(preview.Plans.Length,list.Items.Count);
            Assert.AreEqual(135,((MergePlan)((ListBoxItem)list.Items[0]).Tag).FirstCorner);
            var first=Bitmap(window);Save(window,"merge-135-plan.png");MergePlan? selected=null;window.PlanSelected+=p=>selected=p;
            list.SelectedIndex=1;window.UpdateLayout();Assert.AreEqual(preview.Plans[1],selected);var second=Bitmap(window);Assert.IsFalse(first.SequenceEqual(second));Save(window,"merge-90-plan.png");
            ((Button)window.FindName("ApplyButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(1,calls);Assert.IsTrue(list.IsEnabled);Assert.IsTrue(((Button)window.FindName("ApplyButton")).IsEnabled);Assert.IsTrue(((Button)window.FindName("CancelButton")).IsEnabled);
            Assert.AreEqual(1,list.SelectedIndex);StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"刷新方案");
        }
        finally{window.Close();}
    }
    private static byte[] Bitmap(Window window)
    {
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(window);
        var bytes=new byte[bitmap.PixelWidth*bitmap.PixelHeight*4];bitmap.CopyPixels(bytes,bitmap.PixelWidth*4,0);return bytes;
    }
    private void Save(Window window,string name)
    {
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,System.Windows.Media.PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(path))encoder.Save(stream);TestContext.AddResultFile(path);
    }
}
