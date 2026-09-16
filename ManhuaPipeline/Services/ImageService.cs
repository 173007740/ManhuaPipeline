using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// 文生图服务：只要是对 OpenAI 协议兼容的「中转」接口就能用。
/// 配置来源：LLMConfigs 中 Provider='image' 的 ApiUrl / ApiKey / ModelName。
///
/// 纯文本出图（文生图）走两条路径，前一条不通自动退到后一条，避免中转站差异导致直接失败：
///   ① 主路径 POST {base}/images/generations —— 标准文生图接口（gpt-image / seedream / flux / qwen-image 等）
///   ② 兜底   POST {base}/chat/completions  —— 中转站上不少图像模型（如 gemini-*-image）只挂在对话接口上，
///      图会以 markdown 图片链接或 base64 data URI 的形式出现在回复正文里，这里统一抽出来。
/// 两条都失败时会把两边的原因一起返回，方便排查是中转地址、鉴权还是模型名的问题。
///
/// 带参考图的出图（图生图）由 <see cref="GenerateWithImagesAsync"/> 走另一套端点，同样是两条路径：
///   ① 主路径 POST {base}/images/edits —— multipart/form-data，多张参考图以重复的 image[] 字段提交
///   ② 兜底   POST {base}/chat/completions —— 图片以 data:image/...;base64 塞进多模态 content 数组
/// </summary>
public class ImageService
{
    private readonly HttpClient _http;
    private readonly ILogger<ImageService> _logger;

    public ImageService(HttpClient http, ILogger<ImageService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>出图结果：成功时 Data 为图片原始字节，Ext 为带点的扩展名。</summary>
    public sealed record ImageResult(bool Ok, byte[]? Data, string? Ext, string? Error);

    // ==================== 纯函数（地址 / 尺寸 / 提示词 / 解析），抽出便于单测 ====================

    /// <summary>
    /// 把用户配置的地址归一成具体 endpoint，兼容几种常见填法：
    /// 只填域名（补 /v1）、填到 base（.../v1）、或直接把完整 endpoint 填进来（.../v1/chat/completions）。
    /// </summary>
    public static string BuildEndpoint(string? apiUrl, string path)
    {
        var url = (apiUrl ?? "").Trim().TrimEnd('/');
        if (url.Length == 0 || !url.Contains("://")) return "";

        // 用户可能把某个具体 endpoint 整条粘进来，先退回到 base，避免拼出 .../v1/chat/completions/images/generations
        foreach (var suffix in new[] { "/images/generations", "/chat/completions" })
        {
            if (url.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                url = url[..^suffix.Length].TrimEnd('/');
                break;
            }
        }
        if (url.Length == 0) return "";

        // 只给了域名（没有路径）时按惯例补 /v1，否则绝大多数中转会 404
        var afterScheme = url[(url.IndexOf("://", StringComparison.Ordinal) + 3)..];
        if (!afterScheme.Contains('/')) url += "/v1";

        return url + "/" + path;
    }

    /// <summary>资产卡出图统一尺寸（16:9）。角色/道具/环境/特效四类一致，与成片画幅对齐；
    /// 想让某一类单独用别的尺寸时，改这里的分支即可（前端不传 size，一律取此默认值）。</summary>
    public static string DefaultSizeFor(string category) => AssetImageSize;

    /// <summary>资产卡出图尺寸（16:9，1280×720）。中转接口若不支持该尺寸会在出图时报错，需换成模型支持的尺寸。</summary>
    public const string AssetImageSize = "1280x720";

    /// <summary>
    /// 画幅约束，写进提示词让模型自己就按 16:9 横向构图。
    /// 模型并不保证听话（实测图生图会跟随参考图比例，回了 1214x1295 的近方形），
    /// 所以落盘时还有 <see cref="ImageAspect.AlignTo16By9"/> 居中裁切兜底；
    /// 提示词这层的作用是让主体待在画面中间的「安全区」，把裁切损失降到最低。
    /// </summary>
    public const string AspectPromptHint = "画面规格：16:9 横向宽幅（1280x720），主体完整居中，上下边缘留出安全边距（不要让主体顶到画面上下缘），不要方形或竖版构图。";

    public static bool IsValidSize(string? size) =>
        !string.IsNullOrWhiteSpace(size) && Regex.IsMatch(size.Trim(), @"^\d{3,4}\s*[xX*]\s*\d{3,4}$");

    /// <summary>
    /// 清洗资产描述，压成一行可用的视觉描述。
    /// 特意丢掉「角色别名：xxx」这类行——别名只服务于提示词里的 [资产名] 文本绑定，
    /// 送给图像模型只会造成干扰（模型会尝试把别名文字画进画面）。
    /// </summary>
    public static string CleanDescription(string? description)
    {
        if (string.IsNullOrWhiteSpace(description)) return "";
        var kept = new List<string>();
        foreach (var raw in description.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.Trim().TrimStart('-', '*', '·', ' ', '\t');
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"^(角色)?别名\s*[：:]")) continue;
            line = Regex.Replace(line, @"^(角色描述|人物描述|外观|形象|描述|设定|特征)\s*[：:]", "").Trim();
            if (line.Length == 0) continue;
            kept.Add(line);
        }
        var text = string.Join("；", kept);
        return text.Length > 600 ? text[..600] : text;
    }

    /// <summary>把资产卡内容（名称 + 描述 + 属性 + 项目风格）拼成出图提示词。</summary>
    public static string BuildAssetImagePrompt(
        string category, string name, string? description, string? attributes,
        string? stylePrompt, string? extraPrompt)
    {
        var desc = CleanDescription(description);
        var attr = CleanDescription(attributes);
        var sb = new StringBuilder();

        switch (category)
        {
            case "characters":
                sb.Append("角色设定参考图：").Append(name).Append('。');
                if (desc.Length > 0) sb.Append(desc).Append('。');
                if (attr.Length > 0) sb.Append("形态/属性：").Append(attr).Append('。');
                sb.Append("要求：单人全身正面站姿，纯色干净背景，五官与服装细节清晰完整，构图居中；不要文字、不要水印、不要多视图拼贴。");
                break;

            case "environments":
                sb.Append("场景环境概念图：").Append(name).Append('。');
                if (desc.Length > 0) sb.Append(desc).Append('。');
                sb.Append("要求：没有人物的空镜头，广角全景，光线与氛围明确；不要文字、不要水印。");
                break;

            case "effects":
                sb.Append("特效/技能视觉参考图：").Append(name).Append('。');
                if (desc.Length > 0) sb.Append(desc).Append('。');
                sb.Append("要求：法术或能量的形态与色调清晰可辨，深色背景；不要文字、不要水印。");
                break;

            default: // props
                sb.Append("道具设定图：").Append(name).Append('。');
                if (desc.Length > 0) sb.Append(desc).Append('。');
                sb.Append("要求：单件道具居中，纯色背景，材质与细节清晰；不要文字、不要水印。");
                break;
        }

        if (!string.IsNullOrWhiteSpace(stylePrompt)) sb.Append("画面风格：").Append(stylePrompt.Trim()).Append('。');
        if (!string.IsNullOrWhiteSpace(extraPrompt))
        {
            var extra = extraPrompt.Trim().TrimEnd('。', '.');
            sb.Append(extra).Append('。');
        }
        sb.Append(AspectPromptHint);
        return sb.ToString();
    }

    /// <summary>
    /// 「模版模式」的出图提示词组装：资产正文（提取时按模版生成）+ 统一视觉风格 + 项目画风 + 临时追加 + 统一负面提示词。
    /// 与 BuildAssetImagePrompt 的区别：正文由 LLM 按模版写好并已存库，这里只做拼接，
    /// 所以改一次模版里的风格/负面词，全项目已有资产立刻生效，不用重新提取。
    /// </summary>
    public static string ComposeAssetImagePrompt(
        string? bodyPrompt, string? styleLock, string? negativePrompt, string? stylePrompt, string? extraPrompt)
    {
        var sb = new StringBuilder();
        void Append(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            sb.Append(text.Trim().TrimEnd('。', '.', '；', ';')).Append('。');
        }

        Append(bodyPrompt);
        var styleLockText = string.IsNullOrWhiteSpace(styleLock) ? null : styleLock.Trim().TrimEnd('。', '.');
        var projectStyleText = string.IsNullOrWhiteSpace(stylePrompt) ? null : stylePrompt.Trim().TrimEnd('。', '.');
        if (styleLockText != null) sb.Append("统一视觉风格：").Append(styleLockText).Append('。');
        // 项目画风与模版风格重复时不再拼第二遍（例如把项目画风一键带入本剧模版，两边就是同一段话）
        if (projectStyleText != null && !IsRedundantStyleText(projectStyleText, styleLockText))
            sb.Append("画面风格：").Append(projectStyleText).Append('。');
        Append(extraPrompt);
        Append(AspectPromptHint);
        if (!string.IsNullOrWhiteSpace(negativePrompt))
            sb.Append("负面提示词（画面中禁止出现）：").Append(negativePrompt.Trim().TrimEnd('。', '.')).Append('。');
        return sb.ToString();
    }

    /// <summary>两段风格文本是否重复：忽略空白与标点后完全相同，或一方包含另一方。</summary>
    private static bool IsRedundantStyleText(string a, string? b)
    {
        var y = NormalizeStyleText(b);
        if (y.Length == 0) return false;
        var x = NormalizeStyleText(a);
        if (x.Length == 0) return false;
        return x == y || x.Contains(y) || y.Contains(x);
    }

    private static string NormalizeStyleText(string? s) =>
        Regex.Replace(s ?? "", @"[\s，、；;。.]+", "");

    /// <summary>解析 /images/generations 的响应：data[0].url 或 data[0].b64_json（有的中转把图塞成 data URI）。</summary>
    public static (string? Url, string? B64) ParseImageData(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array
            || data.GetArrayLength() == 0)
            return (null, null);

        var first = data[0];
        string? url = first.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        string? b64 = first.TryGetProperty("b64_json", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;

        if (string.IsNullOrWhiteSpace(b64) && !string.IsNullOrWhiteSpace(url))
        {
            var dataUri = ExtractDataUriBase64(url);
            if (dataUri != null) { b64 = dataUri; url = null; }
        }

        return (string.IsNullOrWhiteSpace(url) ? null : url, string.IsNullOrWhiteSpace(b64) ? null : b64);
    }

    /// <summary>
    /// 解析 /chat/completions 的响应：优先 message.images[0].image_url.url，
    /// 其次从正文里找 markdown 图片链接、base64 data URI 或裸图片 URL。
    /// </summary>
    public static (string? Url, string? B64) ParseChatImage(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return (null, null);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            return (null, null);
        if (!choices[0].TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object)
            return (null, null);

        if (msg.TryGetProperty("images", out var imgs)
            && imgs.ValueKind == JsonValueKind.Array && imgs.GetArrayLength() > 0
            && imgs[0].TryGetProperty("image_url", out var iu) && iu.ValueKind == JsonValueKind.Object
            && iu.TryGetProperty("url", out var iuUrl) && iuUrl.ValueKind == JsonValueKind.String)
        {
            var v = (iuUrl.GetString() ?? "").Trim();
            if (v.Length > 0)
            {
                var dataUri = ExtractDataUriBase64(v);
                if (dataUri != null) return (null, dataUri);
                if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return (v, null);
                return (null, v); // 少数中转直接给裸 base64
            }
        }

        var content = msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        if (string.IsNullOrWhiteSpace(content)) return (null, null);

        var md = Regex.Match(content, @"!\[[^\]]*\]\(\s*(https?://[^)\s]+)\s*\)");
        if (md.Success) return (md.Groups[1].Value, null);

        var mdData = Regex.Match(content, @"data:image/[a-zA-Z+.-]+;base64,([A-Za-z0-9+/=\s]+)");
        if (mdData.Success) return (null, mdData.Groups[1].Value);

        var rawUrl = Regex.Match(content, @"https?://[^\s""'\)\]]+\.(?:png|jpe?g|webp|gif)");
        if (rawUrl.Success) return (rawUrl.Value, null);

        return (null, null);
    }

    /// <summary>
    /// 把 data:image/xxx;base64,AAAA 形式的串里的 base64 正文取出来。
    /// 用 IndexOf 手拆而不是正则——base64 动辄几 MB，正则回溯代价太高。
    /// </summary>
    private static string? ExtractDataUriBase64(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        if (!text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return null;
        const string marker = ";base64,";
        var idx = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var payload = text[(idx + marker.Length)..].Trim();
        return payload.Length == 0 ? null : payload;
    }

    /// <summary>按文件头识别真实格式（中转返回的 content-type 经常不可信）。</summary>
    public static string DetectExt(byte[] data, string? contentType = null)
    {
        if (data.Length >= 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ".png";
        if (data.Length >= 3 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
        if (data.Length >= 12 && data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
            && data[8] == 0x57 && data[9] == 0x45 && data[10] == 0x42 && data[11] == 0x50) return ".webp";
        if (data.Length >= 4 && data[0] == 0x47 && data[1] == 0x49 && data[2] == 0x46) return ".gif";

        if (!string.IsNullOrEmpty(contentType))
        {
            if (contentType.Contains("jpeg", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("jpg", StringComparison.OrdinalIgnoreCase)) return ".jpg";
            if (contentType.Contains("webp", StringComparison.OrdinalIgnoreCase)) return ".webp";
            if (contentType.Contains("gif", StringComparison.OrdinalIgnoreCase)) return ".gif";
        }
        return ".png";
    }

    /// <summary>物理文件名净化（中文保留，仅替换非法字符）。</summary>
    public static string SanitizeName(string? name)
    {
        var s = (name ?? "").Trim();
        foreach (var ch in Path.GetInvalidFileNameChars()) s = s.Replace(ch, '_');
        s = s.Replace('/', '_').Replace('\\', '_').Replace(':', '_').Trim('.', ' ');
        if (s.Length == 0) s = "asset";
        return s.Length > 60 ? s[..60] : s;
    }

    /// <summary>
    /// 把图片落到参考图目录下，返回可访问 URL 与体积。
    /// 落盘前先做一次 16:9 画幅对齐：图生图接口经常忽略 size，回的是模型自己的比例。
    /// </summary>
    public static (string Url, long Size) SaveLocalImage(byte[] data, string ext, string fileNameBase, ILogger? logger = null)
    {
        // 画幅对齐依赖 GDI+，只在 Windows 上做；其它平台按模型原图落盘，不影响出图成败。
        if (OperatingSystem.IsWindows())
            (data, ext) = ImageAspect.AlignTo16By9(data, ext, logger);

        var dir = Path.Combine(AppPaths.Root, "uploads", "reference");
        Directory.CreateDirectory(dir);
        var fileName = $"{SanitizeName(fileNameBase)}_{DateTime.Now:yyyyMMddHHmmssfff}{ext}";
        File.WriteAllBytes(Path.Combine(dir, fileName), data);
        return ($"/uploads/reference/{fileName}", data.Length);
    }

    // ==================== 出图主流程 ====================

    /// <summary>
    /// 调中转接口出图。prompt 由 <see cref="BuildAssetImagePrompt"/> 生成；
    /// apiUrl/apiKey/model 来自 LLMConfigs(Provider='image')。
    /// </summary>
    public async Task<ImageResult> GenerateAsync(
        string prompt, string? apiUrl, string apiKey, string model, string size, CancellationToken ct = default)
    {
        var errors = new List<string>();
        var imagesEndpoint = BuildEndpoint(apiUrl, "images/generations");
        var chatEndpoint = BuildEndpoint(apiUrl, "chat/completions");
        if (imagesEndpoint.Length == 0 && chatEndpoint.Length == 0)
            return new ImageResult(false, null, null, "文生图接口地址无效，请到「API 配置 → 文生图（中转）」检查（例如 https://your-relay.com/v1）");

        // ① 标准文生图接口
        if (imagesEndpoint.Length > 0)
        {
            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["prompt"] = prompt,
                    ["n"] = 1,
                    ["size"] = size
                });
                using var resp = await PostJsonAsync(imagesEndpoint, apiKey, body, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var (url, b64) = ParseImageData(text);
                    var got = await MaterializeAsync(url, b64, ct);
                    if (got != null) return got;
                    errors.Add($"/images/generations 返回成功但没解析出图片：{Truncate(text)}");
                }
                else
                {
                    errors.Add($"/images/generations HTTP {(int)resp.StatusCode}：{Truncate(text)}");
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
            {
                errors.Add($"/images/generations 请求异常：{ex.Message}");
            }
        }

        // ② 兜底：对话接口（gemini-*-image 这类只挂 chat 的图像模型）
        if (chatEndpoint.Length > 0 && !string.Equals(chatEndpoint, imagesEndpoint, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["messages"] = new object[] { new { role = "user", content = prompt } }
                });
                using var resp = await PostJsonAsync(chatEndpoint, apiKey, body, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var (url, b64) = ParseChatImage(text);
                    var got = await MaterializeAsync(url, b64, ct);
                    if (got != null) return got;
                    errors.Add($"/chat/completions 返回成功但回复里没有图片：{Truncate(text)}");
                }
                else
                {
                    errors.Add($"/chat/completions HTTP {(int)resp.StatusCode}：{Truncate(text)}");
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
            {
                errors.Add($"/chat/completions 请求异常：{ex.Message}");
            }
        }

        var message = errors.Count > 0 ? string.Join("；", errors) : "未拿到图片";
        _logger.LogWarning("[ImageService] 出图失败 model={Model} size={Size}: {Message}", model, size, message);
        return new ImageResult(false, null, null, message);
    }

    /// <summary>
    /// 带参考图出图（图生图）：把若干张本地参考图连同 prompt 一起提交，用于
    /// 「角色图 + 服装参考图 + 描述性提示词 → 新的角色卡」这类派生场景。
    /// 参考图的先后顺序有意义，会在提示词里以「第一张/第二张」表述，调用方需按约定顺序传入。
    /// </summary>
    /// <param name="imagePaths">本地图片路径，支持 /uploads/xxx 这类站内相对路径，也支持绝对路径。</param>
    public async Task<ImageResult> GenerateWithImagesAsync(
        string prompt, IReadOnlyList<string> imagePaths,
        string? apiUrl, string apiKey, string model, string size,
        CancellationToken ct = default)
    {
        var files = new List<(byte[] Data, string Ext, string Mime)>();
        foreach (var path in imagePaths ?? Array.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var full = ResolveLocalPath(path);
                if (full == null || !File.Exists(full)) { _logger.LogWarning("[ImageService] 参考图不存在：{Path}", path); continue; }
                var data = await File.ReadAllBytesAsync(full, ct);
                if (data.Length == 0) continue;
                var ext = DetectExt(data);
                files.Add((data, ext, MimeFor(ext)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("[ImageService] 参考图读取失败 {Path}: {Message}", path, ex.Message);
            }
        }
        if (files.Count == 0)
            return new ImageResult(false, null, null, "没有可用的参考图（文件不存在或读取失败）");

        var errors = new List<string>();
        var editsEndpoint = BuildEndpoint(apiUrl, "images/edits");
        var chatEndpoint = BuildEndpoint(apiUrl, "chat/completions");
        if (editsEndpoint.Length == 0 && chatEndpoint.Length == 0)
            return new ImageResult(false, null, null, "文生图接口地址无效，请到「API 配置 → 文生图（中转）」检查（例如 https://your-relay.com/v1）");

        // ① 主路径：图片编辑接口。多张参考图 = 重复的 image[] 字段（OpenAI 的图生图约定）。
        if (editsEndpoint.Length > 0)
        {
            try
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent(model, Encoding.UTF8), "model");
                form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
                form.Add(new StringContent(size, Encoding.UTF8), "size");
                for (var i = 0; i < files.Count; i++)
                {
                    var part = new ByteArrayContent(files[i].Data);
                    part.Headers.ContentType = new MediaTypeHeaderValue(files[i].Mime);
                    // 文件名固定用 ASCII：中转站是按重复的 image[] 收集图片的，不依赖原始文件名，
                    // 而中文名在某些中转的 multipart 解析里会被当成非法编码直接丢掉。
                    form.Add(part, "image[]", $"image{i}{files[i].Ext}");
                }

                using var resp = await PostMultipartAsync(editsEndpoint, apiKey, form, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var (url, b64) = ParseImageData(text);
                    var got = await MaterializeAsync(url, b64, ct);
                    if (got != null) return got;
                    errors.Add($"/images/edits 返回成功但没解析出图片：{Truncate(text)}");
                }
                else
                {
                    errors.Add($"/images/edits HTTP {(int)resp.StatusCode}：{Truncate(text)}");
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
            {
                errors.Add($"/images/edits 请求异常：{ex.Message}");
            }
        }

        // ② 兜底：多模态对话接口（参考图转成 data URI 塞进 content 数组）
        if (chatEndpoint.Length > 0)
        {
            try
            {
                var content = new List<object> { new { type = "text", text = prompt } };
                foreach (var f in files)
                    content.Add(new { type = "image_url", image_url = new { url = ToDataUri(f.Data, f.Mime) } });

                var body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["messages"] = new object[] { new { role = "user", content = content.ToArray() } }
                });
                using var resp = await PostJsonAsync(chatEndpoint, apiKey, body, ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var (url, b64) = ParseChatImage(text);
                    var got = await MaterializeAsync(url, b64, ct);
                    if (got != null) return got;
                    errors.Add($"/chat/completions 返回成功但回复里没有图片：{Truncate(text)}");
                }
                else
                {
                    errors.Add($"/chat/completions HTTP {(int)resp.StatusCode}：{Truncate(text)}");
                }
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
            {
                errors.Add($"/chat/completions 请求异常：{ex.Message}");
            }
        }

        var message = errors.Count > 0 ? string.Join("；", errors) : "未拿到图片";
        _logger.LogWarning("[ImageService] 参考图出图失败 model={Model} size={Size} 参考图={Count}张: {Message}",
            model, size, files.Count, message);
        return new ImageResult(false, null, null, message);
    }

    /// <summary>
    /// 把站内相对路径（/uploads/reference/xxx.png）或绝对路径解析成本地物理路径。
    /// 远程图片地址返回 null —— 参考图目前一律取自本机图库，调用方无需处理下载。
    /// 实测踩过的坑见 <see cref="AppPaths.ResolveUpload"/>：Windows 上 Path.IsPathRooted("/uploads/x.png")
    /// 返回 true，判定顺序写反会让所有站内参考图都被判成文件不存在。
    /// </summary>
    private static string? ResolveLocalPath(string path) => AppPaths.ResolveUpload(path);

    private static string MimeFor(string ext) => ext switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png"
    };

    private static string ToDataUri(byte[] data, string mime) =>
        $"data:{mime};base64,{Convert.ToBase64String(data)}";

    private async Task<ImageResult?> MaterializeAsync(string? url, string? b64, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(b64))
        {
            byte[] raw;
            try { raw = Convert.FromBase64String(Regex.Replace(b64, @"\s", "")); }
            catch (FormatException) { return null; }
            if (raw.Length == 0) return null;
            return new ImageResult(true, raw, DetectExt(raw), null);
        }

        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var raw = await resp.Content.ReadAsByteArrayAsync(ct);
            if (raw.Length == 0) return null;
            return new ImageResult(true, raw, DetectExt(raw, resp.Content.Headers.ContentType?.MediaType), null);
        }
        catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<HttpResponseMessage> PostJsonAsync(string endpoint, string apiKey, string json, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    private async Task<HttpResponseMessage> PostMultipartAsync(
        string endpoint, string apiKey, MultipartFormDataContent form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        req.Content = form;
        return await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
    }

    private static string Truncate(string? text, int max = 300)
    {
        var t = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > max ? t[..max] + "…" : t;
    }
}
