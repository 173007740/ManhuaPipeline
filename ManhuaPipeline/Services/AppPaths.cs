namespace ManhuaPipeline.Services;

/// <summary>
/// 站点的物理根路径（wwwroot）。由 Program.cs 在启动时用 IWebHostEnvironment.WebRootPath 注入一次，
/// 全项目统一从这里取，不要再写 Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", ...)：
/// 进程工作目录取决于「怎么启动的」（VS 调试 / 直接双击 exe / dotnet run --project 从仓库根启动，
/// 三者各不相同），用工作目录拼出来的 wwwroot 会指向错误位置，表现为上传落到别处、参考图读不到。
/// </summary>
public static class AppPaths
{
    /// <summary>wwwroot 的绝对路径，由 Program.cs 启动时注入。</summary>
    public static string WebRoot { get; set; } = "";

    /// <summary>wwwroot 的绝对路径；未注入时退回进程工作目录（旧行为），保证不会返回空串。</summary>
    public static string Root => string.IsNullOrWhiteSpace(WebRoot)
        ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
        : WebRoot;

    /// <summary>
    /// 把站内相对路径（/uploads/reference/xxx.png）解析成本地物理路径；远程 http(s) 地址返回 null。
    /// </summary>
    public static string? ResolveUpload(string? localPath)
    {
        var p = (localPath ?? "").Trim();
        if (p.Length == 0) return null;
        if (p.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || p.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return null;

        // 顺序很关键：必须先判「站内路径写法」，再问 Path.IsPathRooted。
        // Windows 上 '/' 同时是 AltDirectorySeparatorChar，Path.IsPathRooted("/uploads/x.png") 返回 true，
        // 于是站内路径会被当成「当前盘符根目录下的 uploads\x.png」，File.Exists 永远为 false，
        // 参考图出图会全部报「没有可用的参考图（文件不存在或读取失败）」。
        if (!p.StartsWith('/') && !p.StartsWith('\\') && Path.IsPathRooted(p)) return p;

        var rel = p.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(Root, rel);
    }
}
