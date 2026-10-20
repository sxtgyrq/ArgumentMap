using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using ArgumentMap.Business;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using MySqlConnector;
using WpfArgumentMap;

internal static class LoginChecks
{
    private static int checks;
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        checks++;
    }
    private const string AdminHash = "21232F297A57A5A743894AE4A801FC3";

    internal static async Task<int> RunAsync()
    {
        var repository = new InMemoryLineRepository();
        var login = new LoginService(repository);
        Check(login.CreateUser(new("admin", "admin")).Success, "Independent administrator account can be created");
        Check(repository.Transaction(state => state.Accounts["admin"].Md5Password) == AdminHash,
            "Password field matches old project's unpadded uppercase MD5 format");
        Check(!login.CreateUser(new("admin", "changed")).Success, "Duplicate account creation does not replace password");
        Check(!login.Login(new("admin", "wrong")).Success && !login.Login(new("absent", "admin")).Success,
            "Wrong password and nonexistent account rejected");
        Check(!login.CreateUser(new("", "admin")).Success && !login.CreateUser(new("user", "")).Success,
            "Empty credentials cannot create accounts");

        await using var server = await LocalLineServer.StartAsync(repository: repository);
        using var http = new HttpClient { BaseAddress = server.BaseAddress };
        var loginClient = new HttpLoginClient(http);
        foreach (var (path, request) in new (string, object)[]
        {
            ("AddLine", new CreateLineRequest("admin")),
            ("StartToDraw", new LineAccessRequest("ZAAAAAAAAB", "admin")),
            ("CheckShapeIsOKThenSave", new SaveLineRequest("ZAAAAAAAAB", "admin", 0, [])),
            ("RemoveLine", new DeleteLineRequest("ZAAAAAAAAB", "admin", 0)),
            ("GetAllLineData", new SessionRequest("admin", "")),
            ("GetAllCrossData", new SessionRequest("admin", "")),
            ("CalculateCross", new SessionRequest("admin", "")),
            ("EditCross", new EditCrossRequest(CrossKey.Create("ZZZZZZZZZZ", "AAAAAAAAAA", new(0,0)), "admin", 0, CrossState.Connected)),
            ("GetLineDetail", new LineAccessRequest("ZAAAAAAAAB", "admin")),
            ("SetLineProperty", new SaveLinePropertiesRequest("ZAAAAAAAAB", "admin", 0, new LineProperties())),
            ("LockLine", new ToggleLineLockRequest("ZAAAAAAAAB", "admin", 0))
        })
        {
            using var response = await http.PostAsJsonAsync(path, request);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "Every map endpoint requires valid login: " + path);
        }
        Check(repository.Transaction(state => state.Lines.Count == 0), "Unauthenticated requests cannot consume map IDs");
        Check(!(await loginClient.LoginAsync("admin", "wrong")).Success, "HTTP reports incorrect password");
        var first = (await loginClient.LoginAsync(" admin ", "admin")).Data!;
        Check(first.UserName == "admin" && first.LoginCode.Length == 32 && first.LoginCode.All(Uri.IsHexDigit),
            "Old Login endpoint returns opaque 32-character code and client keeps trimmed account identity");
        var client = new HttpLineClient(http, first);
        var editor = new LineEditor(client);
        Check(await editor.NewLineAsync() && await editor.BeginDrawingAsync(), "Logged-in user can create and edit lines");
        editor.AddPoint(new Point(80, 80));
        editor.AddPoint(new Point(90, 80));
        var current = (await loginClient.LoginAsync("admin", "admin")).Data!;
        Check(current.LoginCode != first.LoginCode, "Repeated login rotates code and invalidates prior session");
        Check(!await editor.CompleteAsync() && editor.Mode == LineEditorMode.Drawing && editor.DraftNodes.Count == 2 &&
            editor.Message.Contains("登录"), "Expired login rejects F6 while retaining editable draft");
        Check(!(await loginClient.LogoutAsync(first)).Success, "Old session cannot log out newer session");
        client.UpdateSession(current);
        Check(await editor.CompleteAsync(), "Re-login lets user save original draft without redrawing");
        Check((await client.GetAllAsync()).Data!.Single().Owner == "admin", "Map ownership comes from authenticated account");
        Check(!(await loginClient.LoginAsync("admin", "wrong")).Success && (await client.GetAllAsync()).Success,
            "Failed login does not invalidate valid session");
        server.Accounts.CreateUser(new("other", "other"));
        var otherSession = (await loginClient.LoginAsync("other", "other")).Data!;
        using (var spoof = await http.PostAsJsonAsync("AddLine", new CreateLineRequest("admin", otherSession.LoginCode)))
            Check(spoof.StatusCode == HttpStatusCode.Unauthorized, "Another user's code cannot authenticate as admin");
        var saved = (await client.GetAllAsync()).Data!.Single();
        var other = new HttpLineClient(http, otherSession);
        Check(!(await other.StartDrawingAsync(saved.Code)).Success, "Valid login still enforces line ownership");
        Check((await loginClient.LogoutAsync(current)).Success && !(await client.GetAllAsync()).Success, "Logout invalidates subsequent map requests");
        var parallel = await Task.WhenAll(loginClient.LoginAsync("admin", "admin"), loginClient.LoginAsync("admin", "admin"));
        var validity = await Task.WhenAll(parallel.Select(result => new HttpLineClient(http, result.Data!).GetAllAsync()));
        Check(parallel.All(result => result.Success) && validity.Count(result => result.Success) == 1,
            "Concurrent logins for one account leave exactly one current session");
        return checks;
    }

    internal static async Task<int> CheckMySqlAsync(MySqlSettings settings)
    {
        var before = checks;
        var repository = new MySqlLineRepository(settings);
        var login = new LoginService(repository);
        Check(login.CreateUser(new("登录测试用户😀", "admin")).Success, "Chinese and emoji login account stored in independent database");
        var first = login.Login(new("登录测试用户😀", "admin")).Data!;
        var reopened = new LoginService(new MySqlLineRepository(settings));
        Check(reopened.WithSession(first.UserName, first.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Success,
            "Login session persists across repository/server restart");
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT Md5Password,LoginCode FROM DictLogin WHERE UserName=@name";
            query.Parameters.AddWithValue("@name", first.UserName);
            using var reader = query.ExecuteReader();
            Check(reader.Read() && reader.GetString(0) == AdminHash && reader.GetString(1) == first.LoginCode,
                "DictLogin fields match old password representation and current login code");
        }

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var write = Task.Run(() => login.WithSession(first.UserName, first.LoginCode, (scope, name) =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Login transaction test release timed out.");
            return new LineSaveService(scope).Create(new(name));
        }));
        await entered.Task;
        var rotated = Task.Run(() => reopened.Login(new(first.UserName, "admin")));
        try
        {
            await Task.WhenAny(rotated, Task.Delay(100));
            Check(!rotated.IsCompleted, "Session rotation waits for authorized write in same database transaction");
        }
        finally { release.Set(); }
        var writeResult = await write;
        var second = (await rotated).Data!;
        Check(writeResult.Success && writeResult.Data!.Owner == first.UserName, "Authorized write commits before subsequent login rotation");
        Check(!reopened.WithSession(first.UserName, first.LoginCode, (scope, name) => new LineSaveService(scope).Create(new(name))).Success,
            "Revoked session cannot perform later write");

        if (!settings.Database.StartsWith("ArgumentMap_Integration_", StringComparison.Ordinal))
            throw new InvalidOperationException("Unsafe login failure injection database.");
        using (var connection = new MySqlConnection(settings.ConnectionString()))
        {
            connection.Open();
            using var create = connection.CreateCommand();
            create.CommandText = "CREATE TRIGGER RejectLoginUpdate BEFORE UPDATE ON DictLogin FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected login transaction failure'";
            create.ExecuteNonQuery();
            try
            {
                try { reopened.Login(new(first.UserName, "admin")); throw new InvalidOperationException("Expected login SQL failure."); }
                catch (MySqlException error) when (error.Number == 1644) { checks++; }
            }
            finally
            {
                using var drop = connection.CreateCommand();
                drop.CommandText = "DROP TRIGGER RejectLoginUpdate";
                drop.ExecuteNonQuery();
            }
        }
        var afterFailure = new LoginService(new MySqlLineRepository(settings));
        Check(afterFailure.WithSession(second.UserName, second.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Success,
            "Failed login SQL transaction preserves previously valid code");
        var next = repository.Transaction(state => state.Lines.Count);
        var rejected = afterFailure.WithSession(second.UserName, second.LoginCode, (scope, name) => scope.Transaction(state =>
        {

            state.Accounts[name] = state.Accounts[name] with { LoginCode = null };
            return ServiceResult<bool>.Fail("RejectedOperation", "Injected business failure");
        }));
        Check(!rejected.Success && repository.Transaction(state => state.Lines.Count) == next &&
            afterFailure.WithSession(second.UserName, second.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Success,
            "Authenticated business rejection rolls back account and map state together");
        Check(afterFailure.Logout(new(second.UserName, second.LoginCode)).Success &&
            !new LoginService(new MySqlLineRepository(settings)).WithSession(second.UserName, second.LoginCode, (scope, _) => new LineSaveService(scope).GetAll()).Success,
            "Logout persists invalidation in MySQL");
        return checks - before;
    }
}
