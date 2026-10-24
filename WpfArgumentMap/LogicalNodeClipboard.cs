using System.Text.Json;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

internal sealed record LogicalNodeCopyData(int SideLength,LogicalNodeProperties Properties);

internal static class LogicalNodeClipboard
{
    private const string Format="argumentmap.ln/1";
    internal static bool Valid(int size,LogicalNodeProperties? p)=>size is 4 or 6 or 8 or 10 or 12 &&
        p is {Value:not null,LogicLanguage:not null,Info:not null} && p.Value.Length<=(size==10?LogicalNodeInstanceRules.ValueMaximum:32)&&p.LogicLanguage.Length<=(size==10?LogicalNodeInstanceRules.LogicMaximum:255)&&p.Info.Length<=255 &&
        !LogicalNodeTextRules.HasLineBreak(p) && LogicalNodeOperandRules.Valid(size,p.OperandKind) &&
        (size!=6||p.LogicLanguage==LogicalNodeOperandRules.Normalize(p.LogicLanguage,p.OperandKind)) &&
        (size switch
        {
            4=>Ln4Clipboard.Valid(p),
            8=>LogicalNodeRelationRules.ValidValue(p.Value)&&p.LogicLanguage==p.Value,
            10=>LogicalNodeInstanceRules.ValidFormat(p.Value)&&LogicalNodeInstanceRules.HasEnglishLetter(LogicalNodeInstanceRules.ParentValue(p.Value))&&p.Value==LogicalNodeInstanceRules.Normalize(p.Value)&&p.LogicLanguage==LogicalNodeInstanceRules.Expression(p.Value),
            _=>!string.IsNullOrWhiteSpace(p.LogicLanguage)
        });

    internal static string Encode(int size,LogicalNodeProperties p)
    {
        if(!Valid(size,p))throw new ArgumentException("不支持的 LN 副本。");
        // Preserve compatibility with LN4 copies already on the clipboard.
        return size==4?Ln4Clipboard.Encode(p):size==6?JsonSerializer.Serialize(new{format=Format,size,value=p.Value,logic=p.LogicLanguage,info=p.Info,operandKind=p.OperandKind}):JsonSerializer.Serialize(new{format=Format,size,value=p.Value,logic=p.LogicLanguage,info=p.Info});
    }

    internal static LogicalNodeCopyData? Decode(string? text)
    {
        if(Ln4Clipboard.Decode(text) is {} legacy)return new(4,legacy);
        if(string.IsNullOrWhiteSpace(text)||text.Length>32_768)return null;
        try
        {
            using var document=JsonDocument.Parse(text,new JsonDocumentOptions{MaxDepth=4});var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return null;
            var keys=new HashSet<string>(StringComparer.Ordinal);
            foreach(var field in root.EnumerateObject())
            {
                if(!keys.Add(field.Name))return null;
                if(field.Name=="size")
                {if(field.Value.ValueKind!=JsonValueKind.Number||!field.Value.TryGetInt32(out _))return null;}
                else if(field.Name is not ("format" or "value" or "logic" or "info" or "operandKind")||field.Value.ValueKind!=JsonValueKind.String)return null;
            }
            if(keys.Count is not (5 or 6)||!new[]{"format","size","value","logic","info"}.All(keys.Contains)||root.GetProperty("format").GetString()!=Format)return null;
            var size=root.GetProperty("size").GetInt32();
            var p=new LogicalNodeProperties{Value=root.GetProperty("value").GetString()!,LogicLanguage=root.GetProperty("logic").GetString()!,Info=root.GetProperty("info").GetString()!,OperandKind=root.TryGetProperty("operandKind",out var kind)?kind.GetString()!:LogicalNodeOperandRules.Custom};
            return Valid(size,p)?new(size,p):null;
        }
        catch(JsonException){return null;}
    }
}
