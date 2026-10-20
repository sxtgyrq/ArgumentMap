using System.IO;
using System.Net.Http;
using System.Windows;
using ArgumentMap.Client;
using ArgumentMap.Contracts;

namespace WpfArgumentMap;

public partial class App : Application
{
    private HttpClient? httpClient;
    private HttpLoginClient? loginClient;
    private HttpLineClient? lineClient;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory,"clientsettings.json");
            var settings = ClientConnectionSettings.Load(path);
            // The editor never starts a server or loads database settings.
            var login = new LoginWindow(null, serverUrl: settings.ServerUrl, connect: ConnectAndLoginAsync);
            if (login.ShowDialog() != true || login.Session is null || httpClient is null || loginClient is null)
            { Shutdown(); return; }
            try { new ClientConnectionSettings { ServerUrl = httpClient.BaseAddress!.AbsoluteUri }.Save(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { /* Read-only installation: the address remains editable at the next login. */ }
            lineClient = new HttpLineClient(httpClient, login.Session);
            var window = new MainWindow(lineClient, loginClient);
            MainWindow = window;
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            window.Show();
            await window.InitializeLinesAsync();
        }
        catch (Exception exception)
        {
            MessageBox.Show($"无法打开地图编辑器：{exception.Message}", "Argument Map", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task<ServiceResult<LoginSession>> ConnectAndLoginAsync(string address,string user,string password)
    {
        Uri uri;
        try { uri = ClientConnectionSettings.ParseAddress(address); }
        catch (ArgumentException error) { return ServiceResult<LoginSession>.Fail("InvalidServerAddress",error.Message); }
        var candidate = new HttpClient { BaseAddress = uri, Timeout = TimeSpan.FromSeconds(30) };
        var candidateLogin = new HttpLoginClient(candidate);
        var result = await candidateLogin.LoginAsync(user,password);
        if (!result.Success) { candidate.Dispose(); return result; }
        httpClient?.Dispose(); httpClient = candidate; loginClient = candidateLogin;
        return result;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (lineClient is not null && loginClient is not null)
            loginClient.LogoutAsync(lineClient.Session).GetAwaiter().GetResult();
        httpClient?.Dispose();
        base.OnExit(e);
    }
}
