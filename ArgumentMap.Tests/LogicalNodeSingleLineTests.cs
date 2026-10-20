using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeSingleLineTests
{
    [TestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(10),DataRow(12),DataRow(18)]
    public void CreateAndUpdateRejectLineBreaksBeforeTypeNormalization_InfoStillMultiline(int size)
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);if(size==10)LogicalNodeInstanceTests.Parent(map.Repository);
        var p=new LogicalNodeProperties{Value=size switch{4=>"False",8=>"↔",10=>"people",_=>"NODE"},LogicLanguage=size==4?"":"P",Info="一\r\n二"};
        var valid=service.Create(new("owner",100.5m,100.5m,size,p));Assert.IsTrue(valid.Success,valid.Message);var node=valid.Data!;Assert.AreEqual(p.Info,node.Properties.Info);
        var before=map.Snapshot();
        foreach(var separator in new[]{"\r","\n","\r\n","\v","\f","\u0085","\u2028","\u2029"})
        foreach(var field in new[]{"value","logic"})
        {
            var invalid=field=="value"?p with{Value="A"+separator+"B"}:p with{LogicLanguage="P"+separator+"Q"};
            Assert.AreEqual(LogicalNodeTextRules.ErrorCode,service.Create(new("owner",200.5m,200.5m,size,invalid)).Code);
            Assert.AreEqual(LogicalNodeTextRules.ErrorCode,service.Update(new(node.Code,"owner",node.Version,invalid.Value,p.Info,LogicLanguage:invalid.LogicLanguage)).Code);
            Assert.AreEqual(before,map.Snapshot());
        }
        var edited=service.Update(new(node.Code,"owner",node.Version,node.Properties.Value,"三\n四",LogicLanguage:node.Properties.LogicLanguage));Assert.IsTrue(edited.Success,edited.Message);Assert.AreEqual("三\n四",edited.Data!.Properties.Info);
    }

    [TestMethod]
    public void ImportAndBothClipboardFormatsRejectMultilineFields_ButRetainMultilineInfo()
    {
        var map=new TestMap();var before=map.Snapshot();
        foreach(var field in new[]{"value","logic"})
        {
            var doc=FlowDiagramTests.Small();doc["ln"]![0]![field]="P\r\nQ";
            Assert.AreEqual("InvalidFlowText",FlowDiagramTests.Import(map,doc).Code);Assert.AreEqual(before,map.Snapshot());
            var text=JsonSerializer.Serialize(new{format="argumentmap.ln/1",size=6,value=field=="value"?"P\nQ":"N",logic=field=="logic"?"P\nQ":"P",info="一\n二"});Assert.IsNull(LogicalNodeClipboard.Decode(text));
        }
        var legacy=Ln4Clipboard.Encode(new(){Value="True",LogicLanguage="P\nQ"});Assert.IsNull(LogicalNodeClipboard.Decode(legacy));
        var p=new LogicalNodeProperties{Value="N",LogicLanguage="P",Info="一\n二"};Assert.AreEqual(p,LogicalNodeClipboard.Decode(LogicalNodeClipboard.Encode(6,p))!.Properties);
        var allowed=FlowDiagramTests.Small();allowed["ln"]![0]!["info"]=p.Info;var imported=FlowDiagramTests.Import(map,allowed);Assert.IsTrue(imported.Success,imported.Message);Assert.AreEqual(p.Info,imported.Data!.Added.LogicalNodes.Single().Properties.Info);
    }

    [TestMethod]
    public void InMemoryDalRejectsBypassAndRollsBackOtherChanges()
    {
        var map=new TestMap();var node=new LogicalNodeService(map.Repository).Create(new("owner",100.5m,100.5m,6,new(){LogicLanguage="P"})).Data!;var before=map.Snapshot();
        foreach(var isNew in new[]{false,true})
        Assert.ThrowsExactly<InvalidLogicalNodeTextException>(()=>map.Repository.Transaction(state=>
        {
            state.LogicalNodes[node.Code]=node with{Properties=node.Properties with{Info="must roll back"}};
            var changed=node with{Code=isNew?"ABCDEFGHIJ":node.Code,X=isNew?200.5m:node.X,Properties=node.Properties with{LogicLanguage="P\nQ"}};
            state.LogicalNodes[changed.Code]=changed;return true;
        }));
        Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,TestCategory("MySql")]
    public Task MySqlDalRollsBackBypassAndLeavesLegacyRowsUsableForMoveAndKJ()=>LogicalNodeMySqlTests.Isolated(settings=>
    {
        var repository=new MySqlLineRepository(settings);var service=new LogicalNodeService(repository);
        var node=service.Create(new("owner",100.5m,100.5m,6,new(){LogicLanguage="P"})).Data!;var other=service.Create(new("owner",200.5m,200.5m,6,new(){LogicLanguage="Q"})).Data!;
        var before=LogicalNodeMySqlTests.Snapshot(repository);
        Assert.ThrowsExactly<InvalidLogicalNodeTextException>(()=>repository.Transaction(state=>
        {
            state.LogicalNodes[other.Code]=other with{Properties=other.Properties with{Info="must roll back"}};
            state.LogicalNodes[node.Code]=node with{Properties=node.Properties with{Value="A\nB"}};return true;
        }));Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(repository));
        LogicalNodeMySqlTests.Sql(settings,$"UPDATE LogicalNodes SET `Value`=CONCAT('A',CHAR(10),'B'),LogicLanguage=CONCAT('P',CHAR(10),'Q') WHERE Code='{node.Code}'");
        node=service.GetAll().Data!.Single(n=>n.Code==node.Code);Assert.AreEqual("P\nQ",node.Properties.LogicLanguage);
        Assert.IsTrue(service.Move(new(node.Code,"owner",node.Version,110.5m,110.5m)).Success);Assert.IsTrue(new CrossService(repository).Calculate().Success);Assert.IsTrue(service.Calculate().Success);
        Assert.IsTrue(service.Update(new(other.Code,"owner",other.Version,"other","一\n二",LogicLanguage:"Q")).Success);
        node=service.GetAll().Data!.Single(n=>n.Code==node.Code);Assert.AreEqual("P\nQ",node.Properties.LogicLanguage);
        var corrected=service.Update(new(node.Code,"owner",node.Version,"A B","一\n二",LogicLanguage:"P Q"));Assert.IsTrue(corrected.Success,corrected.Message);
        Assert.AreEqual("P Q",new LogicalNodeService(new MySqlLineRepository(settings)).GetAll().Data!.Single(n=>n.Code==node.Code).Properties.LogicLanguage);
        return Task.CompletedTask;
    });
}
