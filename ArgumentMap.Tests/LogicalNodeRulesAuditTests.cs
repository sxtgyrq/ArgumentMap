using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;
using static ArgumentMap.Tests.FlowDiagramLnAuditTests;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeRulesAuditTests
{
    internal static JsonObject MixedDocument()
    {
        var document=Document();document["ln"]![0]!["logic"]="P";
        document["ln"]!.AsArray().Add(new JsonObject{["id"]="Equal",["at"]=new JsonArray(200.5m,.5m),["size"]=8,["value"]="=",["logic"]="="});
        document["ln"]!.AsArray().Add(new JsonObject{["id"]="Implies",["at"]=new JsonArray(240.5m,.5m),["size"]=8,["value"]="→",["logic"]="→"});
        document["roads"]![0]!["points"]![1]![0]=280;
        return document;
    }
    internal static void SeedExisting(ILineRepository repository)
    {
        Seed(repository,6,"s","P",50.5m);Seed(repository,12,"s","P",90.5m);Seed(repository,4,"True","P",130.5m);Seed(repository,4,"False","¬P",170.5m);Seed(repository,8,"=","P",210.5m);
    }
    internal static void AssertBatch(FlowDiagramResult result,string value,string logic)
    {
        Assert.AreEqual(value,Node(result,"Fact").Properties.Value);Assert.AreEqual("P",Node(result,"Fact").Properties.LogicLanguage);
        Assert.AreEqual("s",Node(result,"Decision").Properties.Value);Assert.AreEqual(logic,Node(result,"Decision").Properties.LogicLanguage);
        Assert.AreEqual(logic,Node(result,"Yes").Properties.LogicLanguage);Assert.AreEqual("¬"+logic,Node(result,"No").Properties.LogicLanguage);Assert.AreEqual("",Node(result,"Blank").Properties.LogicLanguage);
        Assert.AreEqual("=",Node(result,"Equal").Properties.Value);Assert.AreEqual("→",Node(result,"Implies").Properties.Value);
        Assert.AreEqual("=",Node(result,"Equal").Properties.LogicLanguage);Assert.AreEqual("→",Node(result,"Implies").Properties.LogicLanguage);
        Assert.IsTrue(result.Added.LogicalNodes.All(n=>n.Positions.Length==1));
    }

    [TestMethod]
    public void AllFourTypesShareDisplayOrLogicText_OnlyTheirOwnUniqueFieldsAreNumbered_ReferencesRemainSeparate()
    {
        var map=new TestMap();SeedExisting(map.Repository);var service=new FlowDiagramService(map.Repository);var request=new FlowDiagramRequest("alice",MixedDocument().ToJsonString(),500.5m,500.5m);var before=map.Snapshot();
        var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);AssertBatch(preview.Data!,"S1","P1");Assert.AreEqual(before,map.Snapshot());
        var first=service.Import(request);Assert.IsTrue(first.Success,first.Message);AssertBatch(first.Data!,"S1","P1");
        var second=service.Import(request with{X=1000.5m});Assert.IsTrue(second.Success,second.Message);AssertBatch(second.Data!,"S2","P2");
        var nodes=new LogicalNodeService(map.Repository);var source=Node(first.Data!,"Decision");var stable=nodes.GetAll().Data!.Where(n=>n.SideLength is 8 or 12).ToDictionary(n=>n.Code);
        var rename=nodes.Update(new(source.Code,"alice",source.Version,"s","",LogicLanguage:"R"));Assert.IsTrue(rename.Success,rename.Message);
        var all=nodes.GetAll().Data!.ToDictionary(n=>n.Code);Assert.AreEqual("R",all[Node(first.Data!,"Yes").Code].Properties.LogicLanguage);Assert.AreEqual("¬R",all[Node(first.Data!,"No").Code].Properties.LogicLanguage);
        Assert.AreEqual("P2",all[Node(second.Data!,"Yes").Code].Properties.LogicLanguage);
        Assert.IsTrue(nodes.Delete(new(source.Code,"alice",rename.Data!.Version)).Success);all=nodes.GetAll().Data!.ToDictionary(n=>n.Code);
        Assert.AreEqual("True",all[Node(first.Data!,"Yes").Code].Properties.Value);Assert.AreEqual("False",all[Node(first.Data!,"No").Code].Properties.Value);
        Assert.AreEqual("",all[Node(first.Data!,"Yes").Code].Properties.LogicLanguage);Assert.AreEqual("",all[Node(first.Data!,"No").Code].Properties.LogicLanguage);
        foreach(var node in stable.Values){Assert.AreEqual(node.Properties,all[node.Code].Properties);Assert.AreEqual(node.Version,all[node.Code].Version);}
        Assert.AreEqual("P",all.Values.Single(n=>n.Owner=="bob"&&n.SideLength==4&&n.Properties.Value=="True").Properties.LogicLanguage);
        Assert.IsNull(LogicalNodeBranchRules.Validate(all.Values));
    }

    [TestMethod,DataRow("ln12-empty"),DataRow("ln6-empty"),DataRow("ln8-value"),DataRow("ln4-value"),DataRow("ln4-pair"),DataRow("ln4-only-ln12-source"),DataRow("ln4-only-ln8-source")]
    public void InvalidMixedImportRejectsWholeBatch_InPreviewAndCommit(string scenario)
    {
        var map=new TestMap();var d=MixedDocument();
        switch(scenario)
        {
            case "ln12-empty":d["ln"]![0]!["logic"]=" ";break;
            case "ln6-empty":d["ln"]![4]!["logic"]="";break;
            case "ln8-value":d["ln"]![5]!["value"]="蕴含";break;
            case "ln4-value":d["ln"]![1]!["value"]="true";break;
            case "ln4-pair":d["ln"]![2]!["logic"]="P";break;
            default:
                d["ln"]![4]!["logic"]="Q";
                d["ln"]![0]!["logic"]=scenario=="ln4-only-ln12-source"?"P":"Q";
                if(scenario=="ln4-only-ln8-source"){d["ln"]![1]!["logic"]="=";d["ln"]![2]!["logic"]="¬=";}
                break;
        }
        var service=new FlowDiagramService(map.Repository);var request=new FlowDiagramRequest("alice",d.ToJsonString(),500.5m,500.5m);var before=map.Snapshot();
        foreach(var result in new[]{service.Preview(request),service.Import(request)}){Assert.IsFalse(result.Success,scenario);Assert.AreEqual(before,map.Snapshot(),scenario);}
    }

    [STATestMethod]
    public void ImportHintsIncludeAllFourTypes_AndDoNotRequireLogicForLn8()
    {
        var guide=FlowDiagramGuideContent.FullText;
        var prompt=guide[guide.LastIndexOf("> 请按 ArgumentMap Flow v1",StringComparison.Ordinal)..];
        foreach(var term in new[]{"4/6/8/10/12/18","全图 LN8 数量不限","logic 必须与 value 完全一致","不自动检查两端类型"})StringAssert.Contains(prompt,term);
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));var map=new CoordinateMapView();map.ConfigureClient(client);Assert.IsTrue(map.BeginFlowDiagramImport());var window=new FlowDiagramImportWindow(client,map);
        try
        {
            var logic=((TextBlock)window.FindName("LogicRequirementHint")).Text;var unique=((TextBlock)window.FindName("UniquenessRequirementHint")).Text;
            foreach(var term in new[]{"LN6、LN12、LN18","True","False","LN8","=／→","logic 必须与 value 完全一致","所有 logic 均不允许 null"})StringAssert.Contains(logic,term);
            foreach(var term in new[]{"LN12","转大写","logic 可重复","LN6","区分大小写","仅 LN12","LN4、LN8","LN8 全图数量不限"})StringAssert.Contains(unique,term);
            var document=MixedDocument();document["ln"]![5]!.AsObject().Remove("logic");document["ln"]![6]!["logic"]="";((TextBox)window.FindName("SourceText")).Text=document.ToJsonString();
            Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());Assert.AreEqual(0,stub.Repository.Transaction(s=>s.LogicalNodes.Count));Assert.IsTrue(window.ImportAsync().GetAwaiter().GetResult());
            Assert.IsTrue(new LogicalNodeService(stub.Repository).GetAll().Data!.Where(n=>n.SideLength==8).All(n=>n.Properties.LogicLanguage==n.Properties.Value));
        }
        finally{window.Close();}
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LogicalNodeRulesAuditMySqlTests
{
    [TestMethod]
    public Task MixedImportAndLn6ReferenceChanges_AreAtomicAcrossAllFourTypes_AndSurviveConcurrentUsersAndRestart()=>LogicalNodeMySqlTests.Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);LogicalNodeRulesAuditTests.SeedExisting(repository);await using var first=await LocalLineServer.StartAsync(repository:repository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=first.BaseAddress};using var h2=new HttpClient{BaseAddress=second.BaseAddress};var alice=await TestLogin.ClientAsync(h1,first,"alice");var bob=await TestLogin.ClientAsync(h2,second,"bob");var text=LogicalNodeRulesAuditTests.MixedDocument().ToJsonString();
        var before=LineTopologyMySqlTests.RawSnapshot(settings);
        LogicalNodeMySqlTests.Sql(settings,"CREATE TRIGGER TestMixedLnInsert BEFORE INSERT ON LogicalNodes FOR EACH ROW BEGIN IF NEW.SideLength=8 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected mixed LN8 failure'; END IF; END");
        try{Assert.AreEqual("DatabaseError",(await alice.ImportFlowDiagramAsync(text,500.5m,500.5m)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{LogicalNodeMySqlTests.Sql(settings,"DROP TRIGGER TestMixedLnInsert");}
        var results=await Task.WhenAll(alice.ImportFlowDiagramAsync(text,500.5m,500.5m),bob.ImportFlowDiagramAsync(text,1000.5m,500.5m));Assert.IsTrue(results.All(r=>r.Success),string.Join(" ",results.Select(r=>r.Message)));
        foreach(var result in results)
        {var logic=Node(result.Data!,"Decision").Properties.LogicLanguage;LogicalNodeRulesAuditTests.AssertBatch(result.Data!,"S"+logic[1..],logic);}
        CollectionAssert.AreEquivalent(new[]{"P1","P2"},results.Select(r=>Node(r.Data!,"Decision").Properties.LogicLanguage).ToArray());
        var imported=results[0].Data!;var source=Node(imported,"Decision");var branch=Node(imported,"No");Assert.IsTrue(LineCode.IsValid(branch.Code));before=LineTopologyMySqlTests.RawSnapshot(settings);
        LogicalNodeMySqlTests.Sql(settings,$"CREATE TRIGGER TestMixedLnUpdate BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='{branch.Code}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN4 sync failure'; END IF; END");
        try{Assert.AreEqual("DatabaseError",(await alice.UpdateLogicalNodeAsync(source.Code,source.Version,"s","","R")).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{LogicalNodeMySqlTests.Sql(settings,"DROP TRIGGER TestMixedLnUpdate");}
        var renamed=await alice.UpdateLogicalNodeAsync(source.Code,source.Version,"s","","R");Assert.IsTrue(renamed.Success,renamed.Message);var nodes=(await alice.GetLogicalNodesAsync()).Data!.ToDictionary(n=>n.Code);
        Assert.AreEqual("R",nodes[Node(imported,"Yes").Code].Properties.LogicLanguage);Assert.AreEqual("¬R",nodes[branch.Code].Properties.LogicLanguage);
        Assert.AreEqual("P",nodes[Node(imported,"Fact").Code].Properties.LogicLanguage);Assert.AreEqual("→",nodes[Node(imported,"Implies").Code].Properties.LogicLanguage);
        before=LineTopologyMySqlTests.RawSnapshot(settings);
        LogicalNodeMySqlTests.Sql(settings,$"CREATE TRIGGER TestMixedLnDelete BEFORE UPDATE ON LogicalNodes FOR EACH ROW BEGIN IF OLD.Code='{branch.Code}' THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected LN4 clear failure'; END IF; END");
        try{Assert.AreEqual("DatabaseError",(await alice.DeleteLogicalNodeAsync(source.Code,renamed.Data!.Version)).Code);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));}
        finally{LogicalNodeMySqlTests.Sql(settings,"DROP TRIGGER TestMixedLnDelete");}
        Assert.IsTrue((await alice.DeleteLogicalNodeAsync(source.Code,renamed.Data!.Version)).Success);nodes=(await alice.GetLogicalNodesAsync()).Data!.ToDictionary(n=>n.Code);
        Assert.AreEqual("True",nodes[Node(imported,"Yes").Code].Properties.Value);Assert.AreEqual("",nodes[Node(imported,"Yes").Code].Properties.LogicLanguage);Assert.AreEqual("False",nodes[branch.Code].Properties.Value);Assert.AreEqual("",nodes[branch.Code].Properties.LogicLanguage);
        Assert.AreEqual(LogicalNodeMySqlTests.Snapshot(repository),LogicalNodeMySqlTests.Snapshot(new MySqlLineRepository(settings)));
    });
}
