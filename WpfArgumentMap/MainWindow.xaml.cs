using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;
using ArgumentMap.Contracts;
using ArgumentMap.Client;

namespace WpfArgumentMap;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly HttpLineClient? signedClient;
    private readonly ILoginClient? loginClient;
    private bool propertyDialogOpen;
    private ShortcutWindow? shortcutWindow;
    private LogicalNodeWindow? logicalNodeWindow;
    private LogicalNodeSizeWindow? logicalNodeSizeWindow;
    private bool openingLogicalNode;
    private FlowDiagramImportWindow? flowDiagramWindow;
    internal Action<string> WriteLn4Clipboard {get;set;}=Clipboard.SetText;
    internal Func<string?> ReadLn4Clipboard {get;set;}=()=>Clipboard.ContainsText()?Clipboard.GetText():null;
    internal bool HandleLogicalNodeProtectionShortcut(Key key, ModifierKeys modifiers, bool repeat, IInputElement? focus)
    {
        if (key != Key.G || modifiers != ModifierKeys.None) return false;
        for (var current = focus as DependencyObject; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return false;
        if (MainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen)) return false;
        if (!repeat) { MapView.SetShowLogicalNodeProtection(!MapView.ShowLogicalNodeProtection); MapView.Focus(); }
        return true;
    }
    internal bool HandleUndoShortcut(Key key, ModifierKeys modifiers, bool repeat, IInputElement? focus)
    {
        if (modifiers != ModifierKeys.Control || key != Key.Z) return false;
        for (var current = focus as DependencyObject; current is not null; current = current is Visual ? VisualTreeHelper.GetParent(current) : LogicalTreeHelper.GetParent(current))
            if (current is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox) return false;
        if (MainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen)) return false;
        if (!repeat) _ = UndoMapAsync();
        return true;
    }
    private async Task<bool> UndoMapAsync()
    {
        if (propertyDialogOpen || logicalNodeWindow is not null || logicalNodeSizeWindow is not null || flowDiagramWindow is not null)
        { MapView.ClipboardNotice("请先保存并关闭或取消当前属性／导入窗口，再撤销数据库操作；窗口中的内容已保留。"); return false; }
        var success = await MapView.UndoDatabaseAsync(); MapView.Focus(); return success;
    }
    private async void UndoMap_Click(object sender, RoutedEventArgs e) => await UndoMapAsync();
    internal bool HandleLn4ClipboardShortcut(Key key,ModifierKeys modifiers,bool repeat,IInputElement? focus)
    {
        if(modifiers!=ModifierKeys.Control||key is not (Key.C or Key.V))return false;
        for(var current=focus as DependencyObject;current is not null;current=current is Visual?VisualTreeHelper.GetParent(current):LogicalTreeHelper.GetParent(current))
            if(current is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox)return false;
        if(MainMenu.Items.OfType<MenuItem>().Any(item=>item.IsSubmenuOpen))return false;
        if(!repeat){if(key==Key.C)CopyLn4();else PasteLn4();}
        return true;
    }
    private void CopyLn4()
    {
        if(!MapView.TryCopySelection(out var text))return;
        try{WriteLn4Clipboard(text);MapView.ClipboardNotice("已复制：Ctrl+V 后用鼠标选择落点，左键确认。");}
        catch(System.Runtime.InteropServices.ExternalException){MapView.ClipboardNotice("剪贴板暂时被占用，请重试 Ctrl+C。");}
    }
    private void PasteLn4()
    {
        try{if(MapView.BeginPasteSelection(ReadLn4Clipboard()))MapView.Focus();}
        catch(System.Runtime.InteropServices.ExternalException){MapView.ClipboardNotice("剪贴板暂时被占用，请重试 Ctrl+V。");}
    }
    private void CopyLn4_Click(object sender,RoutedEventArgs e){CopyLn4();MapView.Focus();}
    private void PasteLn4_Click(object sender,RoutedEventArgs e)=>PasteLn4();
    public MainWindow() : this(null) { }

    public MainWindow(ILineClient? client, ILoginClient? loginClient = null)
    {
        InitializeComponent();
        MapView.SplitLineRequested+=async(_,_)=>await SplitLineWithNoticeAsync();
        MapView.MergeLinesRequested+=async(_,_)=>await ShowMergePlansAsync();
        signedClient = client as HttpLineClient;
        this.loginClient = loginClient;
        AccountStatus.Text = signedClient is null ? "未登录" : $"用户：{signedClient.Session.UserName}";
        ReloginButton.IsEnabled = signedClient is not null && loginClient is not null;
        if (client is not null) MapView.ConfigureClient(client);
        MapView.ViewChanged += (_, _) => UpdateStatus();
        MapView.LinePropertiesRequested += async (_, _) => await EditLinePropertiesAsync();
        MapView.LogicalNodeWindowRequested += async (_, args) => await OpenLogicalNodeAsync(args);
        MapView.LogicalNodeSizeRequested += (_, _) => OpenLogicalNodeSizes();
        MapView.LogicalNodeConnectionChoicesRequested += async (_,_) =>
        {
            var choice=new LogicalNodeConnectionWindow(MapView.ConnectionSourceTitle,MapView.ConnectionTargetTitle){Owner=this};
            if(choice.ShowDialog()==true)await MapView.PreviewLogicalNodeConnectionAsync(choice.Kind);else MapView.CancelLogicalNodeConnection();
        };
        Closed += (_, _) => { MapView.StopCrossCalculation();MapView.StopLogicalNodeCalculation();logicalNodeWindow?.Close(); logicalNodeSizeWindow?.Close(); flowDiagramWindow?.Close(); };
        UpdateStatus();
    }

    public async Task<bool> InitializeLinesAsync()
    {
        if (!await MapView.RefreshLinesAsync()) return false;
        return await MapView.RefreshLogicalNodesAsync();
    }
    private void ImportFlowDiagram_Click(object sender,RoutedEventArgs e)
    {
        if(flowDiagramWindow is not null){flowDiagramWindow.ReturnToWindow();return;}
        if(signedClient is null||MapView.IsSaving||!MapView.BeginFlowDiagramImport())return;
        var window=new FlowDiagramImportWindow(signedClient,MapView){Owner=this};flowDiagramWindow=window;
        window.Closed+=(_,_)=>{flowDiagramWindow=null;MapView.Focus();};window.Show();
    }
    private void FlowDiagramGuide_Click(object sender,RoutedEventArgs e)=>FlowDiagramGuideWindow.ShowFor(this);

    private async Task OpenLogicalNodeAsync(LogicalNodeWindowRequestEventArgs args)
    {
        if (args.Node is null) return; // Creation is an immediate save; only editing opens a window.
        if (logicalNodeWindow is not null) { logicalNodeWindow.WindowState=WindowState.Normal; logicalNodeWindow.Activate(); return; }
        if (openingLogicalNode || signedClient is null || MapView.IsSaving) return;
        openingLogicalNode = true;
        try
        {
            var node = args.Node;
            if (node is not null)
            {
                var nodes = await signedClient.GetLogicalNodesAsync();
                if (!nodes.Success || nodes.Data is null) { MessageBox.Show(this, nodes.Message, "无法加载逻辑节点"); return; }
                var selectedCode = node.Code;
                node = nodes.Data.FirstOrDefault(n => n.Code == selectedCode);
                if (node is null) { await MapView.RefreshLogicalNodesAsync(); return; }
            }
            logicalNodeSizeWindow?.Close();
            var window = new LogicalNodeWindow(signedClient, signedClient.Session.UserName, args.Center, node, args.SideLength) { Owner = this };
            logicalNodeWindow = window;
            window.Saved += async (_, _) => await MapView.RefreshLogicalNodesAsync();
            window.MoveRequested += (_, _) => { if (node is not null) MapView.BeginLogicalNodeMove(node); };
            void Locate(string code,Point center,LogicalNodeSnapshot? refreshedNode=null)
            {
                if(!MapView.LocateLogicalNode(code,center,refreshedNode))return;
                window.WindowState=WindowState.Minimized;
                Activate();MapView.Focus();
            }
            window.LocationRequested += (code,center)=>Locate(code,center);
            window.BranchSourceLocationRequested += target=>Locate(target.Code,new Point((double)target.X,(double)target.Y),target);
            window.ReferencingBranchLocationRequested += target=>Locate(target.Code,new Point((double)target.X,(double)target.Y),target);
            window.InstanceParentLocationRequested += target=>Locate(target.Code,new Point((double)target.X,(double)target.Y),target);
            window.Closed += (_, _) => { logicalNodeWindow = null; MapView.Focus(); };
            window.Show();
        }
        finally { openingLogicalNode = false; }
    }

    private void OpenLogicalNodeSizes()
    {
        if (logicalNodeWindow is not null) { logicalNodeWindow.Activate(); return; }
        if (logicalNodeSizeWindow is not null) { logicalNodeSizeWindow.Activate(); return; }
        logicalNodeSizeWindow = new LogicalNodeSizeWindow(MapView.HasStartLogicalNode,MapView.InstanceParents) { Owner = this };
        logicalNodeSizeWindow.SizeSelected += size => MapView.BeginLogicalNodePlacement(size);
        logicalNodeSizeWindow.InstanceSelected += parent=>MapView.BeginInstancePlacement(parent);
        logicalNodeSizeWindow.Closed += (_,_) => { logicalNodeSizeWindow = null; MapView.Focus(); };
        logicalNodeSizeWindow.Show();
    }
    private void LogicalNodeMode_Click(object sender, RoutedEventArgs e) => MapView.RequestLogicalNodePlacement();
    private void ConnectLogicalNodes_Click(object sender,RoutedEventArgs e)
    {
        if(logicalNodeWindow is not null||logicalNodeSizeWindow is not null||flowDiagramWindow is not null||propertyDialogOpen)
        {MapView.ClipboardNotice("请先完成或关闭当前属性、落点或导入窗口，再连接两个 LN。");return;}
        MapView.BeginLogicalNodeConnection();MapView.Focus();
    }
    private async void ConfirmLogicalNodeConnection_Click(object sender,RoutedEventArgs e)=>await MapView.SaveLogicalNodeConnectionAsync();
    private async void NewLogicalNode_Click(object sender, RoutedEventArgs e) { await MapView.AddLogicalNodeAsync(); MapView.Focus(); }
    private void SelectLogicalNode_Click(object sender, RoutedEventArgs e) { MapView.ToggleLogicalNodeSelection(); MapView.Focus(); }
    private void EditLogicalNode_Click(object sender, RoutedEventArgs e) => MapView.RequestLogicalNodeEdit();
    private async void RefreshLogicalNodes_Click(object sender, RoutedEventArgs e) { await MapView.RefreshLogicalNodesAsync(); MapView.Focus(); }
    private void FocusLogicalNode_Click(object sender,RoutedEventArgs e)
    {
        if(MapView.IsSaving)return;
        MapView.FocusLogicalNodes();
        ArrowMenu.IsChecked=MapView.ShowArrows;MapView.Focus();
    }
    private async void CalculateLogicalNodes_Click(object sender, RoutedEventArgs e) { MapView.Focus(); await CalculateLogicalNodesWithNoticeAsync(); }
    private async Task CalculateLogicalNodesWithNoticeAsync()
    {
        if (MapView.IsSaving || MapView.IsCalculatingLogicalNodes) return;
        await MapView.CalculateLogicalNodesAsync();
    }

    private async void Relogin_Click(object sender, RoutedEventArgs e)
    {
        if (MapView.IsSaving || signedClient is null || loginClient is null) return;
        var login = new LoginWindow(loginClient, signedClient.Session.UserName, fixedUser: true) { Owner = this };
        if (login.ShowDialog() != true || login.Session is null) return;
        signedClient.UpdateSession(login.Session);
        await InitializeLinesAsync();
        MapView.Focus();
    }

    private void UpdateStatus()
    {
        var center = MapView.Center;
        var pointer = MapView.PointerPosition;
        var pointerText = pointer.HasValue ? $"鼠标 ({pointer.Value.X:N2}, {pointer.Value.Y:N2})" : "鼠标 —";
        ViewStatus.Text = $"范围 0–500000 × 0–500000    中心 ({center.X:N2}, {center.Y:N2})    " +
            $"缩放 {MapView.Scale:P1}    主刻度 {MapView.GridStep:N0}    {pointerText}";
        EditingStatus.Text = $"{MapView.EditingStatus} · {MapView.LineCount} 条线 · {MapView.CrossCount} 个交点关系 · {MapView.LogicalNodeCount} 个 LN    {MapView.OperationMessage}";
        CrossCalculationStatus.Text=MapView.CrossCalculationMessage;
        CrossCalculationStatus.Visibility=string.IsNullOrEmpty(MapView.CrossCalculationMessage)?Visibility.Collapsed:Visibility.Visible;
        LogicalNodeCalculationStatus.Text=MapView.LogicalNodeCalculationMessage;
        LogicalNodeCalculationStatus.Visibility=string.IsNullOrEmpty(MapView.LogicalNodeCalculationMessage)?Visibility.Collapsed:Visibility.Visible;
        LockLineButton.Content = MapView.SelectedLineIsLocked ? "解锁  L" : "锁定  L";
        ConfirmLogicalNodeConnectionButton.Visibility=MapView.HasConnectionPreview?Visibility.Visible:Visibility.Collapsed;
        ConfirmLogicalNodeConnectionButton.IsEnabled=!MapView.IsSaving;
        LockLineMenu.IsChecked = MapView.SelectedLineIsLocked;
        AdditionEndButton.Content = MapView.AddFromStart ? "前端加点  5" : "末端加点  5";
        AdditionEndMenu.IsChecked = MapView.AddFromStart;
        EditNodeButton.Content = MapView.IsEditingLineNode ? "确认节点  E" : "编辑节点  E";
        EditNodeMenu.Header = MapView.IsEditingLineNode ? "确认节点位置" : "编辑最近节点的位置";
        ArrowToggle.IsChecked = MapView.ShowArrows;
        ArrowMenu.IsChecked = MapView.ShowArrows;
        LogicalNodeLabelsToggle.IsChecked=MapView.ShowLogicalNodeLabels;
        LogicalNodeLabelsMenu.IsChecked=MapView.ShowLogicalNodeLabels;
        LogicalNodeProtectionToggle.IsChecked=MapView.ShowLogicalNodeProtection;
        LogicalNodeProtectionMenu.IsChecked=MapView.ShowLogicalNodeProtection;
        LineSpeedsToggle.IsChecked=MapView.ShowLineSpeeds;
        LineSpeedsMenu.IsChecked=MapView.ShowLineSpeeds;
        ConnectedCrossesToggle.IsChecked=ConnectedCrossesMenu.IsChecked=MapView.ShowConnectedCrosses;
        DisconnectedCrossesToggle.IsChecked=DisconnectedCrossesMenu.IsChecked=MapView.ShowDisconnectedCrosses;
        UnsetCrossesToggle.IsChecked=UnsetCrossesMenu.IsChecked=MapView.ShowUnsetCrosses;
        CrossExitLineStatus.Text = MapView.CrossExitLineCode is { } code ? $"出口线路：{code} · U 选交点 →【 单向换路" : "出口线路：未选择（F8 选线 → Enter）";
    }

    public bool IsToolPanelExpanded => ToolPanel.Visibility == Visibility.Visible;

    public void SetToolPanelExpanded(bool expanded)
    {
        ToolPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        ToolbarMenu.IsChecked = expanded;
        PanelToggleButton.Content = expanded ? "⌃ 收起工具栏" : "⌄ 展开全部";
        MapView.Focus();
    }

    private void TogglePanel_Click(object sender, RoutedEventArgs e) => SetToolPanelExpanded(!IsToolPanelExpanded);

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Keep the map usable in a short window; wrapped groups remain accessible by scrolling.
        if (ToolPanel is not null) ToolPanel.MaxHeight = Math.Max(110, e.NewSize.Height * 0.3);
    }

    private void Shortcuts_Click(object sender, RoutedEventArgs e) => ShowShortcuts();

    private void ShowShortcuts()
    {
        if (shortcutWindow is not null) { shortcutWindow.Activate(); return; }
        shortcutWindow = new ShortcutWindow { Owner = this };
        shortcutWindow.Closed += (_, _) => shortcutWindow = null;
        shortcutWindow.Show();
    }

    private void ArrowMenu_Click(object sender, RoutedEventArgs e)
    {
        MapView.SetShowArrows(ArrowMenu.IsChecked);
        MapView.Focus();
    }
    private void LogicalNodeLabelsMenu_Click(object sender,RoutedEventArgs e)
    {
        MapView.SetShowLogicalNodeLabels(LogicalNodeLabelsMenu.IsChecked);MapView.Focus();
    }
    private void LineSpeedsMenu_Click(object sender,RoutedEventArgs e)
    {
        MapView.SetShowLineSpeeds(LineSpeedsMenu.IsChecked);MapView.Focus();
    }
    private void LogicalNodeProtectionMenu_Click(object sender, RoutedEventArgs e)
    { MapView.SetShowLogicalNodeProtection(LogicalNodeProtectionMenu.IsChecked); MapView.Focus(); }
    private void ConnectedCrossesMenu_Click(object sender,RoutedEventArgs e)
    {MapView.SetCrossVisibility(ConnectedCrossesMenu.IsChecked,MapView.ShowDisconnectedCrosses,MapView.ShowUnsetCrosses);MapView.Focus();}
    private void DisconnectedCrossesMenu_Click(object sender,RoutedEventArgs e)
    {MapView.SetCrossVisibility(MapView.ShowConnectedCrosses,DisconnectedCrossesMenu.IsChecked,MapView.ShowUnsetCrosses);MapView.Focus();}
    private void UnsetCrossesMenu_Click(object sender,RoutedEventArgs e)
    {MapView.SetCrossVisibility(MapView.ShowConnectedCrosses,MapView.ShowDisconnectedCrosses,UnsetCrossesMenu.IsChecked);MapView.Focus();}

    private async void RefreshLines_Click(object sender, RoutedEventArgs e) { await MapView.RefreshLinesAsync(); MapView.Focus(); }
    private async void AdditionEnd_Click(object sender, RoutedEventArgs e) { await MapView.HandleEditorKeyAsync(Key.D5); MapView.Focus(); }
    private async void EditNode_Click(object sender,RoutedEventArgs e){await MapView.EditLineNodeAsync();MapView.Focus();}
    private async void RemoveNode_Click(object sender,RoutedEventArgs e){await MapView.RemoveLineNodeAsync();MapView.Focus();}
    private async void CycleCross_Click(object sender, RoutedEventArgs e) { await MapView.HandleEditorKeyAsync(Key.Tab); MapView.Focus(); }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => MapView.ZoomAtCenter(1.25);
    private void AreaMove_Click(object sender,RoutedEventArgs e){MapView.BeginAreaMove();MapView.Focus();}
    private async void AreaDelete_Click(object sender,RoutedEventArgs e){await MapView.DeleteAreaSelectionAsync();MapView.Focus();}
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => MapView.ZoomAtCenter(1 / 1.25);
    private void ResetView_Click(object sender, RoutedEventArgs e) => MapView.ResetView();
    private void FitView_Click(object sender, RoutedEventArgs e) => MapView.FitView();
    private async void NewLine_Click(object sender, RoutedEventArgs e) { await MapView.NewLineAsync(); MapView.Focus(); }
    private async void BeginDrawing_Click(object sender, RoutedEventArgs e) { await MapView.BeginDrawingAsync(); MapView.Focus(); }
    private async void CompleteLine_Click(object sender, RoutedEventArgs e) { await SaveLineAsync(); MapView.Focus(); }
    private void CancelEditing_Click(object sender, RoutedEventArgs e) { MapView.CancelEditing(); MapView.Focus(); }
    private void SelectLine_Click(object sender, RoutedEventArgs e) { MapView.ToggleLineSelection(); MapView.Focus(); }
    private async void EditLine_Click(object sender, RoutedEventArgs e) { await MapView.EditSelectedLineAsync(); MapView.Focus(); }
    private async void LineProperties_Click(object sender, RoutedEventArgs e) { if (MapView.IsLogicalNodeMode) MapView.RequestLogicalNodeEdit(); else await EditLinePropertiesAsync(); MapView.Focus(); }
    private async void LockLine_Click(object sender, RoutedEventArgs e) { await MapView.ToggleSelectedLineLockAsync(); MapView.Focus(); }
    private async void ReverseLine_Click(object sender, RoutedEventArgs e) { await ReverseLineWithNoticeAsync(); MapView.Focus(); }
    private async void SplitLine_Click(object sender,RoutedEventArgs e){await SplitLineWithNoticeAsync();MapView.Focus();}
    private async void MergeLines_Click(object sender,RoutedEventArgs e){await ShowMergePlansAsync();MapView.Focus();}
    private void ArrowToggle_Changed(object sender, RoutedEventArgs e) { if (MapView is not null) MapView.SetShowArrows(ArrowToggle.IsChecked == true); }
    private void LogicalNodeLabelsToggle_Changed(object sender,RoutedEventArgs e) { if(MapView is not null)MapView.SetShowLogicalNodeLabels(LogicalNodeLabelsToggle.IsChecked==true); }
    private void LogicalNodeProtectionToggle_Changed(object sender, RoutedEventArgs e) { if(MapView is not null)MapView.SetShowLogicalNodeProtection(LogicalNodeProtectionToggle.IsChecked==true); }
    private void LineSpeedsToggle_Changed(object sender,RoutedEventArgs e) { if(MapView is not null)MapView.SetShowLineSpeeds(LineSpeedsToggle.IsChecked==true); }
    private void ConnectedCrossesToggle_Changed(object sender,RoutedEventArgs e)
    {if(MapView is not null)MapView.SetCrossVisibility(ConnectedCrossesToggle.IsChecked==true,MapView.ShowDisconnectedCrosses,MapView.ShowUnsetCrosses);}
    private void DisconnectedCrossesToggle_Changed(object sender,RoutedEventArgs e)
    {if(MapView is not null)MapView.SetCrossVisibility(MapView.ShowConnectedCrosses,DisconnectedCrossesToggle.IsChecked==true,MapView.ShowUnsetCrosses);}
    private void UnsetCrossesToggle_Changed(object sender,RoutedEventArgs e)
    {if(MapView is not null)MapView.SetCrossVisibility(MapView.ShowConnectedCrosses,MapView.ShowDisconnectedCrosses,UnsetCrossesToggle.IsChecked==true);}

    private async Task EditLinePropertiesAsync()
    {
        if (propertyDialogOpen || MapView.IsSaving || signedClient is null) return;
        propertyDialogOpen = true;
        try
        {
            var line = await MapView.GetSelectedLineDetailAsync();
            if (line is null) return;
            var window = new LinePropertiesWindow(signedClient, line, signedClient.Session.UserName) { Owner = this };
            if (window.ShowDialog() == true) await MapView.RefreshLinesAsync();
        }
        finally { propertyDialogOpen = false; MapView.Focus(); }
    }
    private void SelectCross_Click(object sender, RoutedEventArgs e) { MapView.ToggleCrossSelection(); MapView.Focus(); }
    private async void CalculateCross_Click(object sender, RoutedEventArgs e) { MapView.Focus();await CalculateCrossesWithNoticeAsync(); }
    private async void ConnectCross_Click(object sender, RoutedEventArgs e) { await MapView.SetCrossStateAsync(CrossState.Connected); MapView.Focus(); }
    private async void DisconnectCross_Click(object sender, RoutedEventArgs e) { await MapView.SetCrossStateAsync(CrossState.Disconnected); MapView.Focus(); }
    private async void ResetCross_Click(object sender, RoutedEventArgs e) { await MapView.SetCrossStateAsync(CrossState.Unset); MapView.Focus(); }
    private void RememberCrossExit_Click(object sender, RoutedEventArgs e) { MapView.RememberCrossExitLine(); MapView.Focus(); }
    private async void OneWayCross_Click(object sender, RoutedEventArgs e) { await MapView.SetCrossOneWayAsync(); MapView.Focus(); }
    private async void RefreshCross_Click(object sender, RoutedEventArgs e) { await MapView.RefreshCrossesAsync(); MapView.Focus(); }

    private async Task SaveLineAsync()
    {
        if (MapView.IsSaving) return;
        if (!await MapView.CompleteLineAsync())
            MessageBox.Show(this, MapView.OperationMessage, "线路保存失败", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task ReverseLineWithNoticeAsync()
    {
        if (MapView.IsSaving) return;
        if (!await MapView.ReverseLineDirectionAsync())
            MessageBox.Show(this, MapView.OperationMessage, "线路方向未反转", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async Task SplitLineWithNoticeAsync()
    {
        if(MapView.IsSaving)return;
        var request=MapView.GetSplitSelection();
        if(request is null){MessageBox.Show(this,MapView.OperationMessage,"未能拆分",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        if(MessageBox.Show(this,$"在节点序号 {request.CutBeforeOrder} 之前断开线路 {request.LineCode}？\n两部分之间的连接段会被移除，后半生成新编码。Cross 和 LN 位置一起同步。","拆分线路",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)return;
        if(!await MapView.SplitLineAsync(request))MessageBox.Show(this,MapView.OperationMessage,"拆分未完成",MessageBoxButton.OK,MessageBoxImage.Information);
    }
    private async Task ShowMergePlansAsync()
    {
        if(propertyDialogOpen||MapView.IsSaving)return;
        var result=await MapView.GetMergePlansAsync();if(result is null)return;
        if(!result.Success||result.Data is null){MessageBox.Show(this,result.Message,"合并方案",MessageBoxButton.OK,MessageBoxImage.Information);return;}
        propertyDialogOpen=true;
        try
        {
            var window=new MergePlansWindow(result.Data,async plan=>
            {
                var ok=await MapView.ApplyMergePlanAsync(result.Data,plan);return(ok,MapView.OperationMessage);
            }){Owner=this};
            window.PlanSelected+=plan=>MapView.SetMergePreview(result.Data,plan);
            if(result.Data.Plans.Length>0)MapView.SetMergePreview(result.Data,result.Data.Plans[0]);window.ShowDialog();
        }
        finally{propertyDialogOpen=false;MapView.CancelMergeChoice();MapView.Focus();}
    }

    private async Task CalculateCrossesWithNoticeAsync()
    {
        if (MapView.IsSaving||MapView.IsCalculatingCrosses) return;
        // Background completion/errors belong to the separate status line. A modal
        // dialog or focus change here would interrupt the user's current edit.
        await MapView.CalculateCrossesAsync();
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.ImeProcessed ? e.ImeProcessedKey : e.Key;
        if (key == Key.G && Keyboard.Modifiers == ModifierKeys.None)
        { e.Handled = HandleLogicalNodeProtectionShortcut(key, Keyboard.Modifiers, e.IsRepeat, Keyboard.FocusedElement); return; }
        if(HandleUndoShortcut(key,Keyboard.Modifiers,e.IsRepeat,Keyboard.FocusedElement)){e.Handled=true;return;}
        if(HandleLn4ClipboardShortcut(key,Keyboard.Modifiers,e.IsRepeat,Keyboard.FocusedElement)){e.Handled=true;return;}
        if (Keyboard.Modifiers == ModifierKeys.Control && key == Key.F1)
        {
            if (!e.IsRepeat) SetToolPanelExpanded(!IsToolPanelExpanded);
            e.Handled = true;
            return;
        }
        var shiftCross = Keyboard.Modifiers == ModifierKeys.Shift && key is Key.I or Key.O or Key.P or Key.OemOpenBrackets;
        if (Keyboard.Modifiers != ModifierKeys.None && !shiftCross && !(Keyboard.Modifiers == ModifierKeys.Shift && key == Key.OemPlus)) return;
        if (key == Key.F1)
        {
            if (!e.IsRepeat) ShowShortcuts();
            e.Handled = true;
            return;
        }
        // A closed menu can retain keyboard focus after a command. Only an open dropdown
        // owns editor keys; otherwise return focus to the map and execute the shortcut.
        if (MainMenu.IsKeyboardFocusWithin)
        {
            if (MainMenu.Items.OfType<MenuItem>().Any(item => item.IsSubmenuOpen)) return;
            if (CoordinateMapView.IsEditorKey(key)) MapView.Focus();
        }
        switch (key)
        {
            case Key.Home: MapView.ResetView(); e.Handled = true; return;
            case Key.Add:
            case Key.OemPlus: MapView.ZoomAtCenter(1.25); e.Handled = true; return;
            case Key.Subtract:
            case Key.OemMinus: MapView.ZoomAtCenter(1 / 1.25); e.Handled = true; return;
        }
        if (e.IsRepeat && CoordinateMapView.IsEditorKey(key))
        {
            e.Handled = true;
            return;
        }
        if (!CoordinateMapView.IsEditorKey(key)) return;
        if (key == Key.Tab)
        {
            e.Handled = await MapView.HandleEditorKeyAsync(key);
            return;
        }
        e.Handled = true;
        if(key==Key.E && logicalNodeWindow is {WindowState:WindowState.Minimized})
        {logicalNodeWindow.WindowState=WindowState.Normal;logicalNodeWindow.Activate();return;}
        if (MapView.IsConnectingNodes || MapView.IsMovingLogicalNode || MapView.IsAreaMoveMode || MapView.IsFlowDiagramImportActive || MapView.IsPastingLine || MapView.IsPastingArea) { await MapView.HandleEditorKeyAsync(key); return; }
        if (key == Key.F6) await SaveLineAsync();
        else if (key == Key.K) await CalculateCrossesWithNoticeAsync();
        else if (key == Key.C) await ReverseLineWithNoticeAsync();
        else if (key == Key.D) await SplitLineWithNoticeAsync();
        else if (key == Key.B) await ShowMergePlansAsync();
        else if (key == Key.J) await CalculateLogicalNodesWithNoticeAsync();
        else await MapView.HandleEditorKeyAsync(key, skipCrossReload: shiftCross);
    }
}
