using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class FlowDiagramGuideTests
{
    public TestContext TestContext { get; set; }=null!;
    private static T Offscreen<T>(T window) where T:Window
    {window.WindowStartupLocation=WindowStartupLocation.Manual;window.Left=-10000;window.Top=-10000;window.ShowInTaskbar=false;window.ShowActivated=false;return window;}
    private static void Click(Window window,string name)=>((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void Press(Window window,Key key)
    {var e=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(window)!,0,key){RoutedEvent=Keyboard.PreviewKeyDownEvent};window.RaiseEvent(e);Assert.IsTrue(e.Handled);}

    [TestMethod]
    [DataRow("LN12、LN6、LN4 规则速查")]
    [DataRow("几何自查公式与逐项清单")]
    [DataRow("常见错误与修正")]
    [DataRow("给 ChatGPT 的提示词")]
    public void IndependentlyCopiedSectionsDistinguishBranchExceptionAndBothUniquenessRules(string title)
    {
        var marker="## "+title;var source=FlowDiagramGuideContent.FullText;var start=source.IndexOf(marker,StringComparison.Ordinal);Assert.IsTrue(start>=0);
        var end=source.IndexOf("\n## ",start+marker.Length,StringComparison.Ordinal);var section=source[start..(end<0?source.Length:end)];
        foreach(var term in new[]{"LN4","True","False","LN6","LN12","大写","logic","value"})StringAssert.Contains(section,term,title);
        Assert.IsTrue(section.Contains("可空",StringComparison.Ordinal)||section.Contains("可以为空",StringComparison.Ordinal)||section.Contains("可省略",StringComparison.Ordinal)||section.Contains("可以省略",StringComparison.Ordinal),title);
        Assert.IsFalse(section.Contains("每个 LN 的 logic 必填",StringComparison.Ordinal));Assert.IsFalse(section.Contains("每个 LN 的逻辑语言 logic 都是必填项",StringComparison.Ordinal));
    }

    [TestMethod]
    public void BundledGuideIsTheDocumentedSource_WithCompleteRulesAndSelfContainedChatGptTask()
    {
        Assert.AreEqual(File.ReadAllText(Path.Combine(ProjectPaths.Root,"FlowDiagramLanguage.md")),FlowDiagramGuideContent.FullText);
        foreach(var term in new[]{"软件如何识别流程图","三类对象","Cross 连通","不会自动求真假","LN 中心不是 Road 节点","45°","135°","500","4096","road1ToRoad2","disconnected","rollback","常见错误","LoginCode"})
            StringAssert.Contains(FlowDiagramGuideContent.FullText,term);
        StringAssert.Contains(FlowDiagramGuideContent.ChatGptTask,FlowDiagramGuideContent.FullText);
        StringAssert.Contains(FlowDiagramGuideContent.ChatGptTask,FlowDiagramGuideContent.BranchExample);
        StringAssert.Contains(FlowDiagramGuideContent.ChatGptTask,"在此填写事实");
        Assert.AreEqual(File.ReadAllText(Path.Combine(ProjectPaths.Root,"WpfArgumentMap","Examples","flow-diagram-task-template.txt")),FlowDiagramGuideContent.TaskTemplate);
        foreach(var term in new[]{"没有任何聊天历史","模式 A","模式 B","适用范围","不得编造","完整替换 JSON","abs(cx1-cx2)","u·v <= 0"})
            StringAssert.Contains(FlowDiagramGuideContent.ChatGptTask,term);
        foreach(var term in new[]{"自行检索","无法联网","提供资料","只输出一个完整的 JSON 代码块","不需要访问本软件","原始错误"})
            StringAssert.Contains(FlowDiagramGuideContent.TaskTemplate,term);
        foreach(var term in new[]{"LN4 强制规则","logic 必填","是/否","先审核流程","审核稿","1–255","非空白","True","False","唯一 LN6"})StringAssert.Contains(FlowDiagramGuideContent.FullText,term);
        StringAssert.Contains(FlowDiagramGuideContent.TaskTemplate,"LN4 的值只能为字符串 True 或 False");StringAssert.Contains(FlowDiagramGuideContent.TaskTemplate,"确认后再按格式形成 JSON");
        foreach(var term in new[]{"默认速度为 100","尽量避免单向换路"})StringAssert.Contains(FlowDiagramGuideContent.TaskTemplate,term);
        StringAssert.Contains(FlowDiagramGuideContent.FullText,"默认 100");StringAssert.Contains(FlowDiagramGuideContent.FullText,"尽量避免单向换路");
        StringAssert.Contains(FlowDiagramGuideContent.FullText,"DuplicateLogicalNodeValue");
        StringAssert.Contains(FlowDiagramGuideContent.TaskTemplate,"DuplicateLogicalNodeValue");
    }

    [TestMethod,DataRow(false),DataRow(true)]
    public void CopyableExamplesPassRealPreviewAndImport_WithoutRelaxingExistingRules(bool branch)
    {
        var text=branch?FlowDiagramGuideContent.BranchExample:FlowDiagramGuideContent.MinimalExample;
        Assert.IsTrue(FlowDiagramLanguage.Parse(text).Success);var repository=new InMemoryLineRepository();var service=new FlowDiagramService(repository);var request=new FlowDiagramRequest("owner",text,1200.5m,5000.5m);
        var preview=service.Preview(request);Assert.IsTrue(preview.Success,preview.Message);Assert.AreEqual(0,repository.Transaction(s=>s.Lines.Count+s.LogicalNodes.Count+s.Crosses.Count));
        var imported=service.Import(request);Assert.IsTrue(imported.Success,imported.Message);Assert.AreEqual(branch?6:4,imported.Data!.Added.LogicalNodes.Length);Assert.AreEqual(branch?2:1,imported.Data.Added.Lines.Length);
        Assert.IsTrue(imported.Data.Added.LogicalNodes.Any(n=>n.SideLength==4&&n.Properties.Value=="True"));Assert.IsTrue(imported.Data.Added.LogicalNodes.Any(n=>n.SideLength==6));
        Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));Assert.IsNull(repository.Transaction(s=>CrossPassageValidator.FindInCurrentGeometry(s)));
        if(branch)
        {
            var cross=imported.Data.Added.Crosses.Single();var main=imported.Data.RoadCodes["main"];var negative=imported.Data.RoadCodes["negative"];
            Assert.AreEqual(CrossState.Connected,cross.State);Assert.IsTrue(cross.AllowsTransfer(main,negative));Assert.IsTrue(cross.AllowsTransfer(negative,main));
            Assert.IsTrue(imported.Data.Added.LogicalNodes.All(n=>!n.Contains(cross.Key.Position)&&n.Positions.Length==1));
            Assert.IsTrue(new CrossService(repository).Recalculate().Success);Assert.IsTrue(new LogicalNodeService(repository).Calculate().Success);
            var after=new CrossService(repository).GetAll().Data!.Single();Assert.IsTrue(after.AllowsTransfer(main,negative));Assert.IsTrue(after.AllowsTransfer(negative,main));
        }
    }

    [TestMethod,DataRow(0,4,1,0),DataRow(1,6,2,1),DataRow(2,10,3,2)]
    public void AllCompleteExamplesInCopiedProtocolImport_AndMatchEmbeddedExamples(int index,int nodes,int roads,int crosses)
    {
        var examples=Regex.Matches(FlowDiagramGuideContent.FullText,"```json\\r?\\n([\\s\\S]*?)```").Cast<Match>()
            .Select(m=>JsonNode.Parse(m.Groups[1].Value)!).Where(n=>n["format"]?.GetValue<string>()==FlowDiagramLanguage.Format).ToArray();
        Assert.AreEqual(4,examples.Length,"A recipient must receive all examples without opening another file.");
        Assert.IsTrue(examples.All(d=>d["roads"]!.AsArray().All(r=>r!["speed"]!.GetValue<int>()==100)),"All supplied examples must explicitly use the recommended speed.");
        Assert.IsTrue(examples.All(d=>d["crosses"]!.AsArray().All(c=>c!["state"]!.GetValue<string>() is "connected" or "disconnected")),"Recommended examples should avoid one-way Crosses.");
        Assert.IsTrue(JsonNode.DeepEquals(examples[1],JsonNode.Parse(FlowDiagramGuideContent.BranchExample)));
        Assert.IsTrue(JsonNode.DeepEquals(examples[2],JsonNode.Parse(FlowDiagramImportWindow.ExampleText)));
        var repository=new InMemoryLineRepository();var result=new FlowDiagramService(repository).Import(new("new_user",examples[index].ToJsonString(),1200.5m,5000.5m));
        Assert.IsTrue(result.Success,result.Message);Assert.AreEqual(nodes,result.Data!.Added.LogicalNodes.Length);Assert.AreEqual(roads,result.Data.Added.Lines.Length);Assert.AreEqual(crosses,result.Data.Added.Crosses.Length);
        Assert.IsTrue(result.Data.Added.LogicalNodes.All(n=>n.Owner=="new_user"));Assert.IsNull(repository.Transaction(s=>LogicalNodeRules.ValidateMap(s)));
        Assert.IsNull(LogicalNodeBranchRules.Validate(result.Data.Added.LogicalNodes));
        if(index==2)
            foreach(var cross in result.Data.Added.Crosses)
            {
                Assert.IsTrue(result.Data.Added.LogicalNodes.All(n=>!n.Contains(cross.Key.Position)),"Equipment routing junctions must be outside all LNs.");
                Assert.IsTrue(cross.AllowsTransfer(cross.Key.RoadCode1,cross.Key.RoadCode2));Assert.IsTrue(cross.AllowsTransfer(cross.Key.RoadCode2,cross.Key.RoadCode1));
            }
    }

    [STATestMethod]
    public void GuideMenuIsImmediatelyBelowImport_OpensOfflineOnce_AndEscClosesOnlyGuide()
    {
        var main=Offscreen(new MainWindow());
        try
        {
            main.Show();var menu=(MenuItem)main.FindName("FlowDiagramGuideMenu");var parent=(MenuItem)menu.Parent;
            Assert.AreSame(menu,parent.Items[parent.Items.IndexOf(main.FindName("ImportFlowDiagramMenu"))+1]);
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));var guide=main.OwnedWindows.OfType<FlowDiagramGuideWindow>().Single();
            menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Assert.AreEqual(1,main.OwnedWindows.OfType<FlowDiagramGuideWindow>().Count());Assert.IsTrue(((RichTextBox)guide.FindName("GuideReader")).IsReadOnly);
            var reader=(RichTextBox)guide.FindName("GuideReader");var text=new TextRange(reader.Document.ContentStart,reader.Document.ContentEnd).Text;StringAssert.Contains(text,"argumentmap.flow/1");StringAssert.Contains(text,"常见错误与修正");
            Press(guide,Key.Escape);Assert.IsTrue(main.IsVisible);Assert.AreEqual(0,main.OwnedWindows.OfType<FlowDiagramGuideWindow>().Count());
        }
        finally{main.Close();}
    }

    [STATestMethod]
    public void CopyButtonsProduceSpecificationTaskAndImportableJson_WithoutTouchingRealClipboard()
    {
        var copies=new List<string>();var guide=Offscreen(new FlowDiagramGuideWindow(text=>copies.Add(text)));
        try
        {
            guide.Show();Click(guide,"CopyGuideButton");Click(guide,"CopyTaskButton");Click(guide,"CopyMinimalButton");Click(guide,"CopyBranchButton");
            Assert.AreEqual(4,copies.Count);Assert.AreEqual(FlowDiagramGuideContent.FullText,copies[0]);Assert.AreEqual(FlowDiagramGuideContent.ChatGptTask,copies[1]);
            Assert.IsTrue(FlowDiagramLanguage.Parse(copies[2]).Success);Assert.IsTrue(FlowDiagramLanguage.Parse(copies[3]).Success);
            StringAssert.Contains(((TextBlock)guide.FindName("Status")).Text,"已复制");
        }
        finally{guide.Close();}
    }

    [STATestMethod]
    public void ClipboardBusyShowsRetry_AndKeepsGuideReadable()
    {
        var guide=Offscreen(new FlowDiagramGuideWindow(_=>throw new ExternalException("Clipboard busy")));
        try{guide.Show();Click(guide,"CopyGuideButton");StringAssert.Contains(((TextBlock)guide.FindName("Status")).Text,"重试");Assert.IsTrue(guide.IsVisible);Assert.IsTrue(((RichTextBox)guide.FindName("GuideReader")).IsReadOnly);}
        finally{guide.Close();}
    }

    [STATestMethod]
    public void ImportHelpButtonAndF1PreserveTextOriginPreviewAndPendingImport_NoWrites()
    {
        using var stub=new DirectLogicalNodeHttpStub();using var http=new HttpClient(stub){BaseAddress=new Uri("http://localhost/")};var main=Offscreen(new MainWindow(new HttpLineClient(http,new("owner","test"))));
        try
        {
            main.Show();((MenuItem)main.FindName("ImportFlowDiagramMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));var import=main.OwnedWindows.OfType<FlowDiagramImportWindow>().Single();
            ((TextBox)import.FindName("SourceText")).Text=FlowDiagramGuideContent.MinimalExample;Assert.IsTrue(import.PreviewAsync().GetAwaiter().GetResult());
            var text=((TextBox)import.FindName("SourceText")).Text;var x=((TextBox)import.FindName("XInput")).Text;Click(import,"GuideButton");var guide=import.OwnedWindows.OfType<FlowDiagramGuideWindow>().Single();guide.Close();
            Press(import,Key.F1);Assert.AreEqual(1,import.OwnedWindows.OfType<FlowDiagramGuideWindow>().Count());import.OwnedWindows.OfType<FlowDiagramGuideWindow>().Single().Close();
            Assert.AreEqual(text,((TextBox)import.FindName("SourceText")).Text);Assert.AreEqual(x,((TextBox)import.FindName("XInput")).Text);Assert.IsTrue(((Button)import.FindName("ImportButton")).IsEnabled);
            Assert.AreEqual(1,stub.FlowPreviews);Assert.AreEqual(0,stub.FlowImports);Assert.AreEqual(0,stub.Repository.Transaction(s=>s.LogicalNodes.Count+s.Lines.Count+s.Crosses.Count));
            import.Close();
        }
        finally{main.Close();}
    }

    [STATestMethod,TestCategory("Rendering")]
    public void GuideOverviewFieldsAndSmallWindowRender_TableOfContentsScrollsToSection()
    {
        var guide=Offscreen(new FlowDiagramGuideWindow(_=>{}));
        try
        {
            guide.Show();guide.UpdateLayout();Save(guide,"flow-guide-overview.png");
            var list=(ListBox)guide.FindName("ContentsList");Assert.IsTrue(list.Items.Count>=15);var sections=list.Items.Cast<FlowDiagramGuideWindow.GuideSection>().ToArray();list.SelectedItem=sections.Single(s=>s.Title=="字段");
            guide.Dispatcher.Invoke(()=>{},DispatcherPriority.Render);guide.UpdateLayout();Assert.IsTrue(((RichTextBox)guide.FindName("GuideReader")).VerticalOffset>0);Save(guide,"flow-guide-fields.png");
            guide.Width=780;guide.Height=580;guide.UpdateLayout();list.SelectedItem=sections.Single(s=>s.Title=="最小可用示例");guide.Dispatcher.Invoke(()=>{},DispatcherPriority.Render);guide.UpdateLayout();Save(guide,"flow-guide-small.png");
        }
        finally{guide.Close();}
    }
    private void Save(Window window,string name)
    {
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);var path=Path.Combine(ProjectPaths.Root,".artifacts","renders",name);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))encoder.Save(file);TestContext.AddResultFile(path);
    }
}
