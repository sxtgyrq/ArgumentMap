using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeEditorReviewTests
{
    private sealed class Client(TestMap map):ILogicalNodeClient
    {
        internal bool FailRead,FailCalculation;
        internal int Reads,Calculations;
        internal int Saves;
        internal TaskCompletionSource<ServiceResult<LogicalNodeSnapshot[]>>? ReadGate;
        internal TaskCompletionSource<ServiceResult<LogicalNodeSnapshot>>? SaveGate;
        public Task<ServiceResult<LogicalNodeSnapshot[]>> GetLogicalNodesAsync()
        {
            Reads++;if(ReadGate is not null)return ReadGate.Task;
            return Task.FromResult(FailRead?ServiceResult<LogicalNodeSnapshot[]>.Fail("ConnectionFailure","连接失败"):new LogicalNodeService(map.Repository).GetAll("owner"));
        }
        public Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeAsync()
        {
            Calculations++;return Task.FromResult(FailCalculation?ServiceResult<LogicalNodeCalculationProgress>.Fail("DatabaseError","数据库已回滚"):new LogicalNodeService(map.Repository).Calculate());
        }
        public Task<ServiceResult<LogicalNodeSnapshot>> CreateLogicalNodeAsync(decimal x,decimal y,int size,LogicalNodeProperties properties)
        { Saves++;return SaveGate?.Task??Task.FromResult(new LogicalNodeService(map.Repository).Create(new("owner",x,y,size,properties))); }
        public Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code,long version,string value,string info)=>Task.FromResult(new LogicalNodeService(map.Repository).Update(new(code,"owner",version,value,info)));
        public Task<ServiceResult<bool>> DeleteLogicalNodeAsync(string code,long version)=>Task.FromResult(new LogicalNodeService(map.Repository).Delete(new(code,"owner",version)));
    }
    private static (TestMap Map,Client Client,LogicalNodeEditor Editor,LogicalNodeSnapshot Node) Fixture()
    {
        var map=new TestMap();map.Add(90,110,130,110);var node=new LogicalNodeService(map.Repository).Create(new("owner",110.5m,110.5m,6,new())).Data!;
        var client=new Client(map);var editor=new LogicalNodeEditor();editor.Configure(client);editor.Activate();return(map,client,editor,node);
    }

    [TestMethod]
    public async Task RefreshFailurePreservesSelectionAndRows_DeletedSelectionClearsAfterSuccessfulRefresh()
    {
        var(map,client,editor,node)=Fixture();Assert.IsTrue(await editor.RefreshAsync());editor.ToggleSelection();editor.SelectClosest(new Point(110.5,110.5),0.1);Assert.AreEqual(node.Code,editor.SelectedCode);
        var loaded=editor.Selected;client.FailRead=true;Assert.IsFalse(await editor.RefreshAsync());Assert.AreSame(loaded,editor.Selected);Assert.AreEqual(1,editor.Nodes.Count);Assert.IsFalse(editor.IsBusy);
        Assert.IsTrue(new LogicalNodeService(map.Repository).Delete(new(node.Code,"owner",node.Version)).Success);client.FailRead=false;
        Assert.IsTrue(await editor.RefreshAsync());Assert.IsNull(editor.Selected);Assert.IsNull(editor.SelectedCode);Assert.AreEqual(0,editor.Nodes.Count);
    }
    [TestMethod]
    public async Task SuccessfulJWithFailedReload_ReportsCommittedAndKeepsCachedRowsUntilF4()
    {
        var(map,client,editor,_)=Fixture();await editor.RefreshAsync();var old=editor.Nodes.Single();
        map.Repository.Transaction(s=>{var key=s.LogicalNodePositions.Keys.Single();s.LogicalNodePositions[key]=s.LogicalNodePositions[key] with{IsChanged=true,RoadPercent=0};return true;});
        client.FailRead=true;Assert.IsTrue(await editor.CalculateAsync(()=>{}));Assert.AreEqual(1,client.Calculations);Assert.AreSame(old,editor.Nodes.Single());Assert.IsFalse(editor.IsBusy);
        StringAssert.Contains(editor.CalculationMessage,"已计算并提交");StringAssert.Contains(editor.CalculationMessage,"F4");Assert.IsFalse(new LogicalNodeService(map.Repository).GetAll().Data!.Single().Positions.Single().IsChanged);
        client.FailRead=false;Assert.IsTrue(await editor.RefreshAsync());Assert.IsTrue(editor.Nodes.Single().Version>old.Version);
    }
    [TestMethod]
    public async Task FailedJDoesNotReloadOrDiscardCachedRows_ConcurrentCommandsAreRejected()
    {
        var(_,client,editor,_)=Fixture();await editor.RefreshAsync();var old=editor.Nodes.Single();var reads=client.Reads;
        client.FailCalculation=true;Assert.IsFalse(await editor.CalculateAsync(()=>{}));Assert.AreEqual(reads,client.Reads);Assert.AreSame(old,editor.Nodes.Single());Assert.IsFalse(editor.IsBusy);
        client.ReadGate=new(TaskCreationOptions.RunContinuationsAsynchronously);var pending=editor.RefreshAsync();Assert.IsTrue(editor.IsBusy);
        Assert.IsFalse(await editor.RefreshAsync());Assert.IsFalse(await editor.CalculateAsync(()=>{}));Assert.AreEqual(reads+1,client.Reads);Assert.AreEqual(1,client.Calculations);
        client.ReadGate.SetResult(ServiceResult<LogicalNodeSnapshot[]>.Fail("LoginExpired","请重新登录"));Assert.IsFalse(await pending);Assert.IsFalse(editor.IsBusy);Assert.AreSame(old,editor.Nodes.Single());
    }
    [STATestMethod,DataRow(true),DataRow(false)]
    public void PropertyWindowUsesServerPermission_AndListsEveryPositionWithoutEditingThem(bool canEdit)
    {
        var(_,client,_,node)=Fixture();node=node with{CanEdit=canEdit,Positions=[node.Positions.Single(),new(node.Code,"ZYXWVUTSRQ",2,0.25,110m,110.5m,3)]};
        var window=new LogicalNodeWindow(client,canEdit?"manager":"admin",new Point(110.5,110.5),node);
        Assert.AreEqual(canEdit,((Button)window.FindName("SaveButton")).IsEnabled);Assert.AreEqual(canEdit,((Button)window.FindName("DeleteButton")).IsEnabled);
        var positions=(DataGrid)window.FindName("PositionsGrid");Assert.IsTrue(positions.IsReadOnly);Assert.AreEqual(2,positions.Items.Count);Assert.IsNull(window.FindName("RoadInput"));window.Close();
    }
    [STATestMethod]
    public void StaleMetadataSavePreservesInputAndPositions()
    {
        var(map,client,_,node)=Fixture();var window=new LogicalNodeWindow(client,"owner",new Point(110.5,110.5),node);
        // Make the window stale through a concurrent road reversal, then attempt a metadata save.
        var road=map.Lines.GetAll().Data!.Single();Assert.IsTrue(map.Lines.Reverse(new(road.Code,road.Owner,road.Version)).Success);
        var weight=(TextBox)window.FindName("ValueInput");weight.Text="99";((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.AreEqual("99",weight.Text);StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"变化");Assert.AreEqual(node.Properties.Value,new LogicalNodeService(map.Repository).GetAll().Data!.Single().Properties.Value);
        CollectionAssert.AreEqual(node.Positions,((DataGrid)window.FindName("PositionsGrid")).Items.Cast<LogicalNodePosition>().ToArray());Assert.IsTrue(((Button)window.FindName("SaveButton")).IsEnabled);window.Close();
    }
    [STATestMethod]
    public void PendingSaveRejectsDoubleClickAndClose_ThenFailureRestoresControls()
    {
        var(_,client,_,_)=Fixture();client.SaveGate=new();var window=new LogicalNodeWindow(client,"owner",new Point(110.5,110.5));
        var closed=0;window.Closed+=(_,_)=>closed++;var save=(Button)window.FindName("SaveButton");var weight=(TextBox)window.FindName("ValueInput");weight.Text="99";
        save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(1,client.Saves);Assert.IsFalse(save.IsEnabled);
        window.Close();Assert.AreEqual(0,closed);client.SaveGate.SetResult(ServiceResult<LogicalNodeSnapshot>.Fail("DatabaseError","数据库已回滚"));
        Assert.IsTrue(save.IsEnabled);Assert.AreEqual("99",weight.Text);Assert.AreEqual(0,closed);StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"回滚");window.Close();Assert.AreEqual(1,closed);
    }
}
