using System.Text;
using System.Text.Json;
using System.IO;
using ManhuaPipeline.Models;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

public class VideoService
{
    private readonly HttpClient _http;
    private readonly DbService _db;
    private readonly ILogger<VideoService> _logger;
    private static readonly Dictionary<int, VideoTaskInfo> _tasks = new();

    public VideoService(HttpClient http, DbService db, ILogger<VideoService> logger)
    {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public class VideoTaskInfo
    {
        public string TaskId { get; set; } = "";
        public string Status { get; set; } = "pending";
        public string? VideoUrl { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public string? Error { get; set; }
        public int ProjectId { get; set; }
    }

    public record VideoGenSettings
    {
        public int Duration { get; init; } = 11;
        public string Ratio { get; init; } = "16:9";
        public bool Watermark { get; init; } = false;
        public bool GenerateAudio { get; init; } = true;
    public string? Resolution { get; init; }
    }

    /// <summary>
    /// 提交视频生成任务
    /// </summary>
    public async Task<VideoTaskInfo> SubmitTask(int projectId, int promptId, string promptText, string apiKey, string apiUrl, string model, List<string>? refImages = null, List<string>? refVideos = null, List<string>? refAudio = null, VideoGenSettings? settings = null)
    {
        settings ??= new VideoGenSettings();
        var activeImgs = refImages?.Where(x => !string.IsNullOrEmpty(x)).ToList() ?? new List<string>();
        var effectivePrompt = promptText;
        // 标题行只用于镜头切分与元数据提取，不进入最终视频提示词（兼容旧库已保存的正文）。
        effectivePrompt = System.Text.RegularExpressions.Regex.Replace(effectivePrompt, @"^\s*【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】\s*(\r?\n|$)", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        // 参考图说明行不进入视频正文，图片通过 reference_image 单独传入；@图N 绑定行保留。
        var promptLines = effectivePrompt.Split('\n').Where(l => !l.TrimStart().StartsWith("参考图:")).ToArray();
        effectivePrompt = string.Join("\n", promptLines).Trim();
        var contentList = new List<object>();
        contentList.Add(new { type = "text", text = effectivePrompt });
        if (activeImgs.Count > 0)
            foreach (var img in activeImgs)
            {
                var url = img;
                if (url.StartsWith("/uploads/") && !url.StartsWith("data:"))
                {
                    var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", url.TrimStart('/'));
                    if (File.Exists(filePath))
                    {
                        var ext = Path.GetExtension(filePath).ToLower();
                        var mime = ext == ".png" ? "image/png" : ext == ".jpg" || ext == ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : ext == ".gif" ? "image/gif" : "image/png";
                        var bytes = File.ReadAllBytes(filePath);
                        url = $"data:{mime};base64,{Convert.ToBase64String(bytes)}";
                    }
                }
                contentList.Add(new { type = "image_url", image_url = new { url = url }, role = "reference_image" });
            }

        if (refVideos != null)
            foreach (var vid in refVideos)
                contentList.Add(new { type = "video_url", video_url = new { url = vid }, role = "reference_video" });
        if (refAudio != null)
            foreach (var aud in refAudio)
                contentList.Add(new { type = "audio_url", audio_url = new { url = aud }, role = "reference_audio" });

        var body = new
        {
            model = model,
            content = contentList,
            generate_audio = settings.GenerateAudio,
            ratio = settings.Ratio,
            duration = settings.Duration,
            watermark = settings.Watermark,
            resolution = settings.Resolution ?? "720p"
        };

        var req = new HttpRequestMessage(HttpMethod.Post, apiUrl.TrimEnd('/') + "/contents/generations/tasks");
        req.Headers.Add("Authorization", $"Bearer {apiKey}");
        req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode) { _logger.LogWarning("[VideoService] API Error: {Response}", json); resp.EnsureSuccessStatusCode(); }
        using var doc = JsonDocument.Parse(json);

        var taskId = doc.RootElement.GetProperty("id").GetString() ?? "";
        var apiSubmitStatus = doc.RootElement.TryGetProperty("status", out var stEl) ? stEl.GetString() : "processing";

        var info = new VideoTaskInfo
        {
            TaskId = taskId,
            Status = "processing",
            CreatedAt = DateTime.Now,
            ProjectId = projectId
        };

        lock (_tasks) { _tasks[promptId] = info; }

        // 更新 SeedancePrompts 状态（重新生成时清掉旧视频地址）
        _db.ResetPromptVideo(promptId);

        // 保存任务记录到 VideoGenerationTasks
        _db.SaveVideoTask(new VideoGenerationTask
        {
            ProjectId = projectId,
            PromptId = promptId,
            TaskId = taskId,
            Status = "processing",
            ApiStatus = apiSubmitStatus,
            RequestDuration = settings.Duration,
            RequestRatio = settings.Ratio,
            RequestWatermark = settings.Watermark,
            RequestGenerateAudio = settings.GenerateAudio,
            CreatedAt = DateTime.Now
        });

        return info;
    }

        /// <summary>
    /// 失败时把该提示词使用的参考图文件名附加到错误信息，便于定位版权问题图片
    /// </summary>
    public static string? AppendRefImageInfo(string? error, string? refImagesJson)
    {
        var names = new List<string>();
        if (!string.IsNullOrWhiteSpace(refImagesJson))
        {
            try
            {
                var arr = JsonSerializer.Deserialize<List<string>>(refImagesJson);
                if (arr != null)
                    foreach (var u in arr)
                    {
                        var n = string.IsNullOrEmpty(u) ? "" : Path.GetFileName(u.Trim().TrimEnd('/'));
                        if (!string.IsNullOrEmpty(n)) names.Add(n);
                    }
            }
            catch { }
        }
        if (!string.IsNullOrWhiteSpace(error) && error.IndexOf("copyright", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var hint = "视频生成被平台版权审核拦截：内容可能涉及受版权保护的角色或素材（参考图或提示词中的角色设定/场景可能命中原著 IP）。"
                + "建议：1) 更换参考图（避免官方立绘/高清同人图，改用原创化处理的角色图）；"
                + "2) 提示词中改用完全原创的角色名与特征描述，去掉原 IP 专有名词（如角色原名、专武名、专属地名）；"
                + "3) 拆分只保留一个角色的镜头分别生成。";
            var tail = names.Count > 0 ? " | 参考图: " + string.Join(", ", names) : "";
            return hint + tail;
        }
        if (names.Count == 0) return error;
        return (error ?? "unknown error") + " | 参考图: " + string.Join(", ", names);
    }

    /// <summary>
    /// 查询任务状态
    /// </summary>
    public async Task<VideoTaskInfo?> QueryTask(int promptId, string apiKey, string apiUrl)
    {
        VideoTaskInfo? info;
        lock (_tasks) { _tasks.TryGetValue(promptId, out info); }
        if (info == null || string.IsNullOrEmpty(info.TaskId)) return null;

        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, apiUrl.TrimEnd('/') + "/contents/generations/tasks/" + info.TaskId);
            req.Headers.Add("Authorization", $"Bearer {apiKey}");

            var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync();
            _logger.LogInformation("[VideoService] QueryTask raw: {Response}", json);
            using var doc = JsonDocument.Parse(json);

            var status = doc.RootElement.GetProperty("status").GetString() ?? "running";
            info.Status = status;

            string? resolution = null;
            double? duration = null;
            int? usageTokens = null;
            int? seed = null;

            if (status == "succeeded" || status == "completed")
            {
                if (doc.RootElement.TryGetProperty("content", out var content))
                {
                    if (content.TryGetProperty("video_url", out var vu))
                        info.VideoUrl = vu.GetString();
                    else if (content.TryGetProperty("url", out var u))
                        info.VideoUrl = u.GetString();
                    else
                        _logger.LogInformation("[VideoService] content raw: {Content}", content.GetRawText());
                }

                if (doc.RootElement.TryGetProperty("resolution", out var res))
                    resolution = res.GetString();
                if (doc.RootElement.TryGetProperty("duration", out var dur))
                {
                    try { duration = dur.GetDouble(); } catch { }
                }
                if (doc.RootElement.TryGetProperty("seed", out var sd))
                    seed = sd.GetInt32();
                if (doc.RootElement.TryGetProperty("usage", out var usage))
                {
                    if (usage.TryGetProperty("total_tokens", out var tt))
                        usageTokens = tt.GetInt32();
                }

                if (!string.IsNullOrEmpty(info.VideoUrl))
                {
                    _db.UpdatePromptVideo(promptId, info.VideoUrl, info.VideoUrl, "completed");
                }
                else
                {
                    _db.UpdatePromptStatus(promptId, "completed");
                    _logger.LogWarning("[VideoService] completed no url for prompt {PromptId}", promptId);
                }

                // 更新 VideoGenerationTasks 记录
                _db.UpdateVideoTaskStatus(promptId, info.TaskId, "completed", info.VideoUrl, null, resolution, usageTokens, seed, DateTime.Now, status, duration);
            }
            else if (status == "failed")
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                    info.Error = err.GetString();
                info.Error = AppendRefImageInfo(info.Error, _db.GetPrompt(promptId)?.ReferenceImages);
                _db.UpdatePromptStatus(promptId, "failed");
                _db.UpdateVideoTaskError(promptId, info.TaskId, info.Error ?? "unknown error");
            }

            return info;
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
            _db.UpdateVideoTaskError(promptId, info.TaskId, ex.Message);
            return info;
        }
    }
    /// <summary>
    /// 根据 API 返回的 taskId 直接查询任务状态（不依赖内存缓存）
    /// </summary>
    public async Task<(string status, string? videoUrl, string? error, string? resolution, int? usageTokens, int? seed, double? duration)> QueryTaskByTaskId(string taskId, string apiKey, string apiUrl)
    {
        try
        {
            var req = new HttpRequestMessage(HttpMethod.Get, apiUrl.TrimEnd('/') + "/contents/generations/tasks/" + taskId);
            req.Headers.Add("Authorization", "Bearer " + apiKey);
            var resp = await _http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return ("unknown", null, null, null, null, null, null);
            var json = await resp.Content.ReadAsStringAsync();
            _logger.LogInformation("[VideoService] QueryTaskByTaskId raw: {Response}", json);
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var status = doc.RootElement.GetProperty("status").GetString() ?? "running";
            string? videoUrl = null;
            string? errorMsg = null;
    
            string? resolution = null;
            int? usageTokens = null;
            int? seed = null;
            double? duration = null;
            if (status == "succeeded" || status == "completed")
            {
                if (doc.RootElement.TryGetProperty("content", out var cnt))
                {
                    if (cnt.TryGetProperty("video_url", out var vu)) videoUrl = vu.GetString();
                    else if (cnt.TryGetProperty("url", out var u)) videoUrl = u.GetString();
                if (doc.RootElement.TryGetProperty("resolution", out var res)) resolution = res.GetString();
                if (doc.RootElement.TryGetProperty("duration", out var dur))
                {
                    try { duration = dur.GetDouble(); } catch { }
                }
                if (doc.RootElement.TryGetProperty("seed", out var sd)) seed = sd.GetInt32();
                if (doc.RootElement.TryGetProperty("usage", out var usage) && usage.TryGetProperty("total_tokens", out var tt)) usageTokens = tt.GetInt32();
                }
            }
            else if (status == "failed")
            {
                if (doc.RootElement.TryGetProperty("error", out var err))
                    errorMsg = err.TryGetProperty("message", out var em) ? em.GetString() : err.GetRawText();
            }
            return (status, videoUrl, errorMsg, resolution, usageTokens, seed, duration);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[VideoService] QueryTaskByTaskId error");
            return ("unknown", null, ex.Message, null, null, null, null);
        }
    }

    /// <summary>
    /// 下载视频到本地存储。同一远程 URL 只会真正下载一次：状态轮询、后台巡检、重启前的重复调用
    /// 都复用同一份本地文件（避免同一份视频在磁盘上堆出多个同内容副本）。
    /// </summary>
    public Task<string?> DownloadVideoToLocal(string videoUrl, int promptId, int projectId, bool isEnhance = false)
        => VideoLocalStore.DownloadOnceAsync(
            videoUrl, () => DownloadVideoToLocalCore(videoUrl, promptId, projectId, isEnhance), _logger, "VideoService");

    /// <summary>真正落盘下载（调用方请走 DownloadVideoToLocal，避免同一份视频被下载多遍）</summary>
    private async Task<string?> DownloadVideoToLocalCore(string videoUrl, int promptId, int projectId, bool isEnhance)
    {
        try
        {
            if (string.IsNullOrEmpty(videoUrl)) return null;

            // 同一个远程地址之前已经下过（例如进程重启前下的）→ 直接复用，不再下一份
            var existingLocalUrl = _db.GetLocalVideoUrlByRemoteUrl(videoUrl);
            var existingPath = VideoLocalStore.MapToPhysicalPath(existingLocalUrl);
            if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath))
            {
                _logger.LogInformation("[VideoService] 复用已下载的本地视频 {LocalUrl}（同一远程地址不重复下载）", existingLocalUrl);
                return existingLocalUrl;
            }

            var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "videos", projectId.ToString());
            Directory.CreateDirectory(uploadDir);
            var ext = ".mp4";
            if (videoUrl.Contains(".mp4")) ext = ".mp4";
            else if (videoUrl.Contains(".webm")) ext = ".webm";
            var fileName = isEnhance
                ? $"prompt_{promptId}_增强_{DateTime.Now:yyyyMMddHHmmssfff}{ext}"
                : $"prompt_{promptId}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}";
            var filePath = Path.Combine(uploadDir, fileName);
            await VideoLocalStore.DownloadToFileAsync(_http, videoUrl, filePath);
            var localUrl = $"/uploads/videos/{projectId}/{fileName}";
            _logger.LogInformation("[VideoService] Downloaded video to {LocalUrl}", localUrl);
            return localUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VideoService] Download video error");
            return null;
        }
    }



    /// <summary>
    /// 获取任务状态（从缓存）
    /// </summary>
    public VideoTaskInfo? GetTaskInfo(int promptId)
    {
        lock (_tasks) { _tasks.TryGetValue(promptId, out var info); return info; }
    }
    
    public async Task<bool> CancelTask(int promptId, string apiKey, string apiUrl, string model, int projectId = 0)
    {
        VideoTaskInfo? info;
        lock (_tasks) { _tasks.TryGetValue(promptId, out info); }
        string taskId = info?.TaskId ?? "";
        if (string.IsNullOrEmpty(taskId))
        {
            if (projectId > 0)
            {
                var dbTasks = _db.GetVideoTasks(projectId);
                var dbTask = dbTasks?.FirstOrDefault(t => t.PromptId == promptId);
                if (dbTask != null && !string.IsNullOrEmpty(dbTask.TaskId))
                    taskId = dbTask.TaskId;
            }
            if (string.IsNullOrEmpty(taskId))
                return false;
        }
        try
        {
            var delUrl = apiUrl.TrimEnd('/') + "/contents/generations/tasks/" + taskId;
            var req = new HttpRequestMessage(HttpMethod.Delete, delUrl);
            req.Headers.Add("Authorization", "Bearer " + apiKey);
            var body = System.Text.Json.JsonSerializer.Serialize(new { model });
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            var resp = await _http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                _db.UpdatePromptStatus(promptId, "cancelled");
                _db.UpdateVideoTaskStatus(promptId, taskId, "cancelled", null, null, null, null, null, DateTime.Now, "cancelled");
                lock (_tasks) { _tasks.Remove(promptId); }
                return true;
            }
            var json = await resp.Content.ReadAsStringAsync();
            _logger.LogWarning("[VideoService] Cancel failed: {Response}", json);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VideoService] Cancel error");
            return false;
        }
    }



}

