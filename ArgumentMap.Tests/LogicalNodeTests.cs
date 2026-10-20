using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LogicalNodeTests
{
    internal static ServiceResult<LogicalNodeSnapshot> Create(TestMap map, LineSnapshot road, decimal x=110.5m, decimal y=110.5m,int size=6) =>
        new LogicalNodeService(map.Repository).Create(new(road.Owner,x,y,size,new(){Value="逻辑节点测试",Info="中文说明"}));
    internal static void Calculate(LogicalNodeService service)
    {
        for(var i=0;i<100;i++){var step=service.Calculate();Assert.IsTrue(step.Success,step.Message);if(!step.Data!.CanContinue)return;}
        Assert.Fail("J did not finish.");
    }

    [TestMethod, DataRow(4), DataRow(6), DataRow(12), DataRow(18)]
    public void HalfIntegerCenter_ProducesHalfIntegerCornersWithinBounds(int size)
    {
        var radius=size/2m;
        foreach(var center in new[]{radius+0.5m,250000.5m,500000-radius-0.5m})
        {
            Assert.IsNull(LogicalNodeRules.ValidateShape(center,center,size));
            var node=new LogicalNodeSnapshot("ABCDEFGHIJ","owner",0,center,center,size,new());
            foreach(var edge in new[]{node.Left,node.Right,node.Bottom,node.Top})
            {Assert.AreEqual(0.5m,edge%1);Assert.IsTrue(edge>=0 && edge<=500000);}
            Assert.IsNotNull(LogicalNodeRules.ValidateShape(radius-0.5m,center,size));
            Assert.IsNotNull(LogicalNodeRules.ValidateShape(center,500000-radius+0.5m,size));
        }
    }

    [TestMethod, DataRow("110"),DataRow("110.1"),DataRow("110.25"),DataRow("110.55"),DataRow("-0.5")]
    public void NonHalfIntegerCenters_RejectWithoutMutation(string coordinate)
    {
        var map=new TestMap();var road=map.Add(90,110,130,110);var before=map.Snapshot();
        var x=decimal.Parse(coordinate,System.Globalization.CultureInfo.InvariantCulture);
        Assert.IsFalse(Create(map,road,x).Success);Assert.IsFalse(Create(map,road,110.5m,x).Success);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow(0),DataRow(5),DataRow(9),DataRow(13)]
    public void UnsupportedSizes_RejectBeforeSave(int size)
    {var map=new TestMap();var road=map.Add(90,110,130,110);Assert.AreEqual("InvalidLogicalNodeSize",Create(map,road,size:size).Code);Assert.AreEqual(0,new LogicalNodeService(map.Repository).GetAll().Data!.Length);}

    [TestMethod,DataRow(0),DataRow(1),DataRow(2)]
    public void EveryPolylineVertex_IsCheckedIncludingEndpoints(int order)
    {
        var map=new TestMap();var points=order switch{0=>new[]{110,110,120,110,130,110},1=>new[]{90,110,110,110,130,110},_=>new[]{90,110,100,110,110,110}};
        var road=map.Add(points);var before=map.Snapshot();Assert.AreEqual("LineNodeInsideLogicalNode",Create(map,road).Code);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod,DataRow(1,0),DataRow(0,1),DataRow(-1,0),DataRow(0,-1)]
    public void LinesMayPassThroughSquare_WithoutVerticesInside(int dx,int dy)
    {
        var map=new TestMap();var road=map.Add(110-20*dx,110-20*dy,110+20*dx,110+20*dy);
        var result=Create(map,road,size:12);Assert.IsTrue(result.Success,result.Message);Assert.IsTrue(LineCode.IsValid(result.Data!.Code));
        var service=new LogicalNodeService(map.Repository);Calculate(service);var node=service.GetAll().Data!.Single();Assert.IsFalse(node.Positions.Single().IsChanged);Assert.AreEqual(road.Version,node.Positions.Single().RoadVersion);Assert.AreEqual(0,node.Positions.Single().RoadOrder);
    }

    [TestMethod,DataRow(CrossState.Unset),DataRow(CrossState.Connected),DataRow(CrossState.Road1ToRoad2),DataRow(CrossState.Road2ToRoad1),DataRow(CrossState.Disconnected)]
    public void ExistingCross_RequiresExplicitDisconnectedState(CrossState state)
    {
        var map=new TestMap();var a=map.Add(90,110,130,110);var b=map.Add(110,90,110,130);map.Calculate();var cross=map.Between(a,b);
        Assert.IsTrue(map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,state)).Success);var before=map.Snapshot();var result=Create(map,a);
        Assert.AreEqual(state==CrossState.Disconnected,result.Success,result.Message);if(!result.Success)Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void MissingCross_BlocksCreationEvenBeforeK()
    {
        var map=new TestMap();var a=map.Add(90,110,130,110);var b=map.Add(110,90,110,130);var before=map.Snapshot();
        Assert.AreEqual("LogicalNodeCrossNotDisconnected",Create(map,a,110.5m,110.5m).Code);Assert.AreEqual(before,map.Snapshot());
        map.Calculate();var cross=map.Between(a,b);Assert.AreEqual(new CrossPosition(220,220),cross.Key.Position);
        Assert.AreEqual("LogicalNodeCrossNotDisconnected",Create(map,a,110.5m,110.5m).Code);
        Assert.IsTrue(map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,CrossState.Disconnected)).Success);Assert.IsTrue(Create(map,a).Success);
    }

    [TestMethod]
    public void F6RejectsInsertedVertexAndCrossEditCannotEnableTransferInsideSquare()
    {
        var map=new TestMap();var a=map.Add(90,110,130,110);Assert.IsTrue(Create(map,a).Success);var before=map.Snapshot();
        Assert.AreEqual("LineNodeInsideLogicalNode",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(a,90,110,110,110,130,110)).Code);Assert.AreEqual(before,map.Snapshot());
        var b=map.Add(110,90,110,130);map.Calculate();var cross=map.Between(a,b);Assert.AreEqual(CrossState.Disconnected,cross.State);
        foreach(var state in new[]{CrossState.Unset,CrossState.Connected,CrossState.Road1ToRoad2,CrossState.Road2ToRoad1})
        {before=map.Snapshot();Assert.AreEqual("LogicalNodeCrossNotDisconnected",map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,state)).Code);Assert.AreEqual(before,map.Snapshot());}
        Assert.IsTrue(map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,CrossState.Disconnected)).Success);
        var third=map.Create();Assert.AreEqual("LinePassesCross",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(third,100,100,120,120)).Code,"LN does not bypass third-road restriction.");
    }

    [TestMethod]
    public void NewCrosses_AutoDisconnectOnlyInsideSquares_AndStaleCrossesDoNotBlockNewSquare()
    {
        var map=new TestMap();var a=map.Add(90,110,130,110);Assert.IsTrue(Create(map,a).Success);
        var b=map.Add(110,90,110,130);var c=map.Add(125,90,125,130);map.Calculate();
        Assert.AreEqual(CrossState.Disconnected,map.Between(a,b).State);Assert.AreEqual(CrossState.Unset,map.Between(a,c).State);
        a=map.Save(a,90,120,130,120);
        var service=new LogicalNodeService(map.Repository);var old=service.GetAll().Data!.Single();
        Assert.IsTrue(service.Delete(new(old.Code,old.Owner,old.Version)).Success);
        Assert.IsTrue(Create(map,a,110.5m,110.5m).Success,"After deleting the previous LN, a ghost Cross at the old coordinate is ignored when current geometry no longer meets.");
    }

    [TestMethod]
    public void KRejectsLegacyInvalidGeometryAndPreservesPendingWork()
    {
        var map=new TestMap();var a=map.Add(90,110,130,110);var node=Create(map,a).Data!;
        map.Repository.Transaction(s=>{s.Lines[a.Code]=a with{Nodes=TestMap.Shape(a,90,110,110,110,130,110).Nodes};return true;});var before=map.Snapshot();
        Assert.AreEqual("LineNodeInsideLogicalNode",map.Crosses.Calculate().Code);Assert.AreEqual(before,map.Snapshot());Assert.AreEqual("LineNodeInsideLogicalNode",map.Crosses.Recalculate().Code);Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    public void RoadEditsAndReversal_UpdatePositionsImmediately_AndDeletingRoadKeepsLn()
    {
        var map=new TestMap();var a=map.Add(90,110,100,110,130,110);var node=Create(map,a).Data!;var service=new LogicalNodeService(map.Repository);Calculate(service);
        node=service.GetAll().Data!.Single();Assert.AreEqual(1,node.Positions.Single().RoadOrder);Assert.AreEqual(0.35,node.Positions.Single().RoadPercent,1e-10);
        a=map.Save(a,90,110,95,110,100,110,130,110);var changed=service.GetAll().Data!.Single();Assert.IsFalse(changed.Positions.Single().IsChanged);Assert.AreEqual(node.Version+1,changed.Version);Calculate(service);node=service.GetAll().Data!.Single();Assert.AreEqual(2,node.Positions.Single().RoadOrder);
        a=map.Lines.Reverse(new(a.Code,a.Owner,a.Version)).Data!;Assert.IsFalse(service.GetAll().Data!.Single().Positions.Any(p=>p.IsChanged));Calculate(service);node=service.GetAll().Data!.Single();Assert.AreEqual(0,node.Positions.Single().RoadOrder);Assert.AreEqual(0.65,node.Positions.Single().RoadPercent,1e-10);
        Assert.IsTrue(map.Lines.Delete(new(a.Code,a.Owner,a.Version)).Success);Assert.IsFalse(service.GetAll().Data!.Single().Positions.Any(p=>p.IsChanged));Calculate(service);Assert.AreEqual(1,service.GetAll().Data!.Length);Assert.AreEqual(0,service.GetAll().Data!.Single().Positions.Length);
    }

    [TestMethod]
    public void JRebuildsAllPositionsInOneTransaction_AndWeightPermissionsVersionAreEnforced()
    {
        var map=new TestMap();var a=map.Add(90,110,130,110);var one=Create(map,a).Data!;var two=Create(map,a,120.5m,110.5m).Data!;var service=new LogicalNodeService(map.Repository);
        Assert.AreEqual(0,service.Calculate().Data!.RemainingNodes);Assert.AreEqual(2,service.GetAll().Data!.Sum(n=>n.Positions.Length));Calculate(service);one=service.GetAll().Data!.Single(n=>n.Code==one.Code);
        var before=map.Snapshot();Assert.AreEqual("NotOwner",service.Update(new(one.Code,"other",one.Version,"5","")).Code);Assert.AreEqual("InvalidLogicalNodeProperties",service.Update(new(one.Code,one.Owner,one.Version,new string('中',33),"")).Code);Assert.AreEqual(before,map.Snapshot());
        Assert.IsTrue(new LoginService(map.Repository).CreateUser(new("admin","admin")).Success);var updated=service.Update(new(one.Code,"admin",one.Version,"99",""));Assert.IsTrue(updated.Success);Assert.AreEqual("admin",updated.Data!.Owner);Assert.AreEqual(one.Properties with{Value="99",Info=""},updated.Data.Properties);
        Assert.AreEqual("VersionConflict",service.Delete(new(one.Code,"admin",one.Version)).Code);Assert.IsTrue(service.Delete(new(one.Code,"admin",updated.Data.Version)).Success);
    }

    [STATestMethod]
    public void LnKeys_AreSeparateFromLineEditing()
    {
        var map=new TestMap();var road=map.Add(90,110,130,110);var view=new CoordinateMapView();
        var editor=(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
        typeof(LineEditor).GetMethod("ApplySnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,[road]);
        view.HandleEditorKeyAsync(Key.F5).GetAwaiter().GetResult();Assert.IsTrue(view.IsLogicalNodeMode);
        var windows=0;view.LogicalNodeWindowRequested+=(_,args)=>windows++;
        var handler=new DirectLogicalNodeHttpStub();using var http=new System.Net.Http.HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        view.ConfigureClient(new ArgumentMap.Client.HttpLineClient(http,new("owner","test")));
        view.HandleEditorKeyAsync(Key.A).GetAwaiter().GetResult();Assert.AreEqual(1,handler.Creates);Assert.AreEqual(250000.5m,handler.LastCreate!.X);Assert.AreEqual(0,windows);
        view.HandleEditorKeyAsync(Key.D7).GetAwaiter().GetResult();Assert.IsTrue(view.IsLogicalNodeMode);
        view.HandleEditorKeyAsync(Key.Escape).GetAwaiter().GetResult();Assert.IsTrue(view.IsLogicalNodeMode);
        view.HandleEditorKeyAsync(Key.F5).GetAwaiter().GetResult();Assert.IsTrue(view.IsLogicalNodeMode);
        view.CancelEditing();Assert.IsFalse(view.IsLogicalNodeMode);
    }
}
