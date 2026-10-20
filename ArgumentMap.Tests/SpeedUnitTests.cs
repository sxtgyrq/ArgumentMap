using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class SpeedUnitTests
{
    [TestMethod]
    public void NewLine_DefaultSpeedIs100AndTransportOmitsRemovedProperties()
    {
        var line = new TestMap().Create(); Assert.AreEqual(100, line.Properties.Speed);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(line.Properties, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.AreEqual(100, json.RootElement.GetProperty("speed").GetInt32());
        foreach (var removed in new[] { "maxSpeed", "carInDirection", "carInOpposeDirection", "walkInDirection", "walkInOpposeDirection", "eBicycleInDirection", "eBicycleInOpposeDirection" })
            Assert.IsFalse(json.RootElement.TryGetProperty(removed, out _), $"Removed wire property: {removed}");
        Assert.AreEqual(1, json.RootElement.GetProperty("passInDirection").GetInt32());
        Assert.AreEqual(0, json.RootElement.GetProperty("passInOpposeDirection").GetInt32());
        Assert.AreEqual(5, json.RootElement.EnumerateObject().Count());
    }

    [STATestMethod,DataRow(100),DataRow(5),DataRow(37),DataRow(500)]
    public void PropertiesPanelDisplaysStoredSpeed_AndMissingJsonSpeedUsesDefault100(int speed)
    {
        var line=new TestMap().Create();Assert.AreEqual(100,line.Properties.Speed);
        Assert.AreEqual(100,JsonSerializer.Deserialize<LineProperties>("{}")!.Speed);
        var window=new LinePropertiesWindow(new NoSaveClient(),line with{Properties=line.Properties with{Speed=speed}},"owner");
        try{Assert.AreEqual(speed.ToString(System.Globalization.CultureInfo.InvariantCulture),((TextBox)window.FindName("SpeedInput")).Text);}
        finally{window.Close();}
    }

    [TestMethod]
    [DataRow(5), DataRow(6), DataRow(120), DataRow(499), DataRow(500)]
    public void SaveSpeed_AcceptsInclusiveRangeWithoutChangingGeometryOrCross(int speed)
    {
        var map = new TestMap(); var a = map.Add(100, 110, 120, 110); var b = map.Add(110, 100, 110, 120); map.Calculate();
        var cross = map.Between(a, b);
        var result = map.Lines.SaveProperties(new(a.Code, a.Owner, a.Version, a.Properties with { Speed = speed }));
        Assert.IsTrue(result.Success, result.Message); Assert.AreEqual(speed, result.Data!.Properties.Speed);
        CollectionAssert.AreEqual(a.Nodes, result.Data.Nodes); Assert.AreEqual(cross, map.Between(a, b));
        Assert.IsTrue(map.Repository.Transaction(s => s.PendingSegments.Count == 0 && s.DirtyLines.Count == 0 && s.StaleCrosses.Count == 0));
    }

    [TestMethod]
    [DataRow(-1), DataRow(0), DataRow(4), DataRow(501), DataRow(int.MaxValue)]
    public void SaveSpeed_RejectsOutsideRangeAndRollsBack(int speed)
    {
        var map = new TestMap(); var line = map.Add(100, 100, 110, 100); var before = map.Snapshot();
        var result = map.Lines.SaveProperties(new(line.Code, line.Owner, line.Version, line.Properties with { Speed = speed }));
        Assert.IsFalse(result.Success); Assert.AreEqual("InvalidSpeed", result.Code); Assert.AreEqual(before, map.Snapshot());
    }

    [STATestMethod]
    [DataRow("0"), DataRow("4"), DataRow("501"), DataRow("5.5"), DataRow("")]
    public void PropertiesPanel_RejectsInvalidSpeedBeforeSendingAndHasNoModeControls(string text)
    {
        var client = new NoSaveClient(); var line = new LineSnapshot("ZAAAAAAAAB", "owner", false, 1, []);
        var window = new LinePropertiesWindow(client, line, "owner");
        try
        {
            Assert.IsNull(window.FindName("CarDirection")); Assert.IsNull(window.FindName("WalkDirection")); Assert.IsNull(window.FindName("EBicycleDirection"));
            ((TextBox)window.FindName("SpeedInput")).Text = text;
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(0, client.SaveCalls);
            Assert.IsFalse(string.IsNullOrWhiteSpace(((TextBlock)window.FindName("SaveStatus")).Text));
        }
        finally { window.Close(); }
    }

    private sealed class NoSaveClient : ILinePropertiesClient
    {
        internal int SaveCalls;
        public Task<ServiceResult<LineSnapshot>> GetLineDetailAsync(string id) => Task.FromResult(ServiceResult<LineSnapshot>.Fail("UnexpectedCall", "Unused"));
        public Task<ServiceResult<LineSnapshot>> ToggleLineLockAsync(string id, long version) => Task.FromResult(ServiceResult<LineSnapshot>.Fail("UnexpectedCall", "Unused"));
        public Task<ServiceResult<LineSnapshot>> SaveLinePropertiesAsync(string id, long version, LineProperties properties)
        { SaveCalls++; return Task.FromResult(ServiceResult<LineSnapshot>.Fail("UnexpectedCall", "Should not send invalid input")); }
    }
}
