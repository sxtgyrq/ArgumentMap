using ArgumentMap.Business;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ArgumentMap.Tests;

[TestClass, TestCategory("Unit")]
public sealed class LoginUnitTests
{
    [TestMethod]
    [DataRow("", "admin", "InvalidUserName")]
    [DataRow("admin", "", "InvalidPassword")]
    [DataRow("admin\naccount", "admin", "InvalidUserName")]
    public void InvalidAccount_IsRejectedWithoutChanges(string user, string password, string code)
    {
        var repository = new InMemoryLineRepository(); var service = new LoginService(repository);
        Assert.AreEqual(code, service.CreateUser(new(user, password)).Code);
        Assert.AreEqual(0, repository.Transaction(s => s.Accounts.Count));
    }

    [TestMethod]
    public void Relogin_RevokesOldSessionButOldLogoutCannotRevokeNewSession()
    {
        var repository = new InMemoryLineRepository(); var service = new LoginService(repository);
        Assert.IsTrue(service.CreateUser(new("admin", "admin")).Success);
        var old = service.Login(new("admin", "admin")).Data!;
        var current = service.Login(new("admin", "admin")).Data!;
        Assert.AreNotEqual(old.LoginCode, current.LoginCode);
        Assert.AreEqual("LoginExpired", service.WithSession(old.UserName, old.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Code);
        Assert.IsFalse(service.Logout(new(old.UserName, old.LoginCode)).Success);
        Assert.IsTrue(service.WithSession(current.UserName, current.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Success);
        Assert.IsTrue(service.Logout(new(current.UserName, current.LoginCode)).Success);
        Assert.IsFalse(service.WithSession(current.UserName, current.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Success);
    }

    [TestMethod]
    public void AuthorizedBusinessFailure_RollsBackMapAndSessionTogether()
    {
        var repository = new InMemoryLineRepository(); var service = new LoginService(repository);
        service.CreateUser(new("admin", "admin")); var session = service.Login(new("admin", "admin")).Data!;
        var next = repository.Transaction(s => s.Lines.Count);
        var rejected = service.WithSession(session.UserName, session.LoginCode, (scope, name) => scope.Transaction(s =>
        {
            s.Lines.Add("ZZZZZZZZZZ", new("ZZZZZZZZZZ", name, false, 0, []));
            s.Accounts[name] = s.Accounts[name] with { LoginCode = null };
            return ServiceResult<bool>.Fail("RejectedOperation", "拒绝此次操作");
        }));
        Assert.IsFalse(rejected.Success); Assert.AreEqual(next, repository.Transaction(s => s.Lines.Count));
        Assert.IsTrue(service.WithSession(session.UserName, session.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Success);
    }
}
