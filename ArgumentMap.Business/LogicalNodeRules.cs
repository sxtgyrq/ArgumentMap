using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using ArgumentMap.Rules;

namespace ArgumentMap.Business;

// The server adapts its transaction snapshot to the same database-free geometry rules used by previews.
public static class LogicalNodeRules
{
    public static bool IsHalfInteger(decimal value) => LogicalNodeGeometryRules.IsHalfInteger(value);
    public static bool ContainsAny(LineStoreState state, CrossPosition point) => state.LogicalNodes.Values.Any(n => n.Contains(point));
    public static LogicalNodeViolation? ValidateShape(decimal x, decimal y, int size) => LogicalNodeGeometryRules.ValidateShape(x,y,size);
    public static LogicalNodeViolation? ValidateNew(LogicalNodeSnapshot node, LineStoreState state) =>
        LogicalNodeGeometryRules.ValidateNew(node,state.Lines.Values,state.Crosses,state.LogicalNodes.Values);
    public static LogicalNodeViolation? ValidateMap(LineStoreState state, LineSnapshot? replacement = null) =>
        LogicalNodeGeometryRules.ValidateMap(state.Lines.Values,state.Crosses,state.LogicalNodes.Values,replacement);
}
