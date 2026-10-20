using System.Net.Http;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ArgumentMap.Business;
using ArgumentMap.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeChangedUiTests
{
    [STATestMethod]
    public void BrowserJRecalculatesLnPositions_KRecalculatesCross_TheyUseSeparateRoutes()
    {
        using var handler=new DirectLogicalNodeHttpStub();var road=LogicalNodeMySqlTests.Add(handler.Repository,20,50,80,50);
        var service=new LogicalNodeService(handler.Repository);var node=service.Create(new("owner",50.5m,50.5m,6,new())).Data!;
        handler.Repository.Transaction(s=>{var p=s.LogicalNodePositions.Values.Single();s.LogicalNodePositions[p.Key]=p with{IsChanged=true,RoadPercent=.123};return true;});
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=new MainWindow(new HttpLineClient(http,new("owner","test")))
        {WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();var view=(CoordinateMapView)window.FindName("MapView");Assert.IsFalse(view.IsLogicalNodeMode);
            Press(window,Key.J);LogicalNodeWindowEntryTests.DrainUntil(()=>handler.LogicalNodeCalculations==1&&!view.IsCalculatingLogicalNodes);
            Assert.AreEqual(0,handler.CrossCalculations);var after=service.GetAll().Data!.Single();Assert.IsFalse(after.Positions.Single().IsChanged);Assert.AreEqual(node.Positions.Single(),after.Positions.Single());
            Assert.AreEqual(1,view.LogicalNodeCount);Press(window,Key.K);LogicalNodeWindowEntryTests.DrainUntil(()=>handler.CrossCalculations>0&&!view.IsCalculatingCrosses);
            Assert.AreEqual(1,handler.LogicalNodeCalculations);Assert.AreEqual(after.Version,service.GetAll().Data!.Single().Version);
            Assert.AreEqual(road.Code,service.GetAll().Data!.Single().Positions.Single().RoadCode);
        }
        finally{window.Close();}
    }
    private static void Press(MainWindow window,Key key)
    {var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);}
}
