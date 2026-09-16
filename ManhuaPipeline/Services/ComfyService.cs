using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 本地 ComfyUI 视频生成引擎（Wan I2V / MiniMax H3 等）。
/// 配置：LLMConfigs 中 Provider='comfyui' 的 ApiUrl 为 ComfyUI 地址；
/// 工作流模板存于 wwwroot/uploads/comfyui/{userId}.workflow.json（ComfyUI 导出的 API 格式）。
/// TaskId 即 ComfyUI 的 prompt_id，轮询 /history，完成后经 /view 下载。
/// </summary>
public class ComfyService
{
    private readonly HttpClient _http;
    private readonly DbService _db;
    private readonly ILogger<ComfyService> _logger;

    public ComfyService(HttpClient http, DbService db, ILogger<ComfyService> logger)
    {
        _http = http;
        _db = db;
        _logger = logger;
    }

    public record ComfyGenSettings
    {
        public int Duration { get; init; } = 11;
        public string Ratio { get; init; } = "16:9";
        public int Fps { get; init; } = 16;
    }

    public static string WorkflowPath(int userId)
        => Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "comfyui", userId + ".workflow.json");

    /// <summary>提交任务到 ComfyUI</summary>
    public async Task<VideoService.VideoTaskInfo> SubmitTask(int projectId, int promptId, int userId, string promptText, string baseUrl, List<string>? refImages, ComfyGenSettings? settings = null, List<string>? refAudios = null)
    {
        settings ??= new ComfyGenSettings();
        baseUrl = baseUrl.TrimEnd('/');

        var workflowPath = WorkflowPath(userId);
        if (!File.Exists(workflowPath))
            throw new Exception("尚未配置 ComfyUI 工作流：请在配置页粘贴 ComfyUI 导出的 API 工作流 JSON");

        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(workflowPath)); }
        catch (Exception ex) { throw new Exception("ComfyUI 工作流 JSON 解析失败：" + ex.Message); }
        if (root == null) throw new Exception("ComfyUI 工作流 JSON 为空");

        // 上传参考图到 ComfyUI input 目录（按工作流 LoadImage 节点数量逐个注入）
        var imgs = refImages?.Where(x => !string.IsNullOrEmpty(x)).ToList() ?? new List<string>();
        var uploadedNames = new List<string>();
        foreach (var img in imgs)
        {
            var name = await UploadMedia(baseUrl, img, false);
            if (!string.IsNullOrEmpty(name)) uploadedNames.Add(name);
        }
        var firstImage = uploadedNames.FirstOrDefault();

        // 音色参考音频：按 VoiceRefResolver 给出的顺序上传，顺序即提示词里的 <Audio 1..n>
        var auds = refAudios?.Where(x => !string.IsNullOrEmpty(x)).ToList() ?? new List<string>();
        var uploadedAudios = new List<string>();
        foreach (var a in auds)
        {
            var name = await UploadMedia(baseUrl, a, true);
            if (!string.IsNullOrEmpty(name)) uploadedAudios.Add(name);
            else _logger.LogWarning("[ComfyService] 音色参考音频上传失败，已跳过：{Ref}", a);
        }

        var (width, height) = RatioToSize(settings.Ratio);
        var frames = Math.Max(4, settings.Duration * settings.Fps);

        // 按常见节点类型注入；也可用占位符 __COMFY_*__ 精确控制
        InjectImages(root, uploadedNames);
        InjectInput(root, "LoadImage", "image", firstImage);
        InjectInput(root, "CLIPTextEncode", "text", promptText);
        InjectInput(root, "PrimitiveStringMultiline", "value", promptText);
        InjectInput(root, "PrimitiveString", "value", promptText);
        // CR Prompt Text（ComfyUI-Custom-Scripts）：新版 H3 工作流用该节点承载提示词（inputs.prompt），
        // 节点类型与前几者都不同，不补这条会静默不注入、直接跑模板里写死的示例文本。
        InjectInput(root, "CR Prompt Text", "prompt", promptText);
        ReplaceToken(root, "__COMFY_IMAGE__", firstImage ?? "");
        ReplaceToken(root, "__COMFY_PROMPT__", promptText);
        ReplaceToken(root, "__COMFY_WIDTH__", width.ToString());
        ReplaceToken(root, "__COMFY_HEIGHT__", height.ToString());
        ReplaceToken(root, "__COMFY_LENGTH__", frames.ToString());
        ReplaceToken(root, "__COMFY_FPS__", settings.Fps.ToString());
        ReplaceToken(root, "__COMFY_DURATION__", settings.Duration.ToString());
        RandomizeSeed(root);

        // 闸门：提示词必须真的进了工作流。模板里的文本承载节点一旦换成未识别的插件节点，
        // 上面按类型注入会静默失效、直接提交模板里写死的示例文本（生成结果与本地提示词完全无关且不报错）。
        if (!string.IsNullOrWhiteSpace(promptText) && !WorkflowContainsText(root, promptText))
            throw new Exception("ComfyUI 工作流模板中找不到可写入提示词的文本节点（已尝试 CLIPTextEncode、PrimitiveString、PrimitiveStringMultiline、CR Prompt Text）。"
                + "请确认工作流中存在上述任一节点，或更新工作流模板，避免提交模板里写死的旧文本。");

        if (HasMiniMax(root))
        {
            // MiniMax H3 工作流：画幅（宽高）+ 时长（秒）由工作流公式计算
            var h3Size = InjectMiniMaxParams(root, settings.Ratio, settings.Duration);
            if (h3Size != null)
                _logger.LogInformation("[ComfyService] H3 画幅 {Ratio} → {Width}×{Height}", settings.Ratio, h3Size.Value.width, h3Size.Value.height);
            // 音色参考音频：接到 H3 节点的 ref_audios 槽位。未传音频时清空模板残留，行为与改动前一致。
            if (root is JsonObject h3Root) await InjectMiniMaxAudios(h3Root, uploadedAudios, baseUrl);
        }
        else
        {
            // 原生 Wan 工作流：按系统设置自动覆盖分辨率/帧数/时长
            InjectWanParams(root, settings.Ratio, settings.Duration, settings.Fps);
        }

        var payload = JsonSerializer.Serialize(new { prompt = root, client_id = "manhua-pipeline" });
        var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/prompt");
        req.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        var resp = await _http.SendAsync(req);
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogWarning("[ComfyService] submit error: {Response}", json);
            resp.EnsureSuccessStatusCode();
        }

        string comfyPromptId;
        using (var doc = JsonDocument.Parse(json))
        {
            if (doc.RootElement.TryGetProperty("node_errors", out var nodeErrs) &&
                nodeErrs.ValueKind == JsonValueKind.Object && nodeErrs.EnumerateObject().Any())
            {
                var first = nodeErrs.EnumerateObject().First();
                var detail = first.Value.ToString();
                throw new Exception("ComfyUI 工作流校验失败（节点 " + first.Name + "）："
                    + (detail.Length > 500 ? detail.Substring(0, 500) : detail));
            }
            if (!doc.RootElement.TryGetProperty("prompt_id", out var pidEl))
                throw new Exception("ComfyUI 未返回 prompt_id：" + json);
            comfyPromptId = pidEl.GetString() ?? "";
        }

        var info = new VideoService.VideoTaskInfo
        {
            TaskId = comfyPromptId,
            Status = "processing",
            CreatedAt = DateTime.Now,
            ProjectId = projectId
        };

        _db.ResetPromptVideo(promptId);
        _db.SaveVideoTask(new VideoGenerationTask
        {
            ProjectId = projectId,
            PromptId = promptId,
            TaskId = comfyPromptId,
            Engine = "comfyui",
            Status = "processing",
            ApiStatus = "queued",
            RequestDuration = settings.Duration,
            RequestRatio = settings.Ratio,
            CreatedAt = DateTime.Now
        });
        _logger.LogInformation("[ComfyService] submitted prompt_id={PromptId} to {BaseUrl}", comfyPromptId, baseUrl);
        return info;
    }

    /// <summary>按 taskId（prompt_id）查询状态，返回与 VideoService 一致的元组</summary>
    public async Task<(string status, string? videoUrl, string? error, string? resolution, int? usageTokens, int? seed)> QueryTaskByTaskId(string taskId, string baseUrl)
    {
        try
        {
            baseUrl = baseUrl.TrimEnd('/');
            var resp = await _http.GetAsync(baseUrl + "/history/" + Uri.EscapeDataString(taskId));
            if (!resp.IsSuccessStatusCode) return ("unknown", null, null, null, null, null);
            var json = await resp.Content.ReadAsStringAsync();

            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(taskId, out var entry))
                return ("processing", null, null, null, null, null);
            if (!entry.TryGetProperty("status", out var st))
                return ("processing", null, null, null, null, null);

            var statusStr = st.TryGetProperty("status_str", out var ss) ? ss.GetString() : "";
            var completed = st.TryGetProperty("completed", out var co) && co.GetBoolean();
            if (statusStr == "error" || statusStr == "error_validation")
                return ("failed", null, ExtractError(st), null, null, null);
            if (!completed) return ("processing", null, null, null, null, null);

            var fileUrl = FindOutputFile(doc.RootElement, taskId, baseUrl);
            return ("completed", fileUrl, null, null, null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ComfyService] Query error");
            return ("unknown", null, ex.Message, null, null, null);
        }
    }

    /// <summary>从 /history 输出里找一个视频文件（优先 mp4/webm/gif），拼出 /view 下载地址</summary>
    private static string? FindOutputFile(JsonElement root, string taskId, string baseUrl)
    {
        if (!root.TryGetProperty(taskId, out var entry)) return null;
        if (!entry.TryGetProperty("outputs", out var outputs)) return null;
        var candidates = new List<(string file, int rank)>();
        foreach (var node in outputs.EnumerateObject())
        {
            foreach (var kind in new[] { "videos", "video", "gifs", "images" })
            {
                if (!node.Value.TryGetProperty(kind, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
                foreach (var item in arr.EnumerateArray())
                {
                    var filename = item.TryGetProperty("filename", out var fn) ? fn.GetString() : null;
                    if (string.IsNullOrEmpty(filename)) continue;
                    var subfolder = item.TryGetProperty("subfolder", out var sf) ? sf.GetString() ?? "" : "";
                    var type = item.TryGetProperty("type", out var tp) ? tp.GetString() ?? "output" : "output";
                    var ext = Path.GetExtension(filename).ToLowerInvariant();
                    var rank = ext == ".mp4" ? 0 : ext == ".webm" ? 1 : ext == ".gif" ? 2 : 3;
                    var url = baseUrl + "/view?filename=" + Uri.EscapeDataString(filename)
                        + "&subfolder=" + Uri.EscapeDataString(subfolder)
                        + "&type=" + Uri.EscapeDataString(type);
                    candidates.Add((url, rank));
                }
            }
        }
        if (candidates.Count == 0) return null;
        return candidates.OrderBy(c => c.rank).First().file;
    }

    private static string? ExtractError(JsonElement statusEl)
    {
        if (statusEl.TryGetProperty("messages", out var msgs))
        {
            // 优先提取 execution_error / execution_interrupted 消息里的真实异常
            foreach (var m in msgs.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2) continue;
                var type = m[0].ValueKind == JsonValueKind.String ? m[0].GetString() : "";
                if (type != "execution_error" && type != "execution_interrupted") continue;
                var data = m[1];
                if (data.ValueKind == JsonValueKind.Object)
                {
                    var parts = new List<string>();
                    if (data.TryGetProperty("exception_type", out var et)) parts.Add(et.GetString() ?? "");
                    if (data.TryGetProperty("exception_message", out var em)) parts.Add(em.GetString() ?? "");
                    if (data.TryGetProperty("node_type", out var nt)) parts.Add("节点类型:" + (nt.GetString() ?? ""));
                    if (data.TryGetProperty("node_id", out var nid)) parts.Add("节点ID:" + (nid.GetString() ?? ""));
                    var text = string.Join(" | ", parts.Where(p => !string.IsNullOrEmpty(p)));
                    if (!string.IsNullOrEmpty(text)) return text.Length > 500 ? text.Substring(0, 500) : text;
                }
            }
            // 退化：跳过 execution_start / execution_cached 等无意义消息，返回第一个有内容的 data
            foreach (var m in msgs.EnumerateArray())
            {
                if (m.ValueKind != JsonValueKind.Array || m.GetArrayLength() < 2) continue;
                var type = m[0].ValueKind == JsonValueKind.String ? m[0].GetString() : "";
                if (type == "execution_start" || type == "execution_cached" || type == "execution_success") continue;
                var msg = m[1].ToString();
                if (msg.Length > 0 && msg != "[]" && msg != "{}") return msg.Length > 500 ? msg.Substring(0, 500) : msg;
            }
        }
        return "ComfyUI 执行失败";
    }

    private static readonly string[] AllowedImageExts = { ".jpg", ".jpeg", ".png", ".webp" };
    private static readonly string[] AllowedAudioExts = { ".wav", ".mp3", ".flac", ".ogg", ".m4a", ".aac", ".opus" };

    /// <summary>把参考素材（参考图 / 音色参考音频）上传到 ComfyUI input 目录，返回 ComfyUI 侧文件名。
    /// 注意：ComfyUI 只有 /upload/image 这一个通用上传端点，音频也走它（字段名固定 image）。</summary>
    private async Task<string?> UploadMedia(string baseUrl, string fileRef, bool isAudio)
    {
        try
        {
            byte[] bytes;
            var ext = isAudio ? ".wav" : ".png";
            if (fileRef.StartsWith("data:"))
            {
                var comma = fileRef.IndexOf(',');
                var mime = comma > 5 ? fileRef.Substring(5, comma - 5).Split(';')[0] : "";
                ext = isAudio
                    ? (AllowedAudioExts.Contains("." + mime.Split('/').Last().Replace("mpeg", "mp3")) ? "." + mime.Split('/').Last().Replace("mpeg", "mp3") : ".wav")
                    : (mime == "image/jpeg" ? ".jpg" : mime == "image/webp" ? ".webp" : ".png");
                bytes = Convert.FromBase64String(fileRef.Substring(comma + 1));
            }
            else if (fileRef.StartsWith("http://") || fileRef.StartsWith("https://"))
            {
                bytes = await _http.GetByteArrayAsync(fileRef);
                ext = Path.GetExtension(new Uri(fileRef).AbsolutePath).ToLowerInvariant();
                var allowed = isAudio ? AllowedAudioExts : AllowedImageExts;
                if (!allowed.Contains(ext)) ext = isAudio ? ".wav" : ".png";
            }
            else
            {
                var filePath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", fileRef.TrimStart('/'));
                if (!File.Exists(filePath)) return null;
                bytes = File.ReadAllBytes(filePath);
                ext = Path.GetExtension(filePath).ToLowerInvariant();
                var allowed = isAudio ? AllowedAudioExts : AllowedImageExts;
                if (!allowed.Contains(ext)) ext = isAudio ? ".wav" : ".png";
            }

            var fileName = (isAudio ? "vo_" : "ref_") + Guid.NewGuid().ToString("N").Substring(0, 8) + ext;
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(bytes);
            var contentType = isAudio
                ? (ext == ".mp3" ? "audio/mpeg" : ext == ".wav" ? "audio/wav" : ext == ".m4a" ? "audio/mp4" : "audio/" + ext.TrimStart('.'))
                : "image/" + ext.TrimStart('.').Replace("jpg", "jpeg");
            fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType);
            form.Add(fileContent, "image", fileName);
            form.Add(new StringContent("true"), "overwrite");
            form.Add(new StringContent("input"), "type");

            var resp = await _http.PostAsync(baseUrl + "/upload/image", form);
            var json = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("[ComfyService] upload error: {Response}", json);
                return null;
            }
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("name", out var nm) ? nm.GetString() : null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ComfyService] Upload error");
            return null;
        }
    }

    /// <summary>给原生 Wan 工作流注入分辨率/时长/帧数（Wan22ImageToVideoLatent + CreateVideo）</summary>
    private static void InjectWanParams(JsonNode root, string ratio, int duration, int fps)
    {
        if (root is not JsonObject obj) return;
        var (w, h) = RatioToSize(ratio);
        foreach (var node in obj)
        {
            if (node.Value is not JsonObject nodeObj) continue;
            var ct = nodeObj["class_type"]?.GetValue<string>();
            if (nodeObj["inputs"] is not JsonObject inputs) continue;
            if (ct == "Wan22ImageToVideoLatent")
            {
                if (inputs["width"] != null) inputs["width"] = JsonValue.Create(w);
                if (inputs["height"] != null) inputs["height"] = JsonValue.Create(h);
                if (inputs["length"] != null) inputs["length"] = JsonValue.Create(Math.Max(4, duration * fps));
            }
            else if (ct == "CreateVideo" && inputs["fps"] != null)
            {
                inputs["fps"] = JsonValue.Create(fps);
            }
        }
    }

    /// <summary>是否 MiniMax H3 工作流（含 MiniMaxH3ReferenceToVideo 节点）</summary>
    private static bool HasMiniMax(JsonNode root)
    {
        if (root is not JsonObject obj) return false;
        foreach (var node in obj)
        {
            if (node.Value is JsonObject nodeObj && nodeObj["class_type"]?.GetValue<string>() == "MiniMaxH3ReferenceToVideo")
                return true;
        }
        return false;
    }

    /// <summary>把上传好的参考图按 LoadImage 节点出现顺序逐个填入（MiniMax H3 多个参考图）。
    /// 传入几张图就保留几个 LoadImage，多余的 LoadImage 节点连同其下游 ref_image_X 连接一并删除——
    /// 避免用第一张图兜底填充导致 H3 同时参考多路相同图片（拖慢生成并干扰效果）。
    /// 模板文件本身不动，保留多参考能力：以后传 N 张图就只参考 N 张。</summary>
    private static void InjectImages(JsonNode root, List<string> names)
    {
        if (root is not JsonObject obj) return;

        // MiniMax H3 工作流：参考图必须按 ref_images.ref_image_N 槽位精确注入，
        // 不能按 LoadImage 出现顺序硬塞（模板里 ref_videos.ref_video_0 也连着 LoadImage，
        // 顺序注入会把第 N 张图误填进视频槽位，触发 "reference videos need at least 5 frames"）。
        // 注意：0 张参考图（纯黑场/纯文本镜头）也必须走此分支——InjectMiniMaxImages
        // 会把全部 ref_images/ref_videos 槽位连同 LoadImage 节点一并删除，否则残留的
        // ref_videos 槽位会把单帧图当参考视频，导致 H3 校验失败提交不了。
        if (HasMiniMax(obj))
        {
            InjectMiniMaxImages(obj, names);
            return;
        }

        if (names.Count == 0) return;

        // 收集所有 LoadImage 节点（保持出现顺序）
        var loadNodes = obj
            .Where(kv => kv.Value is JsonObject n && n["class_type"]?.GetValue<string>() == "LoadImage")
            .ToList();

        var keep = Math.Min(loadNodes.Count, names.Count);
        for (var i = 0; i < loadNodes.Count; i++)
        {
            if (loadNodes[i].Value is not JsonObject nodeObj) continue;
            if (nodeObj["inputs"] is not JsonObject inputs || inputs["image"] == null) continue;
            if (i < keep)
            {
                inputs["image"] = JsonValue.Create(names[i]);
            }
            else
            {
                // 多余的参考图位：先断开所有指向它的输入连接，再移除节点本身
                RemoveRefsTo(obj, loadNodes[i].Key);
                obj.Remove(loadNodes[i].Key);
            }
        }
    }

    /// <summary>MiniMax H3 工作流的参考图注入：
    /// 1) 按 ref_images.ref_image_N 槽位序号注入图片（N 从 0 起，与 @图片1~@图片N 顺序一致）；
    /// 2) ref_videos.ref_video_N 槽位：本引擎当前只支持图片参考，若该槽位连的是 LoadImage（单帧图），
    ///    必须断开引用并删除节点，否则 H3 会把单帧图片当参考视频报错；若连的是 LoadVideo 等视频节点，
    ///    同样断开（无视频可传），避免 ComfyUI 因缺视频输入而校验失败。</summary>
    private static void InjectMiniMaxImages(JsonObject obj, List<string> names)
    {
        // 找到 MiniMax H3 节点及其 inputs
        JsonObject? h3Inputs = null;
        foreach (var kv in obj)
        {
            if (kv.Value is not JsonObject n) continue;
            if (n["class_type"]?.GetValue<string>() != "MiniMaxH3ReferenceToVideo") continue;
            if (n["inputs"] is JsonObject inputs) { h3Inputs = inputs; break; }
        }
        if (h3Inputs == null) return;

        // 解析 ref_images.ref_image_N -> nodeId（按槽位序号排序）
        var refImageSlots = new SortedDictionary<int, string>();
        var refVideoIds = new List<string>();
        foreach (var kv in h3Inputs)
        {
            if (kv.Value is not JsonArray arr || arr.Count < 2) continue;
            var nodeId = arr[0]?.GetValue<string>();
            if (string.IsNullOrEmpty(nodeId)) continue;
            var m = Regex.Match(kv.Key, @"^ref_images\.ref_image_(\d+)$");
            if (m.Success)
                refImageSlots[int.Parse(m.Groups[1].Value)] = nodeId;
            else if (kv.Key.StartsWith("ref_videos."))
                refVideoIds.Add(nodeId);
        }

        // ref_videos 槽位：断开引用并删除其连接的节点（图片/视频节点均断开，当前无视频可传）
        foreach (var vid in refVideoIds.Distinct())
        {
            if (!obj.ContainsKey(vid)) continue;
            RemoveRefsTo(obj, vid);
            obj.Remove(vid);
        }

        // 按 ref_image_N 槽位顺序注入图片；图片数不足时删掉多余槽位及其节点。
        // 模板里多个槽位指向同一个 LoadImage 时（导出工作流常见），必须为后续槽位克隆节点：
        // 否则后写会覆盖先写，多张参考图退化成同一张，与提示词里 @图片N 的对应关系错位。
        var slotList = refImageSlots.ToList();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var nextId = 90001;
        for (var i = 0; i < slotList.Count; i++)
        {
            var slotKey = "ref_images.ref_image_" + slotList[i].Key;
            var nodeId = slotList[i].Value;
            if (i < names.Count)
            {
                if (!obj.TryGetPropertyValue(nodeId, out var node) || node is not JsonObject nodeObj) continue;
                if (nodeObj["inputs"] is not JsonObject inputs || inputs["image"] == null) continue;
                if (!claimed.Add(nodeId))
                {
                    // 该 LoadImage 已被前面的槽位占用：克隆一份给当前槽位
                    while (obj.ContainsKey(nextId.ToString())) nextId++;
                    var cloneId = nextId++.ToString();
                    obj[cloneId] = nodeObj.DeepClone();
                    claimed.Add(cloneId);
                    h3Inputs[slotKey] = new JsonArray(JsonValue.Create(cloneId), JsonValue.Create(0));
                    if (obj.TryGetPropertyValue(cloneId, out var cloneNode) && cloneNode is JsonObject cloneObj
                        && cloneObj["inputs"] is JsonObject cloneInputs)
                        cloneInputs["image"] = JsonValue.Create(names[i]);
                    continue;
                }
                inputs["image"] = JsonValue.Create(names[i]);
            }
            else
            {
                h3Inputs.Remove(slotKey);
                RemoveRefsTo(obj, nodeId);
                obj.Remove(nodeId);
            }
        }
    }

    /// <summary>MiniMax H3 工作流的音色参考音频注入：
    /// 1) 先清理模板里残留的 ref_audios.* / ref_video_audios.* 槽位及其上游音频节点（没传音频时保持与改动前行为一致）；
    /// 2) 再按 names 顺序逐个新建 LoadAudio 节点，接到 ref_audios.ref_audio_N（N 从 0 起，与 ref_images.ref_image_N 同规则）。
    ///    这个顺序就是提示词里 &lt;Audio 1..N&gt; 的编号顺序，两边由 VoiceRefResolver 统一保证。
    /// 槽位名以服务端节点定义（/object_info）探测为准，探测不到时按 0 基默认；服务端明确没有音频槽位则跳过并告警。</summary>
    private async Task InjectMiniMaxAudios(JsonObject obj, List<string> names, string baseUrl)
    {
        JsonObject? h3Inputs = null;
        foreach (var kv in obj)
        {
            if (kv.Value is not JsonObject n) continue;
            if (n["class_type"]?.GetValue<string>() != "MiniMaxH3ReferenceToVideo") continue;
            if (n["inputs"] is JsonObject inputs) { h3Inputs = inputs; break; }
        }
        if (h3Inputs == null) return;

        // 1) 清理模板里可能残留的音频槽位（含参考视频自带音轨的槽位）
        var audioSlotKeys = new List<string>();
        foreach (var input in h3Inputs)
        {
            if (Regex.IsMatch(input.Key, @"^ref_(video_)?audios\.")) audioSlotKeys.Add(input.Key);
        }
        foreach (var key in audioSlotKeys)
        {
            if (h3Inputs[key] is JsonArray arr && arr.Count == 2 && arr[0]?.GetValue<string>() is string linkedId)
            {
                h3Inputs.Remove(key);
                RemoveRefsTo(obj, linkedId);
                obj.Remove(linkedId);
            }
            else
            {
                h3Inputs.Remove(key);
            }
        }

        if (names.Count == 0) return;

        // 2) 探测服务端 H3 节点真正支持的音频槽位名
        var slots = await ProbeRefAudioSlots(baseUrl);
        if (slots is { Count: 0 })
        {
            _logger.LogWarning("[ComfyService] 服务端 MiniMaxH3ReferenceToVideo 节点没有 ref_audios 槽位，本次生成不使用音色参考音频");
            return;
        }
        if (slots == null || slots.Count == 0)
            slots = Enumerable.Range(0, VoiceRefResolver.MaxRefAudios).Select(i => "ref_audios.ref_audio_" + i).ToList();

        var nextId = 90001;
        while (obj.ContainsKey(nextId.ToString())) nextId++;
        for (var i = 0; i < names.Count; i++)
        {
            if (i >= slots.Count)
            {
                _logger.LogWarning("[ComfyService] 音色参考音频 {Count} 段，超出服务端可用槽位 {Slots} 个，多余的已忽略", names.Count, slots.Count);
                break;
            }
            var nodeId = (nextId + i).ToString();
            obj[nodeId] = new JsonObject
            {
                ["inputs"] = new JsonObject { ["audio"] = names[i] },
                ["class_type"] = "LoadAudio",
                ["_meta"] = new JsonObject { ["title"] = "音色参考音频" }
            };
            h3Inputs[slots[i]] = new JsonArray(nodeId, 0);
        }
        _logger.LogInformation("[ComfyService] 已注入 {Count} 段音色参考音频，槽位 {Slots}", names.Count, string.Join(",", slots.Take(names.Count)));
    }

    /// <summary>探测服务端 MiniMaxH3ReferenceToVideo 节点定义里的音频槽位名。
    /// 返回 null = 探测失败（接口不可用/网络异常，调用方按默认 0 基槽位注入）；
    /// 返回空列表 = 服务端节点明确不支持音频参考。</summary>
    private async Task<List<string>?> ProbeRefAudioSlots(string baseUrl)
    {
        try
        {
            var resp = await _http.GetAsync(baseUrl + "/object_info/MiniMaxH3ReferenceToVideo");
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync();
            var full = Regex.Matches(json, @"ref_audios\.ref_audio_\d+").Select(m => m.Value).Distinct().ToList();
            if (full.Count > 0) return full.OrderBy(ExtractTailNumber).ToList();
            var bare = Regex.Matches(json, @"\bref_audio_\d+").Select(m => m.Value).Distinct().ToList();
            if (bare.Count > 0) return bare.OrderBy(ExtractTailNumber).Select(s => "ref_audios." + s).ToList();
            return new List<string>();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ComfyService] 探测 H3 音频槽位失败，按默认槽位注入");
            return null;
        }
    }

    private static int ExtractTailNumber(string s)
    {
        var m = Regex.Match(s, @"(\d+)$");
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }

    /// <summary>删除 obj 中所有节点 inputs 里指向 targetId 的连接（形如 "ref_images.ref_image_2": ["141", 0]）</summary>
    private static void RemoveRefsTo(JsonObject obj, string targetId)
    {
        foreach (var kv in obj)
        {
            if (kv.Value is not JsonObject nodeObj) continue;
            if (nodeObj["inputs"] is not JsonObject inputs) continue;
            List<string>? removeKeys = null;
            foreach (var input in inputs)
            {
                if (input.Value is not JsonArray arr || arr.Count != 2) continue;
                if (arr[0]?.GetValue<string>() == targetId)
                    (removeKeys ??= new List<string>()).Add(input.Key);
            }
            if (removeKeys != null)
                foreach (var k in removeKeys) inputs.Remove(k);
        }
    }

    /// <summary>MiniMax H3：写入画幅宽高 + 把时长（秒）注入时长公式引用的 PrimitiveFloat。
    /// 返回实际写入的宽高（走 ResolutionSelector 时返回 null）；模板里找不到任何可写画幅的节点时抛异常，
    /// 避免像过去那样静默空转、出片尺寸恒为模板里写死的值。</summary>
    private static (int width, int height)? InjectMiniMaxParams(JsonNode root, string ratio, int duration)
    {
        if (root is not JsonObject obj) return null;

        var (w, h) = RatioToH3Size(ratio);
        var wjiApplied = false;       // WJILatentPreset 写入成功
        var selectorApplied = false;  // ResolutionSelector 写入成功

        foreach (var node in obj)
        {
            if (node.Value is not JsonObject nodeObj) continue;
            var ct = nodeObj["class_type"]?.GetValue<string>();
            if (nodeObj["inputs"] is not JsonObject inputs) continue;

            // 画幅①：WJILatentPreset（WJNodes 插件）——模板实际使用的分辨率节点。
            // 节点 tooltip 明确「自定义宽/高」仅在「预设分辨率=自定义」时生效，故把预设一并写成自定义。
            if (ct == "WJILatentPreset")
            {
                if (inputs["预设分辨率"] != null) inputs["预设分辨率"] = JsonValue.Create("自定义");
                if (inputs["横竖对调"] != null) inputs["横竖对调"] = JsonValue.Create(false);
                if (inputs["自定义宽"] != null) { inputs["自定义宽"] = JsonValue.Create(w); wjiApplied = true; }
                if (inputs["自定义高"] != null) { inputs["自定义高"] = JsonValue.Create(h); wjiApplied = true; }
            }
            // 画幅②：ResolutionSelector（另一套 H3 模板用核心节点承载画幅）：按系统画幅设置 aspect_ratio
            else if (ct == "ResolutionSelector" && inputs["aspect_ratio"] != null)
            {
                var label = RatioToAspectLabel(ratio);
                if (label != null) { inputs["aspect_ratio"] = JsonValue.Create(label); selectorApplied = true; }
            }
        }

        // 闸门：画幅必须真的写进工作流。模板换节点后（历史上从 Wan22ImageToVideoLatent 换成 WJILatentPreset）
        // 上面的注入会静默失效——出片尺寸恒等于模板里写死的值、与页面所选画幅无关，而且不报任何错。
        if (!wjiApplied && !selectorApplied)
            throw new Exception("ComfyUI 工作流模板中找不到可写入画幅的节点（已尝试 WJILatentPreset、ResolutionSelector）。"
                + "请确认工作流中存在上述任一分辨率节点，或更新工作流模板，避免出片尺寸恒为模板里写死的值。");

        // 时长（秒）：把 ComfyMathExpression 里引用的 PrimitiveFloat 设为系统时长
        foreach (var node in obj)
        {
            if (node.Value is not JsonObject nodeObj) continue;
            if (nodeObj["class_type"]?.GetValue<string>() != "ComfyMathExpression") continue;
            if (nodeObj["inputs"] is not JsonObject inputs) continue;
            foreach (var kv in inputs)
            {
                if (!kv.Key.StartsWith("values.")) continue;
                if (kv.Value is not JsonArray arr || arr.Count < 1) continue;
                var targetId = arr[0]?.GetValue<string>();
                if (targetId == null || !obj.TryGetPropertyValue(targetId, out var target)) continue;
                if (target is not JsonObject targetObj) continue;
                if (targetObj["class_type"]?.GetValue<string>() != "PrimitiveFloat") continue;
                if (targetObj["inputs"] is JsonObject tIn && tIn["value"] != null)
                {
                    tIn["value"] = JsonValue.Create(duration);
                    return wjiApplied ? (w, h) : null;
                }
            }
        }

        return wjiApplied ? (w, h) : null;
    }

    /// <summary>把 seed 设为随机值，避免导出工作流写死 seed 导致每次生成结果完全相同。
    /// 兼容 SeedNode（核心/常见自定义节点）与 easy seed（ComfyUI-Easy-Use）两种节点类型。</summary>
    private static void RandomizeSeed(JsonNode root)
    {
        if (root is not JsonObject obj) return;
        foreach (var node in obj)
        {
            if (node.Value is not JsonObject nodeObj) continue;
            var ct = nodeObj["class_type"]?.GetValue<string>();
            if (ct != "SeedNode" && ct != "easy seed") continue;
            if (nodeObj["inputs"] is JsonObject inputs && inputs["seed"] != null)
            {
                inputs["seed"] = JsonValue.Create(Random.Shared.NextInt64(0, 1_000_000_000_000));
            }
        }
    }

    /// <summary>系统画幅 → ComfyUI ResolutionSelector 的 aspect_ratio 选项</summary>
    private static string? RatioToAspectLabel(string ratio) => ratio switch
    {
        "16:9" => "16:9 (Widescreen)",
        "9:16" => "9:16 (Portrait)",
        "1:1" => "1:1 (Square)",
        "4:3" => "4:3 (Standard)",
        "3:4" => "3:4 (Portrait)",
        "2:1" => "2:1 (Panorama)",
        _ => null,
    };

    /// <summary>把值注入第一个指定类型的节点（节点存在且含该输入时才写）</summary>
    private static void InjectInput(JsonNode root, string classType, string inputKey, string? value)
    {
        if (value == null) return;
        if (root is not JsonObject obj) return;
        foreach (var node in obj)
        {
            if (node.Value is not JsonObject nodeObj) continue;
            var ct = nodeObj["class_type"]?.GetValue<string>();
            if (ct != classType) continue;
            if (nodeObj["inputs"] is JsonObject inputs && inputs[inputKey] != null)
            {
                inputs[inputKey] = JsonValue.Create(value);
                return;
            }
        }
    }

    /// <summary>递归判断工作流里是否存在值恰好等于 text 的字符串字段（用于确认提示词确实注入成功）。</summary>
    private static bool WorkflowContainsText(JsonNode? node, string text)
    {
        switch (node)
        {
            case JsonObject o:
                foreach (var kv in o) { if (WorkflowContainsText(kv.Value, text)) return true; }
                return false;
            case JsonArray arr:
                foreach (var item in arr) { if (WorkflowContainsText(item, text)) return true; }
                return false;
            case JsonValue v:
                return v.TryGetValue<string>(out var s) && string.Equals(s, text, StringComparison.Ordinal);
            default:
                return false;
        }
    }

    /// <summary>递归替换字符串中的占位符（如 __COMFY_PROMPT__）</summary>
    private static void ReplaceToken(JsonNode node, string token, string value)
    {
        if (node is JsonObject o)
        {
            foreach (var kv in o) { if (kv.Value != null) ReplaceToken(kv.Value, token, value); }
            return;
        }
        if (node is JsonArray arr)
        {
            foreach (var item in arr) { if (item != null) ReplaceToken(item, token, value); }
            return;
        }
        if (node is JsonValue v && v.TryGetValue<string>(out var s) && s.Contains(token))
            v.ReplaceWith(JsonValue.Create(s.Replace(token, value)));
    }

    /// <summary>画幅比例 → 宽高（MiniMax H3：64~8192、32 的倍数，取值与模板原本的 1376×768 同量级 ≈1MP）</summary>
    public static (int width, int height) RatioToH3Size(string ratio)
    {
        return ratio switch
        {
            "9:16" => (768, 1376),
            "1:1" => (1024, 1024),
            "4:3" => (1152, 864),
            "3:4" => (864, 1152),
            "2:1" => (1472, 736),
            _ => (1376, 768),
        };
    }

    /// <summary>画幅比例 → 宽高（Wan 系列要求 16 的倍数，480p 附近）</summary>
    public static (int width, int height) RatioToSize(string ratio)
    {
        return ratio switch
        {
            "9:16" => (480, 832),
            "1:1" => (640, 640),
            "4:3" => (640, 480),
            "3:4" => (480, 640),
            "2:1" => (1024, 512),
            _ => (832, 480),
        };
    }

    /// <summary>下载视频到本地存储（与 VideoService 相同路径）。
    /// 同一远程 URL 只会真正下载一次：状态轮询、后台巡检、重启前的重复调用都复用同一份本地文件。</summary>
    public Task<string?> DownloadVideoToLocal(string videoUrl, int promptId, int projectId)
        => VideoLocalStore.DownloadOnceAsync(
            videoUrl, () => DownloadVideoToLocalCore(videoUrl, promptId, projectId), _logger, "ComfyService");

    /// <summary>真正落盘下载（调用方请走 DownloadVideoToLocal，避免同一份视频被下载多遍）</summary>
    private async Task<string?> DownloadVideoToLocalCore(string videoUrl, int promptId, int projectId)
    {
        try
        {
            if (string.IsNullOrEmpty(videoUrl)) return null;

            // 同一个远程地址之前已经下过（例如进程重启前下的）→ 直接复用，不再下一份
            var existingLocalUrl = _db.GetLocalVideoUrlByRemoteUrl(videoUrl);
            var existingPath = VideoLocalStore.MapToPhysicalPath(existingLocalUrl);
            if (!string.IsNullOrEmpty(existingPath) && File.Exists(existingPath))
            {
                _logger.LogInformation("[ComfyService] 复用已下载的本地视频 {LocalUrl}（同一远程地址不重复下载）", existingLocalUrl);
                return existingLocalUrl;
            }

            var uploadDir = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "videos", projectId.ToString());
            Directory.CreateDirectory(uploadDir);
            var ext = ".mp4";
            if (videoUrl.Contains(".webm")) ext = ".webm";
            else if (videoUrl.Contains(".gif")) ext = ".gif";
            var fileName = "prompt_" + promptId + "_" + DateTime.Now.ToString("yyyyMMddHHmmssfff") + ext;
            var filePath = Path.Combine(uploadDir, fileName);
            await VideoLocalStore.DownloadToFileAsync(_http, videoUrl, filePath);
            var localUrl = "/uploads/videos/" + projectId + "/" + fileName;
            _logger.LogInformation("[ComfyService] Downloaded video to {LocalUrl}", localUrl);

            // 解析视频真实时长/分辨率并写回任务记录
            try
            {
                var (duration, resolution) = ProbeVideo(filePath);
                var task = _db.GetLatestVideoTask(projectId, promptId);
                if (task != null)
                    _db.UpdateVideoTaskMedia(promptId, task.TaskId, resolution, duration);
            }
            catch (Exception ex) { _logger.LogWarning(ex, "[ComfyService] probe video error"); }

            return localUrl;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ComfyService] Download video error");
            return null;
        }
    }

    /// <summary>用 ffprobe 解析视频真实时长（秒）与分辨率</summary>
    public static (double? duration, string? resolution) ProbeVideo(string filePath)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("ffprobe")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-select_streams"); psi.ArgumentList.Add("v:0");
            psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("stream=width,height,duration");
            psi.ArgumentList.Add("-show_entries"); psi.ArgumentList.Add("format=duration");
            psi.ArgumentList.Add("-of"); psi.ArgumentList.Add("json");
            psi.ArgumentList.Add(filePath);
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return (null, null);
            var stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            if (string.IsNullOrWhiteSpace(stdout)) return (null, null);
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;
            double? duration = null;
            if (root.TryGetProperty("format", out var fmt) && fmt.TryGetProperty("duration", out var durEl) && durEl.TryGetDouble(out var durVal))
                duration = durVal;
            string? resolution = null;
            if (root.TryGetProperty("streams", out var streams) && streams.GetArrayLength() > 0)
            {
                var s0 = streams[0];
                var w = s0.TryGetProperty("width", out var wEl) ? wEl.GetInt32() : 0;
                var h = s0.TryGetProperty("height", out var hEl) ? hEl.GetInt32() : 0;
                if (w > 0 && h > 0) resolution = w + "x" + h;
            }
            return (duration, resolution);
        }
        catch { return (null, null); }
    }

    /// <summary>取消任务：标记取消 + 尽力中断 ComfyUI 当前执行</summary>
    public async Task<bool> CancelTask(int promptId, string baseUrl, int projectId)
    {
        try
        {
            baseUrl = baseUrl.TrimEnd('/');
            var tasks = _db.GetVideoTasks(projectId);
            var task = tasks.FirstOrDefault(t => t.PromptId == promptId);
            if (task == null || string.IsNullOrEmpty(task.TaskId)) return false;

            try
            {
                // 中断正在执行的任务；队列里的任务取消由用户自行在 ComfyUI 里操作
                var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/interrupt");
                req.Content = new StringContent("{}", Encoding.UTF8, "application/json");
                await _http.SendAsync(req);
            }
            catch { }

            _db.UpdatePromptStatus(promptId, "cancelled");
            _db.UpdateVideoTaskStatus(promptId, task.TaskId, "cancelled", null, null, null, null, null, DateTime.Now, "cancelled");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ComfyService] Cancel error");
            return false;
        }
    }
}
