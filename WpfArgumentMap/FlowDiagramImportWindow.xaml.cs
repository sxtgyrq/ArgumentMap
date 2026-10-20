using ArgumentMap.Rules;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public partial class FlowDiagramImportWindow:Window
{
    private readonly IFlowDiagramClient client;
    private readonly CoordinateMapView map;
    private bool busy,validated,committed;
    private string completionMessage="已取消流程图导入，未保存预览内容。";
    private string validatedText="";
    private decimal validatedX,validatedY;
    public FlowDiagramImportWindow(IFlowDiagramClient client,CoordinateMapView map)
    {
        this.client=client;this.map=map;InitializeComponent();
        XInput.Text=(Math.Floor(map.Center.X)+.5).ToString(CultureInfo.InvariantCulture);YInput.Text=(Math.Floor(map.Center.Y)+.5).ToString(CultureInfo.InvariantCulture);
        map.FlowDiagramCancelRequested+=Map_CancelRequested;
        map.FlowDiagramReturnRequested+=Map_ReturnRequested;
    }
    internal static string ExampleText
    {
        get
        {
            var assembly=typeof(FlowDiagramImportWindow).Assembly;using var stream=assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n=>n.EndsWith(".equipment-diagnosis.flow.json",StringComparison.Ordinal)))!;
            using var reader=new StreamReader(stream);return reader.ReadToEnd();
        }
    }
    private void Input_Changed(object sender,System.Windows.Controls.TextChangedEventArgs e)
    {
        if(ImportButton is null||busy)return;validated=false;ImportButton.IsEnabled=false;if(MapPreviewButton is not null)MapPreviewButton.IsEnabled=false;map.ShowFlowDiagramPreview(null);
        if(Status is not null)Status.Text="内容或插入点已改变，请重新校验预览。";
    }
    private bool ReadOrigin(out decimal x,out decimal y)
    {
        x=y=0;
        if(!decimal.TryParse(XInput.Text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out x)||
           !decimal.TryParse(YInput.Text,NumberStyles.AllowLeadingSign|NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out y)||
           !LogicalNodeGeometryRules.IsHalfInteger(x)||!LogicalNodeGeometryRules.IsHalfInteger(y)||x>500000||y>500000)
        {Status.Text="插入点 X、Y 必须在地图范围内，且均为整数＋0.5。";return false;}
        return true;
    }
    public bool StartMapPick()
    {
        if(busy)return false;var parsed=FlowDiagramLanguage.Parse(SourceText.Text);
        if(!parsed.Success){Status.Text=parsed.Message;return false;}
        validated=false;ImportButton.IsEnabled=MapPreviewButton.IsEnabled=false;
        if(!map.BeginFlowDiagramOriginPick(point=>
        {
            if(point is {} p){XInput.Text=p.X.ToString(CultureInfo.InvariantCulture);YInput.Text=p.Y.ToString(CultureInfo.InvariantCulture);Status.Text="插入点已选择，请点击校验预览。";}
            else Status.Text="已取消选点，可修改坐标或重新选择。";
            Show();Activate();
        }))return false;
        Hide();Owner?.Activate();map.Focus();return true;
    }
    public async Task<bool> PreviewAsync()
    {
        if(busy||!ReadOrigin(out var x,out var y))return false;
        var text=SourceText.Text;var parsed=FlowDiagramLanguage.Parse(text);
        if(!parsed.Success)
        {
            validated=false;ImportButton.IsEnabled=MapPreviewButton.IsEnabled=false;map.ShowFlowDiagramPreview(null);
            Status.Text=parsed.Message;Status.Foreground=Brushes.Firebrick;map.SetFlowDiagramMessage(parsed.Message);return false;
        }
        SetBusy(true);validated=false;map.ShowFlowDiagramPreview(null);Status.Text="正在服务端按新增规则校验，尚未保存…";
        try
        {
            var result=await client.PreviewFlowDiagramAsync(text,x,y);Status.Text=result.Message;Status.Foreground=result.Success?Brushes.SeaGreen:Brushes.Firebrick;map.SetFlowDiagramMessage(result.Message);
            if(!result.Success||result.Data is null)return false;
            validated=true;validatedText=text;validatedX=x;validatedY=y;map.ShowFlowDiagramPreview(result.Data.Added,fit:true);return true;
        }
        finally{SetBusy(false);}
    }
    public async Task<bool> ImportAsync()
    {
        if(busy||!validated||!ReadOrigin(out var x,out var y))return false;
        if(SourceText.Text!=validatedText||x!=validatedX||y!=validatedY){validated=false;ImportButton.IsEnabled=false;map.ShowFlowDiagramPreview(null);Status.Text="内容或插入点已改变，请重新校验预览。";return false;}
        var text=SourceText.Text;SetBusy(true);Status.Text="正在事务内读取最新地图，校验并新增整张流程图…";
        try
        {
            var result=await client.ImportFlowDiagramAsync(text,x,y);Status.Text=result.Message;Status.Foreground=result.Success?Brushes.SeaGreen:Brushes.Firebrick;map.SetFlowDiagramMessage(result.Message);
            if(!result.Success||result.Data?.Map is null){validated=false;map.ShowFlowDiagramPreview(null);return false;}
            map.ApplyFlowDiagramMap(result.Data.Map);committed=true;completionMessage=result.Message;return true;
        }
        finally{SetBusy(false);if(committed)Close();}
    }
    private void SetBusy(bool value)
    {busy=value;map.SetFlowDiagramBusy(value);InputPanel.IsEnabled=OriginPanel.IsEnabled=CloseButton.IsEnabled=PreviewButton.IsEnabled=!value;ImportButton.IsEnabled=MapPreviewButton.IsEnabled=!value&&validated;}
    private void Map_CancelRequested(object? sender,EventArgs e){if(!busy)Close();}
    internal void ReturnToWindow(){if(!busy)map.StopFlowDiagramOriginPick();Show();Activate();}
    private void Map_ReturnRequested(object? sender,EventArgs e)=>ReturnToWindow();
    private void Window_Closing(object? sender,CancelEventArgs e){if(busy)e.Cancel=true;}
    private void Window_Closed(object? sender,EventArgs e){map.FlowDiagramCancelRequested-=Map_CancelRequested;map.FlowDiagramReturnRequested-=Map_ReturnRequested;map.EndFlowDiagramImport(completionMessage);}
    private void Window_PreviewKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.F1){FlowDiagramGuideWindow.ShowFor(this);e.Handled=true;return;}
        if(e.Key==Key.Escape){if(!busy)Close();e.Handled=true;}
    }
    private void Example_Click(object sender,RoutedEventArgs e){SourceText.Text=ExampleText;}
    private void Guide_Click(object sender,RoutedEventArgs e)=>FlowDiagramGuideWindow.ShowFor(this);
    private void Pick_Click(object sender,RoutedEventArgs e)=>StartMapPick();
    private async void Preview_Click(object sender,RoutedEventArgs e)=>await PreviewAsync();
    private void MapPreview_Click(object sender,RoutedEventArgs e)
    {if(busy||!validated)return;Hide();map.SetFlowDiagramMessage("地图显示尚未保存的流程图预览。Enter 返回窗口确认导入，Esc 取消。");Owner?.Activate();map.Focus();}
    private async void Import_Click(object sender,RoutedEventArgs e)=>await ImportAsync();
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
}
