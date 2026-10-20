using System.Text.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

internal sealed class TestMap
{
    internal InMemoryLineRepository Repository { get; } = new();
    internal LineSaveService Lines { get; }
    internal CrossService Crosses { get; }
    internal TestMap() { Lines = new(Repository); Crosses = new(Repository); }
    internal LineSnapshot Create(string owner = "owner") => Lines.Create(new(owner)).Data!;
    internal static SaveLineRequest Shape(LineSnapshot line, params int[] xy)
    {
        Assert.AreEqual(0, xy.Length % 2);
        return new(line.Code, line.Owner, line.Version, Enumerable.Range(0, xy.Length / 2)
            .Select(i => new LineNode(line.Code, i, xy[i * 2], xy[i * 2 + 1])).ToArray());
    }
    internal LineSnapshot Save(LineSnapshot line, params int[] xy)
    {
        var result = Lines.CheckShapeIsOKThenSave(Shape(line, xy));
        Assert.IsTrue(result.Success, result.Message);
        return result.Data!;
    }
    internal LineSnapshot Add(params int[] xy) => Save(Create(), xy);
    // Precision regression fixtures deliberately model pre-subdivision stored roads. They are
    // loaded through the repository rather than F6, so tiny legacy percentages stay covered.
    internal static LineSnapshot HistoricalRoad(ILineRepository repository,params int[] xy)
    {
        var created=new LineSaveService(repository).Create(new("owner")).Data!;
        var road=created with{Version=1,Nodes=Shape(created,xy).Nodes};
        repository.Transaction(s=>{s.Lines[road.Code]=road;s.DirtyLines.Add(road.Code);for(var i=0;i<road.Nodes.Length-1;i++)s.PendingSegments.Add((road.Code,i));return true;});return road;
    }
    internal void Calculate()
    {
        for (var i = 0; i < 100; i++)
        {
            var step = Crosses.Calculate();
            Assert.IsTrue(step.Success, step.Message);
            if (!step.Data!.CanContinue) return;
        }
        Assert.Fail("Small fixture calculation failed to finish within 100 transactions.");
    }
    internal CrossSnapshot Between(LineSnapshot a, LineSnapshot b) => Crosses.GetAll().Data!.Single(c =>
        c.Key.RoadCode1 == CrossKey.Create(a.Code,b.Code,new()).RoadCode1 && c.Key.RoadCode2 == CrossKey.Create(a.Code,b.Code,new()).RoadCode2);
    internal string Snapshot() => Repository.Transaction(s => JsonSerializer.Serialize(new
    {
        LineCount = s.Lines.Count, CrossCount = s.Crosses.Count,
        Lines = s.Lines.Values.OrderBy(l => l.Code).ToArray(),
        Crosses = s.Crosses.Values.OrderBy(c => c.Key).ToArray(),
        LogicalNodes = s.LogicalNodes.Values.OrderBy(n => n.Code).ToArray(),
        Positions = s.LogicalNodePositions.Values.OrderBy(p=>p.LogicalNodeCode).ThenBy(p=>p.RoadCode).ThenBy(p=>p.RoadOrder).ToArray(),
        Pending = s.PendingSegments.OrderBy(p => p.LineCode).ThenBy(p => p.Segment).Select(p => new { p.LineCode, p.Segment }).ToArray(),
        Stale = s.StaleCrosses.Order().ToArray(), Dirty = s.DirtyLines.Order().ToArray()
    }));
}
