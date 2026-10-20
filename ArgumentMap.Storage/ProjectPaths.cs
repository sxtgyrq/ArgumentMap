namespace ArgumentMap.Storage;

public static class ProjectPaths
{
    public static string Root
    {
        get
        {
            foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
                for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                    if (File.Exists(Path.Combine(directory.FullName, "ArgumentMap.Storage", "ArgumentMap.Storage.csproj")))
                        return directory.FullName;
            throw new InvalidOperationException("找不到 ArgumentMap 项目目录，请从项目目录内运行程序。");
        }
    }

    public static string LocalSettings => ResolveLocalSettings(AppContext.BaseDirectory);

    public static string ResolveLocalSettings(string applicationDirectory)
    {
        var deployed = Path.Combine(applicationDirectory, "appsettings.Local.json");
        if (File.Exists(deployed)) return deployed;
        try { return Path.Combine(Root, "ArgumentMap.Server", "appsettings.Local.json"); }
        catch (InvalidOperationException) { return deployed; }
    }

    public static string BackupDirectory
    {
        get
        {
            try { return Path.Combine(Root,".artifacts","backups"); }
            catch (InvalidOperationException) { return Path.Combine(AppContext.BaseDirectory,"Data","backups"); }
        }
    }
}
