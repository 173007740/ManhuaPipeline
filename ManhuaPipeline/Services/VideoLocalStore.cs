using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ManhuaPipeline.Services;

/// <summary>
/// 本地视频文件的公共设施：
/// ① 同一远程 URL 只下载一次（修掉"并发轮询各下一份、磁盘上堆同内容副本"的问题）；
/// ② 把"字节完全相同"的重复文件搬到隔离目录（只搬不删）。
/// </summary>
internal static class VideoLocalStore
{
    // ================= ① 下载去重 =================

    private static readonly ConcurrentDictionary<string, Task<string?>> InFlight = new();
    private static readonly ConcurrentDictionary<string, string> Downloaded = new();

    /// <summary>
    /// 同一个远程 videoUrl 只真正下载一次：并发调用共享同一份结果，重复调用直接复用已下好的本地文件。
    /// <para>注意：去重键必须是"远程 URL"，不能是 promptId —— 同一镜头的多次重跑、增强前后的视频
    /// 都是**不同的 URL**（内容也不同），必须各下一份，不能误挡。</para>
    /// <para>缓存只在内存里，进程重启即失效；重启后已完成的任务不会再走下载分支，无影响。</para>
    /// </summary>
    public static async Task<string?> DownloadOnceAsync(
        string? videoUrl,
        Func<Task<string?>> download,
        ILogger? logger = null,
        string? source = null)
    {
        if (string.IsNullOrWhiteSpace(videoUrl)) return null;

        for (var attempt = 0; attempt < 3; attempt++)
        {
            // 已经下过且文件还在 → 直接复用
            if (Downloaded.TryGetValue(videoUrl, out var cached))
            {
                if (Exists(cached))
                {
                    logger?.LogInformation("[{Source}] 复用已下载的本地视频 {LocalUrl}（同一远程地址不重复下载）", source, cached);
                    return cached;
                }
                Downloaded.TryRemove(new KeyValuePair<string, string>(videoUrl, cached));
            }

            // 别人正在下同一个 URL → 等它的结果，不再发一次请求
            if (InFlight.TryGetValue(videoUrl, out var running))
            {
                var shared = await running;
                if (!string.IsNullOrEmpty(shared) && Exists(shared)) return shared;
                continue; // 对方失败或文件已被清理，自己再试一次
            }

            var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!InFlight.TryAdd(videoUrl, tcs.Task)) continue; // 被抢先，回到循环去等它

            try
            {
                string? local;
                try
                {
                    local = await download();
                }
                catch
                {
                    tcs.TrySetResult(null);
                    throw;
                }

                if (!string.IsNullOrEmpty(local))
                {
                    if (Downloaded.Count > 5000) Downloaded.Clear(); // 简单封顶，避免无限增长
                    Downloaded[videoUrl] = local;
                }
                tcs.TrySetResult(local);
                return local;
            }
            finally
            {
                InFlight.TryRemove(new KeyValuePair<string, Task<string?>>(videoUrl, tcs.Task));
            }
        }

        return null;
    }

    /// <summary>
    /// 边下边写：把远程文件流式拷到本地路径，**不把整个文件读进内存**。
    /// <para>原来的 GetByteArrayAsync + WriteAllBytesAsync 会一次性申请"整个视频大小"的托管大对象数组
    /// （几十 MB 直接进 LOH）。多个视频同时完成时，几份这样的数组叠加会触发 Full GC，
    /// 表现为整个站点在那几秒内不响应（"刷新卡进程"）。流式拷贝的常驻内存只有几十 KB。</para>
    /// </summary>
    public static async Task DownloadToFileAsync(HttpClient http, string url, string filePath, CancellationToken ct = default)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var dst = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true);
        await src.CopyToAsync(dst, 1024 * 1024, ct);
    }

    /// <summary>本地 URL（/uploads/...）→ 物理路径</summary>
    public static string? MapToPhysicalPath(string? localUrl)
    {
        if (string.IsNullOrEmpty(localUrl)) return null;
        var rel = localUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        return Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", rel);
    }

    private static bool Exists(string localUrl)
    {
        var path = MapToPhysicalPath(localUrl);
        return !string.IsNullOrEmpty(path) && File.Exists(path);
    }

    // ================= ② 重复文件整理（只搬不删） =================

    /// <summary>被搬走的重复文件</summary>
    public sealed record MovedFile(string FileName, string From, string To, string KeptFileName, long Size);

    /// <summary>整理结果</summary>
    public sealed record DedupeReport(
        bool DryRun,
        int ScannedFiles,
        int DuplicateFiles,
        int KeptFiles,
        long DuplicateBytes,
        string QuarantineRoot,
        List<MovedFile> Items,
        List<string> Skipped);

    /// <summary>
    /// 扫描一个项目目录，按**文件内容（SHA-256）**分组，组内只留一份、其余搬到隔离目录。
    /// <para>判定标准只有"字节完全相同"，所以：</para>
    /// <list type="bullet">
    ///   <item>重跑出来的不同版本、增强前后的视频 → 内容不同 → 永远不在同一组 → 一律不动；</item>
    ///   <item>只清理"同一次生成被重复下载多遍"留下的副本。</item>
    /// </list>
    /// <para>保留优先级：数据库引用中的那份（避免前端/数据库指向失效）&gt; 修改时间最早的。
    /// 只搬不删，随时可以从隔离目录取回。dryRun=true 时只出清单、不移动任何文件。</para>
    /// </summary>
    public static async Task<DedupeReport> QuarantineByteIdenticalDuplicatesAsync(
        string projectVideoDir,
        string quarantineDir,
        ISet<string> referencedLocalUrls,
        bool dryRun)
    {
        var items = new List<MovedFile>();
        var skipped = new List<string>();
        var scanned = 0;
        var keptFiles = 0;
        long duplicateBytes = 0;

        if (!Directory.Exists(projectVideoDir))
            return new DedupeReport(dryRun, 0, 0, 0, 0, quarantineDir, items, skipped);

        var projectFolder = Path.GetFileName(
            projectVideoDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        // 按内容 hash 分组
        var groups = new Dictionary<string, List<FileInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(projectVideoDir, "*", SearchOption.TopDirectoryOnly))
        {
            FileInfo fi;
            try { fi = new FileInfo(path); } catch { continue; }
            if (!fi.Exists || fi.Length == 0) continue;

            scanned++;
            string hash;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                hash = Convert.ToHexString(await SHA256.HashDataAsync(fs));
            }
            catch (Exception ex)
            {
                skipped.Add(fi.Name + "（读取失败：" + ex.Message + "）");
                continue;
            }

            if (!groups.TryGetValue(hash, out var list)) groups[hash] = list = new List<FileInfo>();
            list.Add(fi);
        }

        foreach (var files in groups.Values)
        {
            if (files.Count < 2) { keptFiles++; continue; }

            var ordered = files
                .OrderBy(f => IsReferenced(projectFolder, f.Name, referencedLocalUrls) ? 0 : 1)
                .ThenBy(f => f.LastWriteTimeUtc)
                .ThenBy(f => f.Name, StringComparer.Ordinal)
                .ToList();

            var keeper = ordered[0];
            keptFiles++;

            foreach (var dup in ordered.Skip(1))
            {
                var target = UniqueTargetPath(quarantineDir, dup.Name);
                if (!dryRun)
                {
                    try
                    {
                        Directory.CreateDirectory(quarantineDir);
                        File.Move(dup.FullName, target);
                    }
                    catch (Exception ex)
                    {
                        skipped.Add(dup.Name + "（搬移失败：" + ex.Message + "）");
                        continue;
                    }
                }

                items.Add(new MovedFile(dup.Name, dup.FullName, target, keeper.Name, dup.Length));
                duplicateBytes += dup.Length;
            }
        }

        return new DedupeReport(dryRun, scanned, items.Count, keptFiles, duplicateBytes, quarantineDir, items, skipped);
    }

    private static bool IsReferenced(string projectFolder, string fileName, ISet<string> referencedLocalUrls)
        => referencedLocalUrls.Contains($"/uploads/videos/{projectFolder}/{fileName}");

    private static string UniqueTargetPath(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target)) return target;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem}_重复{i}{ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
