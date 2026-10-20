using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("MySql")]
public sealed class MySqlIntegrationTests
{
    [TestMethod("Cross 单向状态 2/3、约束迁移、HTTP 双用户竞争、SQL 回滚、半整数及重算反转保留")]
    public Task OneWayCrossDirections() => MySqlChecks.RunAsync("CrossDirections");
    [TestMethod("数字编码完整迁移、Road1 排序、两侧字段跟随、半整数及断点恢复")]
    public Task NumericCodesMigration() => MySqlChecks.RunAsync("Codes");

    [TestMethod("编码迁移写入失败不改原表，修复后重试保留通行设置")]
    public Task NumericCodesMigrationRollback() => MySqlChecks.RunAsync("CodesRollback");
    [TestMethod("MySQL 保存、中文、半整数交点和重启持久化")]
    public Task StorageAndUnicode() => MySqlChecks.RunAsync("Storage");

    [TestMethod("真实 MySQL 的 F6、K、HTTP 服务重启与删除")]
    public Task HttpRestart() => MySqlChecks.RunAsync("HttpRestart");

    [TestMethod("K 中断后重启继续计算并保留 Cross 设置")]
    public Task ResumeIncrementalCalculation() => MySqlChecks.RunAsync("IncrementalRestart");

    [TestMethod("SQL 写入故障：线路、Cross、节点标记、清理及删除全部回滚")]
    public Task CommitRollbackAndFaultInjection() => MySqlChecks.RunAsync("Rollback");

    [TestMethod("两服务实例、多用户同时保存、K、Cross 设置和第三条线路限制")]
    public Task MultipleUsersAndIndependentServers() => MySqlChecks.RunAsync("ConcurrentUsers");

    [TestMethod("登录持久化、会话与写入同一事务、异常回滚")]
    public Task LoginSessionsAndAtomicWrites() => MySqlChecks.RunAsync("Login");

    [TestMethod("线路属性、方向、锁定持久化和并发版本冲突")]
    public Task PropertyAndLockPersistence() => MySqlChecks.RunAsync("Properties");

    [TestMethod("Cross 标识迁移仍保留原编号和通行设置")]
    public Task CrossIdentityMigration() => MySqlChecks.RunAsync("CrossIdentityMigration");

    [TestMethod("旧表结构迁移保留线路、交点位置、设置和编号")]
    public Task SchemaUpgrade() => MySqlChecks.RunAsync("SchemaMigration");

    [TestMethod("MySQL 禁止第三条线路经过 Cross：保存与计算失败回滚")]
    public Task ThirdPartyCrossRestriction() => MySqlChecks.RunAsync("CrossPassage");

    [TestMethod("删除通行方式字段、迁移速度 5–500、SQL 约束和迁移中断恢复")]
    public Task SpeedPropertyMigration() => MySqlChecks.RunAsync("SpeedMigration");

    [TestMethod("线路方向数据库约束、反转 SQL 回滚、两服务并发、Cross 保留和迁移")]
    public Task DirectionAndReversal() => MySqlChecks.RunAsync("Direction");

    [TestMethod("反转改变多个 Cross 线段序号和位置比例，重启恢复及再次反转保留设置")]
    public Task ReversalCrossLocationsAndRestart() => MySqlChecks.RunAsync("DirectionCrossLocations");
}
