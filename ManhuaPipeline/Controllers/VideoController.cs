using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ManhuaPipeline.Services;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/project/{projectId}/video")]
public class VideoController : ControllerBase
{
    private readonly VideoService _video;
    private readonly ComfyService _comfy;
    private readonly EnhanceService _enhance;
    private readonly DbService _db;
    private readonly VideoEventHub _hub;
    private readonly ILogger<VideoController> _logger;
    private readonly string _publicBaseUrl = "";

    public VideoController(
        VideoService video,
        ComfyService comfy,
        EnhanceService enhance,
        DbService db,
        VideoEventHub hub,
        IConfiguration config,
        ILogger<VideoController> logger)
    {
        _video = video;
        _comfy = comfy;
        _enhance = enhance;
        _db = db;
        _hub = hub;
        _logger = logger;
        _publicBaseUrl = config["PublicBaseUrl"] ?? "";
    }

    private sealed class VideoConfigurationException : Exception
    {
        public VideoConfigurationException(string message) : base(message) { }
    }

    private ObjectResult ServerError(
        Exception exception,
        string operation,
        string clientMessage,
        int projectId,
        int? promptId = null,
        string? taskId = null)
    {
        var errorId = HttpContext.TraceIdentifier;
        _logger.LogError(
            exception,
            "Video operation failed. Operation={Operation}, ProjectId={ProjectId}, PromptId={PromptId}, TaskId={TaskId}, UserId={UserId}, ErrorId={ErrorId}",
            operation,
            projectId,
            promptId,
            taskId,
            GetUserId(),
            errorId);
        return StatusCode(500, new { message = clientMessage, errorId });
    }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private IActionResult? CheckProjectAccess(int projectId, out int userId)
    {
        userId = GetUserId();
        if (userId == 0) return Unauthorized();
        return _db.ProjectBelongsToUser(projectId, userId)
            ? null
            : NotFound(new { message = "项目不存在" });
    }

    private (string apiUrl, string apiKey, string model) GetVideoConfig()
    {
        var uid = GetUserId();
        var config = _db.GetActiveConfig(uid, "volcano_video");
        if (config == null || string.IsNullOrWhiteSpace(config.ApiKey))
            throw new VideoConfigurationException("请先配置火山方舟视频生成的 API Key");
        return (
            string.IsNullOrEmpty(config.ApiUrl) ? "https://ark.cn-beijing.volces.com/api/v3" : config.ApiUrl.TrimEnd('/'),
            config.ApiKey,
            string.IsNullOrEmpty(config.ModelName) ? "doubao-seedance-2-0-mini-260615" : config.ModelName
        );
    }

    private string GetVideoEngine()
    {
        return _db.GetActiveVideoEngine(GetUserId()) == "comfyui" ? "comfyui" : "volcano";
    }

    private string GetComfyBaseUrl()
    {
        var cfg = _db.GetActiveConfig(GetUserId(), "comfyui");
        return string.IsNullOrEmpty(cfg?.ApiUrl) ? "http://127.0.0.1:8188" : cfg.ApiUrl.TrimEnd('/');
    }

    private static ComfyService.ComfyGenSettings? ToComfySettings(VideoService.VideoGenSettings? vs)
    {
        if (vs == null) return null;
        return new ComfyService.ComfyGenSettings { Duration = vs.Duration, Ratio = vs.Ratio };
    }

    /// <summary>
    /// 按引擎选择提示词：ComfyUI（H3 参考生视频）优先使用 PromptTextH3；
    /// 若该镜头还没有 H3 提示词，则回退使用 SD 的 PromptText（H3 同样能理解 SD 紧凑格式），避免切换引擎后无法提交。
    /// </summary>
    private static string ResolveEnginePrompt(SeedancePrompt p, string engine)
    {
        if (engine == "comfyui")
        {
            if (!string.IsNullOrWhiteSpace(p.PromptTextH3))
                return p.PromptTextH3;
            return p.PromptText;
        }
        return p.PromptText;
    }

    /// <summary>
    /// 统计提示词文本里写明的参考图绑定数量（H3：@图片N；SD：@图N）。
    /// 编号需从 1 连续递增且无重复才返回数量，否则（无绑定行/格式无法判断）返回 null。
    /// 这是“参考图槽位 ↔ 文本绑定行 ↔ 画面主体”一致性的提交前闸门。
    /// </summary>
    private static int? CountReferenceBindings(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var ns = new List<int>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"@图片\s*(\d+)"))
            ns.Add(int.Parse(m.Groups[1].Value));
        if (ns.Count == 0)
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"@图\s*(\d+)"))
                ns.Add(int.Parse(m.Groups[1].Value));
        if (ns.Count == 0) return null;
        var distinct = ns.Distinct().ToList();
        var max = distinct.Max();
        return distinct.Count == max && distinct.All(n => n >= 1) ? max : (int?)null;
    }

    /// <summary>解析提示词里的参考图绑定行（@图片N [资产名]），按编号升序返回资产名。</summary>
    private static List<string> ParseRefBindingNames(string? text)
    {
        var list = new List<(int N, string Name)>();
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"@图片\s*(\d+)\s*[\[【]\s*([^\]】\r\n]{1,60})"))
        {
            if (!int.TryParse(m.Groups[1].Value, out var n)) continue;
            var name = m.Groups[2].Value.Trim();
            if (name.Length == 0 || list.Any(x => x.N == n)) continue;
            list.Add((n, name));
        }
        return list.OrderBy(x => x.N).Select(x => x.Name).ToList();
    }

    /// <summary>解析该镜头要带的音色参考音频（ComfyUI 引擎专用）。
    /// 顺序权威来自 VoiceRefResolver —— 与 H3 提示词里的 &lt;Audio 1..n&gt; 编号由同一函数产出，保证"编号 ↔ 音频"严格对应；
    /// 没有角色配音色时回退到该提示词记录上手填的参考音频，行为与改动前一致。</summary>
    private List<string>? ResolveVoiceAudios(int projectId, SeedancePrompt? prompt, string? engineText, List<string>? fallback)
    {
        List<string> list;
        try
        {
            list = VoiceRefResolver.Resolve(_db, projectId, prompt?.FrameId, ParseRefBindingNames(engineText))
                .Select(r => r.AudioUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Video] 解析音色参考音频失败，已跳过。ProjectId={ProjectId}", projectId);
            list = new List<string>();
        }
        if (list.Count == 0)
            list = (fallback ?? new List<string>()).Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
        return list.Count > 0 ? list : null;
    }

    // ---------- 角色音色参考音频（MiniMax H3 参考音频：提示词 <Audio n> ↔ ref_audios 槽位） ----------

    /// <summary>返回该项目的音色参考列表，以及可绑定的角色名（分镜帧绑定里 Character 类资产去重）</summary>
    [HttpGet("voices")]
    public IActionResult GetVoices(int projectId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        try
        {
            var voices = _db.GetVoiceReferences(projectId)
                .Select(v => new { voiceId = v.VoiceId, characterName = v.CharacterName, audioUrl = v.AudioUrl, originalFileName = v.OriginalFileName })
                .ToList();
            var characters = _db.GetFrameAssetBindings(projectId)
                .Where(b => string.Equals(b.Category, "Character", StringComparison.OrdinalIgnoreCase))
                .OrderBy(b => b.SortOrder)
                .Select(b => b.Name)
                .Distinct()
                .ToList();
            return Ok(new { voices, characters });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return BadRequest(new { message = "音色参考表不存在：请先在数据库执行 ManhuaPipeline/Database/Upgrade_VoiceReferences.sql" });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "GetVoices", "读取音色参考失败，请稍后重试", projectId);
        }
    }

    /// <summary>上传/替换某个角色的音色参考音频（同项目同角色覆盖）</summary>
    [HttpPost("voices")]
    [RequestSizeLimit(30 * 1024 * 1024)]
    public async Task<IActionResult> UploadVoice(int projectId, [FromForm] string? characterName, [FromForm] IFormFile? file)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        try
        {
            var name = (characterName ?? "").Trim();
            if (name.Length == 0) return BadRequest(new { message = "请先选择或填写角色名" });
            if (name.Length > 100) return BadRequest(new { message = "角色名过长（最多 100 字）" });
            if (file == null || file.Length == 0) return BadRequest(new { message = "请选择要上传的音频文件" });
            if (file.Length > 25 * 1024 * 1024) return BadRequest(new { message = "音频文件不能超过 25MB" });

            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            var allowed = new[] { ".wav", ".mp3", ".m4a", ".flac", ".ogg", ".aac", ".opus" };
            if (!allowed.Contains(ext))
                return BadRequest(new { message = "仅支持 wav / mp3 / m4a / flac / ogg 音频（官方建议用 2~15 秒的干净人声）" });

            var dir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "voices");
            Directory.CreateDirectory(dir);
            var fileName = "voice_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ext;
            await using (var fs = new FileStream(Path.Combine(dir, fileName), FileMode.Create))
                await file.CopyToAsync(fs);
            var url = "/uploads/voices/" + fileName;

            var old = _db.GetVoiceReferences(projectId)
                .FirstOrDefault(v => string.Equals(v.CharacterName, name, StringComparison.Ordinal));
            var voiceId = _db.UpsertVoiceReference(projectId, name, url, file.FileName);
            if (old != null && !string.IsNullOrWhiteSpace(old.AudioUrl) && old.AudioUrl != url)
            {
                try
                {
                    var oldPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", old.AudioUrl.TrimStart('/'));
                    if (System.IO.File.Exists(oldPath)) System.IO.File.Delete(oldPath);
                }
                catch { /* 旧文件删除失败不影响新音频生效 */ }
            }
            _logger.LogInformation("[Video] 音色参考音频已保存 ProjectId={ProjectId} 角色={Character} VoiceId={VoiceId}", projectId, name, voiceId);
            return Ok(new { voiceId, characterName = name, audioUrl = url });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return BadRequest(new { message = "音色参考表不存在：请先在数据库执行 ManhuaPipeline/Database/Upgrade_VoiceReferences.sql" });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "UploadVoice", "音色参考音频上传失败，请稍后重试", projectId);
        }
    }

    /// <summary>删除某个角色的音色参考音频（连同本地文件）</summary>
    [HttpDelete("voices/{voiceId:int}")]
    public IActionResult DeleteVoice(int projectId, int voiceId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        try
        {
            var target = _db.GetVoiceReferences(projectId).FirstOrDefault(v => v.VoiceId == voiceId);
            var ok = _db.DeleteVoiceReference(projectId, voiceId);
            if (ok && target != null && !string.IsNullOrWhiteSpace(target.AudioUrl))
            {
                try
                {
                    var path = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", target.AudioUrl.TrimStart('/'));
                    if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
                }
                catch { /* 文件删除失败不影响记录已删除 */ }
            }
            return Ok(new { deleted = ok });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 208)
        {
            return BadRequest(new { message = "音色参考表不存在：请先在数据库执行 ManhuaPipeline/Database/Upgrade_VoiceReferences.sql" });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "DeleteVoice", "删除音色参考失败，请稍后重试", projectId);
        }
    }

        [HttpGet("token-usage")]
    public IActionResult TokenUsage(int projectId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        return Ok(new { usedTokens = _db.GetProjectTokenUsage(projectId) });
    }

    [HttpPost("generate/{promptId}")]
    public async Task<IActionResult> Generate(int projectId, int promptId, [FromBody] VideoSettingsRequest? settings = null)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null) return access;
        try
        {
            var prompts = _db.GetPrompts(projectId);
            var prompt = prompts.FirstOrDefault(p => p.PromptId == promptId);
            if (prompt == null) return NotFound(new { message = "提示词不存在" });
            var generatingStatuses = new HashSet<string> { "processing", "running", "pending", "queued", "unknown", "enhancing" };
            if (generatingStatuses.Contains(prompt.Status ?? ""))
                return Conflict(new { message = "该提示词正在生成中，请勿重复提交" });

            var refImgs = prompt.ReferenceImages != null ? JsonSerializer.Deserialize<List<string>>(prompt.ReferenceImages) : null;
            var refVids = prompt.ReferenceVideos != null ? JsonSerializer.Deserialize<List<string>>(prompt.ReferenceVideos) : null;
            var refAud = prompt.ReferenceAudio != null ? JsonSerializer.Deserialize<List<string>>(prompt.ReferenceAudio) : null;

            var vs = settings?.ToVideoGenSettings();
            var engine = GetVideoEngine();
            VideoService.VideoTaskInfo result;
            if (engine == "comfyui")
            {
                var engineText = ResolveEnginePrompt(prompt, engine);
                // 提交前闸门：文本绑定行数与实际上传参考图数必须一致，防止“提示词绑了 N 张、实际只传 M 张”的错位提交
                var activeRefImgs = (refImgs ?? new List<string>()).Count(x => !string.IsNullOrWhiteSpace(x));
                var bindCount = CountReferenceBindings(engineText);
                if (bindCount != null && bindCount.Value > 0 && bindCount.Value != activeRefImgs)
                    return BadRequest(new { message = "该提示词写明 " + bindCount.Value + " 张参考图（@图片1~@图片" + bindCount.Value + "），但当前仅上传 " + activeRefImgs + " 张。请补足参考图或重新生成提示词后再提交，避免参考图与画面主体错位。" });
                result = await _comfy.SubmitTask(projectId, promptId, uid, engineText, GetComfyBaseUrl(), refImgs, ToComfySettings(vs),
                    ResolveVoiceAudios(projectId, prompt, engineText, refAud));
            }
            else
            {
                var (apiUrl, apiKey, model) = GetVideoConfig();
                result = await _video.SubmitTask(projectId, promptId, prompt.PromptText, apiKey, apiUrl, model, refImgs, ToPublicUrls(refVids), refAud, vs);
            }
            return Ok(new { taskId = result.TaskId, status = result.Status, engine });
        }
        catch (VideoConfigurationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "Generate", "视频提交失败，请稍后重试", projectId, promptId);
        }
    }


    [HttpPost("enhance/{promptId}")]
    public async Task<IActionResult> Enhance(int projectId, int promptId, [FromBody] EnhanceRequest? req = null)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null) return access;
        try
        {
            var prompt = _db.GetPrompts(projectId).FirstOrDefault(p => p.PromptId == promptId);
            if (prompt == null) return NotFound(new { message = "提示词不存在" });
            if (string.IsNullOrEmpty(prompt.VideoUrl))
                return BadRequest(new { message = "该镜头还没有生成视频，无法增强" });
            if (prompt.Status == "enhancing")
                return Conflict(new { message = "该视频正在增强中，请勿重复提交" });
            var generatingStatuses = new HashSet<string> { "processing", "running", "pending", "queued", "unknown", "enhancing" };
            if (generatingStatuses.Contains(prompt.Status ?? ""))
                return Conflict(new { message = "该提示词正在生成视频中，请等生成完成后再增强" });

            var cfg = _db.GetActiveConfig(uid, "mediakit");
            if (cfg == null || string.IsNullOrEmpty(cfg.ApiKey))
                return BadRequest(new { message = "请先在 API 配置中填写 AI MediaKit 的 API Key（火山引擎控制台 → AI MediaKit → API Key 管理）" });
            if (!prompt.VideoUrl.StartsWith("https://"))
                return BadRequest(new { message = "当前视频不是公网地址，无法增强。请使用火山方舟生成的视频链接后再试" });

            var baseUrl = string.IsNullOrEmpty(cfg.ApiUrl) ? _enhance.DefaultBaseUrl : cfg.ApiUrl.TrimEnd('/');
            var resolution = (req?.Resolution ?? cfg.ModelName ?? "1080p").Trim();
            if (resolution != "720p" && resolution != "1080p" && resolution != "2k")
                return BadRequest(new { message = "增强分辨率只支持 720p / 1080p / 2k" });

            var taskId = await _enhance.SubmitEnhance(prompt.VideoUrl, cfg.ApiKey, baseUrl, resolution);
            _db.SaveVideoTask(new VideoGenerationTask
            {
                ProjectId = projectId,
                PromptId = promptId,
                TaskId = taskId,
                Engine = "mediakit",
                Status = "enhancing",
                RequestDuration = prompt.Duration,
                RequestRatio = resolution,
                CreatedAt = DateTime.Now
            });
            _db.UpdatePromptStatus(promptId, "enhancing");
            return Ok(new { taskId, status = "enhancing", message = "画质增强已提交，处理中（通常需要几分钟到十几分钟）" });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "Enhance", "视频增强提交失败，请稍后重试", projectId, promptId);
        }
    }

    [HttpPost("batch-generate")]
    public async Task<IActionResult> BatchGenerate(int projectId, [FromBody] BatchVideoRequest req)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null) return access;
        try
        {
            var prompts = _db.GetPrompts(projectId);
            var generating = new HashSet<string> { "processing", "running", "pending", "queued", "unknown" };
            var pending = prompts.Where(p => string.IsNullOrEmpty(p.VideoUrl) && !generating.Contains(p.Status ?? "")).ToList();
            if (pending.Count == 0)
                return Ok(new { message = "没有可生成的视频", count = 0 });

            var tasks = new List<object>();
            var failures = new List<object>();
            var baseSettings = req.Settings?.ToVideoGenSettings();
            var engine = GetVideoEngine();
            foreach (var p in pending)
            {
                try
                {
                    var refImgs = p.ReferenceImages != null ? JsonSerializer.Deserialize<List<string>>(p.ReferenceImages) : null;
                    var refVids = p.ReferenceVideos != null ? JsonSerializer.Deserialize<List<string>>(p.ReferenceVideos) : null;
                    var refAud = p.ReferenceAudio != null ? JsonSerializer.Deserialize<List<string>>(p.ReferenceAudio) : null;
                    var vs = baseSettings == null ? new VideoService.VideoGenSettings { Duration = p.Duration } : baseSettings with { Duration = p.Duration };
                    VideoService.VideoTaskInfo result;
                    if (engine == "comfyui")
                    {
                        var pEngineText = ResolveEnginePrompt(p, engine);
                        result = await _comfy.SubmitTask(projectId, p.PromptId, uid, pEngineText, GetComfyBaseUrl(), refImgs, ToComfySettings(vs),
                            ResolveVoiceAudios(projectId, p, pEngineText, refAud));
                    }
                    else
                    {
                        var (apiUrl, apiKey, model) = GetVideoConfig();
                        result = await _video.SubmitTask(projectId, p.PromptId, p.PromptText, apiKey, apiUrl, model, refImgs, ToPublicUrls(refVids), refAud, vs);
                    }
                    tasks.Add(new { promptId = p.PromptId, taskId = result.TaskId, engine });
                }
                catch (VideoConfigurationException ex)
                {
                    return BadRequest(new { message = ex.Message });
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        ex,
                        "Video batch item failed. ProjectId={ProjectId}, PromptId={PromptId}, UserId={UserId}, ErrorId={ErrorId}",
                        projectId,
                        p.PromptId,
                        uid,
                        HttpContext.TraceIdentifier);
                    failures.Add(new { promptId = p.PromptId, error = "提交失败" });
                    try
                    {
                        _db.UpdatePromptStatus(p.PromptId, "failed");
                    }
                    catch (Exception statusEx)
                    {
                        _logger.LogError(
                            statusEx,
                            "Failed to mark video prompt as failed. ProjectId={ProjectId}, PromptId={PromptId}, ErrorId={ErrorId}",
                            projectId,
                            p.PromptId,
                            HttpContext.TraceIdentifier);
                    }
                }
            }
            var msg = failures.Count > 0
                ? "已提交 " + tasks.Count + " 个，失败 " + failures.Count + " 个" 
                : "已提交 " + tasks.Count + " 个任务";
            return Ok(new { message = msg, count = tasks.Count, failed = failures.Count, tasks, failures });
        }
        catch (VideoConfigurationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "BatchGenerate", "批量生成失败，请稍后重试", projectId);
        }
    }

    [HttpPost("generate-full/{promptId}")]
    public async Task<IActionResult> GenerateFull(int projectId, int promptId, [FromBody] GenerateFullRequest req)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null) return access;
        try
        {
            var prompts = _db.GetPrompts(projectId);
            var prompt = prompts.FirstOrDefault(p => p.PromptId == promptId);
            if (prompt == null) return NotFound(new { message = "提示词不存在" });

            var current = _db.GetPrompts(projectId).FirstOrDefault(x => x.PromptId == promptId);
            _db.UpdatePromptReferences(promptId,
                req.ReferenceImages != null ? JsonSerializer.Serialize(req.ReferenceImages) : current?.ReferenceImages,
                req.ReferenceVideos != null ? JsonSerializer.Serialize(req.ReferenceVideos) : current?.ReferenceVideos,
                req.ReferenceAudio != null ? JsonSerializer.Serialize(req.ReferenceAudio) : current?.ReferenceAudio
            );

            var engine = GetVideoEngine();
            VideoService.VideoTaskInfo result;
            if (engine == "comfyui")
            {
                var fullEngineText = ResolveEnginePrompt(prompt, engine);
                result = await _comfy.SubmitTask(projectId, promptId, uid, fullEngineText, GetComfyBaseUrl(), req.ReferenceImages, null,
                    ResolveVoiceAudios(projectId, prompt, fullEngineText, req.ReferenceAudio));
            }
            else
            {
                var (apiUrl, apiKey, model) = GetVideoConfig();
                result = await _video.SubmitTask(projectId, promptId, prompt.PromptText, apiKey, apiUrl, model,
                    req.ReferenceImages, ToPublicUrls(req.ReferenceVideos), req.ReferenceAudio);
            }
            return Ok(new { taskId = result.TaskId, status = result.Status });
        }
        catch (VideoConfigurationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "GenerateFull", "视频提交失败，请稍后重试", projectId, promptId);
        }
    }

    /// <summary>
    /// 视频状态 SSE 长连接：页面只订阅这一条连接，后台巡检发现状态变化时主动推给它。
    /// 取代原来"每个生成中的镜头各起一条 5 秒轮询链"的做法 —— 多开页面不再成倍放大请求，
    /// 关掉页面后后台也会自动退回低频巡检（见 VideoPollingService）。
    /// GetStatus（单次查询）保留，作为浏览器不支持 EventSource 时的降级入口。
    /// </summary>
    [HttpGet("events")]
    public async Task StreamEvents(int projectId, CancellationToken ct)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null)
        {
            Response.StatusCode = access is UnauthorizedResult ? 401 : 404;
            return;
        }

        Response.StatusCode = 200;
        Response.ContentType = "text/event-stream; charset=utf-8";
        Response.Headers.CacheControl = "no-cache, no-store";
        // 反向代理（nginx 等）默认会缓冲响应体，不显式关掉的话事件会被攒着不发
        Response.Headers["X-Accel-Buffering"] = "no";
        HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

        // 告诉浏览器断线后 3 秒重连（EventSource 默认也是 3 秒，这里显式声明）
        await Response.WriteAsync("retry: 3000\n\n", ct);
        await Response.Body.FlushAsync(ct);

        try
        {
            await foreach (var frame in _hub.SubscribeAsync(uid, projectId, ct))
            {
                await Response.WriteAsync(frame, ct);
                await Response.Body.FlushAsync(ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 页面关闭 / 切换阶段导致的正常断开
        }
        catch (IOException)
        {
            // 客户端已断开、但取消还没传播到 token 时，写响应会抛 IOException —— 同样属正常断开
        }
    }

    [HttpGet("status/{promptId}")]
    public async Task<IActionResult> GetStatus(int projectId, int promptId)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null) return access;
        try
        {
            // 每次调用都直接查数据库任务记录，不依赖内存缓存。
            // 只取该镜头最新的一条：轮询是高频调用，不能再把整个项目的历史任务捞进内存里筛。
            var latest = _db.GetLatestVideoTask(projectId, promptId);

            if (latest != null && !string.IsNullOrEmpty(latest.TaskId))
            {
                var st = string.IsNullOrEmpty(latest.ApiStatus) ? latest.Status : latest.ApiStatus;
                var terminal = st == "completed" || st == "succeeded" || st == "failed" || st == "cancelled" || st == "expired";
                if (!terminal)
                {
                    try
                    {
                        string status;
                        string? videoUrl;
                        string? error;
                        string? resolution;
                        int? usageTokens;
                        int? seed;
                        double? taskDuration = null;
                        if (latest.Engine == "comfyui")
                        {
                            (status, videoUrl, error, resolution, usageTokens, seed) = await _comfy.QueryTaskByTaskId(latest.TaskId, GetComfyBaseUrl());
                        }
                        else if (latest.Engine == "mediakit")
                        {
                            var mcfg = _db.GetActiveConfig(uid, "mediakit");
                            if (mcfg == null || string.IsNullOrEmpty(mcfg.ApiKey))
                                return BadRequest(new { message = "未配置 MediaKit API Key" });
                            var mbase = string.IsNullOrEmpty(mcfg.ApiUrl) ? _enhance.DefaultBaseUrl : mcfg.ApiUrl.TrimEnd('/');
                            (status, videoUrl, error, resolution) = await _enhance.QueryEnhance(latest.TaskId, mcfg.ApiKey, mbase);
                            usageTokens = null; seed = null; taskDuration = null;
                        }
                        else
                        {
                            var (apiUrl, apiKey, _) = GetVideoConfig();
                            (status, videoUrl, error, resolution, usageTokens, seed, taskDuration) = await _video.QueryTaskByTaskId(latest.TaskId, apiKey, apiUrl);
                        }
                        if (!string.IsNullOrEmpty(status) && status != "unknown")
                        {
                            string? localVideoUrl = null;
                            if (status == "succeeded" || status == "completed")
                            {
                                if (!string.IsNullOrEmpty(videoUrl))
                                    localVideoUrl = latest.Engine == "comfyui"
                                        ? await _comfy.DownloadVideoToLocal(videoUrl, promptId, projectId)
                                        : await _video.DownloadVideoToLocal(videoUrl, promptId, projectId, latest.Engine == "mediakit");
                                if (string.IsNullOrEmpty(localVideoUrl))
                                    localVideoUrl = _db.GetPromptLight(promptId)?.LocalVideoUrl;
                                _db.UpdatePromptVideo(promptId, videoUrl, localVideoUrl, "completed");
                                _db.UpdateVideoTaskStatus(promptId, latest.TaskId, "completed", videoUrl, localVideoUrl, resolution, usageTokens, seed, DateTime.Now, status, taskDuration);
                                if (latest.Engine == "mediakit")
                                    _db.SyncEnhanceResultToGenerationTask(promptId, videoUrl, localVideoUrl, resolution);
                            }
                            else if (status == "failed")
                            {
                                error = VideoService.AppendRefImageInfo(error, latest.Engine == "mediakit" ? null : _db.GetPrompt(promptId)?.ReferenceImages);
                                _db.UpdatePromptStatus(promptId, "failed");
                                _db.UpdateVideoTaskError(promptId, latest.TaskId, error ?? "unknown error");
                            }
                            return Ok(new { status = status, videoUrl = videoUrl, localVideoUrl = localVideoUrl ?? "", taskId = latest.TaskId });
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Video status provider query failed; returning stored status. ProjectId={ProjectId}, PromptId={PromptId}, TaskId={TaskId}, UserId={UserId}, ErrorId={ErrorId}",
                            projectId,
                            promptId,
                            latest.TaskId,
                            uid,
                            HttpContext.TraceIdentifier);
                    }
                }
            }

            var p = _db.GetPromptLight(promptId);
            if (p == null) return NotFound(new { message = "提示词不存在" });
            return Ok(new { status = p.Value.Status, videoUrl = p.Value.VideoUrl, taskId = latest?.TaskId ?? "" });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "GetStatus", "视频状态查询失败，请稍后重试", projectId, promptId);
        }
    }

    /// <summary>
    /// 获取该项目的所有视频生成任务记录
    /// </summary>
    [HttpGet("tasks")]
    public IActionResult GetTasks(int projectId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        try
        {
            var tasks = _db.GetVideoTasks(projectId);
            return Ok(tasks);
        }
        catch (Exception ex)
        {
            return ServerError(ex, "GetTasks", "视频任务查询失败，请稍后重试", projectId);
        }
    }

    /// <summary>
    /// 整理重复视频文件：把"字节完全相同"的重复品搬到 _duplicates 目录（**只搬不删**，随时可取回）。
    /// 判定只看文件内容，所以重跑出来的不同版本、增强前后的视频内容不同 → 一律保留，不会误伤。
    /// dryRun=true（默认）只出清单、不移动任何文件。
    /// </summary>
    [HttpPost("maintenance/dedupe-local-files")]
    public async Task<IActionResult> DedupeLocalVideoFiles(int projectId, [FromQuery] bool dryRun = true)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        try
        {
            var uploadsRoot = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads");
            var projectVideoDir = Path.Combine(uploadsRoot, "videos", projectId.ToString());
            var quarantineDir = Path.Combine(uploadsRoot, "videos", "_duplicates", projectId.ToString());

            var referenced = _db.GetAllReferencedLocalVideoUrls();
            var report = await VideoLocalStore.QuarantineByteIdenticalDuplicatesAsync(
                projectVideoDir, quarantineDir, referenced, dryRun);
            return Ok(report);
        }
        catch (Exception ex)
        {
            return ServerError(ex, "DedupeLocalVideoFiles", "整理重复视频文件失败，请稍后重试", projectId);
        }
    }

    [HttpPost("upload")]
    [RequestSizeLimit(UploadValidation.MaxVideoBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxVideoBytes)]
    public async Task<IActionResult> UploadFile(int projectId, IFormFile file)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        if (!UploadValidation.TryValidateVideo(file, UploadValidation.MaxVideoBytes, out var extension, out var validationError))
            return BadRequest(new { message = validationError });
        try
        {
            var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", projectId.ToString());
            Directory.CreateDirectory(uploadDir);
            var fileName = Guid.NewGuid().ToString("N") + extension;
            var filePath = Path.Combine(uploadDir, fileName);
            using (var stream = new FileStream(filePath, FileMode.Create)) { await file.CopyToAsync(stream); }
            return Ok(new { url = "/uploads/" + projectId + "/" + fileName, fileName = file.FileName });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "UploadFile", "视频上传失败，请稍后重试", projectId);
        }
    }

    [HttpPost("refvideo-upload")]
    [RequestSizeLimit(UploadValidation.MaxReferenceVideoBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = UploadValidation.MaxReferenceVideoBytes)]
    public async Task<IActionResult> UploadRefVideo(int projectId, IFormFile file)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        if (!UploadValidation.TryValidateVideo(file, UploadValidation.MaxReferenceVideoBytes, out var extension, out var validationError))
            return BadRequest(new { message = validationError });
        try
        {
            var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "refvideos");
            Directory.CreateDirectory(uploadDir);
            var fileName = Guid.NewGuid().ToString("N") + extension;
            var filePath = Path.Combine(uploadDir, fileName);
            using (var stream = new FileStream(filePath, FileMode.Create)) { await file.CopyToAsync(stream); }
            return Ok(new { url = "/uploads/refvideos/" + fileName, fileName = file.FileName });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "UploadReferenceVideo", "参考视频上传失败，请稍后重试", projectId);
        }
    }

    private List<string>? ToPublicUrls(List<string>? urls)
    {
        if (urls == null) return null;
        var baseUrl = !string.IsNullOrEmpty(_publicBaseUrl)
            ? _publicBaseUrl.TrimEnd('/')
            : $"{Request.Scheme}://{Request.Host}";
        return urls.Select(u =>
            string.IsNullOrEmpty(u) || u.StartsWith("http://") || u.StartsWith("https://") || u.StartsWith("data:")
                ? u
                : baseUrl + u).ToList();
    }

    [HttpPut("references/{promptId}")]
    public IActionResult UpdateReferences(int projectId, int promptId, [FromBody] GenerateFullRequest req)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        if (!_db.PromptBelongsToProject(promptId, projectId)) return NotFound(new { message = "提示词不存在" });
        try
        {
            _db.UpdatePromptReferences(promptId,
                req.ReferenceImages != null ? JsonSerializer.Serialize(req.ReferenceImages) : null,
                req.ReferenceVideos != null ? JsonSerializer.Serialize(req.ReferenceVideos) : null,
                req.ReferenceAudio != null ? JsonSerializer.Serialize(req.ReferenceAudio) : null
            );
            return Ok(new { message = "保存成功" });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "UpdateReferences", "参考素材保存失败，请稍后重试", projectId, promptId);
        }
    }

    [HttpPost("cancel/{promptId}")]
    public async Task<IActionResult> Cancel(int projectId, int promptId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        if (!_db.PromptBelongsToProject(promptId, projectId)) return NotFound(new { message = "提示词不存在" });
        try
        {
            bool ok;
            if (GetVideoEngine() == "comfyui")
                ok = await _comfy.CancelTask(promptId, GetComfyBaseUrl(), projectId);
            else
            {
                var (apiUrl, apiKey, model) = GetVideoConfig();
                ok = await _video.CancelTask(promptId, apiKey, apiUrl, model, projectId);
            }
            return Ok(new { cancelled = ok, message = ok ? "已取消" : "取消失败" });
        }
        catch (VideoConfigurationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "Cancel", "视频任务取消失败，请稍后重试", projectId, promptId);
        }
    }


    [HttpPost("cancel-all")]
    public async Task<IActionResult> CancelAll(int projectId)
    {
        var access = CheckProjectAccess(projectId, out _);
        if (access != null) return access;
        try
        {
            var prompts = _db.GetPrompts(projectId);
            var processing = prompts.Where(p => p.Status == "processing" || p.Status == "running" || p.Status == "pending").ToList();
            var count = 0;
            if (GetVideoEngine() == "comfyui")
            {
                foreach (var p in processing)
                {
                    try
                    {
                        if (await _comfy.CancelTask(p.PromptId, GetComfyBaseUrl(), projectId)) count++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Video batch cancel item failed. Engine=ComfyUI, ProjectId={ProjectId}, PromptId={PromptId}, ErrorId={ErrorId}",
                            projectId,
                            p.PromptId,
                            HttpContext.TraceIdentifier);
                    }
                }
            }
            else
            {
                var (apiUrl, apiKey, model) = GetVideoConfig();
                foreach (var p in processing)
                {
                    try
                    {
                        if (await _video.CancelTask(p.PromptId, apiKey, apiUrl, model, projectId)) count++;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(
                            ex,
                            "Video batch cancel item failed. Engine=Volcano, ProjectId={ProjectId}, PromptId={PromptId}, ErrorId={ErrorId}",
                            projectId,
                            p.PromptId,
                            HttpContext.TraceIdentifier);
                    }
                }
            }
            return Ok(new { cancelled = count, message = "已取消 " + count + " 个任务" });
        }
        catch (VideoConfigurationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "CancelAll", "批量取消视频任务失败，请稍后重试", projectId);
        }
    }



    [HttpPost("refresh-task/{taskId}")]
    public async Task<IActionResult> RefreshTask(int projectId, string taskId)
    {
        var access = CheckProjectAccess(projectId, out var uid);
        if (access != null) return access;
        try
        {
            var tasks = _db.GetVideoTasks(projectId);
            var task = tasks.FirstOrDefault(t => t.TaskId == taskId);
            if (task == null) return NotFound(new { message = "任务不存在" });

            var isComfy = task.Engine == "comfyui";
            var isMediakit = task.Engine == "mediakit";
            string newStatus;
            string? videoUrl;
            string? errorMsg;
            string? resolution;
            int? usageTokens;
            int? seed;
            double? taskDuration2 = null;
            if (isComfy)
                (newStatus, videoUrl, errorMsg, resolution, usageTokens, seed) = await _comfy.QueryTaskByTaskId(taskId, GetComfyBaseUrl());
            else if (isMediakit)
            {
                var mcfg = _db.GetActiveConfig(uid, "mediakit");
                if (mcfg == null || string.IsNullOrEmpty(mcfg.ApiKey))
                    return BadRequest(new { message = "未配置 MediaKit API Key" });
                var mbase = string.IsNullOrEmpty(mcfg.ApiUrl) ? _enhance.DefaultBaseUrl : mcfg.ApiUrl.TrimEnd('/');
                (newStatus, videoUrl, errorMsg, resolution) = await _enhance.QueryEnhance(taskId, mcfg.ApiKey, mbase);
                usageTokens = null; seed = null; taskDuration2 = null;
            }
            else
            {
                var (apiUrl, apiKey, _) = GetVideoConfig();
                (newStatus, videoUrl, errorMsg, resolution, usageTokens, seed, taskDuration2) = await _video.QueryTaskByTaskId(taskId, apiKey, apiUrl);
            }

            DateTime? completedAt = null;
            if (newStatus != "unknown")
            {
                if (newStatus == "succeeded" || newStatus == "completed")
                {
                    completedAt = DateTime.Now;
                    // 本地下载失败（如远程链接过期）时，保留原有的本地路径，避免用过期的远程链接覆盖
                    var prompt = _db.GetPromptLight(task.PromptId);
                    var keepLocal = prompt?.LocalVideoUrl;
                    var dlLocal = !string.IsNullOrEmpty(videoUrl)
                        ? (isComfy
                            ? await _comfy.DownloadVideoToLocal(videoUrl, task.PromptId, projectId)
                            : await _video.DownloadVideoToLocal(videoUrl, task.PromptId, projectId, isMediakit))
                        : null;
                    if (!string.IsNullOrEmpty(dlLocal)) keepLocal = dlLocal;
                    _db.UpdatePromptVideo(task.PromptId, videoUrl, keepLocal, newStatus);
                    _db.UpdateVideoTaskStatus(task.PromptId, taskId, newStatus, videoUrl, keepLocal, resolution, usageTokens, seed, completedAt, newStatus, taskDuration2);
                    if (isMediakit)
                        _db.SyncEnhanceResultToGenerationTask(task.PromptId, videoUrl, keepLocal, resolution);
                }
                else
                {
                    _db.UpdatePromptStatus(task.PromptId, newStatus);
                    _db.UpdateVideoTaskStatus(task.PromptId, taskId, newStatus, videoUrl, task.LocalVideoUrl, resolution, usageTokens, seed, completedAt, newStatus);
                }
            }
            return Ok(new { taskId, status = newStatus, videoUrl, message = "刷新成功" });
        }
        catch (VideoConfigurationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (Exception ex)
        {
            return ServerError(ex, "RefreshTask", "视频任务刷新失败，请稍后重试", projectId, taskId: taskId);
        }
    
}

}
public class VideoSettingsRequest
{
    public int Duration { get; set; } = 11;
    public string Ratio { get; set; } = "16:9";
    public bool Watermark { get; set; } = false;
    public bool GenerateAudio { get; set; } = true;
    public string? Resolution { get; set; }

    public VideoService.VideoGenSettings ToVideoGenSettings()
        => new() { Duration = Duration, Ratio = Ratio, Watermark = Watermark, GenerateAudio = GenerateAudio, Resolution = Resolution };
}

public class GenerateFullRequest
{
    public List<string>? ReferenceImages { get; set; }
    public List<string>? ReferenceVideos { get; set; }
    public List<string>? ReferenceAudio { get; set; }
    public VideoSettingsRequest? Settings { get; set; }
}

public class BatchVideoRequest
{
    public VideoSettingsRequest? Settings { get; set; }
}

public class EnhanceRequest
{
    public string? Resolution { get; set; }
}
