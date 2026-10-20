using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeUiTests
{
    private sealed class Client : ILogicalNodeClient
    {
        internal int Saves;internal bool Reject;
        public Task<ServiceResult<LogicalNodeSnapshot[]>> GetLogicalNodesAsync()=>Task.FromResult(ServiceResult<LogicalNodeSnapshot[]>.Ok([],""));
        public Task<ServiceResult<LogicalNodeSnapshot>> CreateLogicalNodeAsync(decimal x,decimal y,int sideLength,LogicalNodeProperties p)
        {Saves++;return Task.FromResult(Reject?ServiceResult<LogicalNodeSnapshot>.Fail("VersionConflict","请刷新后重试"):ServiceResult<LogicalNodeSnapshot>.Ok(new("ABCDEFGHIJ","owner",0,x,y,sideLength,p),"已保存"));}
        public Task<ServiceResult<LogicalNodeSnapshot>> UpdateLogicalNodeAsync(string code,long version,string value,string info)=>throw new InvalidOperationException("Unexpected write");
        public Task<ServiceResult<bool>> DeleteLogicalNodeAsync(string code,long version)=>throw new InvalidOperationException("Unexpected delete");
        public Task<ServiceResult<LogicalNodeCalculationProgress>> CalculateLogicalNodeAsync()=>Task.FromResult(ServiceResult<LogicalNodeCalculationProgress>.Ok(new(false,0,0),""));
    }
    [STATestMethod]
    public void CreateDialog_RejectsInvalidCenterAndKeepsInputsAfterSaveFailure()
    {
        var client=new Client{Reject=true};var road=new LineSnapshot("ZYXWVUTSRQ","owner",false,1,[new("ZYXWVUTSRQ",0,90,110),new("ZYXWVUTSRQ",1,130,110)]);
        var window=new LogicalNodeWindow(client,"owner",new Point(110.5,110.5));
        var x=(TextBox)window.FindName("XInput");var value=(TextBox)window.FindName("ValueInput");var save=(Button)window.FindName("SaveButton");
        x.Text="110";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(0,client.Saves);StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"0.5");
        x.Text="110.5";value.Text=new string('中',33);save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(0,client.Saves);
        value.Text="99";save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.AreEqual(1,client.Saves);Assert.AreEqual("110.5",x.Text);Assert.AreEqual("99",value.Text);Assert.IsNull(window.FindName("RoadInput"));Assert.IsTrue(save.IsEnabled);
        StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"刷新");window.Close();
    }
    [STATestMethod]
    public void OtherOwnersProperties_AreReadOnlyWhileAdministratorMayEditWeight()
    {
        var client=new Client();var node=new LogicalNodeSnapshot("ABCDEFGHIJ","owner",0,110.5m,110.5m,12,new());
        foreach(var owner in new[]{"owner","other","admin"})
        {
            var window=new LogicalNodeWindow(client,owner,new Point(110.5,110.5),node);Assert.IsFalse(((UIElement)window.FindName("GeometryPanel")).IsEnabled);Assert.AreEqual(owner!="other",((UIElement)window.FindName("MetadataPanel")).IsEnabled);
            Assert.AreEqual(owner!="other",((Button)window.FindName("SaveButton")).IsEnabled);Assert.AreEqual(owner!="other",((TextBox)window.FindName("ValueInput")).IsEnabled);Assert.IsNull(window.FindName("TypeInput"));Assert.IsNull(window.FindName("WeightInput"));window.Close();
        }
    }
}
