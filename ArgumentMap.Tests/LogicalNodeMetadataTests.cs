using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeMetadataTests
{
    [TestMethod,DataRow("value-null"),DataRow("info-null"),DataRow("value-long"),DataRow("info-long")]
    public void InvalidMetadataUpdateRollsBackEverything(string kind)
    {
        var map=new TestMap();map.Add(90,110,130,110);var service=new LogicalNodeService(map.Repository);
        var node=service.Create(new("owner",110.5m,110.5m,6,new())).Data!;var before=map.Snapshot();
        var value=kind=="value-null"?null!:kind=="value-long"?new string('中',33):"值";
        var info=kind=="info-null"?null!:kind=="info-long"?new string('文',256):"说明";
        Assert.AreEqual("InvalidLogicalNodeProperties",service.Update(new(node.Code,node.Owner,node.Version,value,info)).Code);Assert.AreEqual(before,map.Snapshot());
    }
    [TestMethod,DataRow(false),DataRow(true)]
    public void MetadataUpdateChangesOnlyValueInfoAndVersion_GeometryAndPositionsRemainFixed(bool empty)
    {
        var map=new TestMap();map.Add(90,108,130,108,130,112,90,112);var service=new LogicalNodeService(map.Repository);
        var node=service.Create(new("owner",110.5m,110.5m,18,new())).Data!;
        var value=empty?"":new string('中',32);var info=empty?"":new string('文',255);
        var changed=service.Update(new(node.Code,node.Owner,node.Version,value,info)).Data!;
        Assert.AreEqual(node.Code,changed.Code);Assert.AreEqual(node.X,changed.X);Assert.AreEqual(node.Y,changed.Y);Assert.AreEqual(node.SideLength,changed.SideLength);Assert.IsTrue(changed.IsStart);
        Assert.AreEqual(node.Version+1,changed.Version);Assert.AreEqual(value,changed.Properties.Value);Assert.AreEqual(info,changed.Properties.Info);CollectionAssert.AreEqual(node.Positions,changed.Positions);
        var before=map.Snapshot();Assert.AreEqual("VersionConflict",service.Update(new(node.Code,node.Owner,node.Version,"stale","stale")).Code);Assert.AreEqual(before,map.Snapshot());
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(changed.Properties));CollectionAssert.AreEqual(new[]{"Info","LogicLanguage","OperandKind","Value"},json.RootElement.EnumerateObject().Select(p=>p.Name).Order().ToArray());
        Assert.AreEqual(LogicalNodeOperandRules.Custom,changed.Properties.OperandKind);
    }
    [STATestMethod]
    public void ExistingEditorHasReadonlyGeometry_EditableValueInfo_AndNoTypeOrWeightControls()
    {
        var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        var original=service.Create(new("owner",110.5m,110.5m,18,new(){Value="开始",Info="原说明"})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://127.0.0.1:1/")};
        var window=new LogicalNodeWindow(new ArgumentMap.Client.HttpLineClient(http,new("owner","test")),"owner",new Point(110.5,110.5),original);
        try
        {
            Assert.IsFalse(((UIElement)window.FindName("GeometryPanel")).IsEnabled);Assert.IsTrue(((UIElement)window.FindName("MetadataPanel")).IsEnabled);
            Assert.IsNull(window.FindName("NameInput"));Assert.IsNull(window.FindName("TypeInput"));Assert.IsNull(window.FindName("WeightInput"));
            // Even programmatic tampering with disabled controls cannot alter the update payload.
            ((TextBox)window.FindName("XInput")).Text="1.5";((TextBox)window.FindName("YInput")).Text="2.5";((ComboBox)window.FindName("SizeInput")).SelectedIndex=0;
            ((TextBox)window.FindName("ValueInput")).Text="新值";((TextBox)window.FindName("InfoInput")).Text="新说明";
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LogicalNodeWindowEntryTests.DrainUntil(()=>service.GetAll().Data!.Single().Version>original.Version);
            var saved=service.GetAll().Data!.Single();Assert.AreEqual("新值",saved.Properties.Value);Assert.AreEqual("新说明",saved.Properties.Info);Assert.AreEqual(original.X,saved.X);Assert.AreEqual(original.Y,saved.Y);Assert.AreEqual(18,saved.SideLength);
        }
        finally{window.Close();}
    }
}
