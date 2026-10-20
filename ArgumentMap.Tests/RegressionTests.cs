using System.IO;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass]
public sealed class RegressionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod("八方向、大坐标与随机重叠回归"), TestCategory("Unit"), TestCategory("Regression")]
    public void GeometryAndRandomContainment() => Program.CheckGeometry();

    [TestMethod("F6 全图占用、重叠、版本、负责人和锁定校验"), TestCategory("Unit"), TestCategory("Regression")]
    public void FullSaveValidation() => Program.CheckBusiness();

    [TestMethod("同时保存重叠线路只能提交一条"), TestCategory("Unit"), TestCategory("Regression")]
    public Task ConcurrentOverlapSaves() => Program.CheckConcurrencyAsync();

    [TestMethod("业务失败与异常回滚全部内存状态"), TestCategory("Unit"), TestCategory("Regression")]
    public void CallbackRollback() => TransactionChecks.CheckMemory();

    [TestMethod("线路名称、速度、锁定及权限"), TestCategory("Unit"), TestCategory("Regression")]
    public void LinePropertiesAndLocks() => LinePropertyChecks.CheckBusiness();

    [TestMethod("F6 HTTP 提交、整数坐标、草稿保留及节点编辑"), TestCategory("Http")]
    public Task SavingAndNodeEditingOverHttp() => Program.CheckHttpAndEditorAsync();

    [TestMethod("45° 锐角拒绝、135° 允许和所有方向组合"), TestCategory("Http")]
    public Task AcuteCornerWorkflow() => CornerChecks.RunAsync();

    [TestMethod("第三条线路经过 Cross 的 F6、K、旧数据和并发校验"), TestCategory("Http")]
    public Task ThirdPartyCrossPassageWorkflow() => CrossPassageChecks.RunAsync();

    [TestMethod("Cross 整数、半整数、通行设置及旧关系选择"), TestCategory("Http")]
    public Task CrossCalculationAndEditing() => CrossChecks.RunAsync();

    [TestMethod("K 逐段计算和最终清理、待重算标记及幂等"), TestCategory("Http")]
    public Task IncrementalCrossWorkflow() => IncrementalCrossChecks.RunAsync();

    [TestMethod("增删节点、序号变化与交点通行设置恢复"), TestCategory("Http")]
    public Task CrossIdentityAfterNodeEditing() => CrossEditChecks.RunAsync();

    [TestMethod("登录、旧密码格式、重新登录失效及 HTTP 权限"), TestCategory("Http")]
    public Task LoginAndAuthenticatedOperations() => LoginChecks.RunAsync();

    [STATestMethod("WPF 工具栏收起展开、快捷键搜索、箭头及锁定显示"), TestCategory("Rendering")]
    public void WpfRenderingAndPanels()
    {
        var directory = Path.Combine(ProjectPaths.Root, ".artifacts", "renders");
        Program.CheckRender(directory);
        foreach (var file in Directory.EnumerateFiles(directory, "*.png")) TestContext.AddResultFile(file);
    }
}
