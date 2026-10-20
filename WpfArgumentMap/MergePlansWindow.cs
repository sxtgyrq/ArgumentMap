using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public sealed class MergePlansWindow : Window
{
    public event Action<MergePlan>? PlanSelected;
    private readonly ListBox list;
    private readonly Button apply;
    private readonly Button cancel;
    private readonly TextBlock status;
    private readonly MergePlanCanvas canvas;
    private bool busy;

    public MergePlansWindow(MergePreview preview,Func<MergePlan,Task<(bool Success,string Message)>> save)
    {
        Title="合并线路 · 选择方案";Width=820;Height=660;MinWidth=600;MinHeight=480;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=new SolidColorBrush(Color.FromRgb(245,248,252));FontFamily=new FontFamily("Segoe UI, Microsoft YaHei");
        NameScope.SetNameScope(this,new NameScope());
        var grid=new Grid{Margin=new Thickness(20)};
        foreach(var height in new[]{GridLength.Auto,new GridLength(210),new GridLength(1,GridUnitType.Star),GridLength.Auto,GridLength.Auto})grid.RowDefinitions.Add(new(){Height=height});
        var heading=new TextBlock{Text=$"{preview.First.Code} → {preview.Second.Code}\n按方向列出最少移动方案 · 优先 135° · 90° 备选",FontSize=18,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,12)};grid.Children.Add(heading);
        list=new ListBox{Name="PlanList",Margin=new Thickness(0,0,0,12)};RegisterName(list.Name,list);Grid.SetRow(list,1);grid.Children.Add(list);
        foreach(var plan in preview.Plans)
        {
            var direction=(plan.DirectionX,plan.DirectionY) switch{(1,0)=>"向右",(1,1)=>"右上",(0,1)=>"向上",(-1,1)=>"左上",(-1,0)=>"向左",(-1,-1)=>"左下",(0,-1)=>"向下",_=>"右下"};
            list.Items.Add(new ListBoxItem{Tag=plan,Content=$"{direction} ({plan.DirectionX}, {plan.DirectionY}) · 接角 {plan.FirstCorner}° / {plan.SecondCorner}° · 调整 {plan.AdjustedGridSteps} 格\n第一条末点 ({plan.FirstX}, {plan.FirstY}) → 第二条起点 ({plan.SecondX}, {plan.SecondY})",Padding=new Thickness(10,6,10,6)});
        }
        canvas=new MergePlanCanvas(preview){Name="PlanPreview",Margin=new Thickness(0,0,0,10)};RegisterName(canvas.Name,canvas);Grid.SetRow(canvas,2);grid.Children.Add(canvas);
        status=new TextBlock{Name="Status",Text="灰虚线：原线路 · 绿线：合并方案 · 橙点：调整点。确认时会再次校验最新数据库数据。",TextWrapping=TextWrapping.Wrap,Foreground=Brushes.SlateGray,Margin=new Thickness(0,4,0,10)};RegisterName(status.Name,status);Grid.SetRow(status,3);grid.Children.Add(status);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};Grid.SetRow(actions,4);grid.Children.Add(actions);
        cancel=new Button{Name="CancelButton",Content="取消",Padding=new Thickness(18,8,18,8),Margin=new Thickness(0,0,10,0)};
        apply=new Button{Name="ApplyButton",Content="确认合并",Padding=new Thickness(18,8,18,8),Background=new SolidColorBrush(Color.FromRgb(37,99,235)),Foreground=Brushes.White,IsEnabled=preview.Plans.Length>0};
        RegisterName(cancel.Name,cancel);RegisterName(apply.Name,apply);actions.Children.Add(cancel);actions.Children.Add(apply);Content=grid;
        list.SelectionChanged+=(_,_)=>{if(list.SelectedItem is ListBoxItem{Tag:MergePlan plan}){canvas.Plan=plan;PlanSelected?.Invoke(plan);}};
        cancel.Click+=(_,_)=>Close();
        apply.Click+=async(_,_)=>
        {
            if(busy||list.SelectedItem is not ListBoxItem{Tag:MergePlan plan})return;
            busy=true;list.IsEnabled=apply.IsEnabled=cancel.IsEnabled=false;status.Text="正在校验并提交合并…";
            try
            {
                var result=await save(plan);status.Text=result.Message;
                if(result.Success){busy=false;if(IsVisible)DialogResult=true;else Close();}
            }
            finally{busy=false;list.IsEnabled=apply.IsEnabled=cancel.IsEnabled=true;}
        };
        Closing+=(_,e)=>{if(busy)e.Cancel=true;};
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape&&!busy){Close();e.Handled=true;}};
        if(list.Items.Count>0)list.SelectedIndex=0;
    }

    private sealed class MergePlanCanvas(MergePreview preview):FrameworkElement
    {
        private MergePlan? plan;
        internal MergePlan? Plan{get=>plan;set{plan=value;InvalidateVisual();}}
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRoundedRectangle(Brushes.White,new Pen(Brushes.LightSteelBlue,1),new Rect(RenderSize),8,8);
            if(plan is null||ActualWidth<30||ActualHeight<30)return;
            var points=preview.First.Nodes.Concat(preview.Second.Nodes).Select(n=>new Point(n.X,n.Y)).ToArray();
            points[preview.First.Nodes.Length-1]=new(plan.FirstX,plan.FirstY);points[preview.First.Nodes.Length]=new(plan.SecondX,plan.SecondY);
            var all=points.Concat(preview.First.Nodes.Concat(preview.Second.Nodes).Select(n=>new Point(n.X,n.Y))).ToArray();
            double left=all.Min(p=>p.X),right=all.Max(p=>p.X),bottom=all.Min(p=>p.Y),top=all.Max(p=>p.Y);
            var scale=Math.Min((ActualWidth-60)/Math.Max(1,right-left),(ActualHeight-60)/Math.Max(1,top-bottom));
            Point Screen(Point p)=>new(ActualWidth/2+(p.X-(left+right)/2)*scale,ActualHeight/2-(p.Y-(bottom+top)/2)*scale);
            var oldPen=new Pen(Brushes.SlateGray,1.5){DashStyle=DashStyles.Dash};var newPen=new Pen(Brushes.SeaGreen,2.5);
            foreach(var road in new[]{preview.First,preview.Second})for(var i=1;i<road.Nodes.Length;i++)dc.DrawLine(oldPen,Screen(new(road.Nodes[i-1].X,road.Nodes[i-1].Y)),Screen(new(road.Nodes[i].X,road.Nodes[i].Y)));
            for(var i=1;i<points.Length;i++)dc.DrawLine(newPen,Screen(points[i-1]),Screen(points[i]));
            foreach(var p in new[]{points[preview.First.Nodes.Length-1],points[preview.First.Nodes.Length]})dc.DrawEllipse(Brushes.White,new Pen(Brushes.DarkOrange,2),Screen(p),5,5);
            dc.DrawText(new FormattedText($"连接 {plan.DirectionX}, {plan.DirectionY}   接角 {plan.FirstCorner}° / {plan.SecondCorner}°",CultureInfo.CurrentCulture,FlowDirection.LeftToRight,new Typeface("Segoe UI"),12,Brushes.SlateGray,VisualTreeHelper.GetDpi(this).PixelsPerDip),new Point(12,10));
        }
    }
}
