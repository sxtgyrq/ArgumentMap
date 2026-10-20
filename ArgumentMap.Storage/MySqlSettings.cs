using System.Text.Json;
using MySqlConnector;

namespace ArgumentMap.Storage;

public sealed record MySqlSettings
{
    public string Server { get; init; } = "127.0.0.1";
    public uint Port { get; init; } = 3306;
    public string Database { get; init; } = "ArgumentMap";
    public string User { get; init; } = "root";
    public string Password { get; init; } = "";

    public static MySqlSettings Load(string? path = null)
    {
        path = Path.GetFullPath(path ?? Environment.GetEnvironmentVariable("ARGUMENTMAP_MYSQL_CONFIG") ?? ProjectPaths.LocalSettings);
        if (!File.Exists(path))
            throw new InvalidOperationException($"请先填写 Server 的数据库连接配置：{path}");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("MySql").Deserialize<MySqlSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("MySql 配置无效。");
    }

    public string ConnectionString(bool includeDatabase = true) => new MySqlConnectionStringBuilder
    {
        Server = Server, Port = Port, Database = includeDatabase ? Database : "",
        UserID = User, Password = Password, CharacterSet = "utf8mb4",
        ConnectionTimeout = 5, DefaultCommandTimeout = 15
    }.ConnectionString;
}
