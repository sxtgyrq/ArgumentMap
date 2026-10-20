using ArgumentMap.Contracts;

namespace WpfArgumentMap;

// Allows the XAML designer to construct the view without starting a network service.
internal sealed class UnavailableLineClient : ILineClient
{
    private static Task<ServiceResult<T>> Unavailable<T>() => Task.FromResult(ServiceResult<T>.Fail("NotConnected", "线路服务尚未连接。"));
    public Task<ServiceResult<LineSnapshot>> CreateAsync() => Unavailable<LineSnapshot>();
    public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string lineCode) => Unavailable<LineSnapshot>();
    public Task<ServiceResult<LineSnapshot>> SaveAsync(string lineCode, long version, LineNode[] nodes) => Unavailable<LineSnapshot>();
    public Task<ServiceResult<LineSnapshot[]>> GetAllAsync() => Unavailable<LineSnapshot[]>();
    public Task<ServiceResult<bool>> DeleteAsync(string lineCode, long version) => Unavailable<bool>();
}
