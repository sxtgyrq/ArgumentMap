using System.Text.Json;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class CrossRoadNamingTests
{
    [TestMethod]
    public void CrossUsesOldRoadNames_AndEachOrdersPercentAndVersionBelongToItsRoad()
    {
        var key=CrossKey.Create("AAAAAAAAAA","ZZZZZZZZZZ",new(201,221));Assert.AreEqual("ZZZZZZZZZZ",key.RoadCode1);Assert.AreEqual("AAAAAAAAAA",key.RoadCode2);
        var cross=new CrossSnapshot(key,2,5,7,9,CrossState.Road1ToRoad2,3,1d/3,0.125);
        Assert.AreEqual((2,1d/3),cross.LocationOn(key.RoadCode1));Assert.AreEqual((5,0.125),cross.LocationOn(key.RoadCode2));
        Assert.IsTrue(cross.AllowsTransfer(key.RoadCode1,key.RoadCode2));Assert.IsFalse(cross.AllowsTransfer(key.RoadCode2,key.RoadCode1));
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(cross));var root=json.RootElement;
        Assert.AreEqual("ZZZZZZZZZZ",root.GetProperty("Key").GetProperty("RoadCode1").GetString());
        foreach(var obsolete in new[]{"Segment1","Segment2","Line1Version","Line2Version"})Assert.IsFalse(root.TryGetProperty(obsolete,out _));
        foreach(var obsolete in new[]{"Line1Code","Line2Code"})Assert.IsFalse(root.GetProperty("Key").TryGetProperty(obsolete,out _));
        Assert.AreEqual(2,root.GetProperty("RoadOrder1").GetInt32());Assert.AreEqual(7L,root.GetProperty("RoadVersion1").GetInt64());Assert.AreEqual(1d/3,root.GetProperty("Percent1").GetDouble());
        var position=new LogicalNodePosition("ABCDEFGHIJ",key.RoadCode1,2,1d/3,100.5m,110m,7);
        using var ln=JsonDocument.Parse(JsonSerializer.Serialize(position));Assert.AreEqual(key.RoadCode1,ln.RootElement.GetProperty("RoadCode").GetString());Assert.AreEqual(2,ln.RootElement.GetProperty("RoadOrder").GetInt32());Assert.AreEqual(1d/3,ln.RootElement.GetProperty("RoadPercent").GetDouble());
    }
}
