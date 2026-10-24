using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeInstanceTests
{
    internal static LogicalNodeSnapshot Parent(ILineRepository repository,string value="PEOPLE",decimal x=50.5m,decimal y=50.5m)
    {
        var result=new LogicalNodeService(repository).Create(new("owner",x,y,12,new(){Value=value,LogicLanguage="Custom logic remains unchanged"}));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    internal static LogicalNodeSnapshot Child(ILineRepository repository,string value="people(id=1)",decimal x=90.5m,decimal y=50.5m,string owner="owner")
    {
        var result=new LogicalNodeService(repository).Create(new(owner,x,y,10,new(){Value=value,LogicLanguage="ignored by Server",Info="实例说明"}));
        Assert.IsTrue(result.Success,result.Message);return result.Data!;
    }
    internal static string Document(string value="people(id=1)",string parent="PEOPLE",bool includeParent=true)=>JsonSerializer.Serialize(new
    {
        format="argumentmap.flow/1",origin="child",
        ln=new[]{new{id="child",at=new[]{0.5m,0.5m},size=10,value,logic=LogicalNodeInstanceRules.Expression(LogicalNodeInstanceRules.Normalize(value))},
            new{id="copy",at=new[]{40.5m,0.5m},size=10,value,logic=""}}
            .Concat(includeParent?[new{id="parent",at=new[]{-39.5m,0.5m},size=12,value=parent,logic="Original12Logic"}]:[]),
        roads=new[]{new{id="main",points=new[]{new[]{-60,0},new[]{60,0}},direction="both",speed=100}},crosses=Array.Empty<object>()
    });

    [TestMethod,DataRow("people(id=1)","PEOPLE"),DataRow("People人员(id=1)","PEOPLE人员"),DataRow("H人(id=1)","H人")]
    public void InstancesNormalizeAndMayRepeat_LogicIsDerived_ParentLogicIsIndependent(string value,string parentValue)
    {
        var map=new TestMap();var parent=Parent(map.Repository,parentValue);var a=Child(map.Repository,value);var b=Child(map.Repository,value,130.5m);
        Assert.AreNotEqual(a.Code,b.Code);Assert.AreEqual(LogicalNodeInstanceRules.Normalize(value),a.Properties.Value);
        Assert.AreEqual(a.Properties,b.Properties);Assert.AreEqual(LogicalNodeInstanceRules.Expression(a.Properties.Value),a.Properties.LogicLanguage);
        Assert.AreEqual(parent.Properties,new LogicalNodeService(map.Repository).GetAll().Data!.Single(n=>n.Code==parent.Code).Properties);
    }
    [TestMethod]
    public void MissingParentRejected_CreateAndEditLeaveEverythingUnchanged()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);var before=map.Snapshot();
        Assert.AreEqual("InstanceParentNotFound",service.Create(new("owner",90.5m,50.5m,10,new(){Value="people(id=1)"})).Code);
        Assert.AreEqual(before,map.Snapshot());Parent(map.Repository);var child=Child(map.Repository);before=map.Snapshot();
        Assert.AreEqual("InstanceParentNotFound",service.Update(new(child.Code,"owner",child.Version,"unknown(id=1)","",LogicLanguage:"anything")).Code);
        Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void RenameParentSynchronizesEveryInstanceAcrossOwners_StaleEditorCannotOverwrite_DeletionRequiresRemovingInstancesFirst()
    {
        var map=new TestMap();var service=new LogicalNodeService(map.Repository);var parent=Parent(map.Repository);
        var a=Child(map.Repository);var b=Child(map.Repository,"people(id=1)",130.5m,owner:"other");
        var result=service.Update(new(parent.Code,"owner",parent.Version,"Participants人员","parent info",LogicLanguage:"custom(P)"));
        Assert.IsTrue(result.Success,result.Message);
        foreach(var old in new[]{a,b})
        {
            var current=service.GetAll().Data!.Single(n=>n.Code==old.Code);
            Assert.AreEqual(old.Owner,current.Owner);Assert.AreEqual(old.Version+1,current.Version);
            Assert.AreEqual("participants人员(id=1)",current.Properties.Value);Assert.AreEqual("participants人员(id=1)∈PARTICIPANTS人员∧1∈PARTICIPANTS人员.ID",current.Properties.LogicLanguage);
            Assert.AreEqual(old.Properties.Info,current.Properties.Info);CollectionAssert.AreEqual(old.Positions,current.Positions);
            Assert.AreEqual("VersionConflict",service.Update(new(old.Code,old.Owner,old.Version,"people(id=1)","")).Code);
        }
        var currentParent=result.Data!;var before=map.Snapshot();
        Assert.AreEqual("LogicalNodeHasInstances",service.Delete(new(parent.Code,"owner",currentParent.Version)).Code);Assert.AreEqual(before,map.Snapshot());
        var selected=MapAreaMoveTests.Select(map,new(30,30,110,70));
        Assert.AreEqual("LogicalNodeHasInstances",new MapAreaMoveService(map.Repository).Delete(new("owner",selected)).Code);Assert.AreEqual(before,map.Snapshot());
        foreach(var child in service.GetAll().Data!.Where(n=>n.SideLength==10))Assert.IsTrue(service.Delete(new(child.Code,child.Owner,child.Version)).Success);
        Assert.IsTrue(service.Delete(new(parent.Code,"owner",currentParent.Version)).Success);
    }
    [TestMethod,DataRow("case"),DataRow("logic"),DataRow("orphan"),DataRow("deleteParent"),DataRow("deleteBoth")]
    public void DalRejectsInvalidOrOrphanedInstances_AndRollsBack(string change)
    {
        var map=new TestMap();var parent=Parent(map.Repository);var child=Child(map.Repository);var before=map.Snapshot();
        Assert.ThrowsExactly<InvalidLogicalNodeInstanceException>(()=>map.Repository.Transaction(state=>
        {
            if(change=="deleteParent"||change=="deleteBoth")state.LogicalNodes.Remove(parent.Code);
            if(change=="deleteBoth")state.LogicalNodes.Remove(child.Code);
            if(change=="case")state.LogicalNodes[child.Code]=child with{Properties=child.Properties with{Value="PEOPLE"}};
            if(change=="logic")state.LogicalNodes[child.Code]=child with{Properties=child.Properties with{LogicLanguage="∀people(people∈PEOPLE)"}};
            if(change=="orphan")state.LogicalNodes[parent.Code]=parent with{Properties=parent.Properties with{Value="OTHER"}};
            return true;
        }));Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void ImportCreatesParentsFirst_RenamesLocalMembershipTogether_RepeatInstancesAndPreviewAreSafe()
    {
        var map=new TestMap();Parent(map.Repository);var service=new FlowDiagramService(map.Repository);
        for(var index=1;index<=2;index++)
        {
            var request=new FlowDiagramRequest("owner",Document(),1000m*index+0.5m,1000.5m);var before=map.Snapshot();
            var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());
            var imported=service.Import(request);Assert.IsTrue(imported.Success,imported.Message);
            var parent=imported.Data!.Added.LogicalNodes.Single(n=>n.SideLength==12);
            Assert.AreEqual("PEOPLE"+index,parent.Properties.Value);Assert.AreEqual("Original12Logic",parent.Properties.LogicLanguage);
            foreach(var instance in imported.Data.Added.LogicalNodes.Where(n=>n.SideLength==10))
            {
                Assert.AreEqual("people"+index+"(id=1)",instance.Properties.Value);
                Assert.AreEqual($"people{index}(id=1)∈PEOPLE{index}∧1∈PEOPLE{index}.ID",instance.Properties.LogicLanguage);Assert.AreEqual(1,instance.Positions.Length);
            }
        }
    }
    [TestMethod]
    public void ImportMayReferenceExistingParent_ButRejectsMissingParentAndExplicitInvalidLogic()
    {
        var map=new TestMap();var service=new FlowDiagramService(map.Repository);var request=new FlowDiagramRequest("owner",Document(includeParent:false),1000.5m,1000.5m);
        Assert.AreEqual("InstanceParentNotFound",service.Import(request).Code);Assert.AreEqual(0,map.Repository.Transaction(s=>s.LogicalNodes.Count));
        Parent(map.Repository);Assert.IsTrue(service.Import(request).Success);
        var before=map.Snapshot();var bad=request with{Text=request.Text.Replace("people(id=1)∈PEOPLE∧1∈PEOPLE.ID","∀people(people∈PEOPLE)")};
        // JSON serializes non-ASCII by default, use an explicitly conflicting field instead.
        var json=System.Text.Json.Nodes.JsonNode.Parse(request.Text)!;json["ln"]![0]!["logic"]="∀people(people∈PEOPLE)";
        Assert.IsFalse(service.Import(bad with{Text=json.ToJsonString()}).Success);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod]
    public void FullSquareSelectionUsesTenUnitBounds_ConcaveCornersDoNotRelaxGeometry()
    {
        var map=new TestMap();Parent(map.Repository);var child=Child(map.Repository);var service=new MapAreaMoveService(map.Repository);
        var bounds=new MapAreaBounds(child.Left,child.Bottom,child.Right,child.Top);
        Assert.IsTrue(service.Select(new("owner",bounds)).Success);
        Assert.AreEqual("EmptySelection",service.Select(new("owner",bounds with{Left=bounds.Left+0.01m})).Code);
        var before=map.Snapshot();
        Assert.IsFalse(new LogicalNodeService(map.Repository).Create(new("owner",100.5m,50.5m,10,new(){Value="people(id=1)"})).Success,"Touching at the square edge must fail.");
        Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void MovingAndDeletingInstancesUseExistingPositionLifecycle_ParentNeverMovesWithChild()
    {
        var map=new TestMap();var parent=Parent(map.Repository);map.Add(70,100,150,100);
        var child=Child(map.Repository,"people(id=1)",90.5m,100.5m);var service=new LogicalNodeService(map.Repository);
        Assert.AreEqual(1,child.Positions.Length);var moved=service.Move(new(child.Code,"owner",child.Version,90.5m,150.5m));
        Assert.IsTrue(moved.Success,moved.Message);Assert.AreEqual(0,moved.Data!.Positions.Length);
        Assert.AreEqual(parent.X,service.GetAll().Data!.Single(n=>n.Code==parent.Code).X);
        Assert.IsTrue(service.Delete(new(child.Code,"owner",moved.Data.Version)).Success);
        Assert.IsTrue(service.Delete(new(parent.Code,"owner",parent.Version)).Success);
    }
}
