using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using MySqlConnector;

namespace ArgumentMap.Server;

public sealed class LocalLineServer : IAsyncDisposable
{
    private readonly WebApplication application;
    public Uri BaseAddress { get; }
    public LoginService Accounts { get; }

    private LocalLineServer(WebApplication application, LoginService accounts)
    {
        this.application = application;
        BaseAddress = new Uri(application.Urls.Single());
        Accounts = accounts;
    }

    public static async Task<LocalLineServer> StartAsync(string address = "http://127.0.0.1:0", ILineRepository? repository = null, MySqlSettings? settings = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.UseUrls(address);
        builder.Logging.ClearProviders();
        var lineRepository = repository ?? new MySqlLineRepository(settings ?? MySqlSettings.Load());
        var accounts = new LoginService(lineRepository);
        builder.Services.AddSingleton<ILineRepository>(lineRepository);
        builder.Services.AddSingleton<LineSaveService>();
        builder.Services.AddSingleton<CrossService>();
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (DuplicateLogicalNodeValueException error)
            {
                await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail("DuplicateLogicalNodeValue", error.Message));
            }
            catch(DuplicateLogicalNodeLogicException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail("DuplicateLogicalNodeLogic",error.Message));}
            catch(LogicalNodeLogicRequiredException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail("LogicalNodeLogicRequired",error.Message));}
            catch(InvalidLogicalNodeBranchException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail(error.ErrorCode,error.Message));}
            catch(InvalidLogicalNodeRelationException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail("InvalidRelationValue",error.Message));}
            catch(InvalidLogicalNodeTextException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail(LogicalNodeTextRules.ErrorCode,error.Message));}
            catch(InvalidLogicalNodeOperandException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail("InvalidOperandKind",error.Message));}
            catch(InvalidLogicalNodeInstanceException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail(error.ErrorCode,error.Message));}
            catch(InvalidLogicalNodeCoordinateException error)
            {await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail("InvalidLogicalNodeCoordinate",error.Message));}
            catch (MySqlException)
            {
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(ServiceResult<object>.Fail("DatabaseError", "服务端数据库操作失败，未确认保存成功，请联系管理员检查 Server 与 MySQL。当前编辑内容已保留。"));
            }
        });
        IResult Authorized<T>(string name, string code, Func<ILineRepository, string, ServiceResult<T>> operation, string? edit = null)
        {
            var result = accounts.WithSession(name, code, (scope, owner) => edit is null ? operation(scope, owner) :
                new MapUndoService(scope).Record(owner, edit, () => operation(scope, owner)));
            return Results.Json(result, statusCode: result.Code == "LoginExpired" ? StatusCodes.Status401Unauthorized : StatusCodes.Status200OK);
        }
        app.MapPost("/PreviewLogicalNodeConnection", async (PreviewLogicalNodeConnectionRequest request, HttpContext context) =>
        {
            var prepared=accounts.WithSession(request.Owner,request.LoginCode,(scope,name)=>new LogicalNodeConnectionService(scope).Prepare(request with{Owner=name}));
            if(!prepared.Success)return Results.Json(ServiceResult<LogicalNodeConnectionPlan>.Fail(prepared.Code,prepared.Message),statusCode:prepared.Code=="LoginExpired"?401:200);
            // Geometry search is CPU work on a snapshot, outside both the login/map lock and DB transaction.
            var plan=await Task.Run(()=>LogicalNodeConnectionService.Plan(prepared.Data!,context.RequestAborted),context.RequestAborted);
            return Results.Json(plan);
        });
        app.MapPost("/SaveLogicalNodeConnection", (SaveLogicalNodeConnectionRequest request) => Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new LogicalNodeConnectionService(scope).Save(request with{Owner=name}),edit:"连接两个 LN"));
        app.MapPost("/UndoMap", (MapUndoRequest request) => Authorized(request.UserName, request.LoginCode,
            (scope, name) => string.IsNullOrEmpty(request.RequestId) ? ServiceResult<MapUndoResult>.Fail("InvalidUndoRequest", "缺少撤销请求标识，请更新客户端。") :
                new MapUndoService(scope).Undo(name, request.RequestId)));
        // Keep the old Login transport: userName/password/DBPrefix in, bare LoginCode out.
        app.MapPost("/Login", (LoginRequest request) =>
        {
            var result = accounts.Login(request);
            return Results.Text(result.Success ? result.Data!.LoginCode : "");
        });
        app.MapPost("/Logout", (SessionRequest request) =>
        {
            var result = accounts.Logout(request);
            return Results.Json(result, statusCode: result.Code == "LoginExpired" ? StatusCodes.Status401Unauthorized : StatusCodes.Status200OK);
        });
        app.MapPost("/PasteLine",(PasteLineRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new LinePasteService(scope).Paste(request with{Owner=name}), edit: "粘贴线路"));
        app.MapPost("/PasteMapArea",(PasteMapAreaRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new MapAreaPasteService(scope).Paste(request with{Owner=name}), edit: "粘贴框选对象"));
        app.MapPost("/AddLine", (CreateLineRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).Create(request with { Owner = name }), edit: "新建线路"));
        app.MapPost("/StartToDraw", (LineAccessRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).StartDrawing(request with { Owner = name })));
        app.MapPost("/CheckShapeIsOKThenSave", (SaveLineRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).CheckShapeIsOKThenSave(request with { Owner = name }), edit: "保存线路"));
        app.MapPost("/FindNodePosition",(FindNodePositionRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new NodeEditService(scope).FindPosition(request with{Owner=name})));
        app.MapPost("/GetAllLineData", (SessionRequest request) => Authorized(request.UserName, request.LoginCode,
            (scope, _) => new LineSaveService(scope).GetAll()));
        app.MapPost("/SelectMapArea",(SelectMapAreaRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new MapAreaMoveService(scope).Select(request with{Owner=name})));
        app.MapPost("/PreviewFlowDiagram",(FlowDiagramRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new FlowDiagramService(scope).Preview(request with{Owner=name})));
        app.MapPost("/ImportFlowDiagram",(FlowDiagramRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new FlowDiagramService(scope).Import(request with{Owner=name}), edit: "导入流程图"));
        app.MapPost("/MoveMapArea",(MoveMapAreaRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new MapAreaMoveService(scope).Move(request with{Owner=name}), edit: "框选移动"));
        app.MapPost("/DeleteMapArea",(DeleteMapAreaRequest request)=>Authorized(request.Owner,request.LoginCode,
            (scope,name)=>new MapAreaMoveService(scope).Delete(request with{Owner=name}), edit: "删除框选对象"));
        app.MapPost("/RemoveLine", (DeleteLineRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).Delete(request with { Owner = name }), edit: "删除线路"));
        app.MapPost("/GetLineDetail", (LineAccessRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).GetDetail(request with { Owner = name })));
        app.MapPost("/SetLineProperty", (SaveLinePropertiesRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).SaveProperties(request with { Owner = name }), edit: "修改线路属性"));
        app.MapPost("/LockLine", (ToggleLineLockRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).ToggleLock(request with { Owner = name }), edit: "锁定或解锁线路"));
        app.MapPost("/ReverseLine", (ReverseLineRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineSaveService(scope).Reverse(request with { Owner = name }), edit: "反转线路方向"));
        app.MapPost("/SplitLine", (SplitLineRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineTopologyService(scope).Split(request with { Owner = name }), edit: "拆分线路"));
        app.MapPost("/GetMergePlans", (MergeLinesRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineTopologyService(scope).GetMergePlans(request with { Owner = name })));
        app.MapPost("/MergeLines", (ApplyMergeRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LineTopologyService(scope).Merge(request with { Owner = name }), edit: "合并线路"));
        app.MapPost("/GetAllCrossData", (SessionRequest request) => Authorized(request.UserName, request.LoginCode,
            (scope, _) => new CrossService(scope).GetAll()));
        app.MapPost("/CalculateCross", (SessionRequest request) => Authorized(request.UserName, request.LoginCode,
            (scope, _) => new CrossService(scope).Calculate()));
        app.MapPost("/EditCross", (EditCrossRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new CrossService(scope).Edit(request with { Owner = name }), edit: "修改交点通行"));
        app.MapPost("/GetLogicalNodes", (SessionRequest request) => Authorized(request.UserName, request.LoginCode,
            (scope, name) => new LogicalNodeService(scope).GetAll(name)));
        app.MapPost("/AddLogicalNode", (CreateLogicalNodeRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LogicalNodeService(scope).Create(request with { Owner = name }), edit: "新增 LN"));
        app.MapPost("/UpdateLogicalNode", (UpdateLogicalNodeRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LogicalNodeService(scope).Update(request with { Owner = name }), edit: "编辑 LN"));
        // Typed clients use explicit endpoints so an older server cannot silently drop the field.
        app.MapPost("/AddLogicalNodeWithOperandKind", (CreateLogicalNodeRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LogicalNodeService(scope).Create(request with { Owner = name }), edit: "新增 LN"));
        app.MapPost("/UpdateLogicalNodeWithOperandKind", (UpdateLogicalNodeRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LogicalNodeService(scope).Update(request with { Owner = name }), edit: "编辑 LN"));
        app.MapPost("/RemoveLogicalNode", (DeleteLogicalNodeRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LogicalNodeService(scope).Delete(request with { Owner = name }), edit: "删除 LN"));
        app.MapPost("/MoveLogicalNode", (MoveLogicalNodeRequest request) => Authorized(request.Owner, request.LoginCode,
            (scope, name) => new LogicalNodeService(scope).Move(request with { Owner = name }), edit: "移动 LN"));
        app.MapPost("/CalculateLogicalNode", (CalculateLogicalNodeRequest request) => Authorized(request.UserName, request.LoginCode,
            (scope, _) => request.Incremental ? new LogicalNodeService(scope).CalculateStep(request.AfterCode) : new LogicalNodeService(scope).Calculate()));
        try
        {
            await app.StartAsync().ConfigureAwait(false);
            return new LocalLineServer(app, accounts);
        }
        catch
        {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public Task WaitForShutdownAsync() => application.WaitForShutdownAsync();

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await application.StopAsync(timeout.Token).ConfigureAwait(false); }
        finally { await application.DisposeAsync().ConfigureAwait(false); }
    }
}
