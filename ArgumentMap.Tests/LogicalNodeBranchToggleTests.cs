using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class LogicalNodeBranchToggleTests
{
    private static LogicalNodeWindow Window(DirectLogicalNodeHttpStub handler,HttpClient http,LogicalNodeSnapshot branch)=>
        new(new HttpLineClient(http,new("owner","test")),"owner",new((double)branch.X,(double)branch.Y),branch)
        {Left=-10000,Top=-10000,ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual};

    [STATestMethod]
    [DataRow("True","dateSuccess(S,M)")][DataRow("False","dateSuccess(S,M)")]
    [DataRow("True","¬P")][DataRow("False","¬P")]
    [DataRow("True"," (A ∧ B) ")][DataRow("False"," (A ∧ B) ")]
    public void RepeatedSwitchesPreserveExactSource_OpeningDoesNotTransform_AndSavePersistsPair(string initial,string source)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        Assert.IsTrue(service.Create(new("owner",50.5m,50.5m,6,new(){LogicLanguage=source})).Success);
        var originalLogic=LogicalNodeBranchRules.Expression(initial,source);
        var branch=service.Create(new("owner",100.5m,50.5m,4,new(){Value=initial,LogicLanguage=originalLogic})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(handler,http,branch);
        try
        {
            window.Show();var values=(ComboBox)window.FindName("BranchValueInput");var logic=(TextBox)window.FindName("LogicInput");
            Assert.AreEqual(originalLogic,logic.Text);var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            for(var i=0;i<5;i++)
            {
                var next=(string)values.SelectedValue=="True"?"False":"True";values.SelectedValue=next;
                Assert.AreEqual(LogicalNodeBranchRules.Expression(next,source),logic.Text);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository),"Switching edits the draft, not the database.");
            }
            var expectedValue=(string)values.SelectedValue;var expectedLogic=logic.Text;
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            LogicalNodeWindowEntryTests.DrainUntil(()=>!window.IsVisible);
            var current=service.GetAll().Data!.Single(n=>n.Code==branch.Code);
            Assert.AreEqual(expectedValue,current.Properties.Value);Assert.AreEqual(expectedLogic,current.Properties.LogicLanguage);
            Assert.AreEqual(branch.Version+1,current.Version);Assert.AreEqual(branch.X,current.X);Assert.AreEqual(branch.Y,current.Y);Assert.AreEqual(4,current.SideLength);
        }
        finally{window.Close();}
    }

    [STATestMethod,DataRow(""),DataRow(" \t\r\n")]
    public void BlankLogicStaysBlank_AndAnUnsavedReplacementIsWhatGetsNegated(string blank)
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);
        var branch=service.Create(new("owner",100.5m,50.5m,4,new(){Value="False"})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(handler,http,branch);
        try
        {
            window.Show();var values=(ComboBox)window.FindName("BranchValueInput");var logic=(TextBox)window.FindName("LogicInput");logic.Text=blank;
            values.SelectedValue="True";Assert.AreEqual(blank,logic.Text);values.SelectedValue="False";Assert.AreEqual(blank,logic.Text);
            values.SelectedValue="True";logic.Text="新的命题 Q";values.SelectedValue="False";Assert.AreEqual("¬新的命题 Q",logic.Text);
            values.SelectedValue="True";Assert.AreEqual("新的命题 Q",logic.Text);
            // An already mismatched draft without a negation prefix is not truncated when selecting True.
            values.SelectedValue="False";logic.Text="手工未配对";values.SelectedValue="True";Assert.AreEqual("手工未配对",logic.Text);
        }
        finally{window.Close();}
    }

    [STATestMethod]
    public void MaximumLengthSourceIsNeverSilentlyTruncated_OverlongFalseCannotSave_AndSwitchingBackRestoresIt()
    {
        using var handler=new DirectLogicalNodeHttpStub();var service=new LogicalNodeService(handler.Repository);var source=new string('P',255);
        Assert.IsTrue(service.Create(new("owner",50.5m,50.5m,6,new(){LogicLanguage=source})).Success);
        var branch=service.Create(new("owner",100.5m,50.5m,4,new(){Value="True",LogicLanguage=source})).Data!;
        using var http=new HttpClient(handler){BaseAddress=new Uri("http://localhost/")};var window=Window(handler,http,branch);
        try
        {
            window.Show();var values=(ComboBox)window.FindName("BranchValueInput");var logic=(TextBox)window.FindName("LogicInput");var before=LogicalNodeMySqlTests.Snapshot(handler.Repository);
            values.SelectedValue="False";Assert.AreEqual("¬"+source,logic.Text);StringAssert.Contains(((TextBlock)window.FindName("Status")).Text,"超过 255");
            ((Button)window.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Assert.IsTrue(window.IsVisible);Assert.AreEqual(before,LogicalNodeMySqlTests.Snapshot(handler.Repository));
            values.SelectedValue="True";Assert.AreEqual(source,logic.Text);
        }
        finally{window.Close();}
    }
}
