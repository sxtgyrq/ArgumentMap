using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArgumentMap.Client;
using ArgumentMap.Contracts;
using ArgumentMap.Rules;
using ArgumentMap.Server;
using ArgumentMap.Storage;
using ArgumentMap.Business;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MySqlConnector;
using WpfArgumentMap;

namespace ArgumentMap.Tests;

[TestClass,TestCategory("Unit")]
public sealed class ClientServerDeploymentTests
{
    public TestContext TestContext { get; set; }=null!;
    internal static string DirectoryForTest()
    {var path=Path.Combine(ProjectPaths.Root,".artifacts","deployment-tests",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}

    [TestMethod]
    [DataRow("http://192.168.1.10:61011","http://192.168.1.10:61011/")]
    [DataRow("  http://localhost:61011/  ","http://localhost:61011/")]
    [DataRow("https://mapserver:7443/","https://mapserver:7443/")]
    [DataRow("http://[::1]:61011/","http://[::1]:61011/")]
    public void ClientAcceptsOnlyUsableHttpOrigin(string text,string expected)=>Assert.AreEqual(expected,ClientConnectionSettings.ParseAddress(text).AbsoluteUri);

    [TestMethod]
    [DataRow("")][DataRow("192.168.1.10:61011")][DataRow("file:///C:/private/settings")]
    [DataRow("mysql://localhost")][DataRow("http://root:secret@server:61011/")]
    [DataRow("http://server:61011/?password=secret")][DataRow("http://server:61011/#secret")]
    [DataRow("http://0.0.0.0:61011/")][DataRow("http://[::]:61011/")][DataRow("http://server:61011/Login")]
    public void ClientRejectsDatabaseCredentialsNonHttpAndListenAddresses(string text)=>Assert.ThrowsExactly<ArgumentException>(()=>ClientConnectionSettings.ParseAddress(text));

    [TestMethod]
    public void ClientConfigurationRoundTripStoresOnlyAddress_AndRejectsUnknownFields()
    {
        var path=Path.Combine(DirectoryForTest(),"clientsettings.json");Assert.AreEqual("http://127.0.0.1:61011/",ClientConnectionSettings.Load(path).ServerUrl);
        new ClientConnectionSettings{ServerUrl="http://192.168.1.44:61011"}.Save(path);
        Assert.AreEqual("http://192.168.1.44:61011/",ClientConnectionSettings.Load(path).ServerUrl);
        using(var document=JsonDocument.Parse(File.ReadAllText(path)))Assert.AreEqual("ServerUrl",document.RootElement.EnumerateObject().Single().Name);
        foreach(var text in new[]{"null","not json","{}\ntrailing","{\"ServerUrl\":null}","{\"MySql\":{\"User\":\"root\"}}","{\"ServerUrl\":\"file:///C:/private\"}"})
        {File.WriteAllText(path,text);Assert.ThrowsExactly<InvalidOperationException>(()=>ClientConnectionSettings.Load(path));}
        Assert.AreEqual(0,Directory.GetFiles(Path.GetDirectoryName(path)!,"*.tmp").Length);
    }

    [TestMethod]
    public void WpfDependencyClosureContainsNoServerStorageBusinessMySqlOrAspNet()
    {
        var visited=new HashSet<string>();
        void Visit(Assembly assembly)
        {
            if(!visited.Add(assembly.GetName().Name!))return;
            foreach(var reference in assembly.GetReferencedAssemblies())
            {
                var name=reference.Name!;
                Assert.IsFalse(name is "ArgumentMap.Server" or "ArgumentMap.Storage" or "ArgumentMap.Business" || name.Contains("MySql",StringComparison.OrdinalIgnoreCase) || name.StartsWith("Microsoft.AspNetCore",StringComparison.Ordinal),name);
                if(name.StartsWith("ArgumentMap.",StringComparison.Ordinal))Visit(Assembly.Load(reference));
            }
        }
        Visit(typeof(App).Assembly);
        CollectionAssert.IsSubsetOf(new[]{"ArgumentMap.Client","ArgumentMap.Contracts","ArgumentMap.Rules"},visited.ToArray());
        var clientProject=System.Xml.Linq.XDocument.Load(Path.Combine(ProjectPaths.Root,"WpfArgumentMap","WpfArgumentMap.csproj"));
        var projectReferences=clientProject.Descendants("ProjectReference").Select(e=>(string?)e.Attribute("Include")??"").ToArray();
        Assert.IsFalse(projectReferences.Any(p=>p.Contains("ArgumentMap.Server",StringComparison.Ordinal)),"Check references, not the target that deletes legacy server files.");
        var startup=File.ReadAllText(Path.Combine(ProjectPaths.Root,"WpfArgumentMap","App.xaml.cs"));
        Assert.IsFalse(startup.Contains("LocalLineServer",StringComparison.Ordinal));Assert.IsFalse(startup.Contains("MySqlSettings",StringComparison.Ordinal));
    }

    [TestMethod]
    public void ServerConfigurationAndDeployedPrivateSettingsResolveWithoutSourceLayout()
    {
        var directory=DirectoryForTest();var path=Path.Combine(directory,"appsettings.json");
        Assert.AreEqual("http://0.0.0.0:61011",ServerHostSettings.Load(path).ListenUrl);
        File.WriteAllText(path,"{\"Server\":{\"ListenUrl\":\"http://192.168.1.2:62000\"}}");Assert.AreEqual("http://192.168.1.2:62000",ServerHostSettings.Load(path).ListenUrl);
        foreach(var address in new[]{"file:///private","http://user:pass@server","http://server/path","http://server?key=secret","http://server/#fragment"})
            Assert.ThrowsExactly<ArgumentException>(()=>ServerHostSettings.Validate(address));
        var privatePath=Path.Combine(directory,"appsettings.Local.json");File.WriteAllText(privatePath,JsonSerializer.Serialize(new{MySql=new MySqlSettings{User="server_only",Database="ArgumentMap"}}));
        Assert.AreEqual(privatePath,ProjectPaths.ResolveLocalSettings(directory));Assert.AreEqual("server_only",MySqlSettings.Load(privatePath).User);
        Assert.ThrowsExactly<InvalidOperationException>(()=>MySqlSettings.Load(Path.Combine(directory,"missing.json")));
    }

    [STATestMethod,TestCategory("Rendering")]
    public void LoginAddressIsEditable_FailureCanChangeServer_BusyPreventsDuplicateLoginAndClose()
    {
        var attempts=new List<(string Address,string User,string Password)>();var gate=new TaskCompletionSource<ServiceResult<LoginSession>>();
        var window=new LoginWindow(null,serverUrl:"http://192.168.1.10:61011/",connect:(address,user,password)=>{attempts.Add((address,user,password));return gate.Task;})
        {Left=-10000,Top=-10000,WindowStartupLocation=WindowStartupLocation.Manual,ShowInTaskbar=false,ShowActivated=false};
        try
        {
            window.Show();window.UpdateLayout();var address=(TextBox)window.FindName("ServerAddressInput");var user=(TextBox)window.FindName("UserNameBox");var password=(PasswordBox)window.FindName("PasswordInput");var login=(Button)window.FindName("LoginButton");
            Assert.IsTrue(address.IsVisible);Assert.AreEqual("http://192.168.1.10:61011/",address.Text);
            user.Text="alice";password.Password="application_password";login.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.IsFalse(address.IsEnabled);login.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.Close();Assert.IsTrue(window.IsVisible);Assert.AreEqual(1,attempts.Count);
            gate.SetResult(ServiceResult<LoginSession>.Fail("ConnectionFailure","无法连接 Server"));LogicalNodeWindowEntryTests.DrainUntil(()=>address.IsEnabled);
            Assert.AreEqual("",password.Password);address.Text="http://192.168.1.20:61011/";password.Password="retry";gate=new();login.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.AreEqual(2,attempts.Count);Assert.AreEqual((address.Text,"alice","retry"),attempts[1]);
            gate.SetResult(ServiceResult<LoginSession>.Fail("InvalidCredentials","用户名或密码错误"));LogicalNodeWindowEntryTests.DrainUntil(()=>address.IsEnabled);
            window.UpdateLayout();var bitmap=new RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
            var imagePath=Path.Combine(ProjectPaths.Root,".artifacts","renders","server-login.png");Directory.CreateDirectory(Path.GetDirectoryName(imagePath)!);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create(imagePath))encoder.Save(stream);TestContext.AddResultFile(imagePath);
        }
        finally{window.Close();}
    }

    [TestMethod,TestCategory("Http")]
    public async Task UnavailableServerReturnsConnectionFailure_WithoutLocalFallback()
    {
        using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}/"),Timeout=TimeSpan.FromSeconds(2)};
        Assert.AreEqual("ConnectionFailure",(await new HttpLoginClient(http).LoginAsync("alice","password")).Code);
        Assert.AreEqual("ConnectionFailure",(await new HttpLineClient(http,new("alice","expired")).GetAllAsync()).Code);
    }

}

[TestClass,TestCategory("MySql"),TestCategory("Http")]
public sealed class IndependentServerProcessTests
{
    [TestMethod]
    public Task IndependentServerProcess_UsesLocalScopedAccount_AndEnforcesValidationRollbackAndConcurrentClients()=>LogicalNodeMySqlTests.Isolated(async admin=>
    {
        var repository=new MySqlLineRepository(admin);var accounts=new LoginService(repository);Assert.IsTrue(accounts.CreateUser(new("alice","password")).Success);Assert.IsTrue(accounts.CreateUser(new("bob","password")).Success);
        var directory=ClientServerDeploymentTests.DirectoryForTest();var settingsPath=Path.Combine(directory,"appsettings.Local.json");MySqlSettings? scoped=null;Process? process=null;
        try
        {
            scoped=LocalDatabaseProvisioner.Create(admin,settingsPath);Assert.AreNotEqual(admin.User,scoped.User);Assert.AreNotEqual(admin.Password,scoped.Password);
            Assert.ThrowsExactly<IOException>(()=>LocalDatabaseProvisioner.Create(admin,settingsPath));
            using(var connection=new MySqlConnection(admin.ConnectionString(false)))
            {
                await connection.OpenAsync();using var query=connection.CreateCommand();query.CommandText="SELECT Host FROM mysql.user WHERE User=@user ORDER BY Host";query.Parameters.AddWithValue("@user",scoped.User);
                using var reader=await query.ExecuteReaderAsync();var hosts=new List<string>();while(await reader.ReadAsync())hosts.Add(reader.GetString(0));CollectionAssert.AreEqual(new[]{"127.0.0.1","localhost"},hosts);
            }
            using(var limited=new MySqlConnection(scoped.ConnectionString(false)))
            {await limited.OpenAsync();using var denied=limited.CreateCommand();denied.CommandText="SELECT User FROM mysql.user";await Assert.ThrowsExactlyAsync<MySqlException>(()=>denied.ExecuteScalarAsync());}
            // Copy only the independent server's declared runtime dependencies, not the source tree.
            var output=AppContext.BaseDirectory;using(var deps=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"ArgumentMap.Server.deps.json"))))
            {
                foreach(var library in deps.RootElement.GetProperty("targets").EnumerateObject().First().Value.EnumerateObject())
                    if(library.Value.TryGetProperty("runtime",out var runtime))foreach(var asset in runtime.EnumerateObject())
                    {var name=Path.GetFileName(asset.Name);File.Copy(Path.Combine(output,name),Path.Combine(directory,name),true);}
            }
            foreach(var name in new[]{"ArgumentMap.Server.deps.json","ArgumentMap.Server.runtimeconfig.json"})File.Copy(Path.Combine(output,name),Path.Combine(directory,name));
            using var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();var address=new Uri($"http://127.0.0.1:{port}/");
            var start=new ProcessStartInfo("dotnet"){WorkingDirectory=directory,UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            start.ArgumentList.Add(Path.Combine(directory,"ArgumentMap.Server.dll"));start.ArgumentList.Add("--urls");start.ArgumentList.Add(address.AbsoluteUri);start.Environment.Remove("ARGUMENTMAP_MYSQL_CONFIG");
            process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            using var firstHttp=new HttpClient{BaseAddress=address,Timeout=TimeSpan.FromSeconds(5)};var login=new HttpLoginClient(firstHttp);ServiceResult<LoginSession>? session=null;
            for(var attempt=0;attempt<80;attempt++)
            {if(process.HasExited)Assert.Fail("独立 Server 启动失败。请检查运行时和仅服务端配置。");session=await login.LoginAsync("alice","password");if(session.Success)break;await Task.Delay(100);}
            Assert.IsTrue(session?.Success);var alice=new HttpLineClient(firstHttp,session!.Data!);
            using var secondHttp=new HttpClient{BaseAddress=address};var bobSession=await new HttpLoginClient(secondHttp).LoginAsync("bob","password");Assert.IsTrue(bobSession.Success);var bob=new HttpLineClient(secondHttp,bobSession.Data!);
            var road=await LogicalNodeMySqlTests.Add(alice,90,110,130,110);Assert.AreEqual(road.Code,(await bob.GetAllAsync()).Data!.Single().Code);
            var before=LineTopologyMySqlTests.RawSnapshot(admin);Assert.IsFalse((await bob.SaveAsync(road.Code,road.Version,TestMap.Shape(road,90,111,130,111).Nodes)).Success);Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(admin));
            LogicalNodeMySqlTests.Sql(admin,"CREATE TRIGGER DeploymentLNFailure BEFORE INSERT ON LogicalNodePositions FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='Injected deployment write failure'");
            try{Assert.AreEqual("DatabaseError",(await alice.CreateLogicalNodeAsync(110.5m,110.5m,6,new())).Code);}finally{LogicalNodeMySqlTests.Sql(admin,"DROP TRIGGER DeploymentLNFailure");}
            Assert.AreEqual(before,LineTopologyMySqlTests.RawSnapshot(admin));
            var create1=alice.CreateLogicalNodeAsync(110.5m,110.5m,6,new(){Value="中文节点"});var create2=bob.CreateLogicalNodeAsync(110.5m,110.5m,6,new());await Task.WhenAll(create1,create2);
            Assert.AreEqual(1,(create1.Result.Success?1:0)+(create2.Result.Success?1:0));Assert.AreEqual(1,(await alice.GetLogicalNodesAsync()).Data!.Single().Positions.Length);
            process.Kill(entireProcessTree:true);await process.WaitForExitAsync();Assert.AreEqual("ConnectionFailure",(await alice.GetAllAsync()).Code);
            Assert.IsFalse(Directory.GetFiles(directory,"*.csproj").Any());Assert.IsFalse(Directory.Exists(Path.Combine(directory,"ArgumentMap.Storage")));
        }
        finally
        {
            if(process is not null){if(!process.HasExited){process.Kill(entireProcessTree:true);await process.WaitForExitAsync();}process.Dispose();}
            if(scoped is not null)
            {
                using var pool=new MySqlConnection(scoped.ConnectionString());MySqlConnection.ClearPool(pool);
                using var connection=new MySqlConnection(admin.ConnectionString(false));await connection.OpenAsync();using var drop=connection.CreateCommand();drop.CommandText=$"DROP USER IF EXISTS '{scoped.User}'@'127.0.0.1','{scoped.User}'@'localhost'";await drop.ExecuteNonQueryAsync();
            }
            // Private test credentials are not left in artifacts.
            if(File.Exists(settingsPath))File.Delete(settingsPath);
        }
    });
}
