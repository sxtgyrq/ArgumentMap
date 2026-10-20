using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace WpfArgumentMap;

public partial class FlowDiagramGuideWindow:Window
{
    private readonly Action<string> copyText;
    internal sealed record GuideSection(string Title,Paragraph Heading);
    public FlowDiagramGuideWindow():this(null){}
    internal FlowDiagramGuideWindow(Action<string>? copyText)
    {
        this.copyText=copyText??Clipboard.SetText;InitializeComponent();
        Width=Math.Min(1080,SystemParameters.WorkArea.Width-48);Height=Math.Min(820,SystemParameters.WorkArea.Height-48);
        BuildDocument(FlowDiagramGuideContent.FullText);
    }
    internal static FlowDiagramGuideWindow ShowFor(Window owner)
    {
        var existing=owner.OwnedWindows.OfType<FlowDiagramGuideWindow>().FirstOrDefault();
        if(existing is not null){existing.Activate();return existing;}
        var window=new FlowDiagramGuideWindow{Owner=owner};window.Show();return window;
    }
    private void BuildDocument(string markdown)
    {
        var document=new FlowDocument{FontFamily=new FontFamily("Segoe UI, Microsoft YaHei"),FontSize=14,Foreground=BrushesForText,PagePadding=new Thickness(6),LineHeight=23};
        var sections=new List<GuideSection>();var lines=markdown.Replace("\r\n","\n").Split('\n');
        for(var i=0;i<lines.Length;i++)
        {
            var line=lines[i];if(string.IsNullOrWhiteSpace(line))continue;
            if(line.StartsWith("```",StringComparison.Ordinal))
            {
                var code=new List<string>();while(++i<lines.Length&&!lines[i].StartsWith("```",StringComparison.Ordinal))code.Add(lines[i]);
                document.Blocks.Add(new Paragraph(new Run(string.Join("\n",code))){FontFamily=new FontFamily("Consolas, Microsoft YaHei"),FontSize=12,LineHeight=19,Background=new SolidColorBrush(Color.FromRgb(242,246,252)),Padding=new Thickness(12),Margin=new Thickness(0,8,0,16)});continue;
            }
            if(line.StartsWith('|'))
            {
                var table=new Table{CellSpacing=0,Margin=new Thickness(0,6,0,14)};var group=new TableRowGroup();table.RowGroups.Add(group);var rowIndex=0;
                do
                {
                    var cells=lines[i].Trim().Trim('|').Split('|').Select(c=>c.Trim()).ToArray();
                    if(cells.All(c=>Regex.IsMatch(c,"^:?-+:?$"))){i++;continue;}
                    if(rowIndex==0)
                    {
                        var weights=cells.Length==3&&cells[1]=="字段"?new[]{.14,.25,.61}:new[]{.27,.31,.42};
                        for(var column=0;column<cells.Length;column++)table.Columns.Add(new TableColumn{Width=new GridLength(column<weights.Length?weights[column]:1,GridUnitType.Star)});
                    }
                    var row=new TableRow();foreach(var cell in cells)
                    {var paragraph=new Paragraph{Margin=new Thickness(0)};AddInline(paragraph,cell);row.Cells.Add(new TableCell(paragraph){Padding=new Thickness(7),BorderThickness=new Thickness(0,0,0,1),BorderBrush=new SolidColorBrush(Color.FromRgb(222,230,240)),Background=rowIndex==0?new SolidColorBrush(Color.FromRgb(235,241,250)):Brushes.White});}
                    if(rowIndex++==0)row.FontWeight=FontWeights.SemiBold;group.Rows.Add(row);i++;
                }while(i<lines.Length&&lines[i].StartsWith('|'));
                i--;document.Blocks.Add(table);continue;
            }
            var heading=line.StartsWith("## ",StringComparison.Ordinal);var title=heading?line[3..]:line.StartsWith("# ",StringComparison.Ordinal)?line[2..]:null;
            var p=new Paragraph{Margin=new Thickness(0,title is null?0:16,0,title is null?9:10)};
            if(title is not null){p.FontSize=heading?19:24;p.FontWeight=FontWeights.SemiBold;p.Foreground=new SolidColorBrush(Color.FromRgb(23,43,77));AddInline(p,title);if(heading)sections.Add(new(title,p));}
            else AddInline(p,line.StartsWith("- ",StringComparison.Ordinal)?"• "+line[2..]:line.StartsWith("> ",StringComparison.Ordinal)?line[2..]:line);
            document.Blocks.Add(p);
        }
        GuideReader.Document=document;ContentsList.ItemsSource=sections;
    }
    private static readonly Brush BrushesForText=(Brush)new SolidColorBrush(Color.FromRgb(52,64,84)).GetAsFrozen();
    private static void AddInline(Paragraph paragraph,string text)
    {
        // A small offline renderer for this bundled guide; no HTML, scripts or external resources.
        text=Regex.Replace(text,"\\[([^\\]]+)\\]\\([^)]+\\)","$1");
        foreach(var part in Regex.Split(text,"(`[^`]+`|\\*\\*[^*]+\\*\\*)"))
            if(part.Length>=2&&part.StartsWith('`')&&part.EndsWith('`'))paragraph.Inlines.Add(new Run(part[1..^1]){FontFamily=new FontFamily("Consolas, Microsoft YaHei"),Background=new SolidColorBrush(Color.FromRgb(239,244,251))});
            else if(part.Length>=4&&part.StartsWith("**",StringComparison.Ordinal)&&part.EndsWith("**",StringComparison.Ordinal))paragraph.Inlines.Add(new Bold(new Run(part[2..^2])));
            else paragraph.Inlines.Add(new Run(part));
    }
    private void Contents_SelectionChanged(object sender,SelectionChangedEventArgs e)
    {if(ContentsList.SelectedItem is GuideSection section){section.Heading.BringIntoView();GuideReader.ScrollToHorizontalOffset(0);}}
    private void Copy(string text,string message)
    {
        try{copyText(text);Status.Text=message;Status.Foreground=Brushes.SeaGreen;}
        catch(ExternalException){Status.Text="剪贴板正在被其他程序使用，请稍后重试；也可选中文字后按 Ctrl+C。";Status.Foreground=Brushes.Firebrick;}
    }
    private void CopyGuide_Click(object sender,RoutedEventArgs e)=>Copy(FlowDiagramGuideContent.FullText,"已复制完整说明，可发送给其他使用者或 ChatGPT。");
    private void CopyTask_Click(object sender,RoutedEventArgs e)=>Copy(FlowDiagramGuideContent.ChatGptTask,"已复制自包含的完整协议、全部示例和任务表。填写末尾的资料或研究主题，交给从未使用本软件的 ChatGPT 即可开始。");
    private void CopyMinimal_Click(object sender,RoutedEventArgs e)=>Copy(FlowDiagramGuideContent.MinimalExample,"已复制最小可用 JSON 示例，可粘贴到导入窗口，选择空白位置校验预览。");
    private void CopyBranch_Click(object sender,RoutedEventArgs e)=>Copy(FlowDiagramGuideContent.BranchExample,"已复制 LN 外可单向换路的完整分支示例，可粘贴到导入窗口后校验预览。");
    private void Close_Click(object sender,RoutedEventArgs e)=>Close();
    private void Window_PreviewKeyDown(object sender,KeyEventArgs e){if(e.Key==Key.Escape){Close();e.Handled=true;}}
}
