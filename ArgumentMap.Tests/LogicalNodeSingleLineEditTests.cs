using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeSingleLineEditTests
{
    public TestContext TestContext {get;set;}=null!;
    private static LogicalNodeWindow Window(HttpLineClient client,LogicalNodeSnapshot node)=>new(client,"owner",new(),node)
    {Width=520,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false};

    [STATestMethod,DataRow(4),DataRow(6),DataRow(8),DataRow(10),DataRow(12),DataRow(18)]
    public void AllSizesPreventEnterAndMultilinePasteInValueAndLogic_ButInfoAcceptsBoth(int size)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        if(size==10)LogicalNodeInstanceTests.Parent(handler.Repository);
        var node=service.Create(new("owner",100.5m,100.5m,size,new(){Value=size switch{4=>"False",8=>"↔",10=>"people",_=>"节点"},LogicLanguage=size==4?"":"P"})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(new HttpLineClient(http,new("owner","test")),node);
        try
        {
            window.Show();
            foreach(var name in new[]{"ValueInput","LogicInput"})
            {
                var input=(TextBox)window.FindName(name);Assert.IsFalse(input.AcceptsReturn);Assert.AreEqual(TextWrapping.NoWrap,input.TextWrapping);
                var before=input.Text;EditingCommands.EnterParagraphBreak.Execute(null,input);Assert.AreEqual(before,input.Text);
                foreach(var separator in new[]{"\r\n","\n","\u0085","\u2028","\u2029"})
                {
                    var paste=new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText,"A"+separator+"B"),false,DataFormats.UnicodeText);
                    input.RaiseEvent(paste);Assert.IsTrue(paste.CommandCancelled,name+" must reject multiline paste.");Assert.AreEqual(before,input.Text);
                }
            }
            var info=(TextBox)window.FindName("InfoInput");Assert.IsTrue(info.AcceptsReturn);info.Text="说明第一行";info.CaretIndex=info.Text.Length;info.Focus();
            EditingCommands.EnterParagraphBreak.Execute(null,info);info.SelectedText="说明第二行";Assert.AreEqual("说明第一行\r\n说明第二行",info.Text);
            var allowed=new DataObjectPastingEventArgs(new DataObject(DataFormats.UnicodeText,"一\n二"),false,DataFormats.UnicodeText);
            info.RaiseEvent(allowed);Assert.IsFalse(allowed.CommandCancelled);
            Assert.AreEqual(size is 8 or 10,((TextBox)window.FindName("LogicInput")).IsReadOnly);
        }
        finally{window.Close();}
    }

    [STATestMethod,TestCategory("Rendering")]
    public void SingleLineFieldsAndMultilineInfoSaveAndReopen_AndInjectedLineBreakCannotSave()
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        var node=service.Create(new("owner",100.5m,100.5m,6,new(){Value="原节点",LogicLanguage="P"})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var client=new HttpLineClient(http,new("owner","test"));var window=Window(client,node);
        try
        {
            window.Show();var value=(TextBox)window.FindName("ValueInput");var logic=(TextBox)window.FindName("LogicInput");var info=(TextBox)window.FindName("InfoInput");
            var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            foreach(var field in new[]{value,logic})
            {
                var original=field.Text;field.Text="A\nB";((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsTrue(window.IsVisible);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"只能是单行");field.Text=original;
            }
            value.Text="每个人拥有唯一ID";logic.Text="∀x(x ∈ HUMAN → ∃!i HAS_ID(x,i)) ∧ "+new string('P',120);info.Text="说明第一行\r\n说明第二行";
            var expected=logic.Text;window.UpdateLayout();logic.BringIntoView();window.UpdateLayout();Assert.AreEqual(1,logic.LineCount);
            Assert.AreEqual(32,value.MaxLength);Assert.AreEqual(255,logic.MaxLength);Assert.AreEqual(ScrollBarVisibility.Auto,logic.HorizontalScrollBarVisibility);
            SaveImage(window);
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));LogicalNodeWindowEntryTests.DrainUntil(()=>!window.IsVisible);
            var saved=service.GetAll().Data!.Single(n=>n.Code==node.Code);Assert.AreEqual(expected,saved.Properties.LogicLanguage);Assert.AreEqual("说明第一行\r\n说明第二行",saved.Properties.Info);
            var reopened=Window(client,saved);
            try{reopened.Show();Assert.AreEqual(expected,((TextBox)reopened.FindName("LogicInput")).Text);Assert.AreEqual(saved.Properties.Info,((TextBox)reopened.FindName("InfoInput")).Text);}
            finally{reopened.Close();}
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void OpeningLegacyMultilineNodeOnlyFlattensUnsavedDraft()
    {
        using var handler=new DirectLogicalNodeHttpStub();using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};
        var legacy=new LogicalNodeSnapshot("ABCDEFGHIJ","owner",0,100.5m,100.5m,6,new(){Value="第一\r\n第二",LogicLanguage="P\u2028Q",Info="一\n二"});
        var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);var window=Window(new HttpLineClient(http,new("owner","test")),legacy);
        try
        {
            window.Show();Assert.AreEqual("第一 第二",((TextBox)window.FindName("ValueInput")).Text);Assert.AreEqual("P Q",((TextBox)window.FindName("LogicInput")).Text);
            Assert.AreEqual("一\n二",((TextBox)window.FindName("InfoInput")).Text);StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"尚未修改数据库");
            Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));Assert.AreEqual("P\u2028Q",legacy.Properties.LogicLanguage);
        }
        finally{window.Close();}
    }

    private void SaveImage(Window window)
    {
        var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var path=Path.Combine(ProjectPaths.Root,".artifacts","renders","ln-singleline-editor.png");Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));using(var file=File.Create(path))png.Save(file);TestContext.AddResultFile(path);
    }
}
