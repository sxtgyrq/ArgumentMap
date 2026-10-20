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
public sealed class FlowDiagramLogicTests
{
    internal static void Invalidate(JsonObject node,string kind)
    {
        if(kind=="missing")node.Remove("logic");
        else node["logic"]=kind switch{"null"=>null,"empty"=>"",_=>" \t\r\n\u3000\u00a0"};
    }

    [TestMethod]
    [DataRow(6,"missing")][DataRow(6,"null")][DataRow(6,"empty")][DataRow(6,"whitespace")]
    [DataRow(12,"missing")][DataRow(12,"null")][DataRow(12,"empty")][DataRow(12,"whitespace")]
    [DataRow(18,"missing")][DataRow(18,"null")][DataRow(18,"empty")][DataRow(18,"whitespace")]
    public void NonBranchSizesRequireLogic_ParserPreviewAndImportRejectWithoutChangingState(int size,string kind)
    {
        var map=new TestMap();var d=FlowDiagramTests.Small();var node=d["ln"]![0]!.AsObject();node["size"]=size;Invalidate(node,kind);var text=d.ToJsonString();
        var parsed=FlowDiagramLanguage.Parse(text);Assert.IsFalse(parsed.Success);Assert.AreEqual("InvalidFlowText",parsed.Code);StringAssert.Contains(parsed.Message,"LN S");StringAssert.Contains(parsed.Message,"逻辑语言 logic 必填");
        var service=new FlowDiagramService(map.Repository);var request=new FlowDiagramRequest("owner",text,1200.5m,5000.5m);var before=map.Snapshot();
        Assert.AreEqual("InvalidFlowText",service.Preview(request).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual("InvalidFlowText",service.Import(request).Code);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow(1,true),DataRow(255,true),DataRow(256,false)]
    public void LogicLengthBoundariesAreEnforced_ValidTextIsPreserved(int length,bool valid)
    {
        var d=FlowDiagramTests.Small();var logic=new string('P',length);d["ln"]![0]!["logic"]=logic;
        var map=new TestMap();var result=new FlowDiagramService(map.Repository).Import(new("owner",d.ToJsonString(),1200.5m,5000.5m));Assert.AreEqual(valid,result.Success,result.Message);
        if(valid)Assert.AreEqual(logic,result.Data!.Added.LogicalNodes.Single().Properties.LogicLanguage);
        else{StringAssert.Contains(result.Message,"255");Assert.AreEqual(0,map.Repository.Transaction(s=>s.LogicalNodes.Count+s.Lines.Count));}
    }

    [TestMethod]
    public void InvalidLastNodeRejectsWholeBatch_RepairPreservesUnicodeLogicAndAllBranchLogic()
    {
        var map=new TestMap();var d=JsonNode.Parse(FlowDiagramGuideContent.BranchExample)!.AsObject();var last=d["ln"]!.AsArray().Last()!.AsObject();last["logic"]="";var before=map.Snapshot();
        var service=new FlowDiagramService(map.Repository);var result=service.Import(new("owner",d.ToJsonString(),1200.5m,5000.5m));Assert.IsFalse(result.Success);StringAssert.Contains(result.Message,"LN B");Assert.AreEqual(before,map.Snapshot());
        last["logic"]="  (S ∧ ¬P) ⇒ B\n";result=service.Import(new("owner",d.ToJsonString(),1200.5m,5000.5m));Assert.IsFalse(result.Success);Assert.AreEqual(before,map.Snapshot());
        const string logic="  (S ∧ ¬P) ⇒ B ";last["logic"]=logic;result=service.Import(new("owner",d.ToJsonString(),1200.5m,5000.5m));Assert.IsTrue(result.Success,result.Message);
        Assert.IsTrue(result.Data!.Added.LogicalNodes.All(n=>!string.IsNullOrWhiteSpace(n.Properties.LogicLanguage)));
        Assert.AreEqual(logic,result.Data.Added.LogicalNodes.Single(n=>n.Code==result.Data.LogicalNodeCodes["B"]).Properties.LogicLanguage);
        Assert.AreEqual("P",result.Data.Added.LogicalNodes.Single(n=>n.Code==result.Data.LogicalNodeCodes["Yes"]).Properties.LogicLanguage);
        Assert.AreEqual("¬P",result.Data.Added.LogicalNodes.Single(n=>n.Code==result.Data.LogicalNodeCodes["No"]).Properties.LogicLanguage);
    }

    [STATestMethod,DataRow("missing"),DataRow("null"),DataRow("empty"),DataRow("whitespace")]
    public void ImportWindowBlocksMissingLogicWithNodeId_ClearsPreviewAndAllowsRepair(string kind)
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var map=new CoordinateMapView();var client=new HttpLineClient(http,new("owner","test"));map.ConfigureClient(client);Assert.IsTrue(map.BeginFlowDiagramImport());
        var window=new FlowDiagramImportWindow(client,map){Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();Assert.IsTrue(((TextBlock)window.FindName("LogicRequirementHint")).IsVisible);var source=(TextBox)window.FindName("SourceText");source.Text=FlowDiagramGuideContent.BranchExample;
            Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());Assert.AreEqual(1,stub.FlowPreviews);Assert.IsTrue(((Button)window.FindName("ImportButton")).IsEnabled);
            var invalid=JsonNode.Parse(source.Text)!;Invalidate(invalid["ln"]![1]!.AsObject(),kind);source.Text=invalid.ToJsonString();Assert.IsFalse(window.StartMapPick());Assert.IsFalse(window.PreviewAsync().GetAwaiter().GetResult());
            StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"LN P");StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"逻辑语言 logic 必填");Assert.IsFalse(((Button)window.FindName("ImportButton")).IsEnabled);
            Assert.IsFalse(window.ImportAsync().GetAwaiter().GetResult());Assert.AreEqual(1,stub.FlowPreviews);Assert.AreEqual(0,stub.FlowImports);Assert.AreEqual(0,stub.Repository.Transaction(s=>s.LogicalNodes.Count+s.Lines.Count+s.Crosses.Count));Assert.IsTrue(window.IsVisible);
            source.Text=FlowDiagramGuideContent.BranchExample;Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());Assert.IsTrue(window.ImportAsync().GetAwaiter().GetResult());Assert.AreEqual(1,stub.FlowImports);
            Assert.IsTrue(stub.Repository.Transaction(s=>s.LogicalNodes.Values.All(n=>!string.IsNullOrWhiteSpace(n.Properties.LogicLanguage))));
        }
        finally{window.Close();}
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class FlowDiagramLogicMySqlTests
{
    [TestMethod]
    public Task DirectHttpRejectsMissingLogicInPreviewAndCommit_PreservesDatabase_RepairPersistsAcrossRestart()=>LogicalNodeMySqlTests.Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);LogicalNodeMySqlTests.Add(repository,90,110,130,110);
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var client=await TestLogin.ClientAsync(http,server,"owner");
        var text=FlowDiagramGuideContent.BranchExample;Assert.IsTrue((await client.PreviewFlowDiagramAsync(text,1200.5m,5000.5m)).Success);var before=LineTopologyMySqlTests.RawSnapshot(settings);
        foreach(var kind in new[]{"missing","null","empty","whitespace"})
        {
            var d=JsonNode.Parse(text)!;FlowDiagramLogicTests.Invalidate(d["ln"]![1]!.AsObject(),kind);
            foreach(var result in new[]{await client.PreviewFlowDiagramAsync(d.ToJsonString(),1200.5m,5000.5m),await client.ImportFlowDiagramAsync(d.ToJsonString(),1200.5m,5000.5m)})
            {Assert.IsFalse(result.Success);Assert.AreEqual("InvalidFlowText",result.Code);StringAssert.Contains(result.Message,"LN P");StringAssert.Contains(result.Message,"逻辑语言 logic 必填");}
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        }
        var longText=JsonNode.Parse(text)!;longText["ln"]![0]!["logic"]=new string('P',256);Assert.AreEqual("InvalidFlowText",(await client.ImportFlowDiagramAsync(longText.ToJsonString(),1200.5m,5000.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var expired=new HttpLineClient(http,new("owner","invalid"));Assert.AreEqual("LoginExpired",(await expired.ImportFlowDiagramAsync(longText.ToJsonString(),1200.5m,5000.5m)).Code);
        var accepted=JsonNode.Parse(text)!;var boundaryLogic="(S ∧ ¬P) ⇒ B"+new string(' ',255-"(S ∧ ¬P) ⇒ B".Length);accepted["ln"]![5]!["logic"]=boundaryLogic;
        var imported=await client.ImportFlowDiagramAsync(accepted.ToJsonString(),1200.5m,5000.5m);Assert.IsTrue(imported.Success,imported.Message);
        var reopened=new LogicalNodeService(new MySqlLineRepository(settings)).GetAll("owner").Data!;
        Assert.IsTrue(reopened.All(n=>!string.IsNullOrWhiteSpace(n.Properties.LogicLanguage)));Assert.AreEqual(boundaryLogic,reopened.Single(n=>n.Code==imported.Data!.LogicalNodeCodes["B"]).Properties.LogicLanguage);
        Assert.AreEqual("P",reopened.Single(n=>n.Code==imported.Data!.LogicalNodeCodes["Yes"]).Properties.LogicLanguage);Assert.AreEqual("¬P",reopened.Single(n=>n.Code==imported.Data!.LogicalNodeCodes["No"]).Properties.LogicLanguage);
    });
}
