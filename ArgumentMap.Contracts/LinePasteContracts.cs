namespace ArgumentMap.Contracts;

// A clipboard snapshot is only a concurrency token/preview. The server copies the
// persisted source, never the posted owner, properties or lock flag.
public sealed record LineCopyData(string Code,long Version,LineNode[] Nodes,LineProperties Properties,bool IsLocked);
public sealed record PasteLineRequest(string Owner,LineCopyData Source,int X,int Y,string LoginCode="");
public sealed record LinePasteResult(LineSnapshot Line,MapAreaSnapshot Map);
public interface ILinePasteClient
{
    Task<ServiceResult<LinePasteResult>> PasteLineAsync(LineCopyData source,int x,int y);
}
