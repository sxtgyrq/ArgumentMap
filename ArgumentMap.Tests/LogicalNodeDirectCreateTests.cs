using System.Net.Http;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeDirectCreateTests
{
    private static CoordinateMapView Map(DirectLogicalNodeHttpStub handler,HttpClient http)
    {
        var map=new CoordinateMapView();map.ConfigureClient(new HttpLineClient(http,new("owner","test")));
        Assert.IsTrue(map.BeginLogicalNodePlacement(6));return map;
    }
    [STATestMethod]
    public void PendingDirectSaveBlocksRepeatAndOtherCommands_FailureKeepsCandidateForRetry()
    {
        var handler=new DirectLogicalNodeHttpStub{Gate=new(TaskCreationOptions.RunContinuationsAsynchronously)};
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var map=Map(handler,http);
        var pending=map.PlaceLogicalNodeAsync(new Point(110.2,120.7));Assert.IsTrue(map.IsSaving);Assert.AreEqual(0,map.LogicalNodeCount);
        Assert.IsFalse(map.AddLogicalNodeAsync(new Point(110.2,120.7)).GetAwaiter().GetResult());
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(110.2,120.7)).GetAwaiter().GetResult());
        Assert.IsFalse(map.BeginLogicalNodePlacement(12));map.CancelEditing();Assert.IsTrue(map.IsLogicalNodeMode);
        Assert.AreEqual(1,handler.Creates);
        handler.Gate.SetResult(ServiceResult<LogicalNodeSnapshot>.Fail("DatabaseError","数据库已回滚，请重试"));
        LogicalNodeWindowEntryTests.DrainUntil(()=>pending.IsCompleted);Assert.IsFalse(pending.GetAwaiter().GetResult());
        Assert.IsFalse(map.IsSaving);Assert.IsTrue(map.IsPlacingLogicalNode);Assert.AreEqual(0,map.LogicalNodeCount);
        StringAssert.Contains(map.OperationMessage,"回滚");
        handler.Gate=null;Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(110.2,120.7)).GetAwaiter().GetResult());
        Assert.AreEqual(2,handler.Creates);Assert.AreEqual(1,map.LogicalNodeCount);Assert.IsTrue(map.IsPlacingLogicalNode);
    }
    [STATestMethod,DataRow("node"),DataRow("diagonal"),DataRow("cross"),DataRow("start")]
    public void ServerValidatesLatestGeometryAndStartState_EvenWhenClientCacheIsEmpty(string kind)
    {
        var handler=new DirectLogicalNodeHttpStub();
        if(kind=="node")LogicalNodeMySqlTests.Add(handler.Repository,110,110,130,110);
        if(kind=="diagonal")LogicalNodeMySqlTests.Add(handler.Repository,90,90,130,130);
        if(kind=="cross")
        {LogicalNodeMySqlTests.Add(handler.Repository,90,110,130,110);LogicalNodeMySqlTests.Add(handler.Repository,110,90,110,130);}
        if(kind=="start")Assert.IsTrue(new LogicalNodeService(handler.Repository).Create(new("other",300.5m,300.5m,18,new())).Success);
        var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var map=Map(handler,http);
        if(kind=="start")Assert.IsTrue(map.BeginLogicalNodePlacement(18));
        var windows=0;map.LogicalNodeWindowRequested+=(_,_)=>windows++;
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(110.2,110.7)).GetAwaiter().GetResult());
        Assert.AreEqual(1,handler.Creates);Assert.AreEqual(0,map.LogicalNodeCount);Assert.AreEqual(0,windows);
        Assert.IsTrue(map.IsPlacingLogicalNode);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
        Assert.IsFalse(string.IsNullOrWhiteSpace(map.OperationMessage));Assert.IsFalse(map.IsSaving);
    }
    [STATestMethod]
    public void FailedConnectionShowsReasonWithoutOpeningWindowOrInventingNode()
    {
        var handler=new DirectLogicalNodeHttpStub{Failure=ServiceResult<LogicalNodeSnapshot>.Fail("ConnectionFailure","无法连接服务器")};
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var map=Map(handler,http);
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(110,110)).GetAwaiter().GetResult());
        Assert.AreEqual(0,map.LogicalNodeCount);Assert.IsTrue(map.IsPlacingLogicalNode);Assert.IsFalse(map.IsSaving);
        StringAssert.Contains(map.OperationMessage,"无法连接");
    }
    [STATestMethod]
    public void SuccessfulDirectSaveImmediatelyShowsEveryReturnedPosition()
    {
        var handler=new DirectLogicalNodeHttpStub();LogicalNodeMySqlTests.Add(handler.Repository,90,108,130,108,130,112,90,112);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var map=Map(handler,http);
        Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(110.3,110.9)).GetAwaiter().GetResult());
        var node=new LogicalNodeService(handler.Repository).GetAll().Data!.Single();Assert.AreEqual(2,node.Positions.Length);
        var editor=(LogicalNodeEditor)typeof(CoordinateMapView).GetField("logicalNodes",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.GetValue(map)!;
        CollectionAssert.AreEqual(node.Positions,editor.Nodes.Single().Positions);Assert.AreEqual("逻辑节点",node.Properties.Value);Assert.AreEqual("",node.Properties.Info);
    }
    [STATestMethod]
    public void ToolbarAddUsesLastHoveredMapLocationAfterPointerLeavesMap()
    {
        var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};var map=Map(handler,http);
        map.Measure(new Size(800,600));map.Arrange(new Rect(0,0,800,600));
        var flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
        map.UpdateLayout();
        var viewport=(CoordinateViewport)typeof(CoordinateMapView).GetField("viewport",flags)!.GetValue(map)!;
        LogicalNodeWindowEntryTests.DrainUntil(()=>viewport.Size.Width>0);
        var screen=(Point)typeof(CoordinateMapView).GetMethod("ToScreen",flags)!.Invoke(map,[new Point(250010.2,250020.7)])!;
        var pointer=typeof(CoordinateMapView).GetField("lastPointer",flags)!;var refresh=typeof(CoordinateMapView).GetMethod("Refresh",flags)!;
        pointer.SetValue(map,screen);refresh.Invoke(map,null);Assert.IsNotNull(map.PointerPosition);
        pointer.SetValue(map,null);refresh.Invoke(map,null);Assert.IsNull(map.PointerPosition);
        Assert.IsTrue(map.AddLogicalNodeAsync().GetAwaiter().GetResult());Assert.AreEqual(250010.5m,handler.LastCreate!.X);Assert.AreEqual(250020.5m,handler.LastCreate.Y);
    }
}
