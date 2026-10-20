using System.Text.Json;

namespace ArgumentMap.Client;

/// <summary>Only an HTTP endpoint; no database credentials or persistence dependencies.</summary>
public sealed record ClientConnectionSettings
{
    public string ServerUrl { get; init; } = "http://127.0.0.1:61011/";

    public static Uri ParseAddress(string? address)
    {
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Host is "0.0.0.0" or "::" or "[::]" || uri.AbsolutePath != "/")
            throw new ArgumentException("Server 地址必须为 http://服务器IP:端口/ 或 https://服务器名:端口/，不能包含账号密码、路径、查询参数或片段。");
        return uri;
    }

    public static ClientConnectionSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            var settings = JsonSerializer.Deserialize<ClientConnectionSettings>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow })
                ?? throw new JsonException();
            return settings with { ServerUrl = ParseAddress(settings.ServerUrl).AbsoluteUri };
        }
        catch (Exception e) when (e is JsonException or ArgumentException)
        { throw new InvalidOperationException($"客户端连接配置无效，请检查 {path}。只允许 ServerUrl 字段。", e); }
    }

    public void Save(string path)
    {
        var normalized = this with { ServerUrl = ParseAddress(ServerUrl).AbsoluteUri };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(normalized, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
