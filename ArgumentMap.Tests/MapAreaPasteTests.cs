using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class MapAreaPasteTests
{
    internal static readonly MapAreaBounds Bounds=new(10,10,250,90);
    internal static MapAreaSelection Fixture(ILineRepository repository,CrossState crossState=CrossState.Connected)
    {
        var a=LogicalNodeMySqlTests.Add(repository,20,50,240,50);LogicalNodeMySqlTests.Add(repository,110,20,110,80);
        var roadService=new LineSaveService(repository);Assert.IsTrue(roadService.SaveProperties(new(a.Code,"owner",a.Version,new(){RoadName="副本线路",RoadDetail="说明\n保留",Speed=237,PassInOpposeDirection=1})).Success);
        var nodes=new LogicalNodeService(repository);
        foreach(var (x,size,value,logic) in new[]{(50.5m,12,"PEOPLE","CUSTOM"),(90.5m,10,"people",""),(130.5m,6,"判断","P"),(160.5m,4,"True","P"),(180.5m,4,"False","¬P"),(210.5m,8,"↔","↔")})
        {
            var result=nodes.Create(new("owner",x,50.5m,size,new(){Value=value,LogicLanguage=logic,Info="节点说明\n保留"}));Assert.IsTrue(result.Success,result.Message);
        }
        var crosses=new CrossService(repository);while(crosses.Calculate().Data!.CanContinue){}
        var cross=crosses.GetAll().Data!.Single();Assert.IsTrue(crosses.Edit(new(cross.Key,"owner",cross.Version,crossState)).Success);
        return Select(repository,Bounds);
    }
    internal static MapAreaSelection Select(ILineRepository repository,MapAreaBounds bounds,string owner="owner")
    {var result=new MapAreaMoveService(repository).Select(new(owner,bounds));Assert.IsTrue(result.Success,result.Message);return result.Data!;}
    private static string SourceSnapshot(ILineRepository repository,MapAreaSelection source)=>repository.Transaction(s=>JsonSerializer.Serialize(new
    {
        Lines=source.Lines.Select(l=>s.Lines[l.Code]).ToArray(),Nodes=source.LogicalNodes.Select(n=>s.LogicalNodes[n.Code]).ToArray(),
        Crosses=source.Crosses.Select(c=>s.Crosses[c.Key]).ToArray(),Positions=s.LogicalNodePositions.Values.Where(p=>source.LogicalNodes.Any(n=>n.Code==p.LogicalNodeCode)).OrderBy(p=>p.LogicalNodeCode).ThenBy(p=>p.RoadCode).ToArray()
    }));

    [TestMethod,DataRow(CrossState.Connected),DataRow(CrossState.Disconnected),DataRow(CrossState.Unset),DataRow(CrossState.Road1ToRoad2),DataRow(CrossState.Road2ToRoad1)]
    public void PasteIsNew_PreservesShapeAndAttributes_RemapsBothKindsOfReferences_AndCrossDirection(CrossState state)
    {
        var map=new TestMap();var selection=Fixture(map.Repository,state);var before=SourceSnapshot(map.Repository,selection);var service=new MapAreaPasteService(map.Repository);
        for(var index=1;index<=2;index++)
        {
            var shift=300*index;var result=service.Paste(new("owner",selection,shift,shift));Assert.IsTrue(result.Success,result.Message);var pasted=result.Data!;
            Assert.AreEqual(2,pasted.Added.Lines.Length);Assert.AreEqual(6,pasted.Added.LogicalNodes.Length);Assert.AreEqual(1,pasted.Added.Crosses.Length);
            foreach(var original in selection.Lines)
            {
                var copy=pasted.Added.Lines.Single(l=>l.Code==pasted.RoadCodes[original.Code]);Assert.AreNotEqual(original.Code,copy.Code);Assert.AreEqual(original.Properties,copy.Properties);
                CollectionAssert.AreEqual(original.Nodes.Select(n=>(n.X+shift,n.Y+shift)).ToArray(),copy.Nodes.Select(n=>(n.X,n.Y)).ToArray());
                Assert.IsTrue(copy.Nodes.Select((n,i)=>n.LineCode==copy.Code&&n.Order==i).All(v=>v));
            }
            foreach(var original in selection.LogicalNodes)
            {
                var copy=pasted.Added.LogicalNodes.Single(n=>n.Code==pasted.LogicalNodeCodes[original.Code]);Assert.AreNotEqual(original.Code,copy.Code);Assert.AreEqual((original.X+shift,original.Y+shift),(copy.X,copy.Y));Assert.AreEqual(original.SideLength,copy.SideLength);Assert.AreEqual(original.Properties.Info,copy.Properties.Info);
                Assert.IsTrue(copy.Positions.All(p=>p.LogicalNodeCode==copy.Code&&pasted.RoadCodes.Values.Contains(p.RoadCode)&&!p.IsChanged));
                Assert.IsTrue(copy.Positions.Length>0);
            }
            var parent=pasted.Added.LogicalNodes.Single(n=>n.SideLength==12);Assert.AreEqual("PEOPLE"+index,parent.Properties.Value);Assert.AreEqual("CUSTOM",parent.Properties.LogicLanguage);
            var child=pasted.Added.LogicalNodes.Single(n=>n.SideLength==10);Assert.AreEqual("people"+index,child.Properties.Value);Assert.AreEqual($"people{index} ∈ PEOPLE{index}",child.Properties.LogicLanguage);
            Assert.AreEqual("P"+index,pasted.Added.LogicalNodes.Single(n=>n.SideLength==6).Properties.LogicLanguage);
            Assert.AreEqual("P"+index,pasted.Added.LogicalNodes.Single(n=>n.Properties.Value=="True").Properties.LogicLanguage);Assert.AreEqual("¬P"+index,pasted.Added.LogicalNodes.Single(n=>n.Properties.Value=="False").Properties.LogicLanguage);
            Assert.AreEqual("↔",pasted.Added.LogicalNodes.Single(n=>n.SideLength==8).Properties.LogicLanguage);
            var old=selection.Crosses.Single();var cross=pasted.Added.Crosses.Single();Assert.AreEqual(new CrossPosition(old.Key.Position.X2+2*shift,old.Key.Position.Y2+2*shift),cross.Key.Position);
            var from=old.State==CrossState.Road2ToRoad1?old.Key.RoadCode2:old.Key.RoadCode1;var to=from==old.Key.RoadCode1?old.Key.RoadCode2:old.Key.RoadCode1;
            Assert.AreEqual(old.AllowsTransfer(from,to),cross.AllowsTransfer(pasted.RoadCodes[from],pasted.RoadCodes[to]));Assert.AreEqual(old.AllowsTransfer(to,from),cross.AllowsTransfer(pasted.RoadCodes[to],pasted.RoadCodes[from]));
            Assert.AreEqual(before,SourceSnapshot(map.Repository,selection));
        }
    }

    [TestMethod,DataRow("nodes"),DataRow("roads"),DataRow("externalReferences")]
    public void OnlyNodesOrRoadsCanPaste_ReferencesOutsideGroupStayExternal(string kind)
    {
        var map=new TestMap();Fixture(map.Repository);
        var bounds=kind switch{"nodes"=>new MapAreaBounds(40,40,220,60),"roads"=>new(100,10,120,90),_=>new(80,40,98,60)};
        var source=Select(map.Repository,bounds);var result=new MapAreaPasteService(map.Repository).Paste(new("owner",source,500,500));Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(source.Lines.Length,result.Data!.Added.Lines.Length);Assert.AreEqual(source.LogicalNodes.Length,result.Data.Added.LogicalNodes.Length);
        if(kind=="externalReferences")Assert.AreEqual("people ∈ PEOPLE",result.Data.Added.LogicalNodes.Single().Properties.LogicLanguage);
        if(kind=="nodes")Assert.AreEqual(0,result.Data.Added.Lines.Length);
        if(kind=="roads")Assert.AreEqual(0,result.Data.Added.LogicalNodes.Length);
    }

    [TestMethod,DataRow("bounds"),DataRow("overlap"),DataRow("lateRoad"),DataRow("nodeCollision"),DataRow("changedNode"),DataRow("changedRoad"),DataRow("changedCross"),DataRow("lostRights")]
    public void InvalidGeometryOrStaleSourceRollsBackEntireGroup(string change)
    {
        var map=new TestMap();var source=Fixture(map.Repository);var dx=300;var dy=300;
        switch(change)
        {
            case "bounds":dx=-100;break;
            case "overlap":dx=dy=0;break;
            case "lateRoad":map.Add(500,350,550,350);break;
            case "nodeCollision":Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",350.5m,350.5m,6,new())).Success);break;
            case "changedNode":var n=source.LogicalNodes.First();Assert.IsTrue(new LogicalNodeService(map.Repository).Update(new(n.Code,n.Owner,n.Version,n.Properties.Value,"new info",LogicLanguage:n.Properties.LogicLanguage)).Success);break;
            case "changedRoad":var l=source.Lines.First();Assert.IsTrue(map.Lines.SaveProperties(new(l.Code,l.Owner,l.Version,l.Properties with{Speed=499})).Success);break;
            case "changedCross":var c=source.Crosses.Single();Assert.IsTrue(map.Crosses.Edit(new(c.Key,"owner",c.Version,CrossState.Disconnected)).Success);break;
            case "lostRights":map.Repository.Transaction(s=>{var old=s.LogicalNodes.Values.First();s.LogicalNodes[old.Code]=old with{Owner="other"};return true;});break;
        }
        var before=map.Snapshot();var result=new MapAreaPasteService(map.Repository).Paste(new("owner",source,dx,dy));Assert.IsFalse(result.Success,change);Assert.AreEqual(before,map.Snapshot(),change);
    }

    [TestMethod]
    public void ClipboardMembershipIsFrozen_NewObjectsInsideOldBoundsDoNotJoinOrBlockPaste()
    {
        var map=new TestMap();var source=Fixture(map.Repository);
        Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("other",230.5m,70.5m,6,new(){LogicLanguage="LATER"})).Success);
        var result=new MapAreaPasteService(map.Repository).Paste(new("owner",source,300,300));Assert.IsTrue(result.Success,result.Message);
        Assert.AreEqual(6,result.Data!.Added.LogicalNodes.Length);Assert.IsFalse(result.Data.Added.LogicalNodes.Any(n=>n.Properties.LogicLanguage=="LATER"));
    }

    [TestMethod]
    public void SourcePropertiesAreReloaded_NotTrustedFromClipboard_AndStartCannotBeDuplicated()
    {
        var map=new TestMap();var source=Fixture(map.Repository);
        var forged=source with{Lines=source.Lines.Select(l=>l with{Owner="admin",Properties=l.Properties with{Speed=499}}).ToArray(),
            LogicalNodes=source.LogicalNodes.Select(n=>n with{Properties=n.Properties with{Info="forged"}}).ToArray()};
        var result=new MapAreaPasteService(map.Repository).Paste(new("owner",forged,300,300));Assert.IsTrue(result.Success,result.Message);
        Assert.IsTrue(result.Data!.Added.LogicalNodes.All(n=>n.Owner=="owner"&&n.Properties.Info=="节点说明\n保留"));Assert.IsTrue(result.Data.Added.Lines.All(l=>l.Properties.Speed!=499));
        Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("owner",800.5m,800.5m,18,new())).Success);var start=Select(map.Repository,new(780,780,820,820));var before=map.Snapshot();
        Assert.AreEqual("StartLogicalNodeNotCopyable",new MapAreaPasteService(map.Repository).Paste(new("owner",start,50,50)).Code);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void SkippedExternalCrossIsNotCopied_DestinationRelationsRecalculate_AndPendingKJWorkSurvives()
    {
        var map=new TestMap();Fixture(map.Repository);var source=Select(map.Repository,new(10,40,250,60));Assert.AreEqual(0,source.Crosses.Length);
        var external=map.Add(450,330,450,370);var retained=new LogicalNodeService(map.Repository).Create(new("other",450.5m,350.5m,6,new(){LogicLanguage="R"})).Data!;
        var unrelated=map.Add(1000,1000,1100,1000);var pending=new LogicalNodeService(map.Repository).Create(new("other",1050.5m,1000.5m,6,new(){LogicLanguage="T"})).Data!;
        map.Repository.Transaction(s=>{foreach(var p in s.LogicalNodePositions.Values.Where(p=>p.LogicalNodeCode==pending.Code).ToArray())s.LogicalNodePositions[p.Key]=p with{IsChanged=true};return true;});
        var result=new MapAreaPasteService(map.Repository).Paste(new("owner",source,300,300));Assert.IsTrue(result.Success,result.Message);
        var pasted=result.Data!.Added.Lines.Single();var cross=result.Data.Added.Crosses.Single();Assert.IsTrue(cross.Key.Contains(pasted.Code)&&cross.Key.Contains(external.Code));Assert.AreEqual(CrossState.Disconnected,cross.State);
        Assert.AreEqual(2,result.Data.Map.LogicalNodes.Single(n=>n.Code==retained.Code).Positions.Length);
        Assert.IsTrue(map.Repository.Transaction(s=>s.PendingSegments.Any(p=>p.LineCode==unrelated.Code)&&s.DirtyLines.Contains(unrelated.Code)&&s.LogicalNodePositions.Values.Single(p=>p.LogicalNodeCode==pending.Code).IsChanged));
        map.Calculate();LogicalNodeTests.Calculate(new(map.Repository));Assert.IsTrue(map.Repository.Transaction(s=>s.LogicalNodePositions.Values.All(p=>!p.IsChanged)));
    }
}
