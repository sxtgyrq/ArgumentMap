using System.IO;
using System.Text.RegularExpressions;

namespace WpfArgumentMap;

internal static class FlowDiagramGuideContent
{
    internal static string FullText
    {
        get=>ReadResource("WpfArgumentMap.FlowDiagramLanguage.md");
    }
    internal static string MinimalExample=>Regex.Match(FullText,"```json\\r?\\n([\\s\\S]*?)```").Groups[1].Value.Trim();
    internal static string BranchExample=>ReadResource("WpfArgumentMap.ConnectedBranchExample.json");
    internal static string TaskTemplate=>ReadResource("WpfArgumentMap.FlowDiagramTaskTemplate.txt");
    internal static string ChatGptTask=>FullText+"\n\n"+TaskTemplate;
    private static string ReadResource(string name)
    {
        using var stream=typeof(FlowDiagramGuideContent).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("流程图制作协议资源缺失，请重新安装客户端。");
        using var reader=new StreamReader(stream);return reader.ReadToEnd();
    }
}
