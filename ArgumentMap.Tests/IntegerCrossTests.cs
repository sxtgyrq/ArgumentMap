using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class IntegerCrossTests
{
    private static LineSnapshot Road(int id,params int[] xy)
    {
        var code="AAAAAAAA"+(char)('A'+id/26)+(char)('A'+id%26);
        return new(code,"owner",false,0,Enumerable.Range(0,xy.Length/2).Select(i=>new LineNode(code,i,xy[i*2],xy[i*2+1])).ToArray());
    }

    // Independent analytic oracle: continuous segment intersection followed by the
    // specified integer-only rule, with historical road/segment ordering.
    internal static CrossKey[] Oracle(IEnumerable<LineSnapshot> source)
    {
        var lines=source.OrderBy(l=>l.Code,StringComparer.Ordinal).ToArray();var result=new List<CrossKey>();
        for(var i=0;i<lines.Length;i++)for(var j=i+1;j<lines.Length;j++)
        {
            var seen=new HashSet<CrossPosition>();
            for(var a=0;a+1<lines[i].Nodes.Length;a++)for(var b=0;b+1<lines[j].Nodes.Length;b++)
                if(LineGeometry.TryIntersection(lines[i].Nodes[a],lines[i].Nodes[a+1],lines[j].Nodes[b],lines[j].Nodes[b+1],out var p)&&p.IsInteger&&seen.Add(p))
                    result.Add(CrossKey.Create(lines[i].Code,lines[j].Code,p));
        }
        return result.ToArray();
    }

    [TestMethod]
    public void PointIndexMatchesAnalyticOracle_ForRandomEightDirectionsEndpointsBendsAndSelfCrossings()
    {
        var random=new Random(73189);var directions=new[]{(1,0),(0,1),(1,1),(1,-1),(-1,0),(0,-1),(-1,-1),(-1,1)};
        for(var attempt=0;attempt<120;attempt++)
        {
            var roads=new List<LineSnapshot>();
            for(var i=0;i<12;i++)
            {
                var xy=new List<int>{random.Next(30,60),random.Next(30,60)};
                for(var j=0;j<3;j++){var (dx,dy)=directions[random.Next(8)];var length=random.Next(1,9);xy.Add(xy[^2]+dx*length);xy.Add(xy[^2]+dy*length);}
                roads.Add(Road(i,xy.ToArray()));
            }
            var index=new IntegerCrossIndex(roads);Assert.IsTrue(index.UsesPointIndex);
            CollectionAssert.AreEqual(Oracle(roads),index.Intersections.ToArray());
            foreach(var road in roads)for(var segment=0;segment+1<road.Nodes.Length;segment++)
            {
                var expected=new List<IntegerCrossIndex.Contact>();
                foreach(var other in roads.Where(r=>r.Code!=road.Code).OrderBy(r=>r.Code,StringComparer.Ordinal))
                    for(var j=0;j+1<other.Nodes.Length;j++)
                        if(LineGeometry.TryCrossIntersection(road.Nodes[segment],road.Nodes[segment+1],other.Nodes[j],other.Nodes[j+1],out var p))expected.Add(new(other.Code,j,p));
                CollectionAssert.AreEqual(expected.ToArray(),index.ForSegment(road.Code,segment).ToArray());
            }
        }
    }

    [TestMethod]
    public void IsolatedLongRoadAndSelfIntersectionNeedNoPointEnumeration()
    {
        var road=Road(0,0,0,500000,500000,0,500000,500000,0);
        var index=new IntegerCrossIndex([road,Road(1)]);
        Assert.IsFalse(index.UsesPointIndex);Assert.AreEqual(0,index.Intersections.Count);
        Assert.AreEqual(0,index.ForSegment(road.Code,0).Count);Assert.IsTrue(index.Matches([road,Road(1)]));
    }

    [TestMethod]
    public void LargeMapFallbackRetainsExactResultsWithoutExpandingMillionsOfPoints()
    {
        var roads=Enumerable.Range(0,6).Select(i=>Road(i,0,i*20,500000,i*20)).Append(Road(6,100,0,100,500000)).ToArray();
        var index=new IntegerCrossIndex(roads);Assert.IsFalse(index.UsesPointIndex);
        CollectionAssert.AreEqual(Oracle(roads),index.Intersections.ToArray());
    }

    [TestMethod]
    public void HalfIntegerCrossingIsIgnored_ContinuousGeometryAndLnProjectionRemainExact()
    {
        var map=new TestMap();map.Add(100,100,101,101);map.Add(100,101,101,100);map.Calculate();
        Assert.AreEqual(0,map.Crosses.GetAll().Data!.Length);
        var a=Road(0,100,100,101,101);var b=Road(1,100,101,101,100);
        Assert.IsTrue(LineGeometry.TryIntersection(a.Nodes[0],a.Nodes[1],b.Nodes[0],b.Nodes[1],out var geometric));Assert.AreEqual(new CrossPosition(201,201),geometric);
        Assert.IsFalse(LineGeometry.TryCrossIntersection(a.Nodes[0],a.Nodes[1],b.Nodes[0],b.Nodes[1],out _));
        Assert.IsFalse(CrossKey.Create(a.Code,b.Code,geometric).IsValid);
        var snapshot=map.Snapshot();
        Assert.ThrowsExactly<InvalidCrossCoordinateException>(()=>map.Repository.Transaction(s=>{var key=CrossKey.Create(a.Code,b.Code,geometric);s.Crosses[key]=new(key,0,0,1,1,CrossState.Connected,0);return true;}));
        Assert.AreEqual(snapshot,map.Snapshot());
    }

    [TestMethod,DataRow(CrossState.Unset),DataRow(CrossState.Connected),DataRow(CrossState.Disconnected),DataRow(CrossState.Road1ToRoad2),DataRow(CrossState.Road2ToRoad1)]
    public void MarkedIntegerCrossReusesIdentityAndPassageAfterNodeInsertionAndReversal(CrossState passage)
    {
        var map=new TestMap();var a=map.Add(100,110,120,110);var b=map.Add(110,100,110,120);map.Calculate();var old=map.Between(a,b);
        old=map.Crosses.Edit(new(old.Key,"owner",old.Version,passage)).Data!;
        a=map.Save(a,100,110,110,110,120,110);
        Assert.AreEqual(old,map.Repository.Transaction(s=>s.Crosses[old.Key]));Assert.AreEqual(0,map.Crosses.GetAll().Data!.Length);
        map.Calculate();var restored=map.Between(a,b);Assert.AreEqual(old.Key,restored.Key);Assert.AreEqual(passage,restored.State);
        Assert.AreEqual(1,restored.LocationOn(a.Code).RoadOrder);Assert.AreEqual(0d,restored.LocationOn(a.Code).Percent);
        var reversed=map.Lines.Reverse(new(a.Code,"owner",a.Version));Assert.IsTrue(reversed.Success,reversed.Message);map.Calculate();
        Assert.AreEqual(passage,map.Between(reversed.Data!,b).State);
        var stable=map.Snapshot();map.Calculate();Assert.AreEqual(stable,map.Snapshot());
    }

    [TestMethod]
    public void CachedGeometryDetectsShapeChangesWithinTransactionAndDoesNotCacheCrossState()
    {
        var map=new TestMap();var a=map.Add(100,110,120,110);var b=map.Add(110,100,110,120);map.Calculate();
        map.Repository.Transaction(s=>
        {
            var first=CrossGeometry.For(s);Assert.AreSame(first,CrossGeometry.For(s));
            s.Lines[a.Code]=a with{Nodes=TestMap.Shape(a,100,111,120,111).Nodes};
            var second=CrossGeometry.For(s);Assert.AreNotSame(first,second);Assert.AreEqual(new CrossPosition(220,222),second.Intersections.Single().Position);
            s.Lines[a.Code].Nodes[0]=s.Lines[a.Code].Nodes[0] with{Y=112};s.Lines[a.Code].Nodes[1]=s.Lines[a.Code].Nodes[1] with{Y=112};
            var third=CrossGeometry.For(s);Assert.AreNotSame(second,third);Assert.AreEqual(new CrossPosition(220,224),third.Intersections.Single().Position);
            return ServiceResult<bool>.Fail("Probe","discard");
        });
    }

    [TestMethod]
    public void IntegerPointCandidateDoesNotRelaxOverlapOrBendContactAngleRules()
    {
        var map=new TestMap();map.Add(100,100,110,110,110,120);var candidate=map.Create();
        Assert.AreEqual("NonOrthogonalIntersection",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate,100,110,120,110)).Code);
        Assert.AreEqual("SegmentOverlap",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(candidate,102,102,108,108)).Code);
    }

    [TestMethod]
    public void FlowRejectsExplicitHalfCrossButAllowsTheSameGeometricPassageWithoutACross()
    {
        var document=FlowDiagramTests.Small();document["roads"]=JsonNode.Parse("""[{"id":"a","points":[[30,30],[31,31]]},{"id":"b","points":[[30,31],[31,30]]}]""");
        document["crosses"]=JsonNode.Parse("""[{"road1":"a","road2":"b","at":[30.5,30.5],"state":"connected"}]""");
        var map=new TestMap();var before=map.Snapshot();Assert.IsFalse(FlowDiagramTests.Import(map,document).Success);Assert.AreEqual(before,map.Snapshot());
        document["crosses"]=new JsonArray();var imported=FlowDiagramTests.Import(map,document);Assert.IsTrue(imported.Success,imported.Message);Assert.AreEqual(0,imported.Data!.Added.Crosses.Length);
    }
}
