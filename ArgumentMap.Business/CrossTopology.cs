using ArgumentMap.Rules;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

internal static class CrossTopology
{
    // Match the old DictCross query (IsChanged >= 0). Pending records stay in
    // storage so K can restore their passage settings, but are not map markers.
    internal static CrossSnapshot[] Visible(LineStoreState state) => state.Crosses.Values
        .Where(c => c.Key.Position.IsInteger && !state.StaleCrosses.Contains(c.Key) &&
            state.Lines.ContainsKey(c.Key.RoadCode1) && state.Lines.ContainsKey(c.Key.RoadCode2))
        .OrderBy(c => c.Key).ToArray();

    internal static void MarkChanged(LineStoreState state,IReadOnlySet<string> roads,bool recordSqlMarks=false)
    {
        foreach(var key in state.Crosses.Keys.Where(k=>roads.Contains(k.RoadCode1)||roads.Contains(k.RoadCode2)))
        {
            state.StaleCrosses.Add(key);
            if(recordSqlMarks)state.CrossRecalculationMarks.Add(key);
        }
    }

    internal static void Cleanup(LineStoreState state,IReadOnlySet<string>? roads=null)
    {
        bool Affected(CrossKey key)=>roads is null||roads.Contains(key.RoadCode1)||roads.Contains(key.RoadCode2);
        foreach(var key in state.Crosses.Keys.Where(k=>Affected(k)&&(state.StaleCrosses.Contains(k)||
            !state.Lines.ContainsKey(k.RoadCode1)||!state.Lines.ContainsKey(k.RoadCode2))).ToArray())
            state.Crosses.Remove(key);
        state.StaleCrosses.RemoveWhere(Affected);
    }

    internal static void CalculateSegment(LineStoreState state, string lineCode, int segment)
    {
        if (!state.Lines.TryGetValue(lineCode, out var line) || segment < 0 || segment + 1 >= line.Nodes.Length) return;
        foreach (var contact in CrossGeometry.For(state).ForSegment(lineCode,segment))
            {
                var other=state.Lines[contact.OtherRoadCode];var j=contact.OtherSegment;var position=contact.Position;
                var lineFirst = string.CompareOrdinal(line.Code, other.Code) > 0;
                var first = lineFirst ? line : other;
                var second = lineFirst ? other : line;
                var location1 = LineGeometry.LocateCross(first.Nodes, lineFirst ? segment : j, position);
                var location2 = LineGeometry.LocateCross(second.Nodes, lineFirst ? j : segment, position);
                var key = new CrossKey(first.Code, second.Code, position);
                state.Crosses.TryGetValue(key, out var existing);
                var cross = existing is not null
                    ? existing with
                    {
                        Key = key,
                        RoadOrder1 = location1.Segment, RoadOrder2 = location2.Segment,
                        Percent1 = location1.Percent, Percent2 = location2.Percent,
                        RoadVersion1 = first.Version, RoadVersion2 = second.Version,
                        Version = existing.Version + (existing.RoadVersion1 != first.Version || existing.RoadVersion2 != second.Version ||
                            existing.RoadOrder1 != location1.Segment || existing.RoadOrder2 != location2.Segment ||
                            existing.Percent1 != location1.Percent || existing.Percent2 != location2.Percent ? 1 : 0)
                    }
                    : new CrossSnapshot(key, location1.Segment, location2.Segment, first.Version, second.Version,
                        LogicalNodeRules.ContainsAny(state, position) ? CrossState.Disconnected : CrossState.Unset, 0, location1.Percent, location2.Percent);
                if (existing is not null)
                {
                    state.StaleCrosses.Remove(existing.Key);
                }
                state.Crosses[key] = cross;
            }
    }

}
