using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

/// <summary>Choose the square before placing its center on the map.</summary>
public sealed class LogicalNodeSizeWindow : Window
{
    public event Action<int>? SizeSelected;
    public event Action<LogicalNodeSnapshot>? InstanceSelected;
    public LogicalNodeSizeWindow(bool hasStart,IEnumerable<LogicalNodeSnapshot>? parents=null)
    {
        Title = "LN · 选择边长"; Width = 380; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(243,246,251));
        NameScope.SetNameScope(this,new NameScope());
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "选择边长，然后在地图上落点", FontSize = 18, FontWeight = FontWeights.SemiBold });
        var eligible=(parents??[]).Where(LogicalNodeInstanceRules.CanInstantiate).ToArray();
        var instancePanel=new StackPanel{Visibility=Visibility.Collapsed,Margin=new Thickness(0,12,0,0)};
        var parentInput=new ComboBox{Name="InstanceParentInput",ItemsSource=eligible,DisplayMemberPath="Properties.Value",SelectedIndex=eligible.Length>0?0:-1,Margin=new Thickness(0,8,0,8)};
        RegisterName(parentInput.Name,parentInput);
        instancePanel.Children.Add(new TextBlock{Text=eligible.Length==0?"没有可选父节点。请先创建值中含英文字母的 LN12，或 F4 刷新后重试。":"选择所属 LN12：仅显示值中含英文字母的项，如 H人、HUMAN；全中文项不显示。",TextWrapping=TextWrapping.Wrap});
        instancePanel.Children.Add(parentInput);
        var confirm=new Button{Name="ConfirmInstanceButton",Content="在地图上放置此实例",IsEnabled=eligible.Length>0,Padding=new Thickness(12,8,12,8)};
        RegisterName(confirm.Name,confirm);confirm.Click+=(_,_)=>{if(parentInput.SelectedItem is LogicalNodeSnapshot parent){InstanceSelected?.Invoke(parent);Close();}};
        instancePanel.Children.Add(confirm);
        foreach (var size in new[] { 4, 6, 8, 10, 12, 18 })
        {
            var button = new Button { Name = $"Size{size}Button", Content = size == 18 ? "18 · 开始（全图唯一）" : size == 8 ? "8 · 等同／蕴含／等价／集合关系" : size==10?"10 · 实例（先选择所属 LN12）":size.ToString(),
                Margin = new Thickness(0,12,0,0), Padding = new Thickness(12,10,12,10), IsEnabled = size != 18 || !hasStart };
            if (!button.IsEnabled) button.ToolTip = "已有开始节点，请先删除后重新放置。";
            RegisterName(button.Name,button);
            button.Click += (_,_) => {if(size==10){instancePanel.Visibility=Visibility.Visible;return;} SizeSelected?.Invoke(size); Close(); };
            panel.Children.Add(button);
        }
        panel.Children.Add(instancePanel);
        panel.Children.Add(new TextBlock { Text = hasStart ? "已有开始节点；4、6、8、10 和 12 仍可新增。" : "4 表示是／否；8 表示等同／蕴含／等价／集合关系；10 表示实例，先选所属 LN12；18 代表开始。中心自动吸附为整数＋0.5。",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,16,0,0) });
        PreviewKeyDown += (_,e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Content = panel;
    }
}
