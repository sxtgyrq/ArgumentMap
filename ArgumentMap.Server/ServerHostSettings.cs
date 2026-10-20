using System.Text.Json;

namespace ArgumentMap.Server;

public sealed record ServerHostSettings
{
    public string ListenUrl { get; init; } = "http://0.0.0.0:61011";

    public static ServerHostSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var settings = document.RootElement.TryGetProperty("Server",out var section)
            ? section.Deserialize<ServerHostSettings>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Server 配置无效。")
            : new();
        Validate(settings.ListenUrl);
        return settings;
    }

    public static string Validate(string? address)
    {
        if (!Uri.TryCreate(address,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") ||
            string.IsNullOrEmpty(uri.Host) || uri.AbsolutePath != "/" || !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("Server 监听地址必须为单个 HTTP/HTTPS 地址，例如 http://0.0.0.0:61011。");
        return address!;
    }
}
