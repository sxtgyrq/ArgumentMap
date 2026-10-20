using ArgumentMap.Server;
using ArgumentMap.Storage;
using MySqlConnector;
using ArgumentMap.Business;
using ArgumentMap.Contracts;

if (args.FirstOrDefault() == "--provision-local-db")
{
    if (args.Length != 2) { Console.Error.WriteLine("Usage: ArgumentMap.Server --provision-local-db PRIVATE_CONFIG_PATH"); Environment.ExitCode=1;return; }
    LocalDatabaseProvisioner.Create(MySqlSettings.Load(),args[1]);
    Console.WriteLine("已创建仅供 Server 本机使用的独立数据库账号；私密配置已写入指定文件。请用 --settings 指定该文件启动 Server，不要分发到客户端。");
    return;
}

if (args.FirstOrDefault() == "--create-user")
{
    if (args.Length != 2) { Console.Error.WriteLine("Usage: ArgumentMap.Server --create-user USERNAME"); Environment.ExitCode = 1; return; }
    string password;
    if (Console.IsInputRedirected) password = Console.ReadLine() ?? "";
    else
    {
        Console.Write("密码：");
        var input = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (input.Length > 0) input.Length--; }
            else if (!char.IsControl(key.KeyChar)) input.Append(key.KeyChar);
        }
        Console.WriteLine();
        password = input.ToString();
    }
    var repository = new MySqlLineRepository(MySqlSettings.Load());
    var result = new LoginService(repository).CreateUser(new(args[1], password));
    Console.WriteLine(result.Message);
    if (!result.Success) Environment.ExitCode = 1;
    return;
}

if (args.FirstOrDefault() == "--initialize-db")
{
    var repository = new MySqlLineRepository(MySqlSettings.Load());
    var recalculated = new CrossService(repository).Recalculate();
    if (!recalculated.Success) { Console.Error.WriteLine(recalculated.Message); Environment.ExitCode = 1; return; }
    var counts = repository.Transaction(state => (Lines: state.Lines.Count, Nodes: state.Lines.Values.Sum(line => line.Nodes.Length), Crosses: state.Crosses.Count));
    using var connection = new MySqlConnection(MySqlSettings.Load().ConnectionString());
    connection.Open();
    using var encoding = connection.CreateCommand();
    encoding.CommandText = "SELECT DEFAULT_CHARACTER_SET_NAME FROM information_schema.SCHEMATA WHERE SCHEMA_NAME=@name";
    encoding.Parameters.AddWithValue("@name", repository.DatabaseName);
    Console.WriteLine($"MySQL database {repository.DatabaseName} initialized. Encoding: {encoding.ExecuteScalar()}. Lines: {counts.Lines}; Nodes: {counts.Nodes}; Crosses: {counts.Crosses}.");
    return;
}

var address = ServerHostSettings.Load(Path.Combine(AppContext.BaseDirectory,"appsettings.json")).ListenUrl;
string? settingsPath = null;
for (var i = 0; i < args.Length; i++)
{
    if (args[i] == "--urls" && i + 1 < args.Length) address = args[++i];
    else if (args[i] == "--settings" && i + 1 < args.Length) settingsPath = args[++i];
    else if (i == 0 && args[i].StartsWith("http",StringComparison.OrdinalIgnoreCase)) address = args[i];
    else { Console.Error.WriteLine("Usage: ArgumentMap.Server [--urls http://0.0.0.0:61011] [--settings PATH]"); Environment.ExitCode = 1; return; }
}
ServerHostSettings.Validate(address);
await using var server = await LocalLineServer.StartAsync(address, settings: MySqlSettings.Load(settingsPath));
Console.WriteLine($"Argument Map server: {server.BaseAddress}");
Console.WriteLine("客户端请填写本机的局域网 IP 和端口；0.0.0.0 是监听地址，不能作为客户端连接地址。");
await server.WaitForShutdownAsync();
