using System.Net.Http;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;

internal static class TestLogin
{
    private const string Password = "IntegrationOnlyPassword";

    internal static async Task<HttpLineClient> ClientAsync(HttpClient http, LocalLineServer server, string name)
    {
        var created = server.Accounts.CreateUser(new CreateUserRequest(name, Password));
        if (!created.Success && created.Code != "UserExists") throw new InvalidOperationException(created.Message);
        var result = await new HttpLoginClient(http).LoginAsync(name, Password).ConfigureAwait(false);
        if (!result.Success || result.Data is null) throw new InvalidOperationException(result.Message);
        return new HttpLineClient(http, result.Data);
    }
}
