using System.Net.Http;
using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ArgumentMap.Business;
using ArgumentMap.Client;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeContinuousPlacementTests
{
    [STATestMethod,DataRow(4),DataRow(6),DataRow(12)]
    public void RepeatedAAndClicksKeepChosenSize_AfterSuccessAndValidationFailure(int size)
    {
        var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var map=new CoordinateMapView();map.ConfigureClient(new HttpLineClient(http,new("owner","test")));Assert.IsTrue(map.BeginLogicalNodePlacement(size));
        var pointer=typeof(CoordinateMapView).GetField("lastLogicalNodePointer",BindingFlags.Instance|BindingFlags.NonPublic)!;
        pointer.SetValue(map,new Point(100.2,110.7));Assert.IsTrue(map.HandleEditorKeyAsync(Key.A).GetAwaiter().GetResult());
        Assert.IsTrue(map.IsPlacingLogicalNode);Assert.AreEqual(1,map.LogicalNodeCount);
        // Same position is illegal; the failure must not end the continuous mode.
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(100.2,110.7)).GetAwaiter().GetResult());Assert.IsTrue(map.IsPlacingLogicalNode);Assert.AreEqual(1,handler.Creates);
        pointer.SetValue(map,new Point(200.2,210.7));map.HandleEditorKeyAsync(Key.A).GetAwaiter().GetResult();Assert.IsTrue(map.IsPlacingLogicalNode);
        Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(300.2,310.7)).GetAwaiter().GetResult());Assert.IsTrue(map.IsPlacingLogicalNode);
        var nodes=new LogicalNodeService(handler.Repository).GetAll().Data!;Assert.AreEqual(3,nodes.Length);Assert.IsTrue(nodes.All(n=>n.SideLength==size));
        if(size==12)CollectionAssert.AreEquivalent(new[]{"逻辑节点","逻辑节点1","逻辑节点2"},nodes.Select(n=>n.Properties.Value).ToArray());
        var next=size==6?12:6;Assert.IsTrue(map.BeginLogicalNodePlacement(next));Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(400.2,410.7)).GetAwaiter().GetResult());Assert.AreEqual(next,handler.LastCreate!.SideLength);
        map.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsFalse(map.IsPlacingLogicalNode);Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(500,500)).GetAwaiter().GetResult());Assert.AreEqual(4,handler.Creates);
    }
    [STATestMethod]
    public void ServerOnlyDefaultValueConflict_IsNumberedOnFirstAttemptAndKeepsPlacement()
    {
        using var handler=new DirectLogicalNodeHttpStub();
        LogicalNodeUniqueValueTests.Add(handler.Repository,"逻辑节点",300.5m);
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var map=new CoordinateMapView();map.ConfigureClient(new HttpLineClient(http,new("owner","test")));
        Assert.IsTrue(map.BeginLogicalNodePlacement(12));
        Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(100,100)).GetAwaiter().GetResult());
        Assert.IsTrue(map.IsPlacingLogicalNode);Assert.AreEqual("逻辑节点",handler.LastCreate!.Properties.Value);
        CollectionAssert.AreEquivalent(new[]{"逻辑节点","逻辑节点1"},new LogicalNodeService(handler.Repository).GetAll().Data!.Select(n=>n.Properties.Value).ToArray());
    }
    [STATestMethod]
    public void ContinuousStartStillRejectsSecondStart_AndCanSwitchToOrdinarySquare()
    {
        var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var map=new CoordinateMapView();map.ConfigureClient(new HttpLineClient(http,new("owner","test")));Assert.IsTrue(map.BeginLogicalNodePlacement(18));
        Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(100,100)).GetAwaiter().GetResult());Assert.IsTrue(map.IsPlacingLogicalNode);
        Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(200,200)).GetAwaiter().GetResult());Assert.AreEqual(1,handler.Creates);Assert.AreEqual(18,map.LogicalNodePlacementSize);
        Assert.IsTrue(map.BeginLogicalNodePlacement(6));Assert.IsTrue(map.PlaceLogicalNodeAsync(new Point(200,200)).GetAwaiter().GetResult());Assert.IsTrue(map.IsPlacingLogicalNode);
        Assert.AreEqual(1,new LogicalNodeService(handler.Repository).GetAll().Data!.Count(n=>n.IsStart));
        map.ToggleLogicalNodeSelection();Assert.IsFalse(map.IsPlacingLogicalNode);Assert.IsFalse(map.PlaceLogicalNodeAsync(new Point(300,300)).GetAwaiter().GetResult());
    }
}
