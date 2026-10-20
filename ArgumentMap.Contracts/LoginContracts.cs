namespace ArgumentMap.Contracts;

public sealed record LoginRequest(string UserName, string Password, string DBPrefix = "ArgumentMap");
public sealed record LoginSession(string UserName, string LoginCode);
public sealed record SessionRequest(string UserName, string LoginCode);
public sealed record CreateUserRequest(string UserName, string Password);

public interface ILoginClient
{
    Task<ServiceResult<LoginSession>> LoginAsync(string userName, string password);
    Task<ServiceResult<bool>> LogoutAsync(LoginSession session);
}
