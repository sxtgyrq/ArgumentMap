using System.ComponentModel;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using ArgumentMap.Rules;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public partial class LogicalNodeWindow : Window
{
    private readonly ILogicalNodeClient client;
    private readonly LogicalNodeSnapshot? original;
    private bool busy;
    private bool ln12DefaultLogic,settingDefaultLogic;
    private bool settingOperandLogic;
    private string CurrentOperandKind => SizeInput.SelectedValue as string=="6" ? OperandKindInput.SelectedValue as string??LogicalNodeOperandRules.Custom : LogicalNodeOperandRules.Custom;
    private readonly bool canEdit;
    private string CurrentValue=>RelationValueInput.Visibility==Visibility.Visible?RelationValueInput.SelectedValue as string??LogicalNodeRelationRules.Equality:
        ValueInput.Visibility==Visibility.Visible?ValueInput.Text:BranchValueInput.SelectedValue as string??LogicalNodeBranchRules.False;
    public event EventHandler? Saved;
    public event EventHandler? MoveRequested;
    public event Action<string, Point>? LocationRequested;
    public event Action<LogicalNodeSnapshot>? BranchSourceLocationRequested;
    public event Action<LogicalNodeSnapshot>? ReferencingBranchLocationRequested;
    public event Action<LogicalNodeSnapshot>? InstanceParentLocationRequested;
    public LogicalNodeWindow(ILogicalNodeClient client,string userName,Point center,LogicalNodeSnapshot? node = null, int sideLength = 6)
    {
        InitializeComponent(); this.client=client; original=node;
        Heading.Text = node is null ? "新建逻辑节点 · LN" : string.IsNullOrEmpty(node.Properties.Value) ? $"LN {node.Code}" : LogicalNodeTextRules.SingleLineDraft(node.Properties.Value);
        Identity.Text = node is null ? "独立 10 位字母编码 · 自动记录全部穿过线段的位置" : $"编码 {node.Code} · 负责人 {node.Owner}";
        XInput.Text = (node?.X ?? (decimal)center.X).ToString("0.0",CultureInfo.InvariantCulture);
        YInput.Text = (node?.Y ?? (decimal)center.Y).ToString("0.0",CultureInfo.InvariantCulture);
        SizeInput.SelectedValue = (node?.SideLength ?? sideLength).ToString(CultureInfo.InvariantCulture);
        ValueInput.Text=LogicalNodeTextRules.SingleLineDraft(node?.Properties.Value ?? (sideLength == 18 ? "开始" : "逻辑节点")); InfoInput.Text=node?.Properties.Info ?? "";
        if((node?.SideLength??sideLength)==4)
        {ValueInput.Visibility=Visibility.Collapsed;BranchValueInput.Visibility=Visibility.Visible;BranchValueInput.SelectedValue=node?.Properties.Value??LogicalNodeBranchRules.False;}
        if((node?.SideLength??sideLength)==8)
        {ValueInput.Visibility=Visibility.Collapsed;RelationValueInput.Visibility=Visibility.Visible;RelationValueInput.SelectedValue=node?.Properties.Value??LogicalNodeRelationRules.Equality;}
        LogicInput.Text=LogicalNodeTextRules.SingleLineDraft(node?.Properties.LogicLanguage ?? "");
        if((node?.SideLength??sideLength)==12)
        {
            LogicInput.ToolTip="不能为空或仅含空白。新增时默认与最终保存的值相同；可以单独修改为不同的表达式，不要求唯一。";
            ln12DefaultLogic=string.IsNullOrWhiteSpace(LogicInput.Text);
            if(ln12DefaultLogic)SetLn12DefaultLogic();
        }
        if((node?.SideLength??sideLength)==6)
        {
            OperandKindPanel.Visibility=Visibility.Visible;
            OperandKindInput.SelectedValue=node?.Properties.OperandKind??LogicalNodeOperandRules.Custom;
            LogicInput.ToolTip="必填且全图 LN6 唯一。Class 自动大写，Instance 自动小写，Custom 保留原文；转换后按实际表达式判重。";
        }
        if((node?.SideLength??sideLength)==10)
        {
            ValueInput.CharacterCasing=CharacterCasing.Lower;LogicInput.IsReadOnly=true;
            LogicInput.Text=LogicalNodeInstanceRules.Expression(LogicalNodeInstanceRules.Normalize(ValueInput.Text));
            LogicInput.ToolTip="自动生成：小写值 ∈ 对应大写 LN12 值。值和逻辑语言均可重复；新建或改属时，对应 LN12 的值必须含英文字母（A–Z／a–z）。";
        }
        if((node?.SideLength??sideLength)==8)
        {
            LogicInput.Text=CurrentValue;LogicInput.IsReadOnly=true;
            LogicInput.ToolTip="LN8 的逻辑语言必须与值一致，选择 =、→、↔、⊇、⊋、⊆ 或 ⊊ 时自动同步，不可单独修改。LN8 不自动推断两端对象或执行逻辑运算。";
        }
        if((node?.SideLength??sideLength)==4)LogicInput.ToolTip="可以留空。切换 True→False 自动在前面加一个 ¬，False→True 去掉最前面的一个 ¬，空逻辑保持为空。P 必须是全地图唯一一个 LN6 的完整逻辑语言，区分大小写。LN6 改名时同步，删除时仅清空逻辑语言。";
        DeleteButton.Visibility=node is null ? Visibility.Collapsed : Visibility.Visible;
        MoveButton.Visibility=node is null ? Visibility.Collapsed : Visibility.Visible;
        canEdit=node?.CanEdit ?? (node is null || node.Owner==userName || userName=="admin");
        SaveButton.IsEnabled=DeleteButton.IsEnabled=MetadataPanel.IsEnabled=canEdit;
        MoveButton.IsEnabled=canEdit && client is ILogicalNodeMoveClient;
        GeometryPanel.IsEnabled=node is null;
        PositionsGrid.ItemsSource=node?.Positions ?? [];
        AssociationStatus.Text=node is null ? "保存时自动计算全部穿过线段的位置，可为 0 个、1 个或多个。" : $"共 {node.Positions.Length} 个位置；线路修改和删除时，在同一事务更新。";
        if(node is not null) Status.Text=canEdit?"坐标通过“移动节点”在地图上调整；边长不变。可修改值、逻辑语言和说明，或删除节点。":"仅负责人或具备逻辑节点管理权限的用户可以修改、移动或删除该节点。";
        if(node is not null&&LogicalNodeTextRules.HasLineBreak(node.Properties))Status.Text+=" 原内容含换行，当前显示草稿已用空格替换，尚未修改数据库；请核对后保存。";
        foreach(var input in new[]{ValueInput,LogicInput})
        {
            DataObject.AddPastingHandler(input,SingleLine_Pasting);
            input.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Enter)e.Handled=true;};
            input.PreviewTextInput+=(_,e)=>{if(LogicalNodeTextRules.HasLineBreak(e.Text)){e.Handled=true;Status.Text=LogicalNodeTextRules.Message;}};
        }
        // Attach after loading the stored value and expression: opening an editor must not alter either.
        BranchValueInput.SelectionChanged+=BranchValue_SelectionChanged;
        RelationValueInput.SelectionChanged+=(_,_)=>{if(RelationValueInput.Visibility==Visibility.Visible)LogicInput.Text=CurrentValue;};
        ValueInput.TextChanged+=(_,_)=>{if(ln12DefaultLogic&&SizeInput.SelectedValue as string=="12")SetLn12DefaultLogic();
            if(SizeInput.SelectedValue as string=="10")LogicInput.Text=LogicalNodeInstanceRules.Expression(LogicalNodeInstanceRules.Normalize(ValueInput.Text));};
        LogicInput.TextChanged+=(_,_)=>{if(!settingDefaultLogic)ln12DefaultLogic=false;NormalizeOperandDraft();UpdateNavigationAvailability();};
        OperandKindInput.SelectionChanged+=(_,_)=>NormalizeOperandDraft();
        BranchValueInput.SelectionChanged+=(_,_)=>UpdateNavigationAvailability();
        UpdateNavigationAvailability();
    }
    private void NormalizeOperandDraft()
    {
        if(settingOperandLogic||SizeInput.SelectedValue as string!="6")return;
        var normalized=LogicalNodeOperandRules.Normalize(LogicInput.Text,CurrentOperandKind);
        if(normalized==LogicInput.Text)return;
        var caret=LogicInput.CaretIndex;var length=LogicInput.SelectionLength;
        settingOperandLogic=true;
        try {LogicInput.Text=normalized;LogicInput.Select(Math.Min(caret,normalized.Length),Math.Min(length,normalized.Length-Math.Min(caret,normalized.Length)));}
        finally {settingOperandLogic=false;}
    }
    private string? BranchSourceLogic()=>BranchValueInput.Visibility!=Visibility.Visible?null:
        LogicalNodeBranchRules.SourceLogic(new("","",0,0,0,4,new(){Value=CurrentValue,LogicLanguage=LogicInput.Text}));
    private void UpdateNavigationAvailability()
    {
        var isInstance=SizeInput.SelectedValue as string=="10";
        LocateInstanceParentButton.Visibility=isInstance?Visibility.Visible:Visibility.Collapsed;
        LocateInstanceParentButton.IsEnabled=!busy&&isInstance&&!string.IsNullOrWhiteSpace(CurrentValue);
        LocateInstanceParentButton.ToolTip="按当前实例值查找对应大写值的 LN12，读取最新位置后定位；按 E 返回，保留尚未保存的编辑内容。";
        LocateBranchSourceButton.Visibility=BranchValueInput.Visibility;
        LocateBranchSourceButton.IsEnabled=!busy&&BranchSourceLogic() is not null;
        LocateBranchSourceButton.ToolTip=string.IsNullOrWhiteSpace(LogicInput.Text)?"逻辑语言为空，未关联 LN6。":
            BranchSourceLogic() is null?"False 的逻辑语言应为 ¬ 加对应 LN6 的完整逻辑语言。":"按当前逻辑语言查找最新的 LN6，定位后按 E 返回，保留尚未保存的编辑内容。";
        ShowReferencingBranchesButton.Visibility=SizeInput.SelectedValue as string=="6"?Visibility.Visible:Visibility.Collapsed;
        ShowReferencingBranchesButton.IsEnabled=!busy&&original?.SideLength==6;
        ShowReferencingBranchesButton.ToolTip=original is null?"先保存 LN6，再查看引用它的 LN4。":"按此 LN6 最新已保存的逻辑语言，列出全部 True 和 False 引用；未保存的编辑不会改变查询对象。";
    }
    private LogicalNodeSnapshot? PopulateReferencingBranches(LogicalNodeSnapshot[] nodes)
    {
        var source=nodes.SingleOrDefault(n=>n.Code==original?.Code&&n.SideLength==6);
        if(source is null||string.IsNullOrWhiteSpace(source.Properties.LogicLanguage))
        {ReferencingBranchesList.ItemsSource=Array.Empty<LogicalNodeSnapshot>();ReferencingBranchesStatus.Text="此 LN6 已不存在或其逻辑语言无效，请刷新。";return null;}
        var logic=source.Properties.LogicLanguage;
        var branches=nodes.Where(n=>n.SideLength==4&&LogicalNodeBranchRules.ValidValue(n.Properties.Value)&&
            string.Equals(LogicalNodeBranchRules.SourceLogic(n),logic,StringComparison.Ordinal))
            .OrderBy(n=>n.Properties.Value==LogicalNodeBranchRules.True?0:1).ThenBy(n=>n.Code,StringComparer.Ordinal).ToArray();
        ReferencingBranchesList.ItemsSource=branches;
        ReferencingBranchesStatus.Text=$"按已保存的逻辑语言“{logic}”查询：共 {branches.Length} 个（True {branches.Count(n=>n.Properties.Value==LogicalNodeBranchRules.True)}，False {branches.Count(n=>n.Properties.Value==LogicalNodeBranchRules.False)}）。"+
            (branches.Length==0?"暂无 LN4 引用。":"点击任一项定位，按 E 返回。")+
            (LogicInput.Text!=logic?" 当前逻辑语言草稿未保存，查询仍以该 LN6 的已保存内容为准。":"");
        return source;
    }
    internal async Task<bool> LoadReferencingBranchesAsync()
    {
        if(busy||original?.SideLength!=6)return false;
        SetBusy(true);ReferencingBranchesPanel.Visibility=Visibility.Visible;ReferencingBranchesList.ItemsSource=Array.Empty<LogicalNodeSnapshot>();ReferencingBranchesStatus.Text="正在读取最新引用…";
        try
        {
            var result=await client.GetLogicalNodesAsync();
            if(!result.Success||result.Data is null){Status.Text=ReferencingBranchesStatus.Text=result.Message;return false;}
            if(PopulateReferencingBranches(result.Data) is null){Status.Text=ReferencingBranchesStatus.Text;return false;}
            ReferencingBranchesPanel.BringIntoView();return true;
        }
        finally{SetBusy(false);}
    }
    internal async Task<bool> LocateReferencingBranchAsync(string code)
    {
        if(busy||original?.SideLength!=6||!ReferencingBranchesList.Items.OfType<LogicalNodeSnapshot>().Any(n=>n.Code==code))return false;
        SetBusy(true);Status.Text="正在核对 LN4 的最新位置和引用关系…";
        try
        {
            var result=await client.GetLogicalNodesAsync();
            if(!result.Success||result.Data is null)
            {ReferencingBranchesList.ItemsSource=Array.Empty<LogicalNodeSnapshot>();Status.Text=ReferencingBranchesStatus.Text=result.Message;return false;}
            if(PopulateReferencingBranches(result.Data) is null){Status.Text=ReferencingBranchesStatus.Text;return false;}
            var target=ReferencingBranchesList.Items.OfType<LogicalNodeSnapshot>().SingleOrDefault(n=>n.Code==code);
            if(target is null){Status.Text="该 LN4 已删除或不再引用此 LN6，引用列表已刷新，请重新选择。";return false;}
            Status.Text=$"已找到引用此 LN6 的 LN4 {target.Code}，定位保留尚未保存的编辑内容。";
            ReferencingBranchLocationRequested?.Invoke(target);return true;
        }
        finally{SetBusy(false);}
    }
    private async void ShowReferencingBranches_Click(object sender,RoutedEventArgs e)=>await LoadReferencingBranchesAsync();
    private async void LocateReferencingBranch_Click(object sender,RoutedEventArgs e)
    {if(sender is Button{Tag:LogicalNodeSnapshot node})await LocateReferencingBranchAsync(node.Code);}
    internal async Task<bool> LocateBranchSourceAsync()
    {
        if(busy||BranchSourceLogic() is not {} source)return false;
        var value=CurrentValue;var expression=LogicInput.Text;SetBusy(true);Status.Text="正在查找关联 LN6…";
        try
        {
            var result=await client.GetLogicalNodesAsync();
            if(!result.Success||result.Data is null){Status.Text=result.Message;return false;}
            if(value!=CurrentValue||expression!=LogicInput.Text){Status.Text="逻辑语言已改变，请重新点击定位关联 LN6。";return false;}
            var targets=result.Data.Where(n=>n.SideLength==6&&string.Equals(n.Properties.LogicLanguage,source,StringComparison.Ordinal)).ToArray();
            if(targets.Length!=1)
            {Status.Text=targets.Length==0?$"找不到逻辑语言为“{source}”的 LN6，目标可能已被修改或删除，请刷新或核对表达式。":$"存在多个逻辑语言为“{source}”的 LN6，无法唯一定位，请先修复重复数据。";return false;}
            Status.Text=$"已找到关联 LN6 {targets[0].Code}，定位保留尚未保存的编辑内容。";
            BranchSourceLocationRequested?.Invoke(targets[0]);return true;
        }
        finally{SetBusy(false);}
    }
    private async void LocateBranchSource_Click(object sender,RoutedEventArgs e)=>await LocateBranchSourceAsync();
    internal async Task<bool> LocateInstanceParentAsync()
    {
        if(busy||SizeInput.SelectedValue as string!="10"||string.IsNullOrWhiteSpace(CurrentValue))return false;
        var value=CurrentValue;var parentValue=LogicalNodeInstanceRules.ParentValue(value);
        SetBusy(true);Status.Text="正在查找所属 LN12…";
        try
        {
            var result=await client.GetLogicalNodesAsync();
            if(!result.Success||result.Data is null){Status.Text=result.Message;return false;}
            if(value!=CurrentValue){Status.Text="实例值已改变，请重新点击定位所属 LN12。";return false;}
            var targets=result.Data.Where(n=>n.SideLength==12&&string.Equals(n.Properties.Value,parentValue,StringComparison.Ordinal)).ToArray();
            if(targets.Length!=1)
            {
                Status.Text=targets.Length==0?$"找不到值为“{parentValue}”的所属 LN12，目标可能已被修改或删除，请刷新或核对实例值。":
                    $"存在多个值为“{parentValue}”的 LN12，无法唯一定位，请先修复重复数据。";
                return false;
            }
            Status.Text=$"已找到所属 LN12 {targets[0].Code}（值“{parentValue}”），定位保留尚未保存的编辑内容。";
            InstanceParentLocationRequested?.Invoke(targets[0]);return true;
        }
        finally{SetBusy(false);}
    }
    private async void LocateInstanceParent_Click(object sender,RoutedEventArgs e)=>await LocateInstanceParentAsync();
    private void SetLn12DefaultLogic()
    {
        settingDefaultLogic=true;
        try{LogicInput.Text=LogicalNodeValueUniqueness.Normalize(ValueInput.Text);}
        finally{settingDefaultLogic=false;}
    }
    private void BranchValue_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {
        if(busy || !canEdit || BranchValueInput.Visibility!=Visibility.Visible || e.OriginalSource!=BranchValueInput)return;
        var previous=e.RemovedItems.OfType<ComboBoxItem>().FirstOrDefault()?.Tag as string;
        var current=BranchValueInput.SelectedValue as string;var logic=LogicInput.Text;
        if(string.IsNullOrWhiteSpace(logic))return;
        if(previous==LogicalNodeBranchRules.True && current==LogicalNodeBranchRules.False)LogicInput.Text="¬"+logic;
        else if(previous==LogicalNodeBranchRules.False && current==LogicalNodeBranchRules.True && logic.StartsWith("¬",StringComparison.Ordinal))LogicInput.Text=logic[1..];
        if(LogicInput.Text.Length>255)Status.Text="切换后的逻辑语言超过 255 个字符，请缩短对应 LN6 的逻辑语言后再保存；不会自动截断表达式。";
    }
    private void Move_Click(object sender, RoutedEventArgs e)
    {
        if (busy || !canEdit || original is null || client is not ILogicalNodeMoveClient) return;
        if (CurrentValue != original.Properties.Value || InfoInput.Text != original.Properties.Info || LogicInput.Text != original.Properties.LogicLanguage || CurrentOperandKind != original.Properties.OperandKind)
        { Status.Text = "值、逻辑语言、作用对象类型或说明尚未保存，请先保存后重新打开节点，再移动。"; return; }
        Close(); MoveRequested?.Invoke(this, EventArgs.Empty);
    }
    private void SingleLine_Pasting(object sender,DataObjectPastingEventArgs e)
    {
        if(e.DataObject.GetData(DataFormats.UnicodeText) is string text&&LogicalNodeTextRules.HasLineBreak(text))
        {e.CancelCommand();Status.Text=LogicalNodeTextRules.Message;}
        else if(e.DataObject.GetData(DataFormats.Text) is string fallback&&LogicalNodeTextRules.HasLineBreak(fallback))
        {e.CancelCommand();Status.Text=LogicalNodeTextRules.Message;}
    }
    private async void Save_Click(object sender,RoutedEventArgs e)
    {
        if(busy || !canEdit)return;
        NormalizeOperandDraft();
        if(original is not null&&client is not ILogicalNodeOperandClient&&CurrentOperandKind!=original.Properties.OperandKind)
        {Status.Text="当前连接不支持保存 OperandKind，请更新 WPF 和 Server。";return;}
        if(LogicalNodeTextRules.HasLineBreak(CurrentValue)||LogicalNodeTextRules.HasLineBreak(LogicInput.Text)){Status.Text=LogicalNodeTextRules.Message;return;}
        if(CurrentValue.Length>32 || InfoInput.Text.Length>255 || LogicInput.Text.Length>255){Status.Text="值最多 32 个字符，说明和逻辑语言各最多 255 个字符。";return;}
        if(SizeInput.SelectedValue as string=="12"&&string.IsNullOrWhiteSpace(LogicInput.Text)){Status.Text="边长为 12 的 LN 的逻辑语言不能为空或仅含空白。";return;}
        if(original is not null && client is not ILogicalNodeLogicClient && LogicInput.Text!=original.Properties.LogicLanguage)
        {Status.Text="当前连接不支持保存逻辑语言，请升级客户端。";return;}
        decimal x=0,y=0; var size=int.Parse(((ComboBoxItem)SizeInput.SelectedItem).Tag.ToString()!,CultureInfo.InvariantCulture);
        if(original is null)
        {
            if(!decimal.TryParse(XInput.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out x) || !decimal.TryParse(YInput.Text,NumberStyles.Number,CultureInfo.InvariantCulture,out y))
            {Status.Text="请输入整数＋0.5 的中心坐标。";return;}
            if(LogicalNodeGeometryRules.ValidateShape(x,y,size) is {} violation){Status.Text=violation.Message;return;}
        }
        SetBusy(true);
        try
        {
            var result=original is null ? await client.CreateLogicalNodeAsync(x,y,size,new(){Value=CurrentValue,Info=InfoInput.Text,LogicLanguage=size==12&&ln12DefaultLogic?"":LogicInput.Text,OperandKind=CurrentOperandKind}) :
                size==6&&client is ILogicalNodeOperandClient operandClient ? await operandClient.UpdateLogicalNodeAsync(original.Code,original.Version,CurrentValue,InfoInput.Text,LogicInput.Text,CurrentOperandKind) :
                client is ILogicalNodeLogicClient logicClient ? await logicClient.UpdateLogicalNodeAsync(original.Code,original.Version,CurrentValue,InfoInput.Text,LogicInput.Text) :
                await client.UpdateLogicalNodeAsync(original.Code,original.Version,CurrentValue,InfoInput.Text);
            ShowSaveResult(result);
            if(!result.Success)return;
            SetBusy(false);Saved?.Invoke(this,EventArgs.Empty);Close();
        }
        finally {SetBusy(false);}
    }
    private void ShowSaveResult(ServiceResult<LogicalNodeSnapshot> result)
    {
        Status.Text=result.Message;
        if(result.Success || result.Code is not ("DuplicateLogicalNodeValue" or "DuplicateLogicalNodeLogic"))return;
        // The current HTTP contract returns an ordinary message. Recognize only the
        // server's coordinate suffix; never interpret values or messages as XAML/URLs.
        var match=Regex.Match(result.Message,@"已被 LN (?<code>[A-Z]{10})(?<location>（中心坐标 X=(?<x>[0-9]+(?:\.[0-9]+)?), Y=(?<y>[0-9]+(?:\.[0-9]+)?)）)使用，请使用不同的(?:值|逻辑语言)。$",RegexOptions.CultureInvariant);
        if(!match.Success || !decimal.TryParse(match.Groups["x"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var x) ||
            !decimal.TryParse(match.Groups["y"].Value,NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var y) ||
            x<0 || x>500000 || y<0 || y>500000)return;
        var location=match.Groups["location"];
        var link=new Hyperlink(new Run(location.Value))
        {
            Foreground=Brushes.RoyalBlue,TextDecorations=TextDecorations.Underline,Cursor=Cursors.Hand,
            ToolTip="在地图中定位此 LN，保留尚未保存的编辑内容"
        };
        link.Click+=(_,_)=> {if(!busy)LocationRequested?.Invoke(match.Groups["code"].Value,new Point((double)x,(double)y));};
        Status.Inlines.Clear();
        Status.Inlines.Add(new Run(result.Message[..location.Index]));
        Status.Inlines.Add(link);
        Status.Inlines.Add(new Run(result.Message[(location.Index+location.Length)..]));
    }
    private async void Delete_Click(object sender,RoutedEventArgs e)
    {
        if(busy || !canEdit || original is null)return;
        if(MessageBox.Show(this,$"删除逻辑节点 {original.Code}？","删除逻辑节点",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        SetBusy(true);
        try {var result=await client.DeleteLogicalNodeAsync(original.Code,original.Version);Status.Text=result.Message;if(!result.Success)return;SetBusy(false);Saved?.Invoke(this,EventArgs.Empty);Close();}
        finally {SetBusy(false);}
    }
    private void SetBusy(bool value){busy=value;EditablePanel.IsEnabled=CancelButton.IsEnabled=!value;SaveButton.IsEnabled=DeleteButton.IsEnabled=!value && canEdit;MoveButton.IsEnabled=!value && canEdit && client is ILogicalNodeMoveClient;UpdateNavigationAvailability();}
    private void Close_Click(object sender,RoutedEventArgs e){if(!busy)Close();}
    private void Window_Closing(object? sender,CancelEventArgs e){if(busy)e.Cancel=true;}
}
