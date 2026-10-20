using System.Reflection;
using System.Windows;
using System.Windows.Input;
using ArgumentMap.Contracts;
using ArgumentMap.Business;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class CrossDirectionTests
{
    internal static (TestMap Map, LineSnapshot A, LineSnapshot B, CrossSnapshot Cross) Pair()
    {
        var map = new TestMap(); var a = map.Add(100,110,110,110,130,110); var b = map.Add(110,100,110,140);
        map.Calculate(); return (map,a,b,map.Between(a,b));
    }

    [TestMethod]
    [DataRow(CrossState.Disconnected,false,false), DataRow(CrossState.Unset,false,false)]
    [DataRow(CrossState.Connected,true,true), DataRow(CrossState.Road1ToRoad2,true,false), DataRow(CrossState.Road2ToRoad1,false,true)]
    public void TransferDirections_AreRelativeToCanonicalRoadCodes(CrossState state,bool firstToSecond,bool secondToFirst)
    {
        var (map,a,_,cross)=Pair(); var saved=map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,state)); Assert.IsTrue(saved.Success);
        cross=saved.Data!;
        Assert.AreEqual(firstToSecond,cross.AllowsTransfer(cross.Key.RoadCode1,cross.Key.RoadCode2));
        Assert.AreEqual(secondToFirst,cross.AllowsTransfer(cross.Key.RoadCode2,cross.Key.RoadCode1));
        Assert.AreEqual(firstToSecond||secondToFirst,cross.CanTransfer);
        Assert.IsFalse(cross.AllowsTransfer(cross.Key.RoadCode1,cross.Key.RoadCode1));
        Assert.IsFalse(cross.AllowsTransfer("XXXXXXXXXX",cross.Key.RoadCode2));
    }

    [TestMethod]
    [DataRow(CrossState.Road1ToRoad2), DataRow(CrossState.Road2ToRoad1)]
    public void NodeChangesAndReversal_RetainCrossTransferDirectionAndDeleteCleansRelation(CrossState state)
    {
        var (map,a,b,cross)=Pair(); cross=map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,state)).Data!;
        a=map.Save(a,100,110,105,110,110,110,130,110); map.Calculate();
        var current=map.Between(a,b); Assert.AreEqual(cross.Key,current.Key); Assert.AreEqual(state,current.State);
        Assert.AreEqual((2,0.0),current.LocationOn(a.Code));
        a=map.Save(a,100,110,105,110,130,110); map.Calculate();
        current=map.Between(a,b); Assert.AreEqual(state,current.State); Assert.AreEqual((1,0.2),current.LocationOn(a.Code));
        var reversed=map.Lines.Reverse(new(a.Code,a.Owner,a.Version)); Assert.IsTrue(reversed.Success); a=reversed.Data!; map.Calculate();
        current=map.Between(a,b); Assert.AreEqual(cross.Key,current.Key); Assert.AreEqual(state,current.State);
        Assert.AreEqual((0,0.8),current.LocationOn(a.Code));
        Assert.IsTrue(map.Lines.Delete(new(a.Code,a.Owner,a.Version)).Success);
        Assert.AreEqual(0,map.Crosses.GetAll().Data!.Length); Assert.AreEqual(0,map.Repository.Transaction(s=>s.StaleCrosses.Count));
    }

    [TestMethod]
    [DataRow((CrossState)(-2)), DataRow((CrossState)4), DataRow((CrossState)99)]
    public void UnsupportedStates_RollBackWithoutChangingCross(CrossState state)
    {
        var (map,a,_,cross)=Pair(); var before=map.Snapshot();
        Assert.AreEqual("InvalidCrossState",map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,state)).Code);
        Assert.AreEqual(before,map.Snapshot());
    }

    [TestMethod]
    [DataRow(CrossState.Road1ToRoad2), DataRow(CrossState.Road2ToRoad1)]
    public void OneWayEdit_EnforcesOwnerVersionAndBothLockedRules(CrossState state)
    {
        var (map,a,b,cross)=Pair(); var before=map.Snapshot();
        Assert.AreEqual("NotOwner",map.Crosses.Edit(new(cross.Key,"outsider",cross.Version,state)).Code); Assert.AreEqual(before,map.Snapshot());
        cross=map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,state)).Data!; before=map.Snapshot();
        Assert.AreEqual("VersionConflict",map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version-1,CrossState.Connected)).Code); Assert.AreEqual(before,map.Snapshot());
        map.Repository.Transaction(s=> { s.Lines[a.Code]=a with {IsLocked=true}; s.Lines[b.Code]=b with {IsLocked=true}; return true; }); before=map.Snapshot();
        Assert.AreEqual("LineIsLocked",map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,CrossState.Unset)).Code); Assert.AreEqual(before,map.Snapshot());
        map.Repository.Transaction(s=> { s.Lines[b.Code]=b; return true; });
        Assert.IsTrue(map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,CrossState.Unset)).Success);
    }

    [TestMethod]
    [DataRow(CrossState.Road1ToRoad2), DataRow(CrossState.Road2ToRoad1)]
    public void OneWayState_DoesNotAllowThirdRoadToPassCross(CrossState state)
    {
        var (map,a,_,cross)=Pair(); map.Crosses.Edit(new(cross.Key,a.Owner,cross.Version,state));
        var third=map.Create(); var before=map.Snapshot();
        Assert.AreEqual("LinePassesCross",map.Lines.CheckShapeIsOKThenSave(TestMap.Shape(third,105,105,115,115)).Code);
        Assert.AreEqual(before,map.Snapshot());
    }

    [STATestMethod]
    [DataRow(true), DataRow(false)]
    public void EnterAndBracket_WorkflowRemembersRoadAndShiftStillSaves(bool chooseRoad1)
    {
        var (map,a,b,cross)=Pair(); var client=new CrossFixtureClient(map);
        var view=new CoordinateMapView(); view.ConfigureClient(client); var editor=Editor(view);
        foreach(var line in new[]{a,b}) typeof(LineEditor).GetMethod("ApplySnapshot",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(editor,[line]);
        Assert.IsTrue(view.RefreshCrossesAsync().GetAwaiter().GetResult());
        Assert.IsTrue(CoordinateMapView.IsEditorKey(Key.Enter)); Assert.IsTrue(CoordinateMapView.IsEditorKey(Key.OemOpenBrackets));
        var exit=chooseRoad1?cross.Key.RoadCode1:cross.Key.RoadCode2;
        var selected=exit==a.Code?a:b; view.HandleEditorKeyAsync(Key.F8).GetAwaiter().GetResult();
        editor.SelectClosest(new Point(selected.Nodes[0].X,selected.Nodes[0].Y),0.1);
        Assert.AreEqual(exit,editor.SelectedCode);
        view.HandleEditorKeyAsync(Key.Enter).GetAwaiter().GetResult(); Assert.AreEqual(exit,view.CrossExitLineCode);
        view.HandleEditorKeyAsync(Key.U).GetAwaiter().GetResult(); Assert.AreEqual(exit,view.CrossExitLineCode);
        CrossEditor(view).SelectClosest(new Point(110,110),0.1);
        var reloads=client.Reloads; view.HandleEditorKeyAsync(Key.OemOpenBrackets,skipCrossReload:true).GetAwaiter().GetResult();
        Assert.AreEqual(reloads,client.Reloads); Assert.AreEqual(1,client.Saves);
        var expected=chooseRoad1?CrossState.Road1ToRoad2:CrossState.Road2ToRoad1;
        Assert.AreEqual(expected,map.Crosses.GetAll().Data!.Single().State);
        Assert.AreEqual(expected,CrossEditor(view).Selected!.State);
        view.HandleEditorKeyAsync(Key.P).GetAwaiter().GetResult(); Assert.AreEqual(reloads+1,client.Reloads);
        Assert.AreEqual(CrossState.Unset,map.Crosses.GetAll().Data!.Single().State);
        view.HandleEditorKeyAsync(Key.OemOpenBrackets).GetAwaiter().GetResult(); Assert.AreEqual(expected,map.Crosses.GetAll().Data!.Single().State);
    }

    [TestMethod]
    public async Task Bracket_RejectsMissingUnrelatedExitAndFailedSavePreservesLocalState()
    {
        var (map,_,_,cross)=Pair(); var client=new CrossFixtureClient(map); var editor=new CrossEditor(); editor.ConfigureClient(client);
        await editor.RefreshAsync(); editor.ToggleSelection(); editor.SelectClosest(new Point(110,110),0.1);
        Assert.IsFalse(await editor.SetOneWayAsync()); Assert.AreEqual(0,client.Saves);
        editor.RememberExitLine("XXXXXXXXXX"); Assert.IsFalse(await editor.SetOneWayAsync()); Assert.AreEqual(0,client.Saves);
        editor.RememberExitLine(cross.Key.RoadCode1); client.FailSave=true;
        Assert.IsFalse(await editor.SetOneWayAsync()); Assert.AreEqual(CrossState.Unset,editor.Selected!.State);
        Assert.AreEqual(CrossState.Unset,map.Crosses.GetAll().Data!.Single().State); Assert.IsFalse(editor.IsBusy);
        client.FailSave=false; client.FailReload=true;
        Assert.IsTrue(await editor.SetOneWayAsync()); Assert.AreEqual(CrossState.Road1ToRoad2,map.Crosses.GetAll().Data!.Single().State);
        Assert.AreEqual(CrossState.Road1ToRoad2,editor.Selected!.State); StringAssert.Contains(editor.Message,"状态已保存");
    }

    private static LineEditor Editor(CoordinateMapView view)=>(LineEditor)typeof(CoordinateMapView).GetField("editor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;
    private static CrossEditor CrossEditor(CoordinateMapView view)=>(CrossEditor)typeof(CoordinateMapView).GetField("crossEditor",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(view)!;

    private sealed class CrossFixtureClient(TestMap map) : ILineClient,ICrossClient
    {
        public int Reloads {get;private set;} public int Saves {get;private set;} public bool FailSave {get;set;} public bool FailReload {get;set;}
        public Task<ServiceResult<LineSnapshot>> CreateAsync()=>Task.FromResult(map.Lines.Create(new("owner")));
        public Task<ServiceResult<LineSnapshot>> StartDrawingAsync(string code)=>Task.FromResult(map.Lines.StartDrawing(new(code,"owner")));
        public Task<ServiceResult<LineSnapshot>> SaveAsync(string code,long version,LineNode[] nodes)=>Task.FromResult(map.Lines.CheckShapeIsOKThenSave(new(code,"owner",version,nodes)));
        public Task<ServiceResult<LineSnapshot[]>> GetAllAsync()=>Task.FromResult(map.Lines.GetAll());
        public Task<ServiceResult<bool>> DeleteAsync(string code,long version)=>Task.FromResult(map.Lines.Delete(new(code,"owner",version)));
        public Task<ServiceResult<CrossSnapshot[]>> GetCrossesAsync() { Reloads++;return Task.FromResult(FailReload?ServiceResult<CrossSnapshot[]>.Fail("InjectedFailure","刷新失败"):map.Crosses.GetAll()); }
        public Task<ServiceResult<CrossCalculationProgress>> CalculateCrossAsync()=>Task.FromResult(map.Crosses.Calculate());
        public Task<ServiceResult<CrossSnapshot>> SetCrossStateAsync(CrossKey key,long version,CrossState state)
        { Saves++;return Task.FromResult(FailSave?ServiceResult<CrossSnapshot>.Fail("VersionConflict","请刷新交点"):map.Crosses.Edit(new(key,"owner",version,state))); }
    }
}
