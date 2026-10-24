using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;
using static ArgumentMap.Tests.LogicalNodeConnectionTests;
using static ArgumentMap.Tests.LogicalNodeMySqlTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeGoToUiTests
{
    public TestContext TestContext{get;set;}=null!;
    private static T Wait<T>(Task<T> task){LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
    [STATestMethod,TestCategory("Rendering"),DataRow(false),DataRow(true)]
    public void ActualGoToDialogShowsNewAndExistingPaths_EnterSaves_AndUndoRemovesOnlyNewRoad(bool finding)
    {
        var repo=new InMemoryLineRepository();var a=Node(repo,249970.5m,250000.5m,12);var b=Node(repo,250030.5m,250060.5m);
        var old=Add(repo,250030,249970,250030,250090);
        if(finding)
        {
            Assert.IsTrue(new ArgumentMap.Business.LineSaveService(repo).Reverse(new(old.Code,"owner",old.Version)).Success);
            (a,b)=(b,a);
        }
        var server=LocalLineServer.StartAsync(repository:repo).GetAwaiter().GetResult();using var http=new HttpClient{BaseAddress=server.BaseAddress};
        var client=TestLogin.ClientAsync(http,server,"owner").GetAwaiter().GetResult();
        var main=new MainWindow(client){Left=-10000,Top=-10000,Width=1800,Height=1000,ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual};
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
            main.Show();main.UpdateLayout();Assert.IsTrue(Wait(main.InitializeLinesAsync()));var map=(CoordinateMapView)main.FindName("MapView");map.ResetView();map.ZoomAtCenter(4);
            ((Button)main.FindName("ConnectLogicalNodesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));map.PickConnectionPoint(new((double)a.X,(double)a.Y));
            main.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
            {
                var dialog=main.OwnedWindows.OfType<LogicalNodeConnectionWindow>().Single();var go=(Button)dialog.FindName(finding?"FindButton":"GoToButton");Assert.IsTrue(go.IsEnabled);go.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            map.PickConnectionPoint(new((double)b.X,(double)b.Y));LogicalNodeWindowEntryTests.DrainUntil(()=>map.HasConnectionPreview&&!map.IsSaving);
            Assert.AreEqual(finding?LogicalNodeConnectionKind.Find:LogicalNodeConnectionKind.GoTo,map.ConnectionPreview!.Kind);Assert.IsTrue(map.ConnectionPreview.Continuation.Length>=2);
            if(finding){Assert.AreEqual(a.Y,map.ConnectionPreview.Continuation[0].Y);StringAssert.Contains(map.OperationMessage,"接出");}
            StringAssert.Contains(map.OperationMessage,"青色虚线");Assert.AreEqual(1,map.LineCount);
            main.UpdateLayout();var bitmap=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(main);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",finding?"ln-find-preview.png":"ln-goto-preview.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using(var output=File.Create(path))png.Save(output);TestContext.AddResultFile(path);
            Assert.IsTrue(Wait(map.HandleEditorKeyAsync(Key.Enter)));Assert.AreEqual(2,map.LineCount);Assert.IsFalse(map.IsConnectingNodes);
            Assert.IsTrue(Wait(map.UndoDatabaseAsync()));Assert.AreEqual(1,map.LineCount);Assert.IsTrue(repo.Transaction(s=>s.Lines.ContainsKey(old.Code)));
        }
        finally{main.Close();SynchronizationContext.SetSynchronizationContext(previous);server.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
}
