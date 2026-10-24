using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class CrossIntegerClientTests
{
    [TestMethod,DataRow(202,204,true),DataRow(201,204,false),DataRow(202,203,false),DataRow(201,203,false)]
    public async Task HttpResponsesRejectOldHalfCrosses_InEveryMapEnvelope_WithoutRounding(int x2,int y2,bool valid)
    {
        var cross=new CrossSnapshot(CrossKey.Create("AAAAAAAAAA","BBBBBBBBBB",new(x2,y2)),0,0,1,1,CrossState.Connected,0,.5,.5);
        var map=new MapAreaSnapshot([],[],[cross]);var empty=new MapAreaSnapshot([],[],[]);
        var selection=new MapAreaSelection(new(0,0,500,500),[],[],[cross]);
        var line=new LineSnapshot("AAAAAAAAAA","owner",false,0,[]);
        var copy=new LineCopyData(line.Code,line.Version,line.Nodes,line.Properties,line.IsLocked);
        var source=new LogicalNodeConnectionAnchor("CCCCCCCCCC",10.5m,10.5m,6);
        var target=new LogicalNodeConnectionAnchor("DDDDDDDDDD",30.5m,30.5m,6);
        var plan=new LogicalNodeConnectionPlan(source,target,line.Code,[],LogicalNodeConnectionKind.GoTo,new("BBBBBBBBBB",new(x2,y2)));
        await Check(new[]{cross},c=>c.GetCrossesAsync(),valid);
        await Check(cross,c=>c.SetCrossStateAsync(cross.Key,0,CrossState.Disconnected),valid);
        await Check(selection,c=>c.SelectMapAreaAsync(selection.Bounds),valid);
        await Check(map,c=>c.MoveMapAreaAsync(selection,2,2),valid);
        await Check(map,c=>c.DeleteMapAreaAsync(selection),valid);
        await Check(new FlowDiagramResult(map,null,[],[],[]),c=>c.PreviewFlowDiagramAsync("{}",10.5m,10.5m),valid);
        await Check(new FlowDiagramResult(empty,map,[],[],[]),c=>c.ImportFlowDiagramAsync("{}",10.5m,10.5m),valid);
        await Check(new MapAreaPasteResult(map,empty,[],[],[]),c=>c.PasteMapAreaAsync(selection,2,2),valid);
        await Check(new MapAreaPasteResult(empty,map,[],[],[]),c=>c.PasteMapAreaAsync(selection,2,2),valid);
        await Check(new LinePasteResult(line,map),c=>c.PasteLineAsync(copy,2,2),valid);
        await Check(new MapUndoResult("edit",map),c=>c.UndoAsync(),valid);
        await Check(plan,c=>c.PreviewLogicalNodeConnectionAsync(source,target,LogicalNodeConnectionKind.GoTo),valid);
        await Check(new LogicalNodeConnectionResult(line,map,0),c=>c.SaveLogicalNodeConnectionAsync(plan),valid);
        Assert.AreEqual(x2,cross.Key.Position.X2);Assert.AreEqual(y2,cross.Key.Position.Y2);
    }

    private static async Task Check<T>(T data,Func<HttpLineClient,Task<ServiceResult<T>>> action,bool valid)
    {
        using var handler=new Reply<T>(data);using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var result=await action(new(http,new("owner","login")));
        Assert.AreEqual(valid,result.Success,typeof(T).Name+": "+result.Message);
        if(valid)Assert.IsNotNull(result.Data);
        else{Assert.AreEqual("InvalidResponse",result.Code);Assert.IsNull(result.Data);StringAssert.Contains(result.Message,"非整数 Cross");StringAssert.Contains(result.Message,"刷新确认");}
    }

    private sealed class Reply<T>(T data):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(ServiceResult<T>.Ok(data,""))});
    }
}
