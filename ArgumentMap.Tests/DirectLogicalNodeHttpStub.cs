using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Tests;

internal sealed class DirectLogicalNodeHttpStub : HttpMessageHandler
{
    internal readonly InMemoryLineRepository Repository=new();
    internal int Creates;
    internal CreateLogicalNodeRequest? LastCreate;
    internal ServiceResult<LogicalNodeSnapshot>? Failure;
    internal TaskCompletionSource<ServiceResult<LogicalNodeSnapshot>>? Gate;
    internal TaskCompletionSource<ServiceResult<MapAreaSnapshot>>? AreaMoveGate;
    internal int AreaMoves;
    internal int AreaSelections;
    internal MapAreaBounds? LastAreaBounds;
    internal TaskCompletionSource<ServiceResult<MapAreaSnapshot>>? AreaDeleteGate;
    internal int AreaDeletes;
    internal int AreaPastes;
    internal TaskCompletionSource<ServiceResult<MapAreaPasteResult>>? AreaPasteGate;
    internal int LogicalNodeCalculations,CrossCalculations;
    internal int FlowPreviews,FlowImports;
    internal TaskCompletionSource<ServiceResult<FlowDiagramResult>>? FlowGate;
    internal int Undos;
    internal TaskCompletionSource<ServiceResult<MapUndoResult>>? UndoGate;
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
    {
        var service=new LogicalNodeService(Repository);
        if(request.RequestUri!.AbsolutePath=="/UndoMap")
        {
            Undos++; var session=(await request.Content!.ReadFromJsonAsync<SessionRequest>(cancellationToken:token))!;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(UndoGate is {} gate?await gate.Task.WaitAsync(token):new MapUndoService(Repository).Undo(session.UserName))};
        }
        if(request.RequestUri!.AbsolutePath=="/PasteMapArea")
        {
            AreaPastes++;var paste=(await request.Content!.ReadFromJsonAsync<PasteMapAreaRequest>(cancellationToken:token))!;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(AreaPasteGate is {} gate?await gate.Task.WaitAsync(token):new MapAreaPasteService(Repository).Paste(paste))};
        }
        if(request.RequestUri!.AbsolutePath is "/PreviewFlowDiagram" or "/ImportFlowDiagram")
        {
            var flow=(await request.Content!.ReadFromJsonAsync<FlowDiagramRequest>(cancellationToken:token))!;
            var preview=request.RequestUri.AbsolutePath=="/PreviewFlowDiagram";if(preview)FlowPreviews++;else FlowImports++;
            var result=FlowGate is {} gate?await gate.Task.WaitAsync(token):preview?new FlowDiagramService(Repository).Preview(flow):new FlowDiagramService(Repository).Import(flow);
            return new(HttpStatusCode.OK){Content=JsonContent.Create(result)};
        }
        if(request.RequestUri!.AbsolutePath=="/CalculateLogicalNode")
        {LogicalNodeCalculations++;return new(HttpStatusCode.OK){Content=JsonContent.Create(service.Calculate())};}
        if(request.RequestUri.AbsolutePath=="/CalculateCross")
        {CrossCalculations++;return new(HttpStatusCode.OK){Content=JsonContent.Create(new CrossService(Repository).Calculate())};}
        if(request.RequestUri!.AbsolutePath=="/SelectMapArea")
        {
            var area=(await request.Content!.ReadFromJsonAsync<SelectMapAreaRequest>(cancellationToken:token))!;
            AreaSelections++;LastAreaBounds=area.Bounds;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new MapAreaMoveService(Repository).Select(area))};
        }
        if(request.RequestUri.AbsolutePath=="/DeleteMapArea")
        {
            AreaDeletes++;var area=(await request.Content!.ReadFromJsonAsync<DeleteMapAreaRequest>(cancellationToken:token))!;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(AreaDeleteGate is {} gate?await gate.Task.WaitAsync(token):new MapAreaMoveService(Repository).Delete(area))};
        }
        if(request.RequestUri.AbsolutePath=="/MoveMapArea")
        {
            AreaMoves++;var area=(await request.Content!.ReadFromJsonAsync<MoveMapAreaRequest>(cancellationToken:token))!;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(AreaMoveGate is {} gate?await gate.Task.WaitAsync(token):new MapAreaMoveService(Repository).Move(area))};
        }
        if(request.RequestUri!.AbsolutePath=="/GetAllLineData")return new(HttpStatusCode.OK){Content=JsonContent.Create(new LineSaveService(Repository).GetAll())};
        if(request.RequestUri.AbsolutePath=="/RemoveLine")
        {
            var deletion=(await request.Content!.ReadFromJsonAsync<DeleteLineRequest>(cancellationToken:token))!;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(new LineSaveService(Repository).Delete(deletion))};
        }
        if(request.RequestUri.AbsolutePath=="/GetAllCrossData")return new(HttpStatusCode.OK){Content=JsonContent.Create(new CrossService(Repository).GetAll())};
        if(request.RequestUri!.AbsolutePath is "/AddLogicalNode" or "/AddLogicalNodeWithOperandKind")
        {
            Creates++;LastCreate=(await request.Content!.ReadFromJsonAsync<CreateLogicalNodeRequest>(cancellationToken:token))!;
            var result=Gate is {} gate ? await gate.Task.WaitAsync(token) : Failure ?? service.Create(LastCreate);
            return new(HttpStatusCode.OK){Content=JsonContent.Create(result)};
        }
        if(request.RequestUri.AbsolutePath=="/GetLogicalNodes")return new(HttpStatusCode.OK){Content=JsonContent.Create(service.GetAll("owner"))};
        if(request.RequestUri.AbsolutePath is "/UpdateLogicalNode" or "/UpdateLogicalNodeWithOperandKind")
        {
            var update=(await request.Content!.ReadFromJsonAsync<UpdateLogicalNodeRequest>(cancellationToken:token))!;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(service.Update(update))};
        }
        if(request.RequestUri.AbsolutePath=="/MoveLogicalNode")
        {
            var move=(await request.Content!.ReadFromJsonAsync<MoveLogicalNodeRequest>(cancellationToken:token))!;
            return new(HttpStatusCode.OK){Content=JsonContent.Create(service.Move(move))};
        }
        throw new InvalidOperationException("Unexpected request: "+request.RequestUri.AbsolutePath);
    }
}
