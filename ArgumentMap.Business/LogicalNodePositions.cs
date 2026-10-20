using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

internal static class LogicalNodePositions
{
    internal static LogicalNodeSnapshot Snapshot(LineStoreState state,LogicalNodeSnapshot node) => node with
    { Positions=state.LogicalNodePositions.Values.Where(p=>p.LogicalNodeCode==node.Code).OrderBy(p=>p.RoadCode).ThenBy(p=>p.RoadOrder).ToArray() };

    internal static void RebuildNode(LineStoreState state,string code,bool incrementVersion=true)
    {
        var node=state.LogicalNodes[code];
        Replace(state,node,LogicalNodePositionCalculator.Calculate(node,state.Lines.Values),null,incrementVersion);
    }
    internal static void RebuildRoad(LineStoreState state,string roadCode)
    {
        foreach(var node in state.LogicalNodes.Values.ToArray())
        {
            var roads=state.Lines.TryGetValue(roadCode,out var road)?new[]{road}:[];
            Replace(state,node,LogicalNodePositionCalculator.Calculate(node,roads),new HashSet<string>(StringComparer.Ordinal){roadCode},true);
        }
    }
    internal static void RebuildAffected(LineStoreState state,IReadOnlySet<string> roadCodes,IReadOnlySet<string> movedNodes)
    {
        foreach(var node in state.LogicalNodes.Values.ToArray())
        {
            if(movedNodes.Contains(node.Code)){RebuildNode(state,node.Code,incrementVersion:false);continue;}
            // Only changed roads can affect an unmoved LN. Preserve another user's pending
            // positions on unrelated roads; an explicit J still recalculates every relation.
            Replace(state,node,LogicalNodePositionCalculator.Calculate(node,state.Lines.Values.Where(l=>roadCodes.Contains(l.Code))),roadCodes,true);
        }
    }
    private static void Replace(LineStoreState state,LogicalNodeSnapshot node,LogicalNodePosition[] calculated,IReadOnlySet<string>? roadCodes,bool incrementVersion)
    {
        var before=state.LogicalNodePositions.Values.Where(p=>p.LogicalNodeCode==node.Code && (roadCodes is null || roadCodes.Contains(p.RoadCode)))
            .OrderBy(p=>p.RoadCode).ThenBy(p=>p.RoadOrder).ToArray();
        if(before.SequenceEqual(calculated))return;
        // Mirror mark/recalculate/cleanup in memory, then persist the complete result in ONE SQL transaction.
        foreach(var position in before)
        {
            state.LogicalNodePositions[position.Key]=position with{IsChanged=true};
            state.LogicalNodePositionRecalculationMarks.Add(position.Key);
        }
        foreach(var position in calculated)state.LogicalNodePositions[position.Key]=position;
        foreach(var position in before)
            if(state.LogicalNodePositions[position.Key].IsChanged)state.LogicalNodePositions.Remove(position.Key);
        if(incrementVersion)state.LogicalNodes[node.Code]=node with{Version=node.Version+1};
    }
}
