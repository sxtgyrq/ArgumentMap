using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LinePasteTests
{
    internal static LineCopyData Copy(LineSnapshot road)=>new(road.Code,road.Version,road.Nodes.ToArray(),road.Properties,road.IsLocked);
    [TestMethod,DataRow(false,0),DataRow(true,0),DataRow(false,1),DataRow(true,1)]
    public void PastePreservesGeometryAndEveryRoadProperty_WithoutChangingOriginal(bool locked,int oppose)
    {
        var map=new TestMap();var source=map.Add(100,100,130,100,140,110,140,150);
        source=map.Lines.SaveProperties(new(source.Code,source.Owner,source.Version,new(){RoadName="线路甲",RoadDetail="说明\n保留",Speed=237,PassInOpposeDirection=(short)oppose})).Data!;
        if(locked){source=source with{IsLocked=true,Version=source.Version+1};map.Repository.Transaction(s=>{s.Lines[source.Code]=source;return true;});}
        var result=new LinePasteService(map.Repository).Paste(new("new-user",Copy(source),300,300));Assert.IsTrue(result.Success,result.Message);
        var pasted=result.Data!.Line;Assert.AreNotEqual(source.Code,pasted.Code);Assert.AreEqual("new-user",pasted.Owner);Assert.AreEqual(source.Properties,pasted.Properties);Assert.AreEqual(locked,pasted.IsLocked);
        CollectionAssert.AreEqual(source.Nodes.Select(n=>(n.X+200,n.Y+200)).ToArray(),pasted.Nodes.Select(n=>(n.X,n.Y)).ToArray());
        Assert.IsTrue(pasted.Nodes.Select((n,i)=>n.LineCode==pasted.Code&&n.Order==i).All(v=>v));
        var original=map.Lines.GetDetail(new(source.Code,source.Owner)).Data!;Assert.AreEqual(source.Properties,original.Properties);Assert.AreEqual(source.Version,original.Version);Assert.AreEqual(source.IsLocked,original.IsLocked);CollectionAssert.AreEqual(source.Nodes,original.Nodes);
        Assert.IsFalse(map.Repository.Transaction(s=>s.DirtyLines.Contains(pasted.Code)||s.PendingSegments.Any(p=>p.LineCode==pasted.Code)));
    }

    [TestMethod,DataRow("overlap"),DataRow("occupied"),DataRow("nonorthogonal"),DataRow("triple"),DataRow("ln-node"),DataRow("ln-diagonal"),DataRow("bounds")]
    public void ConflictingPasteLeavesNoRoadNoNodesNoRelationOrMarkerChanges(string kind)
    {
        var map=new TestMap();var source=kind=="ln-diagonal"?map.Add(100,100,140,140):map.Add(100,100,140,100);var x=300;var y=300;
        switch(kind)
        {
            case "overlap":x=100;y=100;break;
            case "occupied":map.Add(300,300,300,320);break;
            case "nonorthogonal":map.Add(310,290,330,310);break;
            case "triple":map.Add(310,300,330,300);map.Add(320,290,320,310);map.Calculate();x=320;y=280;source=map.Add(100,150,100,190);break;
            case "ln-node":Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("other",300.5m,300.5m,6,new())).Success);break;
            case "ln-diagonal":Assert.IsTrue(new LogicalNodeService(map.Repository).Create(new("other",320.5m,320.5m,6,new())).Success);break;
            case "bounds":x=499990;break;
        }
        var before=map.Snapshot();var result=new LinePasteService(map.Repository).Paste(new("owner",Copy(source),x,y));Assert.IsFalse(result.Success,kind);Assert.AreEqual(before,map.Snapshot(),kind);
    }

    [TestMethod]
    public void PasteCalculatesNewCrossAndLnPositions_AndKeepsUnrelatedCrossSettingsAndPendingWork()
    {
        var map=new TestMap();var source=map.Add(100,100,200,100);map.Add(150,80,150,120);map.Calculate();var old=map.Crosses.GetAll().Data!.Single();Assert.IsTrue(map.Crosses.Edit(new(old.Key,"owner",old.Version,CrossState.Connected)).Success);
        var vertical=map.Add(350,280,350,320);var ln=new LogicalNodeService(map.Repository).Create(new("owner",350.5m,300.5m,6,new())).Data!;
        var result=new LinePasteService(map.Repository).Paste(new("owner",Copy(source),300,300));Assert.IsTrue(result.Success,result.Message);var pasted=result.Data!.Line;
        var cross=result.Data.Map.Crosses.Single(c=>c.Key.Contains(pasted.Code));Assert.IsTrue(cross.Key.Contains(vertical.Code));Assert.AreEqual(CrossState.Disconnected,cross.State);Assert.IsFalse(map.Repository.Transaction(s=>s.StaleCrosses.Contains(cross.Key)));
        Assert.AreEqual(CrossState.Connected,result.Data.Map.Crosses.Single(c=>c.Key==old.Key).State);
        var position=result.Data.Map.LogicalNodes.Single(n=>n.Code==ln.Code).Positions.Single(p=>p.RoadCode==pasted.Code);Assert.AreEqual((350.5m,300m),(position.X,position.Y));Assert.AreEqual(.505,position.RoadPercent,1e-12);Assert.IsFalse(position.IsChanged);
        Assert.IsTrue(map.Repository.Transaction(s=>s.DirtyLines.Contains(vertical.Code)),"Do not clear another road's pending K work.");
        map.Calculate();Assert.IsTrue(new LogicalNodeService(map.Repository).Calculate().Success);Assert.AreEqual(CrossState.Connected,map.Crosses.GetAll().Data!.Single(c=>c.Key==old.Key).State);
    }

    [TestMethod,DataRow("geometry"),DataRow("properties"),DataRow("lock"),DataRow("delete"),DataRow("forged")]
    public void ConcurrentSourceChangesOrForgedClipboardCannotPasteStaleData(string change)
    {
        var map=new TestMap();var source=map.Add(100,100,140,100);var copy=Copy(source);
        switch(change)
        {
            case "geometry":map.Save(source,100,110,140,110);break;
            case "properties":Assert.IsTrue(map.Lines.SaveProperties(new(source.Code,"owner",source.Version,new(){Speed=250})).Success);break;
            case "lock":map.Repository.Transaction(s=>{s.Lines[source.Code]=source with{IsLocked=true,Version=source.Version+1};return true;});break;
            case "delete":Assert.IsTrue(map.Lines.Delete(new(source.Code,"owner",source.Version)).Success);break;
            case "forged":copy=copy with{Properties=copy.Properties with{Speed=400}};break;
        }
        var before=map.Snapshot();var result=new LinePasteService(map.Repository).Paste(new("owner",copy,300,300));Assert.IsFalse(result.Success);Assert.AreEqual(before,map.Snapshot());
    }
}
