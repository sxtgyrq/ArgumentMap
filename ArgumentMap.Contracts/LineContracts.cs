namespace ArgumentMap.Contracts;

public static class MapLimits
{
    public const int CoordinateMaximum = 500_000;
    public const int SpeedMinimum = 5;
    public const int SpeedMaximum = 500;
    public const int SpeedDefault = 100;
    public const int SavedSegmentLengthMaximum = 500;
}

public sealed record LineNode(string LineCode, int Order, int X, int Y);
public sealed record LineProperties
{
    public string RoadName { get; init; } = "";
    public string RoadDetail { get; init; } = "";
    public int Speed { get; init; } = MapLimits.SpeedDefault;
    public short PassInDirection { get; init; } = 1;
    public short PassInOpposeDirection { get; init; } = 0;
}
public sealed record LineSnapshot(string Code, string Owner, bool IsLocked, long Version, LineNode[] Nodes)
{
    public LineProperties Properties { get; init; } = new();
}
public sealed record CreateLineRequest(string Owner, string LoginCode = "");
public sealed record LineAccessRequest(string LineCode, string Owner, string LoginCode = "");
public sealed record SaveLineRequest(string LineCode, string Owner, long Version, LineNode[] Nodes, string LoginCode = "");
public sealed record DeleteLineRequest(string LineCode, string Owner, long Version, string LoginCode = "");
public sealed record SaveLinePropertiesRequest(string LineCode, string Owner, long Version, LineProperties Properties, string LoginCode = "");
public sealed record ToggleLineLockRequest(string LineCode, string Owner, long Version, string LoginCode = "");
public sealed record ReverseLineRequest(string LineCode, string Owner, long Version, string LoginCode = "");

public interface ILineDirectionClient
{
    Task<ServiceResult<LineSnapshot>> ReverseLineAsync(string lineCode, long version);
}

public interface ILinePropertiesClient
{
    Task<ServiceResult<LineSnapshot>> GetLineDetailAsync(string lineCode);
    Task<ServiceResult<LineSnapshot>> SaveLinePropertiesAsync(string lineCode, long version, LineProperties properties);
    Task<ServiceResult<LineSnapshot>> ToggleLineLockAsync(string lineCode, long version);
}

public interface IServiceResult
{
    bool Success { get; }
}

public sealed record ServiceResult<T>(bool Success, string Code, string Message, T? Data) : IServiceResult
{
    public static ServiceResult<T> Ok(T data, string message) => new(true, "Ok", message, data);
    public static ServiceResult<T> Fail(string code, string message) => new(false, code, message, default);
}

public interface ILineClient
{
    Task<ServiceResult<LineSnapshot>> CreateAsync();
    Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string lineCode);
    Task<ServiceResult<LineSnapshot>> SaveAsync(string lineCode, long version, LineNode[] nodes);
    Task<ServiceResult<LineSnapshot[]>> GetAllAsync();
    Task<ServiceResult<bool>> DeleteAsync(string lineCode, long version);
}
