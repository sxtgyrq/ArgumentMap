using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ArgumentMap.Contracts;
using ArgumentMap.Storage;

namespace ArgumentMap.Business;

public sealed class LoginService(ILineRepository repository)
{
    // Account creation is an administrative entry, as in the old project; it is not a public HTTP route.
    public ServiceResult<bool> CreateUser(CreateUserRequest request)
    {
        var name = request.UserName?.Trim() ?? "";
        if (name.Length is < 1 or > 32 || name.Any(char.IsControl))
            return ServiceResult<bool>.Fail("InvalidUserName", "用户名须为 1–32 个字符。");
        if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 256)
            return ServiceResult<bool>.Fail("InvalidPassword", "请输入有效密码，长度不超过 256 个字符。");
        var hash = PasswordMd5(request.Password.Trim());
        return repository.Transaction(state =>
        {
            if (state.Accounts.ContainsKey(name)) return ServiceResult<bool>.Fail("UserExists", "这个账号已存在。");
            state.Accounts.Add(name, new(name, hash, null, EditAllRoad: name == "admin", EditAllLogicalNodes: name == "admin"));
            return ServiceResult<bool>.Ok(true, $"账号 {name} 已创建。");
        });
    }

    public ServiceResult<LoginSession> Login(LoginRequest request)
    {
        var name = request.UserName?.Trim() ?? "";
        if (name.Length is < 1 or > 32 || string.IsNullOrWhiteSpace(request.Password) || request.Password.Length > 256 ||
            request.DBPrefix != "ArgumentMap") return LoginFailed();
        var hash = PasswordMd5(request.Password.Trim());
        return repository.Transaction(state =>
        {
            if (!state.Accounts.TryGetValue(name, out var account) || account.Md5Password != hash) return LoginFailed();
            // One current login code per account. Logging in again invalidates the previous session.
            var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            state.Accounts[name] = account with { LoginCode = code };
            return ServiceResult<LoginSession>.Ok(new(account.UserName, code), "登录成功。");
        });
    }

    public ServiceResult<T> WithSession<T>(string userName, string loginCode, Func<ILineRepository, string, ServiceResult<T>> operation) =>
        repository.Transaction(state =>
        {
            if (string.IsNullOrWhiteSpace(userName) || string.IsNullOrEmpty(loginCode) ||
                !state.Accounts.TryGetValue(userName, out var account) || account.LoginCode != loginCode)
                return ServiceResult<T>.Fail("LoginExpired", "尚未登录或登录已失效，请重新登录。当前绘制内容已保留。");
            // Validate the session and execute the map operation within the SAME data-layer transaction.
            return operation(new CurrentTransaction(state), account.UserName);
        });

    public ServiceResult<bool> Logout(SessionRequest request) => WithSession(request.UserName, request.LoginCode, (scope, name) =>
        scope.Transaction(state =>
        {
            state.Accounts[name] = state.Accounts[name] with { LoginCode = null };
            return ServiceResult<bool>.Ok(true, "已退出登录。");
        }));

    private static ServiceResult<LoginSession> LoginFailed() => ServiceResult<LoginSession>.Fail("InvalidCredentials", "用户名或密码错误。");

    // Preserve the old F.GetMd5Code byte-by-byte, including its unpadded uppercase hexadecimal format.
    private static string PasswordMd5(string password) => string.Concat(MD5.HashData(Encoding.UTF8.GetBytes(password))
        .Select(value => value.ToString("X", CultureInfo.InvariantCulture)));

    private sealed class CurrentTransaction(LineStoreState state) : ILineRepository
    {
        public T Transaction<T>(Func<LineStoreState, T> operation) => operation(state);
    }
}
