namespace ArgumentMap.Contracts;

public sealed record MapUndoResult(string Operation, MapAreaSnapshot Map);
public sealed record MapUndoRequest(string UserName, string LoginCode, string RequestId);

public interface IMapUndoClient
{
    Task<ServiceResult<MapUndoResult>> UndoAsync();
}
