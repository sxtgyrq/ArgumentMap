using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace ArgumentMap.Storage;

/// <summary>Administrative setup only. Runtime editors never reference this assembly.</summary>
public static class LocalDatabaseProvisioner
{
    public static MySqlSettings Create(MySqlSettings administrator, string outputPath)
    {
        if (administrator.Server is not ("127.0.0.1" or "localhost" or "::1"))
            throw new ArgumentException("此命令只用于同机部署，MySQL 必须在本机。分机部署请管理员按 Server IP 创建账号。");
        if (!Regex.IsMatch(administrator.Database,"^[A-Za-z][A-Za-z0-9_]{0,63}$")) throw new ArgumentException("数据库名称格式无效。");
        outputPath = Path.GetFullPath(outputPath);
        if (File.Exists(outputPath)) throw new IOException("输出配置已存在，不会覆盖。请指定新的私密配置路径。");
        var user = "am_server_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var settings = administrator with { Server="127.0.0.1",User=user,Password=Convert.ToHexString(RandomNumberGenerator.GetBytes(32)) };
        using var connection = new MySqlConnection(administrator.ConnectionString(includeDatabase:false));
        connection.Open();
        var created = new List<string>(); var createdFile = false;
        try
        {
            foreach (var host in new[] { "127.0.0.1", "localhost" })
            {
                using var create = connection.CreateCommand();
                create.CommandText = $"CREATE USER '{user}'@'{host}' IDENTIFIED BY @password";
                create.Parameters.AddWithValue("@password",settings.Password); create.ExecuteNonQuery();created.Add(host);
                using var grant = connection.CreateCommand();
                grant.CommandText = $"GRANT ALL PRIVILEGES ON `{settings.Database}`.* TO '{user}'@'{host}'";grant.ExecuteNonQuery();
            }
            // Verify the account and schema before publishing its credentials.
            _ = new MySqlLineRepository(settings);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using (var file = new FileStream(outputPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { createdFile=true;JsonSerializer.Serialize(file,new {MySql=settings},new JsonSerializerOptions {WriteIndented=true}); }
            return settings;
        }
        catch
        {
            if (createdFile) File.Delete(outputPath);
            foreach (var host in created)
            { using var remove = connection.CreateCommand();remove.CommandText=$"DROP USER IF EXISTS '{user}'@'{host}'";remove.ExecuteNonQuery(); }
            throw;
        }
    }
}
