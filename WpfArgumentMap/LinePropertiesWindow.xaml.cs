using System.ComponentModel;
using System.Globalization;
using System.Windows;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public partial class LinePropertiesWindow : Window
{
    private readonly ILinePropertiesClient client;
    private readonly LineSnapshot original;
    private readonly bool editable;
    private bool busy;
    public LineSnapshot? SavedLine { get; private set; }

    public LinePropertiesWindow(ILinePropertiesClient client, LineSnapshot line, string userName)
    {
        InitializeComponent();
        this.client = client;
        original = line;
        var properties = line.Properties;
        LineHeading.Text = string.IsNullOrEmpty(properties.RoadName) ? $"线 {line.Code}" : properties.RoadName;
        LineIdentity.Text = $"编码 {line.Code}    负责人 {line.Owner}    {(line.IsLocked ? "已锁定" : "未锁定")}";
        NameInput.Text = properties.RoadName;
        DetailInput.Text = properties.RoadDetail;
        SpeedInput.Text = properties.Speed.ToString(CultureInfo.InvariantCulture);
        PassDirectionInput.SelectedIndex = properties.PassInOpposeDirection;
        editable = !line.IsLocked && line.Owner == userName;
        EditablePanel.IsEnabled = SaveButton.IsEnabled = editable;
        if (!editable) SaveStatus.Text = line.IsLocked ? "线路已锁定。解锁后可以修改属性。" : "仅线路负责人可以修改属性。";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !editable) return;
        if (!int.TryParse(SpeedInput.Text, out var speed)) { SaveStatus.Text = "请输入整数速度。"; return; }
        if (speed is < MapLimits.SpeedMinimum or > MapLimits.SpeedMaximum) { SaveStatus.Text = "速度须为 5–500 的整数。"; return; }
        var properties = new LineProperties
        {
            RoadName = NameInput.Text, RoadDetail = DetailInput.Text, Speed = speed,
            PassInDirection = 1, PassInOpposeDirection = (short)PassDirectionInput.SelectedIndex
        };
        busy = true;
        EditablePanel.IsEnabled = SaveButton.IsEnabled = CancelButton.IsEnabled = false;
        SaveStatus.Text = "正在保存……";
        try
        {
            var result = await client.SaveLinePropertiesAsync(original.Code, original.Version, properties);
            if (!result.Success || result.Data is null) { SaveStatus.Text = result.Message; return; }
            SavedLine = result.Data;
            busy = false;
            DialogResult = true;
        }
        finally
        {
            busy = false;
            EditablePanel.IsEnabled = SaveButton.IsEnabled = editable;
            CancelButton.IsEnabled = true;
        }
    }
    private void Window_Closing(object? sender, CancelEventArgs e) { if (busy) e.Cancel = true; }
}
