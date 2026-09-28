using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using ManhuaPipeline.Models;

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
    /// 出图质量档位（gpt-image-2.5：low / medium / high / xhigh / max）。
    /// 2.5 重新分过档：它的 high 只等于上一代的 medium，max 才等于旧 high，
    /// 所以继续传 high 会整整掉一档——人脸五官、手部、材质细节最先糊，这里统一走 xhigh。
    /// 说明：max 更细但耗时逼近 10 分钟超时（我们 HttpClient 给的是 600 秒），不划算。
    /// 部分中转站不认这个字段会直接 4xx，出图时会自动降级成不带 quality 重试一次。
    /// </summary>
    public const string ImageQuality = "xhigh";

    // ==================== 火山方舟（ark.cn-beijing.volces.com）适配 ====================
    // 方舟的图像接口跟 OpenAI 兼容协议有三处不一样，所以单独走一套分支：
    //   ① 尺寸只认固定档位（2048x1152 这类），传 1280x720 会被拒；
    //   ② 图生图不是 multipart /images/edits，而是同一个 /images/generations 带 image 数组（data URI）；
    //   ③ 响应可以要 b64_json 或 url，url 只有 24 小时有效期，优先要 b64 免得再下载一次。

    /// <summary>方舟 16:9 档位：2560x1440（369 万像素）。seedream-5.0-lite 的下限就是 3686400 像素，
    /// 传更小的 2048x1152 会被拒（size not valid），所以这里必须取到 2K；且正好 16:9，落盘画幅对齐不会裁掉画面。</summary>
    public const string ArkSizeLandscape = "2560x1440";

    /// <summary>方舟正方形档位（同为 ≥369 万像素档）。</summary>
    public const string ArkSizeSquare = "2048x2048";

    /// <summary>方舟竖版档位。</summary>
    public const string ArkSizePortrait = "1440x2560";

    /// <summary>地址指向火山方舟（ark.*.volces.com）时为 true。</summary>
    public static bool IsArkApi(string? apiUrl) =>
        !string.IsNullOrWhiteSpace(apiUrl) && apiUrl.Contains("volces.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 把我们的尺寸（如 1280x720）映射成方舟认的档位。按宽高比就近取，
    /// 认不出来时按 16:9 处理——资产卡本来就是 16:9。
    /// </summary>
    public static string MapArkSize(string? size)
    {
        var m = Regex.Match(size ?? "", @"^(\d{3,4})\s*[xX*]\s*(\d{3,4})$");
        if (!m.Success) return ArkSizeLandscape;
        var w = int.Parse(m.Groups[1].Value);
        var h = int.Parse(m.Groups[2].Value);
        if (h <= 0) return ArkSizeLandscape;
        var ratio = (double)w / h;
        return ratio >= 1.6 ? ArkSizeLandscape : ratio <= 0.63 ? ArkSizePortrait : ArkSizeSquare;
    }

    /// <summary>
    /// 画幅约束，写进提示词让模型自己就按 16:9 横向构图。
    /// 模型并不保证听话（实测图生图会跟随参考图比例，回了 1214x1295 的近方形），
    /// 所以落盘时还有 <see cref="ImageAspect.AlignTo16By9"/> 居中裁切兜底；
    /// 提示词这层的作用是让主体待在画面中间的「安全区」，把裁切损失降到最低。
    /// </summary>
    public const string AspectPromptHint = "画面规格：16:9 横向宽幅（2560x1440），主体完整居中，上下边缘留出安全边距（不要让主体顶到画面上下缘），不要方形或竖版构图。";

    // ---------------------------------------------------------------------------------
    // 方舟出图前的提示词预处理
    //
    // ① 剥离负面词：我们的提示词里有一段「负面提示词（画面中禁止出现）：真人、cosplay、低幼Q版…」，
    //    这段是给能理解否定式指令的模型写的。Seedream 拿到中文长句里的「低幼Q版」这类词反而会被激活，
    //    真就给你画个 Q 版出来。所以出图前把这段切出来，走方舟自己的 negative_prompt 字段，
    //    正面 prompt 只保留「该画什么」。
    //
    // ② 钉死头身比：二次元/萌系项目（星穹铁道 PV 风这类）模型默认很容易把人画成 chibi 大头，
    //    这里显式写成年人体型与头身比，只在提示词确实涉及人物时追加，避免给空镜头/道具图凭空招来人物。
    // ---------------------------------------------------------------------------------

    /// <summary>匹配提示词末尾那段「负面提示词（…）：xxx。」。</summary>
    private static readonly Regex ArkNegativeBlock = new(
        @"负面提示词[（(][^）)]*[)）]\s*[:：]\s*(?<neg>.+?)\s*[。.]\s*$",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private const string ArkFigureHint =
        "人物比例：成年人体型，正常头身比（约 7 头身），五官与身体比例正常协调；不要 Q 版、不要 chibi 大头娃娃比例、不要幼儿化。";

    private static readonly Regex NoFigure = new(
        "没有人|无人物|空镜头|不含人物|没有人物的|不包含人物", RegexOptions.Compiled);

    private static readonly Regex HasFigure = new(
        "角色|人物|女孩|少女|少年|男孩|女人|男人|女生|男生|肖像|半身|全身|站姿|coser",
        RegexOptions.Compiled);

    /// <summary>方舟专用：切出负面词段，并按需要补一条人物比例约束。</summary>
    private static (string Prompt, string? Negative) PrepareArkPrompt(string prompt)
    {
        var p = (prompt ?? "").Trim();
        string? negative = null;

        var m = ArkNegativeBlock.Match(p);
        if (m.Success)
        {
            var neg = m.Groups["neg"].Value.Trim().TrimEnd('。', '.');
            var body = p[..m.Index].Trim();
            // 只剩负面词一条的情况下不要把正文清空，宁可原样发出去
            if (body.Length > 0 && neg.Length > 0)
            {
                p = body;
                negative = neg;
            }
        }

        if (!NoFigure.IsMatch(p) && HasFigure.IsMatch(p))
            p += "\n" + ArkFigureHint;

        return (p, negative);
    }

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
                // 1280x720 的全身构图里脸只占很小一块，不点名强调的话模型很容易糊脸/五官跑偏，
                // 所以这里显式约束面部质量与构图留白。
                sb.Append("要求：单人全身正面站姿，纯色干净背景，人物面部清晰写实、五官对称不变形、皮肤质感真实，手部结构正常，服装细节清晰完整，构图居中、头部上方留白；不要文字、不要水印、不要多视图拼贴、不要面部畸变或糊脸。");
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
    /// <param name="options">页面可配的出图参数（质量 / 背景 / 输出格式；比例已在调用方转成 size）。
    /// 传 null 时退化为原来的「只带 quality」行为。</param>
    public async Task<ImageResult> GenerateAsync(
        string prompt, string? apiUrl, string apiKey, string model, string size,
        CancellationToken ct = default, ImageGenOptions? options = null)
    {
        // 火山方舟协议不一样，单独走一套，别混进 OpenAI 兼容那两条路径。
        if (IsArkApi(apiUrl))
            return await ArkGenerateAsync(prompt, apiUrl, apiKey, model, size, ct);

        var errors = new List<string>();
        var imagesEndpoint = BuildEndpoint(apiUrl, "images/generations");
        var chatEndpoint = BuildEndpoint(apiUrl, "chat/completions");
        if (imagesEndpoint.Length == 0 && chatEndpoint.Length == 0)
            return new ImageResult(false, null, null, "文生图接口地址无效，请到「API 配置 → 文生图（中转）」检查（例如 https://your-relay.com/v1）");

        var quality = string.IsNullOrWhiteSpace(options?.Quality) ? ImageQuality : options!.Quality!.Trim();

        // ① 标准文生图接口
        if (imagesEndpoint.Length > 0)
        {
            // quality / background / output_format 都是「各家中转认不认不一定」的字段：
            // 先全带上，撞到 4xx 就逐级往下摘（只留 quality → 全部去掉），
            // 免得换了个中转站就整站出不了图。
            var level = 0;
            var maxLevel = options == null ? 1 : 2;
            while (true)
            {
                try
                {
                    var payload = new Dictionary<string, object?>
                    {
                        ["model"] = model,
                        ["prompt"] = prompt,
                        ["n"] = 1,
                        ["size"] = size
                    };
                    if (level <= 1) payload["quality"] = quality;
                    if (level == 0 && options != null)
                    {
                        if (!string.IsNullOrWhiteSpace(options.Background))
                            payload["background"] = options.Background.Trim();
                        if (!string.IsNullOrWhiteSpace(options.OutputFormat))
                            payload["output_format"] = options.OutputFormat.Trim();
                    }

                    using var resp = await PostJsonAsync(imagesEndpoint, apiKey, JsonSerializer.Serialize(payload), ct);
                    var text = await resp.Content.ReadAsStringAsync(ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var (url, b64) = ParseImageData(text);
                        var got = await MaterializeAsync(url, b64, ct);
                        if (got != null) return got;
                        errors.Add($"/images/generations 返回成功但没解析出图片：{Truncate(text)}");
                        break;
                    }

                    var code = (int)resp.StatusCode;
                    errors.Add($"/images/generations HTTP {code}(参数级别{level})：{Truncate(text)}");
                    if (level >= maxLevel || code is < 400 or >= 500) break;
                    level++;
                }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
                {
                    errors.Add($"/images/generations 请求异常：{NetErrorText.Describe(ex)}");
                    break;
                }
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
                    ["messages"] = new object[] { new { role = "user", content = prompt } },
                    // gemini-*-image / nano-banana 这类只挂在对话接口上的图像模型，
                    // 不显式声明输出模态的话它只会回一段文字，拿不到图。多余的字段中转站一般会忽略。
                    ["modalities"] = new[] { "image", "text" }
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
                errors.Add($"/chat/completions 请求异常：{NetErrorText.Describe(ex)}");
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
    /// <param name="imageLabels">与 imagePaths 一一对应的图片名（节点标题等）。走对话接口时会写成每张图前面的
    /// 标签，让「[图N]」在请求里就地绑定，而不是只在末尾说明里口头约定。</param>
    public async Task<ImageResult> GenerateWithImagesAsync(
        string prompt, IReadOnlyList<string> imagePaths,
        string? apiUrl, string apiKey, string model, string size,
        CancellationToken ct = default, IReadOnlyList<string>? imageLabels = null,
        ImageGenOptions? options = null)
    {
        var files = new List<(byte[] Data, string Ext, string Mime)>();
        var labels = new List<string>();
        var pathList = imagePaths ?? Array.Empty<string>();
        for (var pi = 0; pi < pathList.Count; pi++)
        {
            var path = pathList[pi];
            if (string.IsNullOrWhiteSpace(path)) continue;
            try
            {
                var full = ResolveLocalPath(path);
                if (full == null || !File.Exists(full)) { _logger.LogWarning("[ImageService] 参考图不存在：{Path}", path); continue; }
                var data = await File.ReadAllBytesAsync(full, ct);
                if (data.Length == 0) continue;
                var ext = DetectExt(data);
                files.Add((data, ext, MimeFor(ext)));
                labels.Add(imageLabels != null && pi < imageLabels.Count ? (imageLabels[pi] ?? "") : "");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning("[ImageService] 参考图读取失败 {Path}: {Message}", path, ex.Message);
            }
        }
        if (files.Count == 0)
            return new ImageResult(false, null, null, "没有可用的参考图（文件不存在或读取失败）");

        // 同上：参考图出图在方舟上是 /images/generations 带 image 数组，不是 multipart /images/edits。
        if (IsArkApi(apiUrl))
            return await ArkGenerateWithImagesAsync(prompt, files, apiUrl, apiKey, model, size, ct);

        var errors = new List<string>();
        var editsEndpoint = BuildEndpoint(apiUrl, "images/edits");
        var chatEndpoint = BuildEndpoint(apiUrl, "chat/completions");
        if (editsEndpoint.Length == 0 && chatEndpoint.Length == 0)
            return new ImageResult(false, null, null, "文生图接口地址无效，请到「API 配置 → 文生图（中转）」检查（例如 https://your-relay.com/v1）");

        var quality = string.IsNullOrWhiteSpace(options?.Quality) ? ImageQuality : options!.Quality!.Trim();

        // ① 主路径：图片编辑接口。多张参考图 = 重复的 image[] 字段（OpenAI 的图生图约定）。
        if (editsEndpoint.Length > 0)
        {
            // 与文生图同理：先带齐 quality / background / output_format，
            // 被中转以 4xx 拒绝时逐级往下摘（只留 quality → 全部去掉）。
            var level = 0;
            var maxLevel = options == null ? 1 : 2;
            while (true)
            {
                try
                {
                    using var form = new MultipartFormDataContent();
                    form.Add(new StringContent(model, Encoding.UTF8), "model");
                    form.Add(new StringContent(prompt, Encoding.UTF8), "prompt");
                    form.Add(new StringContent(size, Encoding.UTF8), "size");
                    if (level <= 1) form.Add(new StringContent(quality, Encoding.UTF8), "quality");
                    if (level == 0 && options != null)
                    {
                        if (!string.IsNullOrWhiteSpace(options.Background))
                            form.Add(new StringContent(options.Background.Trim(), Encoding.UTF8), "background");
                        if (!string.IsNullOrWhiteSpace(options.OutputFormat))
                            form.Add(new StringContent(options.OutputFormat.Trim(), Encoding.UTF8), "output_format");
                    }
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
                        break;
                    }

                    var code = (int)resp.StatusCode;
                    errors.Add($"/images/edits HTTP {code}(参数级别{level})：{Truncate(text)}");
                    if (level >= maxLevel || code is < 400 or >= 500) break;
                    level++;
                }
                catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
                {
                    errors.Add($"/images/edits 请求异常：{NetErrorText.Describe(ex)}");
                    break;
                }
            }
        }

        // ② 兜底：多模态对话接口（参考图转成 data URI 塞进 content 数组）
        if (chatEndpoint.Length > 0)
        {
            try
            {
                // 结构：每张图前面先挂一个「[图N] 名字」的文本标签，图全部给完后再给一次完整指令。
                // 之前是「一大段文本 + 一串没标签的图」，模型只能靠末尾一句说明去猜哪张是 [图1]，
                // 换人/换装这类主次任务经常搞反（表现为只换了衣服、人没换）。就地绑定后编号不会漂。
                var content = new List<object>();
                for (var i = 0; i < files.Count; i++)
                {
                    var name = i < labels.Count ? (labels[i] ?? "").Trim() : "";
                    var tag = "[图" + (i + 1) + "]" + (name.Length > 0 ? "：" + name : "");
                    content.Add(new { type = "text", text = tag });
                    content.Add(new { type = "image_url", image_url = new { url = ToDataUri(files[i].Data, files[i].Mime) } });
                }
                content.Add(new { type = "text", text = prompt });

                var body = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["messages"] = new object[] { new { role = "user", content = content.ToArray() } },
                    // 同上：走对话接口的图像模型需要显式声明要输出图片
                    ["modalities"] = new[] { "image", "text" }
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
                errors.Add($"/chat/completions 请求异常：{NetErrorText.Describe(ex)}");
            }
        }

        var message = errors.Count > 0 ? string.Join("；", errors) : "未拿到图片";
        _logger.LogWarning("[ImageService] 参考图出图失败 model={Model} size={Size} 参考图={Count}张: {Message}",
            model, size, files.Count, message);
        return new ImageResult(false, null, null, message);
    }

    /// <summary>
    /// 火山方舟文生图：POST /api/v3/images/generations。
    /// 先要 b64_json（省一次外网下载，也绕开 url 的 24 小时有效期），被拒时退回 url。
    /// </summary>
    private async Task<ImageResult> ArkGenerateAsync(
        string prompt, string? apiUrl, string apiKey, string model, string size, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(apiUrl, "images/generations");
        if (endpoint.Length == 0)
            return new ImageResult(false, null, null, "火山方舟接口地址无效，ApiUrl 应为 https://ark.cn-beijing.volces.com/api/v3");

        var arkSize = MapArkSize(size);
        var (arkPrompt, arkNegative) = PrepareArkPrompt(prompt);
        var errors = new List<string>();
        foreach (var fmt in new[] { "b64_json", "url" })
        {
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["prompt"] = arkPrompt,
                    ["size"] = arkSize,
                    ["response_format"] = fmt,
                    ["watermark"] = false
                };
                if (!string.IsNullOrWhiteSpace(arkNegative)) payload["negative_prompt"] = arkNegative;
                using var resp = await PostJsonAsync(endpoint, apiKey, JsonSerializer.Serialize(payload), ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var (url, b64) = ParseImageData(text);
                    var got = await MaterializeAsync(url, b64, ct);
                    if (got != null) return got;
                    errors.Add($"方舟出图成功但没解析出图片（{fmt}）：{Truncate(text)}");
                    continue;
                }

                var code = (int)resp.StatusCode;
                errors.Add($"方舟 /images/generations HTTP {code}（{fmt}）：{Truncate(text)}");
                if (code is >= 400 and < 500) continue;   // 换响应格式再试一次
                break;
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
            {
                errors.Add($"方舟 /images/generations 请求异常：{NetErrorText.Describe(ex)}");
                break;
            }
        }

        var message = errors.Count > 0 ? string.Join("；", errors) : "未拿到图片";
        _logger.LogWarning("[ImageService] 方舟出图失败 model={Model} size={Size}→{ArkSize}: {Message}", model, size, arkSize, message);
        return new ImageResult(false, null, null, message);
    }

    /// <summary>
    /// 火山方舟图生图：同一个 /api/v3/images/generations，参考图以 data URI 放进 image 数组。
    /// Seedream 4.x/5.x 与 seededit 都吃这个字段，所以模型名沿用配置里的那个即可。
    /// </summary>
    private async Task<ImageResult> ArkGenerateWithImagesAsync(
        string prompt, IReadOnlyList<(byte[] Data, string Ext, string Mime)> files,
        string? apiUrl, string apiKey, string model, string size, CancellationToken ct)
    {
        var endpoint = BuildEndpoint(apiUrl, "images/generations");
        if (endpoint.Length == 0)
            return new ImageResult(false, null, null, "火山方舟接口地址无效，ApiUrl 应为 https://ark.cn-beijing.volces.com/api/v3");

        var arkSize = MapArkSize(size);
        var (arkPrompt, arkNegative) = PrepareArkPrompt(prompt);
        var images = files.Select(f => ToDataUri(f.Data, f.Mime)).ToArray();
        var errors = new List<string>();
        foreach (var fmt in new[] { "b64_json", "url" })
        {
            try
            {
                var payload = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["prompt"] = arkPrompt,
                    ["image"] = images,
                    ["size"] = arkSize,
                    ["response_format"] = fmt,
                    ["watermark"] = false
                };
                if (!string.IsNullOrWhiteSpace(arkNegative)) payload["negative_prompt"] = arkNegative;
                using var resp = await PostJsonAsync(endpoint, apiKey, JsonSerializer.Serialize(payload), ct);
                var text = await resp.Content.ReadAsStringAsync(ct);
                if (resp.IsSuccessStatusCode)
                {
                    var (url, b64) = ParseImageData(text);
                    var got = await MaterializeAsync(url, b64, ct);
                    if (got != null) return got;
                    errors.Add($"方舟参考图出图成功但没解析出图片（{fmt}）：{Truncate(text)}");
                    continue;
                }

                var code = (int)resp.StatusCode;
                errors.Add($"方舟 /images/generations(图生图) HTTP {code}（{fmt}）：{Truncate(text)}");
                if (code is >= 400 and < 500) continue;
                break;
            }
            catch (Exception ex) when ((ex is HttpRequestException or TaskCanceledException or JsonException) && !ct.IsCancellationRequested)
            {
                errors.Add($"方舟 /images/generations(图生图) 请求异常：{NetErrorText.Describe(ex)}");
                break;
            }
        }

        var message = errors.Count > 0 ? string.Join("；", errors) : "未拿到图片";
        _logger.LogWarning("[ImageService] 方舟参考图出图失败 model={Model} size={Size}→{ArkSize} 参考图={Count}张: {Message}",
            model, size, arkSize, files.Count, message);
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
