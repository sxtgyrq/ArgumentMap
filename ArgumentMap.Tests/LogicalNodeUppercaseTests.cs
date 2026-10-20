using System.Globalization;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeUppercaseTests
{
    [TestMethod]
    public void OnlySize12_NormalizesBeforeNumberingAndUpdate_AndPreservesInfoLogic()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);
        var a=LogicalNodeUniqueValueTests.Add(map.Repository,"s");Assert.AreEqual("S",a.Properties.Value);
        Assert.AreEqual("S1",LogicalNodeUniqueValueTests.Add(map.Repository,"S",100.5m).Properties.Value);
        var before=map.Snapshot();
        Assert.AreEqual("DuplicateLogicalNodeValue",service.Update(new(a.Code,"owner",a.Version,"s1","不能修改")).Code);
        Assert.AreEqual(before,map.Snapshot());
        var same=service.Update(new(a.Code,"owner",a.Version,"s","small info",LogicLanguage:"s ∧ p"));
        Assert.IsTrue(same.Success);Assert.AreEqual("S",same.Data!.Properties.Value);
        Assert.AreEqual("small info",same.Data.Properties.Info);Assert.AreEqual("s ∧ p",same.Data.Properties.LogicLanguage);
        var changed=service.Update(new(a.Code,"owner",same.Data.Version,"new","",LogicLanguage:"s ⇒ a"));
        Assert.IsTrue(changed.Success);Assert.AreEqual("NEW",changed.Data!.Properties.Value);
        foreach(var (size,x) in new[]{(4,200.5m),(6,250.5m),(18,300.5m)})
        {
            var node=LogicalNodeUniqueValueTests.Add(map.Repository,"mixed",x,size);
            Assert.AreEqual(size==4?"False":"mixed",node.Properties.Value);
            var result=service.Update(new(node.Code,"owner",node.Version,size==4?"True":"small",""));
            Assert.IsTrue(result.Success);Assert.AreEqual(size==4?"True":"small",result.Data!.Properties.Value);
        }
    }

    [TestMethod]
    public void NormalizationIsIndependentOfWindowsLanguage_AndDalRejectsUnnormalizedBypass()
    {
        var culture=CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture=CultureInfo.GetCultureInfo("tr-TR");
            var map=new TestMap();var node=LogicalNodeUniqueValueTests.Add(map.Repository,"iıéσя");
            Assert.AreEqual("IIÉΣЯ",node.Properties.Value);var before=map.Snapshot();
            Assert.ThrowsExactly<InvalidOperationException>(()=>map.Repository.Transaction(s=>
            {s.LogicalNodes[node.Code]=node with{Properties=node.Properties with{Value="lower"}};return true;}));
            Assert.AreEqual(before,map.Snapshot());
            var b=LogicalNodeUniqueValueTests.Add(map.Repository,"other",100.5m);before=map.Snapshot();
            Assert.ThrowsExactly<DuplicateLogicalNodeValueException>(()=>map.Repository.Transaction(s=>
            {s.LogicalNodes[b.Code]=b with{Properties=b.Properties with{Value="iiéσя"}};return true;}));
            Assert.AreEqual(before,map.Snapshot());
        }
        finally{CultureInfo.CurrentCulture=culture;}
    }

    [TestMethod]
    public void ImportedSize12IsUppercaseAndNumbered_OtherSizesAndLogicStayOriginal()
    {
        var map=new TestMap();LogicalNodeUniqueValueTests.Add(map.Repository,"S",200.5m);
        var d=FlowDiagramTests.Small();d["ln"]![0]!["size"]=12;d["ln"]![0]!["value"]="s";d["ln"]![0]!["logic"]="s";
        d["ln"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("""{"id":"yes","at":[40.5,0.5],"size":4,"value":"True","logic":""}"""));
        var service=new FlowDiagramService(map.Repository);var before=map.Snapshot();
        var request=new FlowDiagramRequest("owner",d.ToJsonString(),50.5m,50.5m);
        var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
        var imported=service.Import(request);Assert.IsTrue(imported.Success,imported.Message);
        Assert.AreEqual("S1",imported.Data!.Added.LogicalNodes.Single(n=>n.SideLength==12).Properties.Value);
        Assert.AreEqual("s",imported.Data.Added.LogicalNodes.Single(n=>n.SideLength==12).Properties.LogicLanguage);
        Assert.AreEqual("True",imported.Data.Added.LogicalNodes.Single(n=>n.SideLength==4).Properties.Value);
    }
}
