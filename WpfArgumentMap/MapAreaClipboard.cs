using System.Text.Json;
using System.Text.Json.Serialization;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

internal static class MapAreaClipboard
{
    private sealed record Envelope(string Format,MapAreaSelection Selection);
    private const string Format="argumentmap.area/1";
    private static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=32};
    internal static string Encode(MapAreaSelection selection)
    {
        if(!MapAreaCopyRules.Valid(selection))throw new ArgumentException("无效的框选副本。");
        // Derived positions are recomputed at the destination, never pasted.
        var data=selection with{LogicalNodes=selection.LogicalNodes.Select(n=>n with{Positions=[],CanEdit=null}).ToArray()};
        return JsonSerializer.Serialize(new Envelope(Format,data),Options);
    }
    internal static MapAreaSelection? Decode(string? text)=>Decode(text,out _);
    internal static MapAreaSelection? Decode(string? text,out bool isAreaClipboard)
    {
        isAreaClipboard=false;
        if(string.IsNullOrWhiteSpace(text)||text.Length>MapAreaCopyRules.MaximumTextLength)return null;
        try
        {
            using var document=JsonDocument.Parse(text,new(){MaxDepth=32});
            isAreaClipboard=document.RootElement.ValueKind==JsonValueKind.Object&&document.RootElement.TryGetProperty("format",out var format)&&
                format.ValueKind==JsonValueKind.String&&format.GetString()!.StartsWith("argumentmap.area/",StringComparison.Ordinal);
            if(HasDuplicates(document.RootElement))return null;
            var data=document.RootElement.Deserialize<Envelope>(Options);
            return data?.Format==Format&&MapAreaCopyRules.Valid(data.Selection)?data.Selection:null;
        }
        catch(JsonException){return null;}
    }
    private static bool HasDuplicates(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var keys=new HashSet<string>(StringComparer.Ordinal);
            return element.EnumerateObject().Any(p=>!keys.Add(p.Name)||HasDuplicates(p.Value));
        }
        return element.ValueKind==JsonValueKind.Array&&element.EnumerateArray().Any(HasDuplicates);
    }
}
