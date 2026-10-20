using System.Net.Http.Json;
using ArgumentMap.Contracts;

namespace ArgumentMap.Client;

public sealed class HttpLoginClient(HttpClient client) : ILoginClient
{
    public async Task<ServiceResult<LoginSession>> LoginAsync(string userName, string password)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("Login", new LoginRequest(userName.Trim(), password)).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return ServiceResult<LoginSession>.Fail("LoginFailed", "登录服务不可用，请稍后重试。");
            var code = (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).Trim();
            if (code.Length != 32 || !code.All(Uri.IsHexDigit)) return ServiceResult<LoginSession>.Fail("InvalidCredentials", "用户名或密码错误。");
            return ServiceResult<LoginSession>.Ok(new(userName.Trim(), code), "登录成功。");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException)
        { return ServiceResult<LoginSession>.Fail("ConnectionFailure", "无法连接登录服务，请稍后重试。"); }
    }

    public async Task<ServiceResult<bool>> LogoutAsync(LoginSession session)
    {
        try
        {
            using var response = await client.PostAsJsonAsync("Logout", new SessionRequest(session.UserName, session.LoginCode)).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<ServiceResult<bool>>().ConfigureAwait(false)
                ?? ServiceResult<bool>.Fail("InvalidResponse", "服务端没有返回退出结果。");
        }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException)
        { return ServiceResult<bool>.Fail("ConnectionFailure", "无法连接登录服务。"); }
    }
}
