using ArgumentMap.Rules;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;
using ArgumentMap.Storage;
using System.IO;
using System.Text.RegularExpressions;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class FlowDiagramTests
{
    internal static string Example=>FlowDiagramImportWindow.ExampleText;
    internal static JsonObject Small()=>JsonNode.Parse("""
        {"format":"argumentmap.flow/1","origin":"S","ln":[{"id":"S","at":[0.5,0.5],"size":6,"value":"事实","logic":"S"}],"roads":[{"id":"main","points":[[-10,0],[10,0]]}],"crosses":[]}
        """)!.AsObject();
    internal static ServiceResult<FlowDiagramResult> Import(TestMap map,JsonObject? text=null,decimal x=50.5m,decimal y=50.5m)=>new FlowDiagramService(map.Repository).Import(new("owner",(text??Small()).ToJsonString(),x,y));

    [TestMethod]
    public void EquipmentExample_PreviewIsReadOnly_CommitUsesExistingObjectsAndMetadata_KJAreIdempotent()
    {
        var map=new TestMap();var service=new FlowDiagramService(map.Repository);var request=new FlowDiagramRequest("owner",Example,1200.5m,5000.5m);var before=map.Snapshot();
        var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(before,map.Snapshot());Assert.IsNull(preview.Data!.Map);
        Assert.AreEqual(10,preview.Data.Added.LogicalNodes.Length);Assert.AreEqual(3,preview.Data.Added.Lines.Length);Assert.AreEqual(2,preview.Data.Added.Crosses.Length);
        var result=service.Import(request);Assert.IsTrue(result.Success,result.Message);var d=result.Data!;Assert.IsNotNull(d.Map);
        Assert.AreEqual(10,d.Map.LogicalNodes.Length);Assert.AreEqual(10,d.Map.LogicalNodes.Sum(n=>n.Positions.Length));
        var origin=d.Added.LogicalNodes.Single(n=>n.Code==d.LogicalNodeCodes["S"]);Assert.AreEqual(1200.5m,origin.X);Assert.AreEqual(5000.5m,origin.Y);Assert.AreEqual("S",origin.Properties.LogicLanguage);
        Assert.IsTrue(d.Added.Lines.All(l=>l.Owner=="owner"&&l.Code.Length==10&&!l.IsLocked&&l.Properties.PassInDirection==1));Assert.IsTrue(d.Added.LogicalNodes.All(n=>n.Code.Length==10&&n.Owner=="owner"));
        Assert.IsTrue(d.Added.Crosses.All(c=>c.State==CrossState.Connected));before=map.Snapshot();map.Calculate();Assert.IsTrue(new LogicalNodeService(map.Repository).Calculate().Success);Assert.AreEqual(before,map.Snapshot());
        var duplicate=service.Import(request);Assert.IsFalse(duplicate.Success);Assert.AreEqual(before,map.Snapshot());
        var reused=service.Import(request with{X=2200.5m,Y=6000.5m});Assert.IsTrue(reused.Success,reused.Message);
        CollectionAssert.AreEquivalent(new[]{"P1","Q1"},reused.Data!.Added.LogicalNodes.Where(n=>n.SideLength==6).Select(n=>n.Properties.LogicLanguage).ToArray());
        Assert.IsTrue(reused.Data.Notes.Any(n=>n.Contains("命题引用未自动改写",StringComparison.Ordinal)));
        Assert.IsTrue(reused.Data!.Added.LogicalNodes.Where(n=>n.SideLength==12).All(n=>n.Properties.Value.EndsWith("1",StringComparison.Ordinal)));
        Assert.IsNull(LogicalNodeValueUniqueness.FindConflict(reused.Data.Map!.LogicalNodes));
    }

    [TestMethod,DataRow("unknown"),DataRow("duplicateJson"),DataRow("nullLn"),DataRow("nullItem"),DataRow("format"),DataRow("origin"),DataRow("duplicateId"),DataRow("fractionalRoad"),DataRow("lnInteger"),DataRow("size"),DataRow("speed"),DataRow("direction"),DataRow("crossRef"),DataRow("nullPoint")]
    public void InvalidLanguageReturnsHelpfulFailure_WithoutMutation(string kind)
    {
        var map=new TestMap();var d=Small();string? raw=null;
        switch(kind)
        {
            case "unknown":d["sql"]="DELETE FROM Lines";break;
            case "duplicateJson":raw=d.ToJsonString().Replace("\"origin\":\"S\"","\"origin\":\"S\",\"origin\":\"X\"");break;
            case "nullLn":d["ln"]=null;break;
            case "nullItem":d["ln"]![0]=null;break;
            case "format":d["format"]="wrong";break;
            case "origin":d["origin"]="Missing";break;
            case "duplicateId":d["ln"]!.AsArray().Add(d["ln"]![0]!.DeepClone());break;
            case "fractionalRoad":d["roads"]![0]!["points"]![0]![0]=.5;break;
            case "nullPoint":d["roads"]![0]!["points"]![0]=null;break;
            case "lnInteger":d["ln"]![0]!["at"]![0]=0;break;
            case "size":d["ln"]![0]!["size"]=10;break;
            case "speed":d["roads"]![0]!["speed"]=501;break;
            case "direction":d["roads"]![0]!["direction"]="reverse";break;
            default:d["crosses"]!.AsArray().Add(JsonNode.Parse("""{"road1":"main","road2":"unknown","at":[0,0],"state":"connected"}"""));break;
        }
        var before=map.Snapshot();var result=new FlowDiagramService(map.Repository).Import(new("owner",raw??d.ToJsonString(),50.5m,50.5m));Assert.AreEqual("InvalidFlowText",result.Code,result.Message);Assert.IsTrue(result.Message.Length>5);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow("node","LineNodeInsideLogicalNode"),DataRow("direction","InvalidDirection"),DataRow("duplicate","DuplicateNode"),DataRow("acute","AcuteCorner"),DataRow("overlap","SegmentOverlap"),DataRow("diagonal","DiagonalInsideLogicalNode")]
    public void RoadGeometryUsesF6Rules_RejectsWholeDiagramWithAlias(string kind,string code)
    {
        var map=new TestMap();var d=Small();var points=kind switch
        {"node"=>"[[0,0],[10,0]]","direction"=>"[[-10,0],[10,2]]","duplicate"=>"[[-10,0],[10,0],[-10,0]]","acute"=>"[[-20,10],[20,10],[0,-10]]","overlap"=>"[[-20,10],[20,10],[-10,10]]",_=>"[[-10,-10],[10,10]]"};
        d["roads"]![0]!["points"]=JsonNode.Parse(points);var before=map.Snapshot();var result=Import(map,d);Assert.AreEqual(code,result.Code,result.Message);StringAssert.Contains(result.Message,"main");Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow("overlap","SegmentOverlap"),DataRow("oblique","NonOrthogonalIntersection"),DataRow("third","LinePassesCross"),DataRow("lnTouch","LogicalNodesTouchOrOverlap"),DataRow("start","StartLogicalNodeExists")]
    public void ExistingMapConstraintsAreValidatedBeforeAtomicCommit(string kind,string code)
    {
        var map=new TestMap();var d=Small();
        switch(kind)
        {
            case "overlap":map.Add(43,50,57,50);break;
            case "oblique":map.Add(60,45,70,55);d["roads"]![0]!["points"]=JsonNode.Parse("[[-20,0],[20,0]]");break;
            case "third":map.Add(60,45,70,55);map.Add(60,55,70,45);d["roads"]![0]!["points"]=JsonNode.Parse("[[-20,0],[20,0]]");break;
            case "lnTouch":MapAreaMoveTests.Node(map,56.5m,50.5m);break;
            default:MapAreaMoveTests.Node(map,400.5m,400.5m,18);d["ln"]![0]!["size"]=18;break;
        }
        map.Calculate();var before=map.Snapshot();var result=Import(map,d);Assert.AreEqual(code,result.Code,result.Message);Assert.AreEqual(before,map.Snapshot());
    }

    internal static JsonObject Crossing()
    {
        var d=Small();d["ln"]![0]!["at"]=JsonNode.Parse("[-19.5,20.5]");
        d["roads"]!.AsArray().Add(JsonNode.Parse("""{"id":"branch","points":[[0,-10],[0,10]],"direction":"both","speed":500}"""));
        d["crosses"]!.AsArray().Add(JsonNode.Parse("""{"road1":"main","road2":"branch","at":[0,0],"state":"road1ToRoad2"}"""));return d;
    }
    [TestMethod,DataRow("connected"),DataRow("disconnected"),DataRow("unset"),DataRow("road1ToRoad2"),DataRow("road2ToRoad1")]
    public void CrossDirectionsAreRelativeToAliases_NotRandomGeneratedCanonicalCodeOrder(string state)
    {
        for(var i=0;i<8;i++)
        {
            var map=new TestMap();var d=Crossing();d["crosses"]![0]!["state"]=state;var result=Import(map,d);Assert.IsTrue(result.Success,result.Message);var data=result.Data!;
            var a=data.RoadCodes["main"];var b=data.RoadCodes["branch"];var c=data.Added.Crosses.Single();
            Assert.AreEqual(state is "connected" or "road1ToRoad2",c.AllowsTransfer(a,b));Assert.AreEqual(state is "connected" or "road2ToRoad1",c.AllowsTransfer(b,a));
            var branch=data.Added.Lines.Single(r=>r.Code==b);Assert.AreEqual((short)1,branch.Properties.PassInOpposeDirection);Assert.AreEqual(500,branch.Properties.Speed);
        }
    }

    [TestMethod,DataRow("missing","CrossNotFound"),DataRow("duplicate","DuplicateFlowCross"),DataRow("connectedInLn","LogicalNodeCrossNotDisconnected")]
    public void InvalidCrossDeclarationRollsBackAllCreatedRoadsAndLns(string kind,string code)
    {
        var map=new TestMap();var d=Crossing();if(kind=="missing")d["crosses"]![0]!["at"]=JsonNode.Parse("[1,0]");
        if(kind=="duplicate")d["crosses"]!.AsArray().Add(d["crosses"]![0]!.DeepClone());
        if(kind=="connectedInLn")d["ln"]![0]!["at"]=JsonNode.Parse("[0.5,0.5]");
        var before=map.Snapshot();var result=Import(map,d);Assert.AreEqual(code,result.Code,result.Message);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void LongRoadSubdividesAroundLn_AndOriginHalfIntegerAndWorldBoundsAreEnforced()
    {
        var map=new TestMap();var d=Small();d["roads"]![0]!["points"]=JsonNode.Parse("[[-300,0],[300,0]]");var result=Import(map,d,1000.5m,1000.5m);Assert.IsTrue(result.Success,result.Message);
        var line=result.Data!.Added.Lines.Single();var ln=result.Data.Added.LogicalNodes.Single();Assert.IsTrue(line.Nodes.Length>2);
        Assert.IsTrue(line.Nodes.All(p=>!ln.Contains(p.X,p.Y)));Assert.IsTrue(line.Nodes.Zip(line.Nodes.Skip(1)).All(p=>Math.Abs(p.Second.X-p.First.X)<=500));
        var before=map.Snapshot();Assert.AreEqual("InvalidFlowOrigin",Import(map,x:50,y:50.5m).Code);Assert.AreEqual("LogicalNodeOutOfBounds",Import(map,x:.5m,y:.5m).Code);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void MarkdownFenceIsAccepted_OtherUsersPendingCrossAndLnMarksArePreserved()
    {
        var map=new TestMap();var pair=MapAreaMoveTests.Pair(map,CrossState.Disconnected);MapAreaMoveTests.Node(map);
        map.Repository.Transaction(s=>{s.StaleCrosses.Add(pair.Cross.Key);s.DirtyLines.Add(pair.A.Code);s.PendingSegments.Add((pair.A.Code,0));var p=s.LogicalNodePositions.Values.First();s.LogicalNodePositions[p.Key]=p with{IsChanged=true};return true;});
        string Old()=>map.Repository.Transaction(s=>JsonSerializer.Serialize(new{Cross=s.Crosses[pair.Cross.Key],Stale=s.StaleCrosses.Order(),Dirty=s.DirtyLines.Order(),Pending=s.PendingSegments.OrderBy(p=>p.LineCode).Select(p=>new{p.LineCode,p.Segment}),Positions=s.LogicalNodePositions.Values.Where(p=>p.RoadCode==pair.A.Code||p.RoadCode==pair.B.Code).OrderBy(p=>p.RoadCode)}));
        var before=Old();var result=new FlowDiagramService(map.Repository).Import(new("owner","```json\n"+Example+"\n```",2000.5m,2000.5m));Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(before,Old());
    }

    [TestMethod]
    public void HalfIntegerDiagonalCrossAndResponseArraysAreDetachedFromCommittedState()
    {
        var map=new TestMap();var d=Crossing();d["roads"]![0]!["points"]=JsonNode.Parse("[[-10,-10],[11,11]]");d["roads"]![1]!["points"]=JsonNode.Parse("[[-10,11],[11,-10]]");d["crosses"]![0]!["at"]=JsonNode.Parse("[0.5,0.5]");
        var result=Import(map,d);Assert.IsTrue(result.Success,result.Message);var data=result.Data!;Assert.AreEqual(70.5,data.Added.Crosses.Single().Key.Position.X);Assert.AreEqual(30.5,data.Added.Crosses.Single().Key.Position.Y);
        var before=map.Snapshot();data.Added.Lines[0].Nodes[0]=data.Added.Lines[0].Nodes[0] with{X=12345};data.Map!.Lines[0].Nodes[0]=data.Map.Lines[0].Nodes[0] with{Y=54321};Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void PublishedMinimalDocumentationExampleCanBeImported()
    {
        var docs=File.ReadAllText(Path.Combine(ProjectPaths.Root,"FlowDiagramLanguage.md"));var json=Regex.Match(docs,"```json\\r?\\n([\\s\\S]*?)```").Groups[1].Value;
        var result=new FlowDiagramService(new InMemoryLineRepository()).Import(new("owner",json,1200.5m,5000.5m));Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(4,result.Data!.Added.LogicalNodes.Length);Assert.AreEqual(1,result.Data.Added.Lines.Length);
    }

    [TestMethod]
    public void NewRoadAlsoUpdatesExistingLnProjections_PreservesOtherOwnerAndMetadata()
    {
        var map=new TestMap();var existing=new LogicalNodeService(map.Repository).Create(new("other",80.5m,50.5m,6,new(){Value="已有 LN",Info="保留资料"})).Data!;
        var d=Small();d["roads"]![0]!["points"]=JsonNode.Parse("[[-10,0],[50,0]]");var result=Import(map,d);Assert.IsTrue(result.Success,result.Message);
        var after=result.Data!.Map!.LogicalNodes.Single(n=>n.Code==existing.Code);Assert.IsTrue(after.Version>existing.Version);Assert.AreEqual(existing.Properties,after.Properties);Assert.AreEqual("other",after.Owner);Assert.AreEqual(false,after.CanEdit);
        Assert.AreEqual(result.Data.Added.Lines.Single().Version,after.Positions.Single().RoadVersion);
        var committed=map.Snapshot();Assert.IsTrue(new LogicalNodeService(map.Repository).Calculate().Success);Assert.AreEqual(committed,map.Snapshot());
        Assert.AreEqual(80.5m,after.Positions.Single().X);Assert.AreEqual(result.Data.RoadCodes["main"],after.Positions.Single().RoadCode);Assert.AreEqual(1,result.Data.Added.LogicalNodes.Length);
    }
}
