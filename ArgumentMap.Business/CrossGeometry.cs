using System.Runtime.CompilerServices;
using ArgumentMap.Rules;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

internal static class CrossGeometry
{
    private sealed class Cache {internal IntegerCrossIndex? Index;}
    private static readonly ConditionalWeakTable<LineStoreState,Cache> caches=new();
    internal static IntegerCrossIndex For(LineStoreState state)
    {
        var cache=caches.GetOrCreateValue(state);
        if(cache.Index is null||!cache.Index.Matches(state.Lines.Values))cache.Index=new(state.Lines.Values);
        return cache.Index;
    }
}
