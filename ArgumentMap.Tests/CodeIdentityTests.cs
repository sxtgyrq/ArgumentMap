using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class CodeIdentityTests
{
    [TestMethod]
    public void GenerateCodes_UsesTenUppercaseLettersAndNoNumericSequence()
    {
        var codes = Enumerable.Range(0, 10000).Select(_ => LineCode.Generate()).ToArray();
        Assert.IsTrue(codes.All(LineCode.IsValid));
        Assert.AreEqual(codes.Length, codes.Distinct(StringComparer.Ordinal).Count());
    }

    [TestMethod]
    [DataRow(null), DataRow(""), DataRow("ABCDEFGHI"), DataRow("ABCDEFGHIJK")]
    [DataRow("abcdefghij"), DataRow("ABCDEFGHI1"), DataRow("道路编码道路编码道路")]
    public void RejectInvalidRoadCode(string? code) => Assert.IsFalse(LineCode.IsValid(code));

    [TestMethod]
    [DataRow("AAAAAAAAAA", "ZZZZZZZZZZ"), DataRow("ZZZZZZZZZZ", "AAAAAAAAAA")]
    public void Cross_Road1IsGreaterCodeAndLocationFieldsFollowTheirRoad(string route, string other)
    {
        var map = new TestMap();
        map.Repository.Transaction(s =>
        {
            s.Lines.Add(route, new(route,"owner",false,0,[]));
            s.Lines.Add(other, new(other,"owner",false,0,[])); return true;
        });
        var a = map.Save(map.Lines.GetDetail(new(route,"owner")).Data!,100,110,110,110,130,110);
        var b = map.Save(map.Lines.GetDetail(new(other,"owner")).Data!,110,100,110,140);
        map.Calculate(); var cross = map.Between(a,b);
        Assert.AreEqual("ZZZZZZZZZZ",cross.Key.RoadCode1); Assert.AreEqual("AAAAAAAAAA",cross.Key.RoadCode2);
        Assert.AreEqual((1,0.0),cross.LocationOn(a.Code)); Assert.AreEqual((0,0.25),cross.LocationOn(b.Code));
        var saved = map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,CrossState.Connected)); Assert.IsTrue(saved.Success);
        a = map.Save(a,100,110,105,110,110,110,130,110); map.Calculate();
        cross = map.Between(a,b);
        Assert.AreEqual(saved.Data!.Key,cross.Key); Assert.AreEqual(CrossState.Connected,cross.State);
        Assert.AreEqual((2,0.0),cross.LocationOn(a.Code)); Assert.AreEqual((0,0.25),cross.LocationOn(b.Code));
        var reversed = map.Lines.Reverse(new(a.Code,a.Owner,a.Version)); Assert.IsTrue(reversed.Success); map.Calculate();
        cross = map.Between(reversed.Data!,b);
        Assert.AreEqual(saved.Data.Key,cross.Key); Assert.AreEqual(CrossState.Connected,cross.State);
        Assert.AreEqual((1,0.0),cross.LocationOn(a.Code)); Assert.AreEqual((0,0.25),cross.LocationOn(b.Code));
        var json = JsonSerializer.Serialize(cross);
        Assert.IsFalse(json.Contains("\"Id\"",StringComparison.Ordinal));
        Assert.IsTrue(map.Lines.Delete(new(reversed.Data!.Code,a.Owner,reversed.Data.Version)).Success);
        Assert.AreEqual(0,map.Crosses.GetAll().Data!.Length);
    }

    [TestMethod]
    public async Task ConcurrentCreate_GeneratesUniqueCodesInsideRepositoryTransactions()
    {
        var map = new TestMap();
        var results = await Task.WhenAll(Enumerable.Range(0,128).Select(_ => Task.Run(() => map.Lines.Create(new("owner")))));
        Assert.IsTrue(results.All(r => r.Success));
        Assert.AreEqual(128,results.Select(r => r.Data!.Code).Distinct(StringComparer.Ordinal).Count());
        Assert.AreEqual(128,map.Repository.Transaction(s => s.Lines.Count));
    }

    [TestMethod]
    public void Cross_RejectsNoncanonicalOrderingAndNumericRoadCodesWithoutCommit()
    {
        var map = new TestMap(); var before = map.Snapshot();
        foreach (var key in new[] { new CrossKey("AAAAAAAAAA","ZZZZZZZZZZ",new(0,0)), new("0000000001","0000000002",new(0,0)) })
            Assert.AreEqual("InvalidCrossKey",map.Crosses.Edit(new(key,"owner",0,CrossState.Connected)).Code);
        Assert.AreEqual(before,map.Snapshot());
    }
}
