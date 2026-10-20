using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Rendering")]
public sealed class LogicalNodeWindowEntryTests
{
    private static LogicalNodeWindow? Dialog(MainWindow window)=>(LogicalNodeWindow?)typeof(MainWindow).GetField("logicalNodeWindow",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(window);
    private static MainWindow Window(HttpLineClient client)=>new(client)
    {WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};
    private static void DrainDispatcher()
    {
        var frame=new DispatcherFrame();Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>frame.Continue=false));Dispatcher.PushFrame(frame);
    }

    [STATestMethod,DataRow("keyboard"),DataRow("button"),DataRow("menu"),DataRow("ime"),DataRow("closed-menu")]
    public void ActualMainWindowEntry_SavesDirectlyWithoutOpeningLnDialog(string entry)
    {
        var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var client=new HttpLineClient(http,new("owner","test-session"));var window=Window(client);
        try
        {
            window.Show();DrainDispatcher();var map=(CoordinateMapView)window.FindName("MapView");
            ((CoordinateMapView)window.FindName("MapView")).ToggleLogicalNodeMode();Assert.IsTrue(map.IsLogicalNodeMode);
            if(entry=="button")((Button)window.FindName("NewLogicalNodeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else if(entry=="menu")((MenuItem)window.FindName("NewLogicalNodeMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            else
            {
                if(entry=="closed-menu")
                {
                    var menu=(Menu)window.FindName("MainMenu");((MenuItem)menu.Items[3]).Focus();DrainDispatcher();
                    Assert.IsTrue(menu.IsKeyboardFocusWithin);Assert.IsFalse(menu.Items.Cast<MenuItem>().Any(item=>item.IsSubmenuOpen));
                }
                var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.A){RoutedEvent=Keyboard.PreviewKeyDownEvent};
                if(entry=="ime")typeof(KeyEventArgs).GetMethod("MarkImeProcessed",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(key,null);
                window.RaiseEvent(key);Assert.IsTrue(key.Handled);
            }
            DrainUntil(()=>map.LogicalNodeCount==1);Assert.IsNull(Dialog(window),"New LN entries must save without opening an attribute window.");
            Assert.AreEqual(1,handler.Creates);Assert.AreEqual(250000.5m,handler.LastCreate!.X);
            Assert.AreEqual(1,handler.Repository.Transaction(s=>s.LogicalNodes.Count));
            map.RequestLogicalNodeEdit();DrainUntil(()=>Dialog(window) is not null);
            var dialog=Dialog(window)!;Assert.IsTrue(dialog.IsVisible);Assert.AreSame(window,dialog.Owner);
            Assert.IsFalse(((UIElement)dialog.FindName("GeometryPanel")).IsEnabled);dialog.Close();Assert.IsNull(Dialog(window));
        }
        finally{Dialog(window)?.Close();window.Close();DrainDispatcher();}
    }
    [STATestMethod]
    public void ExpandedMenuKeepsKeyboardNavigation_AndDoesNotOpenLnDialog()
    {
        var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var window=Window(new HttpLineClient(http,new("owner","test-session")));
        try
        {
            window.Show();DrainDispatcher();((CoordinateMapView)window.FindName("MapView")).ToggleLogicalNodeMode();
            var menu=(Menu)window.FindName("MainMenu");var lnMenu=(MenuItem)menu.Items[3];lnMenu.Focus();lnMenu.IsSubmenuOpen=true;DrainDispatcher();Assert.IsTrue(menu.IsKeyboardFocusWithin);
            var key=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window),0,Key.A){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(key);
            Assert.IsFalse(key.Handled);Assert.IsNull(Dialog(window));Assert.AreEqual(0,handler.Creates);lnMenu.IsSubmenuOpen=false;
        }
        finally{Dialog(window)?.Close();window.Close();DrainDispatcher();}
    }
    internal static void DrainUntil(Func<bool> predicate)
    {
        var timer=System.Diagnostics.Stopwatch.StartNew();
        while(!predicate() && timer.Elapsed<TimeSpan.FromSeconds(5)){DrainDispatcher();System.Threading.Thread.Sleep(1);}
        Assert.IsTrue(predicate(),"Timed out waiting for the UI operation.");
    }
}
