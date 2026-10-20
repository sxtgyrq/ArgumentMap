using System.Text.Json;
using System.Text.Json.Serialization;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

internal static class LineClipboard
{
    private sealed record Payload(string Format,LineCopyData Source);
    private const string Format="argumentmap.road/1";
    private static readonly JsonSerializerOptions Options=new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow};
    internal static string Encode(LineCopyData source)=>JsonSerializer.Serialize(new Payload(Format,source));
    internal static LineCopyData? Decode(string? text)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>8_388_608)return null;
        try
        {
            using var document=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=8});
            if(!UniqueFields(document.RootElement))return null;
            var payload=JsonSerializer.Deserialize<Payload>(text,Options);
            return payload?.Format==Format&&Valid(payload.Source)?payload.Source:null;
        }
        catch(JsonException){return null;}
    }
    private static bool UniqueFields(JsonElement element)=>element.ValueKind switch
    {
        JsonValueKind.Object=>element.EnumerateObject().Select(p=>p.Name).Distinct(StringComparer.Ordinal).Count()==element.EnumerateObject().Count()&&element.EnumerateObject().All(p=>UniqueFields(p.Value)),
        JsonValueKind.Array=>element.EnumerateArray().All(UniqueFields),_=>true
    };
    internal static bool Valid(LineCopyData? source)=>source is {Nodes.Length:>=2 and <=100000,Properties:{} p}&&LineCode.IsValid(source.Code)&&source.Version>=0&&
        source.Nodes.Select((n,i)=>n is not null&&n.LineCode==source.Code&&n.Order==i&&n.X is >=0 and <=MapLimits.CoordinateMaximum&&n.Y is >=0 and <=MapLimits.CoordinateMaximum).All(valid=>valid)&&
        p.RoadName is not null&&p.RoadName.Length<16&&p.RoadDetail is not null&&p.RoadDetail.Length<128&&p.Speed is >=MapLimits.SpeedMinimum and <=MapLimits.SpeedMaximum&&p.PassInDirection==1&&p.PassInOpposeDirection is 0 or 1;
}
