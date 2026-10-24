using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeConnectionUiTests
{
    public TestContext TestContext{get;set;}=null!;
    private static T Wait<T>(Task<T> task){LogicalNodeWindowEntryTests.DrainUntil(()=>task.IsCompleted);return task.GetAwaiter().GetResult();}
    private static MainWindow Window(HttpLineClient client)=>new(client){Left=-10000,Top=-10000,Width=1800,Height=1000,ShowInTaskbar=false,ShowActivated=false,WindowStartupLocation=WindowStartupLocation.Manual};
    [STATestMethod,TestCategory("Rendering")]
    public void RealLnPanelPointsDialogPreviewConfirmationAndUndo_WorkWithoutEditingNodeValues()
    {
        var repo=new InMemoryLineRepository();var a=LogicalNodeConnectionTests.Node(repo,249970.5m,250000.5m,12);var b=LogicalNodeConnectionTests.Node(repo,250030.5m,250010.5m,6);
        var server=LocalLineServer.StartAsync(repository:repo).GetAwaiter().GetResult();using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=TestLogin.ClientAsync(http,server,"owner").GetAwaiter().GetResult();var main=Window(client);
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(main.Dispatcher));
        try
        {
            main.Show();main.UpdateLayout();Assert.IsTrue(Wait(main.InitializeLinesAsync()));var map=(CoordinateMapView)main.FindName("MapView");map.ResetView();map.ZoomAtCenter(8);
            ((Button)main.FindName("ConnectLogicalNodesButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsTrue(map.IsConnectingNodes);
            Assert.IsFalse(map.PickConnectionPoint(new(0,0)));Assert.IsTrue(map.PickConnectionPoint(new((double)a.X-1,(double)a.Y+1)));Assert.IsFalse(map.PickConnectionPoint(new((double)a.X,(double)a.Y)));StringAssert.Contains(map.OperationMessage,"同一个 LN");
            main.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
            {
                var dialog=main.OwnedWindows.OfType<LogicalNodeConnectionWindow>().Single();Assert.IsFalse(((Button)dialog.FindName("OtherButton")).IsEnabled);
                ((Button)dialog.FindName("DirectButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Assert.IsTrue(map.PickConnectionPoint(new((double)b.X+1,(double)b.Y-1)));
            LogicalNodeWindowEntryTests.DrainUntil(()=>map.HasConnectionPreview&&!map.IsSaving);Assert.AreEqual(0,repo.Transaction(s=>s.Lines.Count));
            Assert.AreEqual(Visibility.Visible,((Button)main.FindName("ConfirmLogicalNodeConnectionButton")).Visibility);
            Assert.IsFalse(Wait(map.UndoDatabaseAsync()));Assert.IsTrue(map.HasConnectionPreview);Assert.IsFalse(map.BeginPasteSelection("plain text"));Assert.IsTrue(map.HasConnectionPreview);
            main.UpdateLayout();var bitmap=new RenderTargetBitmap((int)main.ActualWidth,(int)main.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(main);
            var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","ln-connection-preview.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);using(var output=File.Create(path))png.Save(output);TestContext.AddResultFile(path);
            Assert.IsTrue(Wait(map.HandleEditorKeyAsync(Key.Enter)));Assert.IsFalse(map.IsConnectingNodes);Assert.AreEqual(1,map.LineCount);
            Assert.AreEqual(a.Properties,repo.Transaction(s=>s.LogicalNodes[a.Code].Properties));Assert.IsTrue(Wait(map.UndoDatabaseAsync()));Assert.AreEqual(0,map.LineCount);
        }
        finally{main.Close();SynchronizationContext.SetSynchronizationContext(previous);server.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
    [STATestMethod]
    public void BusyRequestsCannotDuplicateOrCancel_SavedFailureKeepsPreview_AndBackspaceAndEscapeAreSafe()
    {
        var repo=new InMemoryLineRepository();var a=LogicalNodeConnectionTests.Node(repo,50.5m,50.5m);var b=LogicalNodeConnectionTests.Node(repo,150.5m,70.5m);
        var server=LocalLineServer.StartAsync(repository:repo).GetAwaiter().GetResult();using var initial=new HttpClient{BaseAddress=server.BaseAddress};var session=TestLogin.ClientAsync(initial,server,"owner").GetAwaiter().GetResult().Session;
        using var gate=new SaveGate();using var http=new HttpClient(gate){BaseAddress=server.BaseAddress};var client=new HttpLineClient(http,session);var map=new CoordinateMapView();map.ConfigureClient(client);
        var previous=SynchronizationContext.Current;SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(map.Dispatcher));
        try
        {
            Assert.IsTrue(Wait(map.RefreshLogicalNodesAsync()));
            Assert.IsTrue(map.BeginLogicalNodeConnection());map.PickConnectionPoint(new((double)a.X,(double)a.Y));map.PickConnectionPoint(new((double)b.X,(double)b.Y));Assert.IsTrue(Wait(map.PreviewLogicalNodeConnectionAsync()));
            var preview=map.ConnectionPreview;var saving=map.SaveLogicalNodeConnectionAsync();LogicalNodeWindowEntryTests.DrainUntil(()=>gate.Saves==1);Assert.IsTrue(map.IsSaving);
            map.CancelLogicalNodeConnection();Assert.IsTrue(map.IsConnectingNodes);Assert.IsFalse(Wait(map.SaveLogicalNodeConnectionAsync()));Assert.IsTrue(Wait(map.HandleEditorKeyAsync(Key.Back)));Assert.AreSame(preview,map.ConnectionPreview);Assert.AreEqual(1,gate.Saves);
            gate.Resume.SetResult(true);Assert.IsFalse(Wait(saving));Assert.IsTrue(map.HasConnectionPreview);StringAssert.Contains(map.OperationMessage,"未确认成功");Assert.AreEqual(0,repo.Transaction(s=>s.Lines.Count));
            Wait(map.HandleEditorKeyAsync(Key.Back));Assert.IsFalse(map.HasConnectionPreview);Assert.IsTrue(map.IsConnectingNodes);Wait(map.HandleEditorKeyAsync(Key.Escape));Assert.IsFalse(map.IsConnectingNodes);
        }
        finally{gate.Resume.TrySetResult(true);SynchronizationContext.SetSynchronizationContext(previous);server.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
    private sealed class SaveGate:DelegatingHandler
    {
        internal int Saves;internal readonly TaskCompletionSource<bool> Resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SaveGate():base(new HttpClientHandler()){}
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            if(request.RequestUri!.AbsolutePath=="/SaveLogicalNodeConnection")
            {Saves++;await Resume.Task.WaitAsync(cancellationToken);return new(System.Net.HttpStatusCode.OK){Content=JsonContent.Create(ServiceResult<LogicalNodeConnectionResult>.Fail("InjectedFailure","模拟保存失败"))};}
            return await base.SendAsync(request,cancellationToken);
        }
    }
}
