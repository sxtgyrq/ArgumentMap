using System.Net.Http;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class FlowDiagramLnAuditTests
{
    internal static JsonObject Document()=>JsonNode.Parse("""
        {"format":"argumentmap.flow/1","origin":"Fact","ln":[
          {"id":"Fact","at":[0.5,0.5],"size":12,"value":"s","logic":"S"},
          {"id":"Yes","at":[80.5,0.5],"size":4,"value":"True","logic":"P"},
          {"id":"No","at":[120.5,0.5],"size":4,"value":"False","logic":"¬P"},
          {"id":"Blank","at":[160.5,0.5],"size":4,"value":"False"},
          {"id":"Decision","at":[40.5,0.5],"size":6,"value":"s","logic":"P"}],
         "roads":[{"id":"main","points":[[-20,0],[180,0]]}],"crosses":[]}
        """)!.AsObject();

    internal static LogicalNodeSnapshot Seed(ILineRepository repository,int size,string value,string logic,decimal x)
    {
        var result=new LogicalNodeService(repository).Create(new("bob",x,50.5m,size,new(){Value=value,LogicLanguage=logic}));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    internal static LogicalNodeSnapshot Node(FlowDiagramResult result,string id)=>result.Added.LogicalNodes.Single(n=>n.Code==result.LogicalNodeCodes[id]);

    [TestMethod,DataRow(false),DataRow(true)]
    public void NumberingChainsBindByOriginalLocalSource_NotNewlyAllocatedTextOrArrayOrder(bool reverse)
    {
        var repository=new InMemoryLineRepository();Seed(repository,6,"已有判断","P",50.5m);
        var d=Document();var nodes=d["ln"]!.AsArray();
        nodes.Add(JsonNode.Parse("""{"id":"NextDecision","at":[200.5,0.5],"size":6,"value":"s","logic":"P1"}"""));
        nodes.Add(JsonNode.Parse("""{"id":"NextNo","at":[240.5,0.5],"size":4,"value":"False","logic":"¬P1"}"""));
        if(reverse)d["ln"]=new JsonArray(nodes.Reverse().Select(n=>n!.DeepClone()).ToArray());
        var result=new FlowDiagramService(repository).Import(new("alice",d.ToJsonString(),500.5m,500.5m));Assert.IsTrue(result.Success,result.Message);
        var data=result.Data!;var p=Node(data,"Decision").Properties.LogicLanguage;var p1=Node(data,"NextDecision").Properties.LogicLanguage;
        Assert.AreNotEqual(p,p1);Assert.AreEqual(p,Node(data,"Yes").Properties.LogicLanguage);Assert.AreEqual("¬"+p,Node(data,"No").Properties.LogicLanguage);
        Assert.AreEqual("¬"+p1,Node(data,"NextNo").Properties.LogicLanguage);Assert.AreEqual("s",Node(data,"Decision").Properties.Value);
        Assert.AreEqual("S",Node(data,"Fact").Properties.Value);Assert.IsNull(repository.Transaction(s=>LogicalNodeBranchRules.Validate(s.LogicalNodes.Values)));
    }

    [TestMethod,DataRow("missing"),DataRow("boolean"),DataRow("lowercase"),DataRow("nullLogic"),DataRow("missingSource"),DataRow("wrongPair")]
    public void InvalidImportedBranchRejectsEntireDiagram_WithAliasAndNoPartialWrites(string kind)
    {
        var map=new TestMap();var d=Document();var branch=d["ln"]![1]!.AsObject();
        switch(kind)
        {
            case "missing":branch.Remove("value");break;
            case "boolean":branch["value"]=true;break;
            case "lowercase":branch["value"]="true";break;
            case "nullLogic":branch["logic"]=null;break;
            case "missingSource":branch["logic"]="p";break;
            default:branch["value"]="False";break;
        }
        var service=new FlowDiagramService(map.Repository);var request=new FlowDiagramRequest("alice",d.ToJsonString(),500.5m,500.5m);var before=map.Snapshot();
        foreach(var result in new[]{service.Preview(request),service.Import(request)})
        {
            Assert.IsFalse(result.Success,kind);Assert.AreEqual(before,map.Snapshot());
            if(kind!="boolean")StringAssert.Contains(result.Message,"LN Yes");
        }
    }

    [TestMethod]
    public void ExternalReferencesAreCaseSensitive_AndWhitespaceBranchIsEmptyWithoutRenameClaim()
    {
        var repository=new InMemoryLineRepository();Seed(repository,6,"外部判断","p",50.5m);var d=Document();
        d["ln"]![1]!["logic"]="p";d["ln"]![2]!["logic"]="¬p";d["ln"]![3]!["logic"]=" \t\u3000";
        var result=new FlowDiagramService(repository).Import(new("alice",d.ToJsonString(),500.5m,500.5m));Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual("p",Node(result.Data!,"Yes").Properties.LogicLanguage);Assert.AreEqual("¬p",Node(result.Data!,"No").Properties.LogicLanguage);
        Assert.AreEqual("P",Node(result.Data!,"Decision").Properties.LogicLanguage);Assert.AreEqual("",Node(result.Data!,"Blank").Properties.LogicLanguage);
        var note=result.Data!.Notes.Single(n=>n.StartsWith("LN Blank：",StringComparison.Ordinal));StringAssert.Contains(note,"空白逻辑语言已规范为空字符串");Assert.IsFalse(note.Contains("引用已同步",StringComparison.Ordinal));
    }

    [TestMethod,DataRow(254,true),DataRow(255,false)]
    public void FalsePrefixLengthLimitRemainsAtomicWhenSourceHasMaximumLength(int length,bool valid)
    {
        var map=new TestMap();var d=Document();var source=new string('P',length);
        d["ln"]![4]!["logic"]=source;d["ln"]![1]!["logic"]=source;d["ln"]![2]!["logic"]="¬"+source;
        var before=map.Snapshot();var result=new FlowDiagramService(map.Repository).Import(new("alice",d.ToJsonString(),500.5m,500.5m));Assert.AreEqual(valid,result.Success,result.Message);
        if(valid)Assert.AreEqual(255,Node(result.Data!,"No").Properties.LogicLanguage.Length);else Assert.AreEqual(before,map.Snapshot());
    }

    [STATestMethod]
    public void ImportWindowExplainsAllThreeSizes_AndAcceptsAnUnboundLn4AlongsideBoundBranches()
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));
        var map=new CoordinateMapView();map.ConfigureClient(client);Assert.IsTrue(map.BeginFlowDiagramImport());
        var window=new FlowDiagramImportWindow(client,map){Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};
        try
        {
            window.Show();var hint=(TextBlock)window.FindName("UniquenessRequirementHint");Assert.IsTrue(hint.IsVisible);
            foreach(var term in new[]{"LN12","转大写","LN6","区分大小写","新增","LN4","预览不预占"})StringAssert.Contains(hint.Text,term);
            ((TextBox)window.FindName("SourceText")).Text=Document().ToJsonString();
            Assert.IsTrue(window.PreviewAsync().GetAwaiter().GetResult());Assert.AreEqual(0,stub.Repository.Transaction(s=>s.LogicalNodes.Count));
            Assert.IsTrue(window.ImportAsync().GetAwaiter().GetResult());
            var nodes=new LogicalNodeService(stub.Repository).GetAll("owner").Data!;Assert.AreEqual(5,nodes.Length);Assert.IsNull(LogicalNodeBranchRules.Validate(nodes));
            Assert.AreEqual(1,nodes.Count(n=>n.SideLength==4&&n.Properties.LogicLanguage==""));Assert.IsTrue(nodes.All(n=>n.Positions.Length==1));
        }
        finally{window.Close();}
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class FlowDiagramLnAuditMySqlTests
{
    [TestMethod]
    public Task TwoUsersImportSameSymbolsConcurrently_EachDiagramKeepsItsOwnBranchBindings()=>LogicalNodeMySqlTests.Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);
        await using var a=await LocalLineServer.StartAsync(repository:repository);await using var b=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=a.BaseAddress};using var h2=new HttpClient{BaseAddress=b.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,a,"alice");var bob=await TestLogin.ClientAsync(h2,b,"bob");var text=FlowDiagramLnAuditTests.Document().ToJsonString();
        var results=await Task.WhenAll(alice.ImportFlowDiagramAsync(text,500.5m,500.5m),bob.ImportFlowDiagramAsync(text,1000.5m,500.5m));
        foreach(var result in results)
        {
            Assert.IsTrue(result.Success,result.Message);var data=result.Data!;var source=FlowDiagramLnAuditTests.Node(data,"Decision").Properties.LogicLanguage;
            Assert.AreEqual(source,FlowDiagramLnAuditTests.Node(data,"Yes").Properties.LogicLanguage);Assert.AreEqual("¬"+source,FlowDiagramLnAuditTests.Node(data,"No").Properties.LogicLanguage);
            Assert.AreEqual("",FlowDiagramLnAuditTests.Node(data,"Blank").Properties.LogicLanguage);Assert.IsTrue(data.Added.LogicalNodes.All(n=>n.Positions.Length==1));
        }
        CollectionAssert.AreEquivalent(new[]{"P","P1"},results.Select(r=>FlowDiagramLnAuditTests.Node(r.Data!,"Decision").Properties.LogicLanguage).ToArray());
        CollectionAssert.AreEquivalent(new[]{"S","S1"},results.Select(r=>FlowDiagramLnAuditTests.Node(r.Data!,"Fact").Properties.Value).ToArray());
        var reopened=new MySqlLineRepository(settings);Assert.IsNull(reopened.Transaction(s=>LogicalNodeBranchRules.Validate(s.LogicalNodes.Values)));
        Assert.AreEqual(10,reopened.Transaction(s=>s.LogicalNodes.Count));Assert.AreEqual(10,reopened.Transaction(s=>s.LogicalNodePositions.Count));
    });

    [TestMethod]
    public Task OtherUserClaimsPreviewedNames_CommitReallocatesAndBindsOnlyNewBranches_LateFailureRollsBack()=>LogicalNodeMySqlTests.Isolated(async settings=>
    {
        var repository=new MySqlLineRepository(settings);FlowDiagramLnAuditTests.Seed(repository,12,"S","Fact",50.5m);FlowDiagramLnAuditTests.Seed(repository,6,"判断","P",100.5m);
        var oldBranch=FlowDiagramLnAuditTests.Seed(repository,4,"False","¬P",150.5m);
        await using var a=await LocalLineServer.StartAsync(repository:repository);await using var b=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var h1=new HttpClient{BaseAddress=a.BaseAddress};using var h2=new HttpClient{BaseAddress=b.BaseAddress};
        var alice=await TestLogin.ClientAsync(h1,a,"alice");var bob=await TestLogin.ClientAsync(h2,b,"bob");var text=FlowDiagramLnAuditTests.Document().ToJsonString();
        var before=LineTopologyMySqlTests.RawSnapshot(settings);var preview=await alice.PreviewFlowDiagramAsync(text,500.5m,500.5m);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        Assert.AreEqual("S1",FlowDiagramLnAuditTests.Node(preview.Data!,"Fact").Properties.Value);Assert.AreEqual("P1",FlowDiagramLnAuditTests.Node(preview.Data!,"Yes").Properties.LogicLanguage);
        Assert.IsTrue((await bob.CreateLogicalNodeAsync(200.5m,50.5m,12,new(){Value="s",LogicLanguage="Fact"})).Success);
        Assert.IsTrue((await bob.CreateLogicalNodeAsync(250.5m,50.5m,6,new(){Value="判断",LogicLanguage="P"})).Success);
        before=LineTopologyMySqlTests.RawSnapshot(settings);
        LogicalNodeMySqlTests.Sql(settings,"CREATE TRIGGER FailAuditBranch BEFORE INSERT ON LogicalNodes FOR EACH ROW BEGIN IF NEW.SideLength=4 THEN SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected branch import failure'; END IF; END");
        try{Assert.AreEqual("DatabaseError",(await alice.ImportFlowDiagramAsync(text,500.5m,500.5m)).Code);}
        finally{LogicalNodeMySqlTests.Sql(settings,"DROP TRIGGER FailAuditBranch");}
        Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));
        var imported=await alice.ImportFlowDiagramAsync(text,500.5m,500.5m);Assert.IsTrue(imported.Success,imported.Message);var data=imported.Data!;
        Assert.AreEqual("S2",FlowDiagramLnAuditTests.Node(data,"Fact").Properties.Value);Assert.AreEqual("P2",FlowDiagramLnAuditTests.Node(data,"Decision").Properties.LogicLanguage);
        Assert.AreEqual("P2",FlowDiagramLnAuditTests.Node(data,"Yes").Properties.LogicLanguage);Assert.AreEqual("¬P2",FlowDiagramLnAuditTests.Node(data,"No").Properties.LogicLanguage);
        Assert.AreEqual("",FlowDiagramLnAuditTests.Node(data,"Blank").Properties.LogicLanguage);Assert.IsTrue(data.Added.LogicalNodes.All(n=>n.Positions.Length==1));
        var reopened=new MySqlLineRepository(settings);Assert.IsNull(reopened.Transaction(s=>LogicalNodeBranchRules.Validate(s.LogicalNodes.Values)));
        var original=reopened.Transaction(s=>s.LogicalNodes[oldBranch.Code]);Assert.AreEqual("¬P",original.Properties.LogicLanguage);Assert.AreEqual(oldBranch.Version,original.Version);
        Assert.AreEqual(LogicalNodeMySqlTests.Snapshot(repository),LogicalNodeMySqlTests.Snapshot(reopened));
    });
}
