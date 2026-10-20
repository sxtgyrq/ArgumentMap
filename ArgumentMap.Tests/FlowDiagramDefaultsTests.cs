using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class FlowDiagramDefaultsTests
{
    [TestMethod,DataRow(-1,100),DataRow(5,5),DataRow(100,100),DataRow(500,500)]
    public void OmittedSpeedUses100_ExplicitSpeedsArePreservedInPreviewAndImport(int specified,int expected)
    {
        var d=FlowDiagramTests.Small();if(specified>=0)d["roads"]![0]!["speed"]=specified;
        var text=d.ToJsonString();Assert.AreEqual(expected,FlowDiagramLanguage.Parse(text).Data!.Roads.Single().Speed);
        var repository=new InMemoryLineRepository();var service=new FlowDiagramService(repository);var request=new FlowDiagramRequest("owner",text,1200.5m,5000.5m);
        var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(expected,preview.Data!.Added.Lines.Single().Properties.Speed);
        Assert.AreEqual(0,repository.Transaction(s=>s.Lines.Count));
        var imported=service.Import(request);Assert.IsTrue(imported.Success,imported.Message);Assert.AreEqual(expected,imported.Data!.Map!.Lines.Single().Properties.Speed);
    }

    [STATestMethod]
    public void ImportWindowExplainsDefaults_AndLoadedExampleUses100()
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var map=new CoordinateMapView();var client=new HttpLineClient(http,new("owner","test"));map.ConfigureClient(client);Assert.IsTrue(map.BeginFlowDiagramImport());
        var window=new FlowDiagramImportWindow(client,map){Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();var hint=(TextBlock)window.FindName("ImportDefaultsHint");Assert.IsTrue(hint.IsVisible);StringAssert.Contains(hint.Text,"默认速度 100");StringAssert.Contains(hint.Text,"尽量避免单向换路");
            ((Button)window.FindName("ExampleButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var parsed=FlowDiagramLanguage.Parse(((TextBox)window.FindName("SourceText")).Text);Assert.IsTrue(parsed.Success,parsed.Message);Assert.IsTrue(parsed.Data!.Roads.All(r=>r.Speed==100));
            Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());Assert.IsTrue(window.ImportAsync().GetAwaiter().GetResult());
            Assert.IsTrue(stub.Repository.Transaction(s=>s.Lines.Values.All(l=>l.Properties.Speed==100)));
        }
        finally{window.Close();}
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class FlowDiagramDefaultsMySqlTests
{
    [TestMethod]
    public Task HttpPersistsOmittedSpeed100_ExplicitSpeedAndConnectedCross_WithoutChangingExistingRoad()=>LogicalNodeMySqlTests.Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);var lines=new LineSaveService(repository);
        var old=LogicalNodeMySqlTests.Add(repository,90,110,130,110);var code=old.Code;
        var saved=lines.SaveProperties(new(code,"owner",old.Version,old.Properties with{Speed=37}));Assert.IsTrue(saved.Success,saved.Message);
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var d=JsonNode.Parse(FlowDiagramGuideContent.BranchExample)!;d["roads"]![0]!.AsObject().Remove("speed");d["roads"]![1]!["speed"]=5;
        var text=d.ToJsonString();var before=LineTopologyMySqlTests.RawSnapshot(settings);var preview=await client.PreviewFlowDiagramAsync(text,1200.5m,5000.5m);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var imported=await client.ImportFlowDiagramAsync(text,1200.5m,5000.5m);Assert.IsTrue(imported.Success,imported.Message);var result=imported.Data!;
        var reopened=new MySqlLineRepository(settings);reopened.Transaction(s=>
        {
            Assert.AreEqual(100,s.Lines[result.RoadCodes["main"]].Properties.Speed);Assert.AreEqual(5,s.Lines[result.RoadCodes["negative"]].Properties.Speed);Assert.AreEqual(37,s.Lines[code].Properties.Speed);
            var cross=s.Crosses.Values.Single();Assert.AreEqual(CrossState.Connected,cross.State);Assert.IsTrue(cross.AllowsTransfer(result.RoadCodes["main"],result.RoadCodes["negative"]));Assert.IsTrue(cross.AllowsTransfer(result.RoadCodes["negative"],result.RoadCodes["main"]));return 0;
        });
        Assert.IsTrue(new CrossService(reopened).Recalculate().Success);Assert.IsTrue(new LogicalNodeService(reopened).Calculate().Success);
        Assert.AreEqual(CrossState.Connected,reopened.Transaction(s=>s.Crosses.Values.Single().State));
    });
}
