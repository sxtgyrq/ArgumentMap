using System.Net.Http.Json;
using System.Text.Json;
using ArgumentMap.Contracts;

namespace ArgumentMap.Client;

public sealed class HttpLineClient(HttpClient client, LoginSession session) : ILineClient, ICrossClient, ILinePropertiesClient, ILineDirectionClient, ILogicalNodeClient, ILogicalNodeStepClient, ILogicalNodeMoveClient, ILogicalNodeLogicClient, ILineTopologyClient, ILineNodeEditClient, IMapAreaMoveClient, IFlowDiagramClient, ILinePasteClient, IMapAreaPasteClient, IMapUndoClient, ILogicalNodeOperandClient
{
    private string? pendingUndoRequest;
    public async Task<ServiceResult<MapUndoResult>> UndoAsync()
    {
        var candidate = Guid.NewGuid().ToString("N");
        var id = Interlocked.CompareExchange(ref pendingUndoRequest, candidate, null) ?? candidate;
        var result = await Post<MapUndoRequest, MapUndoResult>("UndoMap", new(Session.UserName, Session.LoginCode, id)).ConfigureAwait(false);
        // A lost response may follow a committed transaction. Retrying that same
        // request must refresh its result rather than undo the preceding operation.
        if (result.Code is not ("ConnectionFailure" or "InvalidResponse" or "HttpError" or "DatabaseError" or "LoginExpired")) Interlocked.CompareExchange(ref pendingUndoRequest, null, id);
        return result;
    }
    private readonly SemaphoreSlim requestGate=new(1,1);
    private int foregroundRequests;
    public Task<ServiceResult<MapAreaPasteResult>> PasteMapAreaAsync(MapAreaSelection selection,int deltaX,int deltaY)=>Post<PasteMapAreaRequest,MapAreaPasteResult>("PasteMapArea",new(Session.UserName,selection,deltaX,deltaY,Session.LoginCode));
    public Task<ServiceResult<LinePasteResult>> PasteLineAsync(LineCopyData source,int x,int y)=>Post<PasteLineRequest,LinePasteResult>("PasteLine",new(Session.UserName,source,x,y,Session.LoginCode));
    public LoginSession Session { get; private set; } = session;
    public Task<ServiceResult<FlowDiagramResult>> PreviewFlowDiagramAsync(string text,decimal x,decimal y)=>Post<FlowDiagramRequest,FlowDiagramResult>("PreviewFlowDiagram",new(Session.UserName,text,x,y,Session.LoginCode));
    public Task<ServiceResult<FlowDiagramResult>> ImportFlowDiagramAsync(string text,decimal x,decimal y)=>Post<FlowDiagramRequest,FlowDiagramResult>("ImportFlowDiagram",new(Session.UserName,text,x,y,Session.LoginCode));
    public void UpdateSession(LoginSession value)
    {
        if (value.UserName != Session.UserName) throw new ArgumentException("重新登录须使用当前账号。");
        Session = value;
    }
    public Task<ServiceResult<MapAreaSelection>> SelectMapAreaAsync(MapAreaBounds bounds)=>
        Post<SelectMapAreaRequest,MapAreaSelection>("SelectMapArea",new(Session.UserName,bounds,Session.LoginCode));
    public Task<ServiceResult<MapAreaSnapshot>> MoveMapAreaAsync(MapAreaSelection selection,int deltaX,int deltaY)=>
        Post<MoveMapAreaRequest,MapAreaSnapshot>("MoveMapArea",new(Session.UserName,selection,deltaX,deltaY,Session.LoginCode));
    public Task<ServiceResult<MapAreaSnapshot>> DeleteMapAreaAsync(MapAreaSelection selection)=>
        Post<DeleteMapAreaRequest,MapAreaSnapshot>("DeleteMapArea",new(Session.UserName,selection,Session.LoginCode));
    public Task<ServiceResult<LineSnapshot>> CreateAsync() => Post<CreateLineRequest, LineSnapshot>("AddLine", new(Session.UserName, Session.LoginCode));
    public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string lineCode) => Post<LineAccessRequest, LineSnapshot>("StartToDraw", new(lineCode, Session.UserName, Session.LoginCode));
    public Task<ServiceResult<LineSnapshot>> SaveAsync(string lineCode, long version, LineNode[] nodes) =>
        Post<SaveLineRequest, LineSnapshot>("CheckShapeIsOKThenSave", new(lineCode, Session.UserName, version, nodes, Session.LoginCode));
    public Task<ServiceResult<NodePositionProposal>> FindNodePositionAsync(string lineCode,long version,LineNode[] nodes,int nodeOrder,double targetX,double targetY)=>
        Post<FindNodePositionRequest,NodePositionProposal>("FindNodePosition",new(lineCode,Session.UserName,version,nodes,nodeOrder,targetX,targetY,Session.LoginCode));
    public Task<ServiceResult<bool>> DeleteAsync(string lineCode, long version) => Post<DeleteLineRequest, bool>("RemoveLine", new(lineCode, Session.UserName, version, Session.LoginCode));
    public Task<ServiceResult<LineSnapshot>> GetLineDetailAsync(string lineCode) => Post<LineAccessRequest, LineSnapshot>("GetLineDetail", new(lineCode, Session.UserName, Session.LoginCode));
    public Task<ServiceResult<LineSnapshot>> SaveLinePropertiesAsync(string lineCode, long version, LineProperties properties) =>
        Post<SaveLinePropertiesRequest, LineSnapshot>("SetLineProperty", new(lineCode, Session.UserName, version, properties, Session.LoginCode));
    public Task<ServiceResult<LineSnapshot>> ToggleLineLockAsync(string lineCode, long version) =>
        Post<ToggleLineLockRequest, LineSnapshot>("LockLine", new(lineCode, Session.UserName, version, Session.LoginCode));
    public Task<ServiceResult<LineSnapshot>> ReverseLineAsync(string lineCode, long version) =>
        Post<ReverseLineRequest, LineSnapshot>("ReverseLine", new(lineCode, Session.UserName, version, Session.LoginCode));
    public Task<ServiceResult<LineTopologyResult>> SplitLineAsync(string lineCode, long version, int cutBeforeOrder) =>
        Post<SplitLineRequest, LineTopologyResult>("SplitLine", new(lineCode, Session.UserName, version, cutBeforeOrder, Session.LoginCode));
    public Task<ServiceResult<MergePreview>> GetMergePlansAsync(string firstCode, long firstVersion, string secondCode, long secondVersion) =>
        Post<MergeLinesRequest, MergePreview>("GetMergePlans", new(firstCode, firstVersion, secondCode, secondVersion, Session.UserName, Session.LoginCode));
    public Task<ServiceResult<LineTopologyResult>> ApplyMergePlanAsync(MergePreview preview,MergePlan plan) =>
        Post<ApplyMergeRequest, LineTopologyResult>("MergeLines", new(preview.First.Code,preview.First.Version,preview.Second.Code,preview.Second.Version,Session.UserName,
            plan.FirstX,plan.FirstY,plan.SecondX,plan.SecondY,Session.LoginCode));
    public Task<ServiceResult<LineSnapshot[]>> GetAllAsync() => Post<SessionRequest, LineSnapshot[]>("GetAllLineData", new(Session.UserName, Session.LoginCode));
    public Task<ServiceResult<CrossSnapshot[]>> GetCrossesAsync() => Post<SessionRequest, CrossSnapshot[]>("GetAllCrossData", new(Session.UserName, Session.LoginCode));
    public Task<ServiceResult<CrossCalculationProgress>> CalculateCrossAsync() => Post<SessionRequest, CrossCalculationProgress>("CalculateCross", new(Session.UserName, Session.LoginCode), background:true);
    public Task<ServiceResult<CrossSnapshot>> SetCrossStateAsync(CrossKey key, long version, CrossState state) =>
        Post<EditCrossRequest, CrossSnapshot>("EditCross", new(key, Session.UserName, version, state, Session.LoginCode));
    public Task<ServiceResult<LogicalNodeSnapshot[]>> GetLogicalNodesAsync() => Post<SessionRequest, LogicalNodeSnapshot[]>("GetLogicalNodes", new(Session.UserName, Session.LoginCode));
    public Task<ServiceResult<LogicalNodeSnapshot>> CreateLogicalNodeAsync(decimal x, decimal y, int sideLength, LogicalNodeProperties properties) =>
        Post<CreateLogicalNodeRequest, LogicalNodeSnapshot>(sideLength==6&&properties.OperandKind!=LogicalNodeOperandRules.Custom?"AddLogicalNodeWithOperandKind":"AddLogicalNode", new(Session.UserName, x, y, sideLength, properties, Session.LoginCode));
    public Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code, long version, string value, string info) =>
        Post<UpdateLogicalNodeRequest, LogicalNodeSnapshot>("UpdateLogicalNode", new(code, Session.UserName, version, value, info, Session.LoginCode));
    public Task<ServiceResult<bool>> DeleteLogicalNodeAsync(string code, long version) =>
        Post<DeleteLogicalNodeRequest, bool>("RemoveLogicalNode", new(code, Session.UserName, version, Session.LoginCode));
    public Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code, long version, string value, string info, string logicLanguage) =>
        Post<UpdateLogicalNodeRequest, LogicalNodeSnapshot>("UpdateLogicalNode", new(code, Session.UserName, version, value, info, Session.LoginCode, logicLanguage));
    public Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code, long version, string value, string info, string logicLanguage, string operandKind) =>
        Post<UpdateLogicalNodeRequest, LogicalNodeSnapshot>("UpdateLogicalNodeWithOperandKind", new(code, Session.UserName, version, value, info, Session.LoginCode, logicLanguage, operandKind));
    public Task<ServiceResult<LogicalNodeSnapshot>> MoveLogicalNodeAsync(string code, long version, decimal x, decimal y) =>
        Post<MoveLogicalNodeRequest, LogicalNodeSnapshot>("MoveLogicalNode", new(code, Session.UserName, version, x, y, Session.LoginCode));
    public Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeAsync() =>
        Post<SessionRequest, LogicalNodeCalculationProgress>("CalculateLogicalNode", new(Session.UserName, Session.LoginCode));
    public Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeStepAsync(string? afterCode) =>
        Post<CalculateLogicalNodeRequest, LogicalNodeCalculationProgress>("CalculateLogicalNode", new(Session.UserName, Session.LoginCode, true, afterCode), background:true);

    private Task<ServiceResult<T>> Post<TRequest, T>(string path, TRequest request,bool background=false) => Schedule<T>(() => client.PostAsJsonAsync(path, request),background);

    private async Task<ServiceResult<T>> Schedule<T>(Func<Task<HttpResponseMessage>> send,bool background)
    {
        if(!background)Interlocked.Increment(ref foregroundRequests);
        try
        {
            while(true)
            {
                await requestGate.WaitAsync().ConfigureAwait(false);
                // A K/J request already running may finish, but its next unit must
                // yield to queued editing/reading requests from this client.
                if(!background||Volatile.Read(ref foregroundRequests)==0)break;
                requestGate.Release();
                await Task.Delay(10).ConfigureAwait(false);
            }
            try{return await Send<T>(send).ConfigureAwait(false);}
            finally{requestGate.Release();}
        }
        finally{if(!background)Interlocked.Decrement(ref foregroundRequests);}
    }

    private static async Task<ServiceResult<T>> Send<T>(Func<Task<HttpResponseMessage>> send)
    {
        try
        {
            using var response = await send().ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    // Error middleware returns no data; a boolean operation must not try to deserialize null as bool.
                    var error = await response.Content.ReadFromJsonAsync<ServiceResult<JsonElement>>().ConfigureAwait(false);
                    if (error is { Success: false } && !string.IsNullOrWhiteSpace(error.Message)) return ServiceResult<T>.Fail(error.Code, error.Message);
                }
                return ServiceResult<T>.Fail("HttpError", $"服务端拒绝提交 ({(int)response.StatusCode})，请检查节点格式和整数坐标。");
            }
            return await response.Content.ReadFromJsonAsync<ServiceResult<T>>().ConfigureAwait(false)
                ?? ServiceResult<T>.Fail("InvalidResponse", "服务端没有返回保存结果。");
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return ServiceResult<T>.Fail("ConnectionFailure", "无法完成服务请求，请检查服务后重试。当前绘制内容已保留。");
        }
    }
}
