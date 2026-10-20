namespace ArgumentMap.Contracts;

// The selection is a source concurrency token. Paste reloads authoritative data;
// posted properties/owners/positions never become new objects directly.
public sealed record PasteMapAreaRequest(string Owner,MapAreaSelection Selection,int DeltaX,int DeltaY,string LoginCode="");
public sealed record MapAreaPasteResult(MapAreaSnapshot Added,MapAreaSnapshot Map,
    Dictionary<string,string> LogicalNodeCodes,Dictionary<string,string> RoadCodes,string[] Notes);
public interface IMapAreaPasteClient
{
    Task<ServiceResult<MapAreaPasteResult>> PasteMapAreaAsync(MapAreaSelection selection,int deltaX,int deltaY);
}

public static class MapAreaCopyRules
{
    public const int MaximumTextLength=1_000_000;
    public static bool Valid(MapAreaSelection? s)
    {
        if(s?.Bounds is not {} b||b.Left<0||b.Bottom<0||b.Right>500000||b.Top>500000||b.Left>=b.Right||b.Bottom>=b.Top||
            s.Lines is null||s.LogicalNodes is null||s.Crosses is null||s.Lines.Length>256||s.LogicalNodes.Length>256||s.Crosses.Length>4096||s.Lines.Length+s.LogicalNodes.Length==0)return false;
        var roads=new HashSet<string>(StringComparer.Ordinal);var nodes=new HashSet<string>(StringComparer.Ordinal);var points=0;
        foreach(var l in s.Lines)
        {
            if(l is null||!LineCode.IsValid(l.Code)||!roads.Add(l.Code)||l.Nodes is null||l.Nodes.Length<2||l.Properties is null)return false;
            points+=l.Nodes.Length;if(points>4096||l.Nodes.Any(n=>n is null||n.LineCode!=l.Code||!b.Contains(n.X,n.Y)))return false;
        }
        foreach(var n in s.LogicalNodes)
            if(n is null||!LineCode.IsValid(n.Code)||!nodes.Add(n.Code)||n.Properties is not {Value:not null,Info:not null,LogicLanguage:not null}||
                n.SideLength is not (4 or 6 or 8 or 10 or 12)||!b.ContainsEntireSquare(n)||LogicalNodeTextRules.HasLineBreak(n.Properties)||!LogicalNodeOperandRules.Valid(n.SideLength,n.Properties.OperandKind))return false;
        var crosses=new HashSet<CrossKey>();
        return s.Crosses.All(c=>c is not null&&c.Key.IsValid&&crosses.Add(c.Key)&&roads.Contains(c.Key.RoadCode1)&&roads.Contains(c.Key.RoadCode2)&&
            b.Contains(c.Key.Position.X2/2m,c.Key.Position.Y2/2m)&&Enum.IsDefined(c.State));
    }
}
