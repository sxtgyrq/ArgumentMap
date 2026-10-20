using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeBranchRulesTests
{
    internal static LogicalNodeSnapshot Add(TestMap map,int size,string value,string logic,decimal x,string owner="owner")
    {
        var result=new LogicalNodeService(map.Repository).Create(new(owner,x,50.5m,size,new(){Value=value,LogicLanguage=logic,Info="保留说明"}));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    [TestMethod]
    public void NewLn4DefaultsFalseWithBlankLogic_EmptyLogicIsAllowedForBothValues()
    {
        var map=new TestMap();var result=new LogicalNodeService(map.Repository).Create(new("owner",50.5m,50.5m,4,new()));Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual("False",result.Data!.Properties.Value);Assert.AreEqual("",result.Data.Properties.LogicLanguage);
        Assert.AreEqual("",Add(map,4,"True"," \t",100.5m).Properties.LogicLanguage);
    }
    [TestMethod,DataRow("true"),DataRow("FALSE"),DataRow("是"),DataRow("否"),DataRow(""),DataRow("True "),DataRow("other")]
    public void CreateAndEditRejectValuesOutsideExactTrueFalse_WithoutPartialWrites(string value)
    {
        var map=new TestMap();var node=Add(map,4,"False","",50.5m);var service=new LogicalNodeService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("InvalidBranchValue",service.Create(new("owner",100.5m,50.5m,4,new(){Value=value})).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.AreEqual("InvalidBranchValue",service.Update(new(node.Code,node.Owner,node.Version,value,"changed",LogicLanguage:"")).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow("True","P",true),DataRow("False","¬P",true),DataRow("True","¬P",false),DataRow("False","P",false),DataRow("True","p",false),DataRow("False","¬p",false),DataRow("True","missing",false),DataRow("False","¬",false)]
    public void NonemptyLogicUsesExactUniqueLn6AndMatchingBoolean(string value,string logic,bool valid)
    {
        var map=new TestMap();Add(map,6,"判断","P",50.5m,"another");var service=new LogicalNodeService(map.Repository);var before=map.Snapshot();
        var result=service.Create(new("owner",100.5m,50.5m,4,new(){Value=value,LogicLanguage=logic}));Assert.AreEqual(valid,result.Success,result.Message);
        if(!valid)Assert.AreEqual(before,map.Snapshot());else Assert.AreEqual(logic,result.Data!.Properties.LogicLanguage);
    }
    [TestMethod]
    public void RenameLn6SynchronizesAllReferencesAcrossOwners_AndRejectsStaleBranchEditOrDuplicateSource()
    {
        var map=new TestMap();var source=Add(map,6,"判断","P",50.5m);Add(map,6,"其他判断","Q",250.5m);
        var yes=Add(map,4,"True","P",100.5m,"other");var no=Add(map,4,"False","¬P",150.5m);var other=Add(map,4,"False","¬Q",300.5m);
        var service=new LogicalNodeService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("DuplicateLogicalNodeLogic",service.Update(new(source.Code,"owner",source.Version,"changed","",LogicLanguage:"Q")).Code);Assert.AreEqual(before,map.Snapshot());
        var renamed=service.Update(new(source.Code,"owner",source.Version,"新判断","新说明",LogicLanguage:"R ∧ S"));Assert.IsTrue(renamed.Success,renamed.Message);
        var nodes=map.Repository.Transaction(s=>s.LogicalNodes);Assert.AreEqual("R ∧ S",nodes[yes.Code].Properties.LogicLanguage);Assert.AreEqual("¬R ∧ S",nodes[no.Code].Properties.LogicLanguage);
        foreach(var old in new[]{yes,no}){var current=nodes[old.Code];Assert.AreEqual(old.Version+1,current.Version);Assert.AreEqual(old.Owner,current.Owner);Assert.AreEqual(old.Properties.Value,current.Properties.Value);Assert.AreEqual(old.Properties.Info,current.Properties.Info);Assert.AreEqual(old.X,current.X);CollectionAssert.AreEqual(old.Positions,current.Positions);}
        Assert.AreEqual(other,nodes[other.Code] with{CanEdit=true});
        Assert.AreEqual("VersionConflict",service.Update(new(no.Code,no.Owner,no.Version,"False","",LogicLanguage:"")).Code);
        var version=nodes[no.Code].Version;Assert.IsTrue(service.Update(new(source.Code,"owner",renamed.Data!.Version,"只改显示值","说明")).Success);Assert.AreEqual(version,map.Repository.Transaction(s=>s.LogicalNodes[no.Code].Version));
    }
    [TestMethod]
    public void DeleteAndBoxDeleteClearReferencesOutsideSelection_KeepBooleanValuesAndUnrelatedSources()
    {
        foreach(var box in new[]{false,true})
        {
            var map=new TestMap();var source=Add(map,6,"判断","P",50.5m);var yes=Add(map,4,"True","P",150.5m,"other");var no=Add(map,4,"False","¬P",200.5m);
            if(box){var area=new MapAreaMoveService(map.Repository);var selection=area.Select(new("owner",new(40,40,60,60)));Assert.IsTrue(selection.Success,selection.Message);var result=area.Delete(new("owner",selection.Data!));Assert.IsTrue(result.Success,result.Message);}
            else Assert.IsTrue(new LogicalNodeService(map.Repository).Delete(new(source.Code,"owner",source.Version)).Success);
            foreach(var old in new[]{yes,no}){var updated=map.Repository.Transaction(s=>s.LogicalNodes[old.Code]);Assert.AreEqual("",updated.Properties.LogicLanguage);Assert.AreEqual(old.Properties.Value,updated.Properties.Value);Assert.AreEqual(old.Version+1,updated.Version);}
        }
    }
    [TestMethod]
    public void MaximumLengthNegationFailureAndDalBypassRollbackAllChanges()
    {
        var map=new TestMap();var source=Add(map,6,"判断","P",50.5m);var branch=Add(map,4,"False","¬P",100.5m);var service=new LogicalNodeService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("InvalidBranchLogic",service.Update(new(source.Code,"owner",source.Version,"changed","",LogicLanguage:new string('P',255))).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.ThrowsExactly<InvalidLogicalNodeBranchException>(()=>map.Repository.Transaction(s=>{s.LogicalNodes.Remove(source.Code);return true;}));Assert.AreEqual(before,map.Snapshot());
        Assert.ThrowsExactly<InvalidLogicalNodeBranchException>(()=>map.Repository.Transaction(s=>{s.LogicalNodes[branch.Code]=branch with{Properties=branch.Properties with{Value="True"}};return true;}));Assert.AreEqual(before,map.Snapshot());
        var updated=service.Update(new(source.Code,"owner",source.Version,"ok","",LogicLanguage:new string('P',254)));Assert.IsTrue(updated.Success,updated.Message);Assert.AreEqual(255,map.Repository.Transaction(s=>s.LogicalNodes[branch.Code].Properties.LogicLanguage.Length));
    }
    [TestMethod]
    public void ImportSupportsForwardReferencesAndBlankBranches_RenamesBindingsWithNewLn6_AndRejectsAmbiguousBatch()
    {
        var map=new TestMap();Add(map,6,"已有判断","P",50.5m);var service=new FlowDiagramService(map.Repository);
        var document=JsonNode.Parse("""{"format":"argumentmap.flow/1","origin":"P","ln":[{"id":"T","at":[40.5,0.5],"size":4,"value":"True","logic":"P"},{"id":"F","at":[80.5,0.5],"size":4,"value":"False","logic":"¬P"},{"id":"B","at":[120.5,0.5],"size":4,"value":"False"},{"id":"P","at":[0.5,0.5],"size":6,"value":"判断","logic":"P"}],"roads":[],"crosses":[]}""")!;
        var request=new FlowDiagramRequest("owner",document.ToJsonString(),500.5m,500.5m);var before=map.Snapshot();var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
        var result=service.Import(request);Assert.IsTrue(result.Success,result.Message);var nodes=result.Data!.Added.LogicalNodes;
        Assert.AreEqual("P1",nodes.Single(n=>n.Code==result.Data.LogicalNodeCodes["T"]).Properties.LogicLanguage);Assert.AreEqual("¬P1",nodes.Single(n=>n.Code==result.Data.LogicalNodeCodes["F"]).Properties.LogicLanguage);Assert.AreEqual("",nodes.Single(n=>n.Code==result.Data.LogicalNodeCodes["B"]).Properties.LogicLanguage);
        document["ln"]!.AsArray().Add(JsonNode.Parse("""{"id":"P2","at":[160.5,0.5],"size":6,"value":"判断2","logic":"P"}"""));before=map.Snapshot();
        Assert.AreEqual("AmbiguousBranchSource",service.Import(request with{Text=document.ToJsonString(),X=1000.5m}).Code);Assert.AreEqual(before,map.Snapshot());
    }
}
