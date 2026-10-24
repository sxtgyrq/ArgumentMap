using System.Windows;
using System.Windows.Input;
using ArgumentMap.Contracts;
namespace WpfArgumentMap;
public partial class LogicalNodeConnectionWindow:Window
{
    public LogicalNodeConnectionKind Kind { get; private set; } = LogicalNodeConnectionKind.Direct;
    public LogicalNodeConnectionWindow(string source,string target)
    {InitializeComponent();SourceText.Text="起点："+source;TargetText.Text="终点："+target;}
    private void Direct_Click(object sender,RoutedEventArgs e)=>DialogResult=true;
    private void GoTo_Click(object sender,RoutedEventArgs e){Kind=LogicalNodeConnectionKind.GoTo;DialogResult=true;}
    private void Find_Click(object sender,RoutedEventArgs e){Kind=LogicalNodeConnectionKind.Find;DialogResult=true;}
    private void Window_PreviewKeyDown(object sender,KeyEventArgs e)
    {if(e.Key==Key.Escape){DialogResult=false;e.Handled=true;}}
}
