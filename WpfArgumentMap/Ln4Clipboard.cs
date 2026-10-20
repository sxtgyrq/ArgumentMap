using System.Text.Json;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

internal static class Ln4Clipboard
{
    private const string Format="argumentmap.ln4/1";
    internal static bool Valid(LogicalNodeProperties properties)=>LogicalNodeBranchRules.ValidValue(properties.Value)&&
        properties.LogicLanguage is not null&&properties.Info is not null&&properties.LogicLanguage.Length<=255&&properties.Info.Length<=255&&!LogicalNodeTextRules.HasLineBreak(properties);
    internal static string Encode(LogicalNodeProperties properties)=>JsonSerializer.Serialize(new{format=Format,value=properties.Value,logic=properties.LogicLanguage,info=properties.Info});
    internal static LogicalNodeProperties? Decode(string? text)
    {
        if(string.IsNullOrWhiteSpace(text)||text.Length>8192)return null;
        try
        {
            using var document=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=4});var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return null;
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in root.EnumerateObject())
                if(field.Name is not ("format" or "value" or "logic" or "info")||field.Value.ValueKind!=JsonValueKind.String||!keys.Add(field.Name))return null;
            if(keys.Count!=4||root.GetProperty("format").GetString()!=Format)return null;
            var properties=new LogicalNodeProperties{Value=root.GetProperty("value").GetString()!,LogicLanguage=root.GetProperty("logic").GetString()!,Info=root.GetProperty("info").GetString()!};
            return Valid(properties)?properties:null;
        }
        catch(JsonException){return null;}
    }
}
