using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;

namespace ArgumentMap.Tests;

// Enumerate the actual running host instead of maintaining a partial hand-written endpoint list.
// Every future HTTP endpoint is automatically included; only /Login may obtain a session without one.
internal static class LoginCodeEndpointAudit
{
    internal static string[] ProtectedRoutes(LocalLineServer server)
    {
        var app=(WebApplication)typeof(LocalLineServer).GetField("application",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(server)!;
        var endpoints=((IEndpointRouteBuilder)app).DataSources.SelectMany(s=>s.Endpoints).OfType<RouteEndpoint>().ToArray();
        Assert.IsTrue(endpoints.Length>=28,"Route discovery must include the whole server.");
        Assert.AreEqual(1,endpoints.Count(e=>e.RoutePattern.RawText=="/Login"));
        foreach(var endpoint in endpoints)
            CollectionAssert.AreEqual(new[]{"POST"},endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.ToArray(),endpoint.RoutePattern.RawText);
        return endpoints.Where(e=>e.RoutePattern.RawText!="/Login").Select(e=>e.RoutePattern.RawText!).Order(StringComparer.Ordinal).ToArray();
    }

    internal static void Seed(ILineRepository repository)
    {
        var accounts=new LoginService(repository);Assert.IsTrue(accounts.CreateUser(new("admin","password")).Success);Assert.IsTrue(accounts.CreateUser(new("other","password")).Success);
        var lines=new LineSaveService(repository);
        foreach(var xy in new[]{new[]{90,110,130,110},new[]{110,90,110,130}})
        {var road=lines.Create(new("admin")).Data!;var saved=lines.CheckShapeIsOKThenSave(TestMap.Shape(road,xy));Assert.IsTrue(saved.Success,saved.Message);}
        var crosses=new CrossService(repository);Assert.IsTrue(crosses.Recalculate().Success);var cross=crosses.GetAll().Data!.Single();
        Assert.IsTrue(crosses.Edit(new(cross.Key,"admin",cross.Version,CrossState.Disconnected)).Success);
        Assert.IsTrue(new LogicalNodeService(repository).Create(new("admin",110.5m,110.5m,6,new(){Value="登录验证样本"})).Success);
    }

    internal static JsonObject Body(ILineRepository repository,string user,string? code,bool omitCode=false)
    {
        var body=repository.Transaction(state=>
        {
            var roads=state.Lines.Values.OrderBy(r=>r.Code).ToArray();var ln=state.LogicalNodes.Values.Single();var cross=state.Crosses.Values.Single();
            var bounds=new MapAreaBounds(70,70,150,150);
            return JsonSerializer.SerializeToNode(new
            {
                owner=user,userName=user,loginCode=code,lineCode=roads[0].Code,version=roads[0].Version,nodes=roads[0].Nodes,
                code=ln.Code,x=210,y=210,sideLength=6,properties=new LineProperties(),value="不应写入",info="不应写入",logicLanguage="P",
                source=new LineCopyData(roads[0].Code,roads[0].Version,roads[0].Nodes,roads[0].Properties,roads[0].IsLocked),
                cross=cross.Key,state=CrossState.Connected,firstCode=roads[0].Code,firstVersion=roads[0].Version,secondCode=roads[1].Code,secondVersion=roads[1].Version,
                firstX=130,firstY=110,secondX=110,secondY=90,cutBeforeOrder=1,nodeOrder=0,targetX=90,targetY=110,
                bounds,selection=new MapAreaSelection(bounds,roads,[ln],[cross]),deltaX=200,deltaY=200,
                text=FlowDiagramTests.Small().ToJsonString()
            },new JsonSerializerOptions{PropertyNamingPolicy=JsonNamingPolicy.CamelCase})!.AsObject();
        });
        if(omitCode)body.Remove("loginCode");
        return body;
    }

    internal static async Task RejectEveryRoute(HttpClient http,string[] routes,JsonObject body,string label)
    {
        foreach(var route in routes)
        {
            using var response=await http.PostAsJsonAsync(route,body);
            Assert.AreEqual(HttpStatusCode.Unauthorized,response.StatusCode,$"{label}: {route} must check LoginCode before doing business work.");
            var result=await response.Content.ReadFromJsonAsync<ServiceResult<JsonElement>>();
            Assert.IsNotNull(result);Assert.IsFalse(result.Success,route);Assert.AreEqual("LoginExpired",result.Code,route);
            // Boolean operations serialize their default result as false; reference results are null.
            Assert.IsTrue(result.Data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False,$"{route} must not disclose map data.");
        }
    }

    internal static string Snapshot(ILineRepository repository)=>repository.Transaction(s=>JsonSerializer.Serialize(new
    {
        Lines=s.Lines.Values.OrderBy(l=>l.Code).ToArray(),Crosses=s.Crosses.Values.OrderBy(c=>c.Key).ToArray(),Nodes=s.LogicalNodes.Values.OrderBy(n=>n.Code).ToArray(),
        Positions=s.LogicalNodePositions.Values.OrderBy(p=>p.LogicalNodeCode).ThenBy(p=>p.RoadCode).ThenBy(p=>p.RoadOrder).ToArray(),
        Pending=s.PendingSegments.OrderBy(p=>p.LineCode).ThenBy(p=>p.Segment).Select(p=>new{p.LineCode,p.Segment}).ToArray(),Dirty=s.DirtyLines.Order().ToArray(),Stale=s.StaleCrosses.Order().ToArray(),
        Accounts=s.Accounts.Values.OrderBy(a=>a.UserName).ToArray()
    }));
}

[TestClass,TestCategory("Unit"),TestCategory("Http")]
public sealed class LoginCodeEndpointTests
{
    [TestMethod]
    [DataRow("missing")][DataRow("empty")][DataRow("null")][DataRow("wrong")][DataRow("revoked")][DataRow("other-account")][DataRow("unknown-user")]
    public async Task AllRegisteredBusinessEndpoints_RejectInvalidSessionWithoutChangingMapOrAccounts(string scenario)
    {
        var repository=new InMemoryLineRepository();LoginCodeEndpointAudit.Seed(repository);
        await using var server=await LocalLineServer.StartAsync(repository:repository);using var http=new HttpClient{BaseAddress=server.BaseAddress};var login=new HttpLoginClient(http);
        var old=(await login.LoginAsync("admin","password")).Data!;var current=(await login.LoginAsync("admin","password")).Data!;var other=(await login.LoginAsync("other","password")).Data!;
        Assert.AreNotEqual(old.LoginCode,current.LoginCode);var code=scenario switch{"empty"=>"","null"=>null,"wrong"=>new string('0',32),"revoked"=>old.LoginCode,"other-account"=>other.LoginCode,_=>current.LoginCode};
        var routes=LoginCodeEndpointAudit.ProtectedRoutes(server);var body=LoginCodeEndpointAudit.Body(repository,scenario=="unknown-user"?"absent":"admin",code,scenario=="missing");var before=LoginCodeEndpointAudit.Snapshot(repository);
        await LoginCodeEndpointAudit.RejectEveryRoute(http,routes,body,scenario);Assert.AreEqual(before,LoginCodeEndpointAudit.Snapshot(repository));
        Assert.IsTrue((await new HttpLineClient(http,current).GetAllAsync()).Success,"Rejected requests, including old Logout, must not revoke the current login.");
    }

    [TestMethod]
    public async Task LogoutInvalidatesEveryEndpoint_AndPasswordFailureDoesNotRotateCode()
    {
        var repository=new InMemoryLineRepository();LoginCodeEndpointAudit.Seed(repository);await using var server=await LocalLineServer.StartAsync(repository:repository);
        using var http=new HttpClient{BaseAddress=server.BaseAddress};var login=new HttpLoginClient(http);var current=(await login.LoginAsync("admin","password")).Data!;
        Assert.IsFalse((await login.LoginAsync("admin","wrong")).Success);Assert.IsTrue((await new HttpLineClient(http,current).GetAllAsync()).Success);
        Assert.IsTrue((await login.LogoutAsync(current)).Success);Assert.IsNull(repository.Transaction(s=>s.Accounts["admin"].LoginCode));
        var before=LoginCodeEndpointAudit.Snapshot(repository);await LoginCodeEndpointAudit.RejectEveryRoute(http,LoginCodeEndpointAudit.ProtectedRoutes(server),LoginCodeEndpointAudit.Body(repository,"admin",current.LoginCode),"logged out");
        Assert.AreEqual(before,LoginCodeEndpointAudit.Snapshot(repository));
    }
}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class LoginCodeEndpointMySqlTests
{
    [TestMethod]
    public Task TwoServersReadCurrentDatabaseCode_EveryEndpointRejectsRevokedSpoofedAndLoggedOutSessions()=>LogicalNodeMySqlTests.Isolated(async settings=>
    {
        var firstRepository=new MySqlLineRepository(settings);LoginCodeEndpointAudit.Seed(firstRepository);
        await using var first=await LocalLineServer.StartAsync(repository:firstRepository);await using var second=await LocalLineServer.StartAsync(repository:new MySqlLineRepository(settings));
        using var firstHttp=new HttpClient{BaseAddress=first.BaseAddress};using var secondHttp=new HttpClient{BaseAddress=second.BaseAddress};var firstLogin=new HttpLoginClient(firstHttp);var secondLogin=new HttpLoginClient(secondHttp);
        string? DatabaseCode()
        {using var connection=new MySqlConnection(settings.ConnectionString());connection.Open();using var query=connection.CreateCommand();query.CommandText="SELECT LoginCode FROM DictLogin WHERE UserName='admin'";var value=query.ExecuteScalar();return value is null or DBNull?null:(string)value;}
        var old=(await firstLogin.LoginAsync("admin","password")).Data!;Assert.AreEqual(old.LoginCode,DatabaseCode());
        var current=(await secondLogin.LoginAsync("admin","password")).Data!;Assert.AreNotEqual(old.LoginCode,current.LoginCode);Assert.AreEqual(current.LoginCode,DatabaseCode());
        var other=(await secondLogin.LoginAsync("other","password")).Data!;
        var routes=LoginCodeEndpointAudit.ProtectedRoutes(first);CollectionAssert.AreEqual(routes,LoginCodeEndpointAudit.ProtectedRoutes(second));
        foreach(var body in new[]{LoginCodeEndpointAudit.Body(firstRepository,"admin",null,true),LoginCodeEndpointAudit.Body(firstRepository,"admin",""),LoginCodeEndpointAudit.Body(firstRepository,"admin",null),
            LoginCodeEndpointAudit.Body(firstRepository,"admin",new string('0',32)),LoginCodeEndpointAudit.Body(firstRepository,"admin",old.LoginCode),LoginCodeEndpointAudit.Body(firstRepository,"admin",other.LoginCode),LoginCodeEndpointAudit.Body(firstRepository,"absent",current.LoginCode)})
        {
            var before=LineTopologyMySqlTests.RawSnapshot(settings);var accounts=LoginCodeEndpointAudit.Snapshot(firstRepository);
            await LoginCodeEndpointAudit.RejectEveryRoute(firstHttp,routes,body,"MySQL current code");Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(settings));Assert.AreEqual(accounts,LoginCodeEndpointAudit.Snapshot(firstRepository));
        }
        Assert.AreEqual(current.LoginCode,DatabaseCode());Assert.IsTrue((await new HttpLineClient(firstHttp,current).GetAllAsync()).Success);
        Assert.IsTrue((await secondLogin.LogoutAsync(current)).Success);Assert.IsNull(DatabaseCode());var snapshot=LineTopologyMySqlTests.RawSnapshot(settings);
        await LoginCodeEndpointAudit.RejectEveryRoute(firstHttp,routes,LoginCodeEndpointAudit.Body(firstRepository,"admin",current.LoginCode),"MySQL logout");Assert.AreEqual(snapshot,LineTopologyMySqlTests.RawSnapshot(settings));
        // Simultaneous successful logins still leave exactly one usable database code.
        var logins=await Task.WhenAll(firstLogin.LoginAsync("admin","password"),secondLogin.LoginAsync("admin","password"));Assert.IsTrue(logins.All(r=>r.Success));
        var reads=await Task.WhenAll(logins.Select(r=>new HttpLineClient(firstHttp,r.Data!).GetAllAsync()));Assert.AreEqual(1,reads.Count(r=>r.Success));
        Assert.AreEqual(1,logins.Count(r=>r.Data!.LoginCode==DatabaseCode()));
    });
}
