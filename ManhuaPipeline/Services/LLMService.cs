using System.Text;
using System.Collections.Generic;
using System.Text.Json;
using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;
using Microsoft.Extensions.Logging;
using ManhuaPipeline.Services.Combat;

namespace ManhuaPipeline.Services;

public class LLMService
{
    private readonly HttpClient _http;
    private readonly ILogger<LLMService> _logger;
    public LLMService(HttpClient http, ILogger<LLMService> logger) { _http = http; _logger = logger; }

    private static readonly JsonSerializerOptions CombatJsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<string> CallAsync(string apiUrl, string apiKey, string model, string systemPrompt, string userMessage, bool jsonMode = false, double temperature = 0.7, string? thinkingMode = null)
    {
        var mode = thinkingMode?.Trim().ToLowerInvariant();

        // 千问（DashScope）与 OpenAI/DeepSeek 的「思考」不是同一套协议：
        //   千问    → 顶层布尔 enable_thinking + 数值 thinking_budget
        //   OpenAI/DeepSeek → thinking 对象（{type:enabled/disabled}）+ reasoning_effort
        // 传错字段不会报错，只会被静默忽略，表现为「改了配置但完全没生效」。
        var isQwen = (apiUrl != null && (apiUrl.Contains("dashscope", StringComparison.OrdinalIgnoreCase)
                                        || apiUrl.Contains("aliyuncs", StringComparison.OrdinalIgnoreCase)))
                     || (!string.IsNullOrEmpty(model) && model.Contains("qwen", StringComparison.OrdinalIgnoreCase));

        var body = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["messages"] = new[] {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userMessage }
            },
            ["temperature"] = temperature
        };
        // OpenAI 官方接口的 max_tokens 有模型上限（如 gpt-4o-mini 为 16384），超限会返回 400。
        // 千问更严：思考模式下 max_tokens 有效范围被限定为 [1,32768]，传 65536 必定 400
        // （qwen3.8 等系列默认就开思考，所以这是必现错误）。官方推荐改用无此上限的
        // max_completion_tokens，它限制「思维链 + 回复」的合计长度。
        if (isQwen)
            body["max_completion_tokens"] = 65536;
        else
            body["max_tokens"] = apiUrl != null && apiUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ? 16384 : 65536;

        if (jsonMode)
            body["response_format"] = new { type = "json_object" };

        if (isQwen)
        {
            // "低/高推理"用 thinking_budget（有效范围 1~32768）实现；官方明确 reasoning_effort
            // 与 thinking_budget 互斥、且各模型可选档位不一致，故统一用数值预算最稳妥。
            // 未配置（null）时也显式给 8192 上限：qwen3.8 系列默认开启思考且思维链上限高达 262K，
            // 不设预算等于让模型跑满推理，Stage 4 这类长任务会被 HttpClient 超时掐断。
            body["enable_thinking"] = mode != "disabled";
            if (mode != "disabled")
                body["thinking_budget"] = mode == "low" ? 1024 : 8192;
        }
        else if (mode == "disabled")
            body["thinking"] = new { type = "disabled" };
        else if (mode is "low" or "high")
        {
            body["thinking"] = new { type = "enabled" };
            body["reasoning_effort"] = mode;
        }
        // —— 调用（带瞬时故障自动重试）——
        // 批量生成时（如 H3 提示词逐镜头生成）单次调用失败就会被上层算作「该镜头失败」并丢弃其内容，
        // 而 429（限流）/5xx/网络抖动/连接中断这些瞬时故障重试一次大多就能成功，必须在这里兜住，
        // 否则表现为「批量跑完少了几条、还查不出原因」。
        // 400/401/403 等是参数或权限问题，重试无用，直接抛出，避免把真实错误淹没成「重试后仍失败」。
        const int maxAttempts = 3;
        string? lastTransientReason = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, apiUrl);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                req.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
                using var resp = await _http.SendAsync(req);
                if (!resp.IsSuccessStatusCode)
                {
                    // 保留响应体：403/401 等错误的真正原因（令牌被禁用、IP 未在白名单、模型无权限等）都在 body 里，
                    // 用 EnsureSuccessStatusCode 会把 body 丢掉，导致只看到 "403 (Forbidden)" 无法定位。
                    var errBody = "";
                    try { errBody = await resp.Content.ReadAsStringAsync(); } catch { /* ignore */ }
                    var retryable = IsRetryableStatus(resp.StatusCode);
                    _logger.LogError("[LLM] HTTP {Code} {Status}; model={Model}; url={Url}; 可重试={Retryable}; 第{Attempt}/{Max}次; body={Body}",
                        (int)resp.StatusCode, resp.StatusCode, model, apiUrl, retryable, attempt, maxAttempts, errBody);
                    if (retryable && attempt < maxAttempts)
                    {
                        lastTransientReason = $"HTTP {(int)resp.StatusCode} {resp.StatusCode}";
                        var wait = RetryDelay(attempt, (int)(resp.Headers.RetryAfter?.Delta?.TotalSeconds ?? 0));
                        _logger.LogWarning("[LLM] HTTP {Code} 属瞬时故障，{Delay} 秒后重试（第 {Next}/{Max} 次）",
                            (int)resp.StatusCode, wait.TotalSeconds, attempt + 1, maxAttempts);
                        await Task.Delay(wait);
                        continue;
                    }
                    throw new HttpRequestException(
                        $"LLM 请求失败 {(int)resp.StatusCode} {resp.StatusCode}（model={model}, url={apiUrl}" +
                        (attempt > 1 ? $", 已重试 {attempt - 1} 次" : "") +
                        $"）；响应体：{(errBody.Length > 800 ? errBody.Substring(0, 800) : errBody)}");
                }
                var json = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
                if (attempt > 1)
                    _logger.LogInformation("[LLM] 第 {Attempt} 次调用成功（前 {Prev} 次为瞬时故障：{Reason}）", attempt, attempt - 1, lastTransientReason);
                return content;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsTransientFailure(ex))
            {
                lastTransientReason = ex.GetBaseException().Message;
                var wait = RetryDelay(attempt);
                _logger.LogWarning(ex, "[LLM] 第 {Attempt}/{Max} 次调用异常（{Type}），{Delay} 秒后重试；model={Model}",
                    attempt, maxAttempts, ex.GetType().Name, wait.TotalSeconds, model);
                await Task.Delay(wait);
            }
        }
        // 循环内要么 return 要么 throw，正常不可达；兜底防止编译器认为缺少返回。
        throw new HttpRequestException($"LLM 请求失败（已重试 {maxAttempts} 次）：{lastTransientReason ?? "未知原因"}");
    }

    /// <summary>HTTP 状态码是否属于「重试可能成功」的瞬时故障：限流 429、请求超时 408、冲突 409、以及 5xx。</summary>
    private static bool IsRetryableStatus(System.Net.HttpStatusCode code)
        => (int)code == 408 || (int)code == 409 || (int)code == 429 || (int)code >= 500;

    /// <summary>异常是否属于瞬时故障（网络中断、连接重置、超时、返回体不是合法 JSON 等），可安全重试。
    /// 本方法刻意不把「自己抛出的 HttpRequestException（无 InnerException）」算作瞬时故障，
    /// 否则 403/400 这类确定性错误也会被重试 3 次。</summary>
    private static bool IsTransientFailure(Exception ex) => ex switch
    {
        TaskCanceledException => true,                              // HttpClient 超时（本服务配置 30 分钟）或连接被掐断
        TimeoutException => true,
        System.Net.Sockets.SocketException => true,
        System.IO.IOException => true,                              // 连接被重置、传输中断
        JsonException => true,                                      // 200 但返回体不是合法 JSON（网关错误页等）
        KeyNotFoundException => true,                               // 200 但缺少 choices/message/content 字段
        HttpRequestException hre => hre.InnerException is System.IO.IOException
            or System.Net.Sockets.SocketException or TaskCanceledException,
        _ => false
    };

    /// <summary>重试等待时长：默认 5 秒、15 秒递增；服务端给了 Retry-After（≤120 秒）时以服务端为准。</summary>
    private static TimeSpan RetryDelay(int attempt, int retryAfterSeconds = 0)
    {
        Span<int> backoff = stackalloc int[] { 5, 15 };
        var delay = TimeSpan.FromSeconds(backoff[Math.Min(attempt - 1, backoff.Length - 1)]);
        if (retryAfterSeconds > 0)
        {
            var server = TimeSpan.FromSeconds(Math.Min(retryAfterSeconds, 120));
            if (server > delay) return server;
        }
        return delay;
    }

    // ========== 1. Creative Conception ==========
    public Task<string> AnalyzeScript(string script, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个创意构思专家。根据用户提供的剧本，分析其创意构思，严格按照以下完整结构输出（用中文，自然段落，不要JSON）。必须完整输出所有章节，不得省略任何部分，尤其是最后的 Seedance 2.0 提示词部分必须存在：\n\n### 故事主题\n- 分析剧本的核心情感主题，用一段话描述故事传达的情感回响。\n\n### 风格建议\n- 给出视觉风格建议：线条风格、色彩倾向、画面质感、背景处理方式等。\n\n### 核心创意卖点\n- 提炼2-3个让这个剧本脱颖而出的创意亮点，比如独特的视觉语言、情绪递进方式、对白设计等。\n\n### 目标受众\n- 分析适合观看该作品的受众群体特征。\n\n### 谁（矛盾的职业，矛盾的身份，奇葩的系统，反差的性格，秘密？）\n### 目标是什么：（反常的目标，猎奇的目标）\n### 动机是什么：（让反常的目标合理化，获得高价值，或者避免失去高价值）\n### 阻碍和代价是什么：（剧情的核心，走向目标的过程遇到的重重困难，可递进叠加，有压力有代价）\n### 高潮是什么：（人物会做出怎样的选择，两难的选择更容易让观众看下去）\n### 结局是什么：（主人公最后的命运如何，价值观改变了没有）\n\n---\n\n### Seedance 2.0 提示词（写实风格）\n\n场景设定\n\n人物动作与表情\n\n镜头语言\n\n光线与特效\n\n情绪说明\n\n以上每个部分根据剧本内容填充具体细节，输出完整的创意构思分析报告。重要提示：必须完整包含以上所有部分，不得省略 \"### Seedance 2.0 提示词（写实风格）\" 及其后续内容！",
            script, thinkingMode: thinkingMode);

    // ========== 2. Story Analysis ==========
    public Task<string> AnalyzeStoryStructure(string scriptAnalysis, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个故事结构分析师。基于剧本分析结果，分析故事结构：\n- 主要角色关系\n- 故事背景设定\n- 起承转合结构\n- 冲突设置与升级\n- 核心主题\n用中文，结构化输出。",
            scriptAnalysis, thinkingMode: thinkingMode);

    // ========== 3. Global Blueprint ==========
    public Task<string> GenerateBlueprint(string storyAnalysis, int episodeCount, int existingCount, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null)
        => CallAsync(apiUrl, apiKey, model,
            existingCount > 0
            ? ("你是一个漫剧编剧。基于故事分析，将整个剧本内容分配到 " + episodeCount + " 集中，生成总集蓝图。\n重要：必须严格生成恰好 " + episodeCount + " 集，把全部剧情均衡分配到每集，不能遗漏任何剧情！\n注意：已有第1-" + existingCount + "集，请保持这些集的标题和概要不变，仅补充新增的第" + (existingCount + 1) + "-" + episodeCount + "集。\n每集严格按以下格式输出：\n## 第1集: 标题\n概要：一句话概要\n禁止在标题后使用“| 概要”字样。")
            : ("你是一个漫剧编剧。基于故事分析，将整个剧本内容分配到 " + episodeCount + " 集中，生成总集蓝图。\n重要：必须严格生成恰好 " + episodeCount + " 集，把全部剧情均衡分配到每集，不能遗漏任何剧情！\n每集严格按以下格式输出：\n## 第1集: 标题\n概要：一句话概要\n禁止在标题后使用“| 概要”字样。"),
            storyAnalysis, thinkingMode: thinkingMode);

    // ========== 3b. Story Foundation（L1：阶段 1/2/3 合并为一次调用） ==========
    /// <summary>
    /// L1 故事基线：一次调用产出「故事核心 / 主线因果链 / 观众必须看懂的信息 / 情绪曲线 / 视觉锚点 / 分集大纲」。
    /// 结果由 <see cref="StoryFoundationRenderer"/> 渲染后分别写入阶段 1、2、3，
    /// 替代原先「创意构思 → 基于其输出再分析故事结构 → 再基于其输出生成蓝图」的三次串联调用（每次都在转述上一段产物，信息逐级衰减）。
    /// 解析失败返回 null，调用方回退到逐阶段单调用路径。
    /// </summary>
    public async Task<StoryFoundation?> GenerateStoryFoundation(string script, int episodeCount, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        if (string.IsNullOrWhiteSpace(script)) return null;
        if (episodeCount <= 0) episodeCount = 1;

        var systemPrompt = "你是短剧故事架构师。只依据用户提供的剧本原文提炼故事基线，禁止添加剧本中不存在的人物、事件或设定。\n" +
            "只输出一个 JSON 对象（不要 Markdown 代码块、不要任何解释文字），字段与含义如下：\n" +
            "{\n" +
            "  \"storyCore\": \"一句话故事核心（30-60字，讲清 谁 + 处境 + 核心冲突）\",\n" +
            "  \"mainChain\": [\"因：… → 果：…\"],\n" +
            "  \"audienceMustKnow\": [\"…\"],\n" +
            "  \"emotionCurve\": [{\"position\":\"第X集 前/中/后段\",\"emotion\":\"情绪标签\",\"beat\":\"承载该情绪的剧情节拍\"}],\n" +
            "  \"visualAnchors\": [\"…\"],\n" +
            "  \"episodes\": [{\"episodeNumber\":1,\"title\":\"集标题\",\"summary\":\"一句话概要\"}]\n" +
            "}\n" +
            "字段规则：\n" +
            "1. mainChain：主线因果链，6-12 条，按剧情顺序，每条必须是「前一件事直接导致后一件事」，写成「因：… → 果：…」。\n" +
            "2. audienceMustKnow：观众必须看懂的信息 5-10 条，即漏掉就会看不懂剧情的关键前提、规则、身份或伏笔。\n" +
            "3. emotionCurve：情绪曲线 6-14 个点，跨全集覆盖，position 用「第X集 前段/中段/后段」。\n" +
            "4. visualAnchors：视觉锚点 5-10 条，指反复出现且必须保持一致的标志性画面、道具、造型或光影特征。\n" +
            "5. episodes：必须恰好 " + episodeCount + " 集，episodeNumber 从 1 连续递增，覆盖全部剧情，禁止合并、遗漏或超出集数。\n" +
            "6. 所有内容必须来自剧本原文，禁止意译发挥，禁止改写原文台词、技能名与专有名词。\n" +
            "7. 数组字段没有内容时输出空数组 []，禁止输出 null。";

        var raw = await CallAsync(apiUrl, apiKey, model, systemPrompt, script, jsonMode: true, temperature: 0.1, thinkingMode: thinkingMode);
        return StoryFoundationRenderer.Parse(raw);
    }

    // ========== 3c. Keyframes（L3：每剧情节点一张关键帧） ==========
    /// <summary>
    /// L3 关键帧层：把系统挑出的候选剧情节点写成关键帧方案（构图 + 锁定项 + 图像提示词）。
    /// 节点挑选是系统确定性行为（见 <see cref="KeyframePlanService"/>），模型只负责把节点写具体。
    /// 解析失败返回 null，调用方降级为按分镜拼装（Source=fallback）。
    /// </summary>
    public async Task<KeyframePlanPayload?> GenerateKeyframes(
        string candidateText,
        string? storyContext,
        string? continuityText,
        string? assetCatalog,
        string apiUrl, string apiKey, string model,
        string? thinkingMode = null)
    {
        var systemPrompt =
            "你是短剧关键帧设计专家。为给定的每个剧情节点各设计一张关键帧，作用是锁定后续镜头不得漂移的要素。\n" +
            "只输出一个 JSON 对象（不要 Markdown 代码块、不要解释文字）：\n" +
            "{\"keyframes\":[{\"nodeLabel\":\"节点标签\",\"nodeReason\":\"为什么这是不可省的节点\",\"shotLabel\":\"镜头编号\",\"unitNumber\":\"单元编号\"," +
            "\"composition\":\"构图：机位+景别+主体在画面中的位置\",\"lockedCharacters\":\"锁定角色站位与朝向（谁在画面左/右、面向哪、与谁对视）\"," +
            "\"lockedProps\":\"锁定道具状态（在哪只手、什么状态/磨损程度、位于何处；无道具写「无」）\"," +
            "\"lockedSceneDirection\":\"锁定场景空间与朝向（入口出口、光源方向、动作轴线）\"," +
            "\"clueVisible\":\"线索可见性：观众必须看到什么/必须看不到什么（无则写「无」）\"," +
            "\"nextConnection\":\"与下一张关键帧的衔接（谁/什么状态延续过去，切在什么上）\"," +
            "\"imagePrompt\":\"可直接文生图的关键帧提示词\"}]}\n" +
            "规则：\n" +
            "1. keyframes 必须与【候选剧情节点】一一对应、顺序一致、数量完全相同，禁止增删节点。\n" +
            "2. 所有内容只能依据【候选剧情节点】原文，禁止新增人物、道具、场景、情节与台词。\n" +
            "3. composition 必须写清机位与景别（如「中景，低角度，主角居画面右侧三分之一」），禁止写「画面唯美」这类空词。\n" +
            "4. lockedCharacters 只写画面方位与朝向/视线关系，禁止写心理活动。\n" +
            "5. lockedProps 只写道具的可验证状态（位置/持握/完好或破损），无道具写「无」。\n" +
            "6. clueVisible 必须区分「必须看到」与「必须看不到」，用于防止线索提前暴露或遗漏。\n" +
            "7. imagePrompt 100 字以内，包含：景别与机位、主体与站位、场景与光色、材质与瑕疵锚点（如湿混凝土、磨损金属、皮肤细纹、地面积水）、以及一句「无字幕无文字，画面不出现可读文字（屏幕/纸面一律留白）」。\n" +
            "8. 所有人物、场景、道具的命名必须与【资产规范名参考】一致（没有该段时用原文名）。\n" +
            "9. 数组字段没有内容时输出空数组，禁止输出 null。";

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(storyContext))
        {
            sb.AppendLine("【故事与分集背景】");
            sb.AppendLine(storyContext);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(continuityText))
        {
            sb.AppendLine("【连续性表（站位/道具/线索的唯一依据）】");
            sb.AppendLine(continuityText);
            sb.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(assetCatalog))
        {
            sb.AppendLine("【资产规范名参考】");
            sb.AppendLine(assetCatalog);
            sb.AppendLine();
        }
        sb.AppendLine("【候选剧情节点】");
        sb.AppendLine(candidateText);
        sb.AppendLine();
        sb.AppendLine("请为上述每个节点各输出一张关键帧，按 JSON 结构返回。");

        try
        {
            var raw = await CallAsync(apiUrl, apiKey, model, systemPrompt, sb.ToString(), jsonMode: true, temperature: 0.2, thinkingMode: thinkingMode);
            return ParseKeyframes(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[关键帧层] 生成调用失败");
            return null;
        }
    }

    private static KeyframePlanPayload? ParseKeyframes(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var json = ExtractJsonObject(raw);
        if (json == null) return null;
        try
        {
            var payload = JsonSerializer.Deserialize<KeyframePlanPayload>(json, CombatJsonOptions);
            return payload?.Keyframes == null || payload.Keyframes.Count == 0 ? null : payload;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从模型返回里抠出最外层 JSON 对象（容忍 ```json 包裹与前后解释文字）。</summary>
    private static string? ExtractJsonObject(string raw)
    {
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        var json = raw.Substring(start, end - start + 1);
        // 常见脏数据：模型在 JSON 里写了尾随逗号，直接反序列化会抛
        return System.Text.RegularExpressions.Regex.Replace(json, @",\s*([\]}])", "$1");
    }

    // ========== 4. Scene Split ==========
    public Task<string> SplitIntoUnits(string script, int episodeCount, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null, string? skillList = null, string? assetCatalog = null, int? totalDurationMinSeconds = null, int? totalDurationMaxSeconds = null, string? totalDurationDisplay = null, string? continuityText = null)
    {
        // L2 连续性层：把六类连续性表作为硬约束注入，让「地点 / 起始状态 / 结束状态」有据可依，
        // 而不是靠模型自行记忆（对应 short-drama-agent 第 6-11 节）。
        var continuitySection = string.IsNullOrWhiteSpace(continuityText)
            ? ""
            : "\n\n【连续性硬约束】以下为本片拍摄必须锁定的连续性信息，拆分单元时必须逐条遵守：\n" + continuityText + "\n规则：\n" +
              "1. 「地点」必须使用场景空间表中的环境规范名；同一场景内相邻单元的空间方位必须与表中一致，禁止方位漂移、禁止凭空改变镜头朝向。\n" +
              "2. 每个单元的「起始状态」必须接住上一单元的「结束状态」；角色伤势、服装、随身道具的状态必须与角色连续性表、道具状态表一致，禁止状态倒退（如道具已落地又回到手中、伤势自愈）。\n" +
              "3. 线索必须按线索揭示表的顺序与位置揭示，禁止提前揭示，禁止漏掉表中「观众必须注意到」的内容。\n" +
              "4. 每个单元的「核心动作/情绪」必须承载动作因果表中该单元的「新信息」，禁止出现只有动作、没有信息推进的空转单元。\n" +
              "5. 相邻单元的衔接必须符合转场动机表中标注的动机类型。\n";
        return CallAsync(apiUrl, apiKey, model,
            "你是一个视频场景拆分专家。将剧本拆分为适合 Seedance 2.0 生成的短视频单元（每个最长15秒）。\n\n总集数：" + episodeCount + "集\n请严格按照" + episodeCount + "集来拆分内容，每集包含多个15秒视频单元。\n\n拆分规则：\n1. 每个单元最长 15 秒\n2. 每个单元只处理一个主要地点\n3. 每个单元只承载一个核心动作或一次情绪变化\n4. 每个单元必须有起始状态和结束状态\n5. 结束状态要能被下一条接住\n6. 每个单元必须自动判断场景类型并标注，类型只准从以下 6 个标准类型词中选一个：打斗/动作、文戏/情感、追逐/逃亡、悬疑/惊悚、日常/喜剧、高潮/对决；禁止拼接、缩写、简写或添加括号备注（如「反复对峙/打斗」「斗法对轰」「对决高潮」「法相对决（打斗）」「围攻决裂」「悬疑」「文戏」「追逐」等写法一律禁止）——打斗对轰一律写「打斗/动作」，对决收尾一律写「高潮/对决」，追逃一律写「追逐/逃亡」，阴谋悬念一律写「悬疑/惊悚」，日常戏一律写「日常/喜剧」\n7. 每个单元的时长只能从 5秒、11秒、15秒 三档中选择（视频生成API只支持这三档）：打斗/动作、追逐/逃亡、高潮/对决默认11秒，但5秒同样允许多个快速攻防回合，时长按内容密度决定；重大打斗/终极对决可用15秒；文戏/情感、日常/喜剧、悬疑/惊悚一律默认5秒，只有该单元台词（含旁白）合计确实超过20字时才允许用11秒，禁止用15秒；时长宁短勿长，内容不足以填满当前档位就拆成多个短单元，禁止注水\n8. 每个单元涉及的所有台词必须逐字保留剧本原文，禁止改写、删减或新增；同一段对话尽量留在同一个单元内，不要拆散\n9. 每个单元的「关键元素」必须写出本单元实际出场对象（人物/道具/环境/特效）的规范名，用「、」分隔；人物一律写资产清单中的角色规范名，正文用「她/他/他们」等代词指代时也必须补出所指角色的规范名；禁止照抄输出格式里的说明文字（如「人物/道具/环境」）、禁止留空，禁止用「主角」「众人」「人物」等泛称代替规范名；仅当该单元确实没有任何出场对象（如纯字幕后空镜）时才允许写「无」\n\n输出格式（每集一组）：\n【第X集】\n【单元X.Y】\n类型：\n时长：X秒\n地点：\n核心动作/情绪：\n起始状态：\n结束状态：\n对话/台词：（逐字引用剧本原句，多个角色按说话顺序用“角色名：台词”分行列出；该单元无台词写「无」）\n关键元素：（逐项写出本单元实际出场的人物、道具、环境、特效的规范名，用「、」分隔；人物必须写角色规范名——正文只用「她/他/他们」指代时也要写出所指角色；必须是具体名称，禁止照抄本行说明文字、禁止留空、禁止只写「无」）\n\n用中文。\n\n【台词铁律】对话/台词字段必须逐字复制剧本原文，一字不改、一个标点都不能改；禁止意译、概括、改写或自行补充台词。\n\n【字幕与画面文字铁律】\n1. 字幕不是台词、不是旁白：剧本中的片头/时间地点字幕或画面文字（常写作「字幕：…」「白字字幕」「字幕淡入/浮现」等）一律禁止写入「对话/台词」字段，该字段写「无」。\n2. 禁止把「出现字幕/字幕淡入」当作单元的核心动作或情绪变化，禁止为纯字幕内容单独建立视频单元；字幕内容依附的真实画面动作与情绪应并入承载该内容的单元。\n3. 画面文字承载的时间/地点信息只能转化为画面元素融入「地点」「关键元素」或环境描述（如 民国二十六年初夏的江南省立中学图书馆），禁止在「对话/台词」中出现可被朗读/配音的字幕文本。",
            script + "\n\n【稳定性铁律】同一份输入剧本在内容不变时必须输出完全一致的拆分结果：集数、单元编号、类型、时长、地点、台词和关键元素都不得因重跑而改变；单元编号严格使用【单元X.Y】格式，禁止写成【单元X.Y-Z】或【单元X.Y.Z】等带后缀的形式；剧本场次标题只是拆分依据，不能限制输出单元数量；禁止改写、合并、删减或重新排序原文台词；只输出拆分结果，不要输出任何额外说明。\n\n【拆分铁律】\n1. 剧本里的「### 单元X.Y」只是场次标题，不是最终视频单元；每个场次必须按内容量继续拆成 3-5 个短视频单元，禁止把整场戏压成 1 个单元。\n2. 打斗/高潮/对决场次必须按「起手/试探→攻防回合→僵持或转折→收招/结果」至少拆成 4 个单元；文戏按对话回合和情绪节点拆成 2-4 个单元。\n3. 一个单元只能承载一个核心动作、一轮完整对话或一次情绪变化；内容超过 11 秒容量（多回合打斗、多段对白、多阶段推进）时禁止压缩，必须拆开。\n4. 输出单元编号从 1.1 开始按顺序重新编号，禁止沿用剧本场次编号，禁止合并不同场次。\n\n【无损拆分铁律】\n1. 分集细化是「拆」，不是「压缩」：剧本原文的每一段动作、每一个技能名、每一句台词、每一个状态变化都必须有单元承载，禁止用「缠斗」「对轰」「激战」「压制」等概括词替代具体内容。\n2. 剧本点名的每个技能（含法相/阵法/合击/领域/大招，如碎金劲、擒龙手、霜刃千葬、凝月冰牢、碎金撼岳、法天象地等）必须原样出现在某个单元的「核心动作/情绪」或「关键元素」中，一个都不能丢；禁止改名、简写或替换。\n3. 打斗/高潮场次按「技能出招单元」拆：每个技能的一轮完整攻防（出招→对手接招/中招→结果）独立成一个单元；同一技能多回合拆多个单元；禁止把多个技能压进一个单元。\n4. 台词超过 3 句或对话超过 2 轮的场次按对话回合拆成多个单元，禁止把整段对话塞进一个单元；台词的每一句都必须逐字出现在某个单元的「对话/台词」中。\n5. 输出前自查：剧本每个关键节点（技能、台词、转折、状态变化）都能在某个单元找到对应，找不到就是漏了，必须补单元，禁止为了凑场次数量跳过内容。\n\n【技能清单参考】以下为本项目技能库中的技能名，仅作识别参考：\n" + (string.IsNullOrWhiteSpace(skillList) ? "（无）" : skillList) + "\n规则：只有剧本正文点名出现的技能才必须拆入单元并原样保留技能名；清单中剧本未出现的技能禁止强行加入单元；剧本点名技能与清单写法不一致时以剧本原文为准。\n\n【资产规范名参考】本项目中主要人物、环境、道具与特效已有资产卡，规范名如下（仅作识别与命名参考，不是强制名单）：\n" + (string.IsNullOrWhiteSpace(assetCatalog) ? "（无）" : assetCatalog) + "\n规则：单元「地点」只要实际处于清单内环境，就必须沿用该环境规范名作为「地点」；「关键元素」中的主要人物/道具/特效只要实际出现在单元里、且能对应到清单对象，就必须沿用清单中的规范名，禁止同义改写、缩写或自行换名；剧本原文名称与清单名称不一致时，以剧本原文为主、允许在原文后括号补上清单规范名；清单未覆盖的真实对象照常描述。「关键元素」必须逐单元填写并写具体规范名：本单元有角色出场（含正文只用「她/他/他们」指代的情况）时，必须写出该角色的资产规范名，禁止照抄格式说明文字或写成「主角」「众人」「人物」等泛称。" + continuitySection + (totalDurationMaxSeconds.HasValue && totalDurationMaxSeconds.Value > 0 ? "\n\n【全片目标时长区间】\n剧本头部标注建议时长" + (string.IsNullOrWhiteSpace(totalDurationDisplay) ? "" : "：" + totalDurationDisplay) + "，该时长为全片（所有集、所有单元合计）的目标时长区间：下限 " + (totalDurationMinSeconds ?? 0) + " 秒、上限 " + totalDurationMaxSeconds.Value + " 秒。\n1. 所有单元时长之和必须落在该区间内：禁止超过上限 " + totalDurationMaxSeconds.Value + " 秒，同时应达到或尽量接近下限 " + (totalDurationMinSeconds ?? 0) + " 秒，禁止明显偏短。\n2. 若按默认规则（文戏一律低档、宁短勿长）会让总时长明显低于下限，说明有单元档位被低估：凡是台词合计超过 20 字的长对白/文戏单元、多回合或多技能连打的打斗单元、情绪推进明显的单元，必须按内容量升到 11 秒或 15 秒档，或把此前拆得过粗的单元按内容放细成多个单元；内容足以填满高档时升档不算注水，但禁止凭空编造台词/技能/剧情来凑时长。\n3. 若按前述拆分规则会超出上限：优先合并相邻同地点、动作/情绪连续、类型相近的短单元（合并后时长按内容取 11 或 15 秒档），或把不足 5 秒容量的过渡单元吸收进相邻单元；仅在合并后仍超且确属重复时才允许把重复性动作精简为一句话承载。\n4. 合并/升档/放细时剧本台词必须逐字保留，技能名、关键道具、起始/结束状态与转折点一个都不能丢；「无损拆分铁律」在区间约束内仍然有效，与区间冲突时以不超上限为优先。\n5. 单元格式、类型与时长档位（5/11/15 秒）规则不变；输出只含拆分结果，禁止输出额外说明。": ""),
            temperature: 0.1, thinkingMode: thinkingMode);
    }

    // ========== 4b. Split Coverage Repair ==========
    public Task<string> RepairSplitCoverage(string script, string splitResult, string? skillList, string apiUrl, string apiKey, string model, string? thinkingMode = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个剧本拆分完整性校验员。任务：比对「剧本原文」与「拆分结果」，找出剧本中有但拆分结果中没有承载的内容（打斗动作阶段、技能名、关键道具、台词、情绪转折、剧情节点等），输出修复后的完整拆分结果。\n\n校验规则：\n1. 逐段核对剧本每个内容点：每一段动作、每一个技能名、每一句台词、每一个状态变化，都必须能在拆分结果的某个单元中找到对应；禁止用「缠斗」「对轰」「激战」等概括词算作已承载。\n2. 剧本点名的每个技能（含法相/阵法/合击/领域/大招）必须原样出现在某个单元的「核心动作/情绪」或「关键元素」中。\n3. 若发现遗漏：保留原有单元全部内容不变，在正确位置补充新单元承载遗漏内容，全部单元按剧本顺序重新编号为【单元X.Y】。\n4. 若没有遗漏：逐字原样输出输入的拆分结果，不要做任何改动。\n5. 补充单元的格式与拆分结果完全一致：类型只准从打斗/动作、文戏/情感、追逐/逃亡、悬疑/惊悚、日常/喜剧、高潮/对决中选一个；时长只准5秒、11秒、15秒；台词必须逐字引用剧本原文；关键元素必须写出该单元实际出场对象的规范名（人物必须写角色规范名，含正文只用「她/他/他们」指代的情况），禁止写「人物/道具/环境」等说明文字或「主角」「众人」等泛称。\n6. 只输出拆分结果本身，禁止输出校验说明、比对报告、遗漏清单或任何额外文字。",
            script + "\n\n===== 拆分结果 =====\n" + splitResult + (string.IsNullOrWhiteSpace(skillList) ? "" : "\n\n【技能清单参考】" + skillList + "\n剧本点名技能必须原样保留；清单中剧本未出现的技能禁止强行加入。"),
            temperature: 0.1, thinkingMode: thinkingMode);

    // ========== 4c. Split Duration Budget Repair ==========
    public Task<string> RepairSplitBudget(string script, string splitResult, int totalMaxSeconds, string totalDurationDisplay, string? skillList, string apiUrl, string apiKey, string model, string? thinkingMode = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个分集细化时长预算修复器。任务：把给定拆分结果压缩到全片时长预算内，同时保持剧本内容完整。\n\n预算：全片（所有集、所有单元合计）建议时长 " + totalDurationDisplay + "；硬上限 " + totalMaxSeconds + " 秒。当前拆分总时长 " + StageUnitParser.Parse(splitResult).Sum(u => u.Duration) + " 秒，超过上限，必须压到 ≤ " + totalMaxSeconds + " 秒。\n\n修复规则：\n1. 手段优先级：a) 合并相邻（同集、剧情连续、地点相同或前后承接）的短单元，新单元类型取主导类型，时长档按承载内容取 11 或 15 秒；b) 把不足 5 秒容量的过渡/氛围单元吸收进相邻单元；c) 仍然超时且确属同一动作重复时，才允许把重复性动作浓缩为一句概述承载。\n2. 任何合并/浓缩都禁止删减：台词逐字保留在对应单元「对话/台词」；技能名、关键道具、起始/结束状态、地点、剧情转折点一个都不能丢；「关键元素」必须保留具体规范名，禁止清空或改写成「人物/道具/环境」「主角」「众人」等泛称；禁止用「缠斗」「激战」等空词替代具体内容。\n3. 单元格式严格保持：【单元X.Y】+ 类型/时长/地点/核心动作/情绪/起始状态/结束状态/对话/台词/关键元素；时长只能 5/11/15 秒；单元编号按剧本顺序全片连续重排，禁止沿用被合并掉的旧编号。\n4. 输出只含修复后的拆分结果，禁止输出修复说明、比对报告或遗漏清单。\n5. 若给定拆分总时长已经 ≤ " + totalMaxSeconds + " 秒，则逐字原样输出。",
            script + "\n\n===== 当前拆分结果 =====\n" + splitResult + (string.IsNullOrWhiteSpace(skillList) ? "" : "\n\n【技能清单参考】" + skillList + "\n剧本点名技能必须原样保留；清单中剧本未出现的技能禁止强行加入。"),
            temperature: 0.1, thinkingMode: thinkingMode);

    // ========== 4d. Split Duration Floor Repair ==========
    public Task<string> RepairSplitFloor(string script, string splitResult, int targetMinSeconds, string totalDurationDisplay, string? skillList, string apiUrl, string apiKey, string model, string? thinkingMode = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个分集细化时长下限修复器。任务：把给定拆分结果的全片总时长提升到目标下限以上，同时保持剧本内容无损、禁止注水。\n\n目标：全片（所有集、所有单元合计）建议时长 " + totalDurationDisplay + "；总时长不得低于 " + targetMinSeconds + " 秒。当前拆分总时长 " + StageUnitParser.Parse(splitResult).Sum(u => u.Duration) + " 秒，低于下限，必须提升到 ≥ " + targetMinSeconds + " 秒。\n\n修复规则：\n1. 优先升档：凡是台词合计确实超过 20 字的长对白/文戏/日常单元、多回合或多技能连打的打斗单元、情绪或阶段推进明显的单元，把时长档位从 5 秒升到 11 秒；内容特别充分（台词超 40 字、多轮攻防加收招）的可升到 15 秒。档位必须与内容密度匹配，禁止无内容空升档。\n2. 其次放细：把此前拆得过粗的单元（一场大戏只压成 1 个单元、多技能多回合被压进一个单元）按剧本原文重新拆成多个连续单元，全部单元按剧本顺序重排编号为【单元X.Y】。\n3. 禁止删减与编造：台词逐字保留在对应单元「对话/台词」，技能名、关键道具、起始/结束状态、地点、剧情转折点一个都不能丢；「关键元素」必须保留具体规范名，禁止清空或改写成泛称；禁止凭空新增剧本中不存在的台词、技能或剧情来凑时长。\n4. 若合理升档/放细后仍达不到下限，尽量贴近下限输出，宁可略低于下限也不得编造内容；若当前总时长已 ≥ " + targetMinSeconds + " 秒，则逐字原样输出。\n5. 单元格式严格保持：【单元X.Y】+ 类型/时长/地点/核心动作/情绪/起始状态/结束状态/对话/台词/关键元素；时长只能 5/11/15 秒；类型只能从 6 个标准词中选。\n6. 输出只含修复后的拆分结果，禁止输出修复说明、比对报告或遗漏清单。",
            script + "\n\n===== 当前拆分结果 =====\n" + splitResult + (string.IsNullOrWhiteSpace(skillList) ? "" : "\n\n【技能清单参考】" + skillList + "\n剧本点名技能必须原样保留；清单中剧本未出现的技能禁止强行加入。"),
            temperature: 0.1, thinkingMode: thinkingMode);

    // ========== 4e. Continuity tables（L2 连续性层，对标 short-drama-agent 第 6-11 节） ==========

    /// <summary>
    /// 一次调用产出六类连续性表（场景空间 / 角色连续性 / 道具状态 / 线索揭示 / 动作因果 / 转场动机）。
    /// 结果由 ContinuityExtractionService 拆分成 ProjectContinuityTables 落库，供 Stage 4 注入。
    /// </summary>
    public async Task<ContinuityExtractionResult?> ExtractContinuity(
        string script, string? storyAnalysis, string? blueprint, string? assetCatalog,
        string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var systemPrompt =
            "你是一位影视剧「连续性监督」。这是一个「提取任务」，不是创作任务：只从给定的剧本与故事分析中提取拍摄必须锁定的连续性信息，禁止虚构剧本中不存在的人物、道具、地点、情节或台词。\n" +
            "只输出一个 JSON 对象，禁止 Markdown 代码块围栏，禁止解释，禁止输出任何额外文字。\n\n" +
            "JSON 结构（六个字段全部必填，无内容时给空数组）：\n" +
            "{\n" +
            "  \"sceneSpace\": [{\"scene\":\"环境规范名\",\"left\":\"画面左侧具体是什么\",\"right\":\"画面右侧具体是什么\",\"midBack\":\"中后方是什么\",\"foreground\":\"前景是什么\",\"entrance\":\"入口\",\"exit\":\"出口\",\"dangerDirection\":\"危险来源方向\",\"escapeDirection\":\"逃生方向\",\"actionAxis\":\"主要对峙双方的站位关系\",\"fixedProps\":[\"固定道具及其位置\"],\"forbiddenChanges\":[\"禁止改变的空间关系\"]}],\n" +
            "  \"characterContinuity\": [{\"character\":\"角色规范名\",\"appearanceAnchor\":\"跨镜必须一致的外貌特征（发色发型/瞳色/体型/气质）\",\"costume\":\"本片服装\",\"weaponOrProp\":\"随身武器或专属道具\",\"injuryState\":\"伤势状态及在第几单元发生变化，无则写无\",\"forbiddenChanges\":[\"禁止变化项\"]}],\n" +
            "  \"propState\": [{\"prop\":\"道具规范名\",\"states\":[{\"at\":\"unit_1\",\"state\":\"持在右手\"}],\"mustAppear\":[\"必须出现的单元\"],\"forbidden\":[\"禁止出现的状态，如落地后不得再出现在手中\"]}],\n" +
            "  \"clueReveal\": [{\"clue\":\"线索名\",\"revealOrder\":1,\"revealAt\":\"在哪个单元揭示\",\"audienceMustNotice\":\"观众必须注意到的具体内容\",\"mustNotRevealBefore\":\"禁止在此单元之前出现\"}],\n" +
            "  \"actionCausality\": [{\"unitNumber\":\"1.1\",\"newInformation\":\"观众在这一单元获得的新信息，没有写无\",\"becauseOf\":\"因为上一单元发生了什么本单元才成立\",\"leadsTo\":\"本单元结果导向下一单元什么\"}],\n" +
            "  \"transitionMotive\": [{\"fromUnit\":\"1.1\",\"toUnit\":\"1.2\",\"motiveType\":\"声音桥/视线/动作/信息问题/空间\",\"detail\":\"具体怎么衔接\"}]\n" +
            "}\n\n" +
            "提取规则：\n" +
            "1. 场景空间表只收录剧本中真实发生剧情的主要场景，每场景一条；方位描述必须具体到「什么物体在左/右/中后」，禁止「左边有一些东西」这类空话；剧本未交代的方位写「未交代」，禁止编造与剧情冲突的布置。\n" +
            "2. 角色连续性表覆盖所有有实体出镜的角色；外貌锚定只写跨镜必须保持一致的部分；伤势状态必须标注变化发生在第几单元（如「第3单元左臂受伤，之后不得自愈」）。\n" +
            "3. 道具状态表只收录会随剧情改变状态的关键道具（武器、信物、证物等）；states 按剧情顺序排列，每个时间点写清在哪个单元、处于什么状态；状态只可单向推进，禁止倒退。\n" +
            "4. 线索揭示表按揭示顺序编号；揭示顺序、揭示位置、观众必须注意到的内容三者必须与剧本一致，禁止提前揭示。\n" +
            "5. 动作因果表按剧本单元顺序逐单元填写；newInformation 写「观众因这一单元新知道了什么」，而不是「发生了什么动作」。\n" +
            "6. 转场动机表覆盖相邻单元之间的衔接点；motiveType 只能从「声音桥、视线、动作、信息问题、空间」中选一个。\n" +
            "7. 涉及人物、道具、环境、特效时一律使用给定的资产规范名；资产清单未覆盖的真实对象照实描述。\n" +
            "8. 全部内容必须来自给定的剧本与故事分析，禁止补充剧本之外的情节。";

        var sb = new StringBuilder();
        sb.AppendLine("【剧本】");
        sb.AppendLine(script ?? "");
        if (!string.IsNullOrWhiteSpace(storyAnalysis))
        {
            sb.AppendLine();
            sb.AppendLine("【故事分析】");
            sb.AppendLine(storyAnalysis);
        }
        if (!string.IsNullOrWhiteSpace(blueprint))
        {
            sb.AppendLine();
            sb.AppendLine("【全局蓝图】");
            sb.AppendLine(blueprint);
        }
        if (!string.IsNullOrWhiteSpace(assetCatalog))
        {
            sb.AppendLine();
            sb.AppendLine("【资产规范名参考】");
            sb.AppendLine(assetCatalog);
        }
        sb.AppendLine();
        sb.AppendLine("请按上述 JSON 结构输出六类连续性表。");

        try
        {
            var raw = await CallAsync(apiUrl, apiKey, model, systemPrompt, sb.ToString(), jsonMode: true, temperature: 0.1, thinkingMode: thinkingMode);
            return ParseContinuity(raw);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[连续性表] 抽取调用失败");
            return null;
        }
    }

    private static ContinuityExtractionResult? ParseContinuity(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try
        {
            return JsonSerializer.Deserialize<ContinuityExtractionResult>(raw.Substring(start, end - start + 1), CombatJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    // ========== 5. Storyboard ==========
    public async Task<string> PlanShots(string unitContent, string apiUrl, string apiKey, string model, string? cameraText = null, string? skillText = null, string? fightText = null, string? directorPlanText = null, string? envText = null, string? thinkingMode = null, System.Collections.Generic.IReadOnlyList<string>? complianceFeedback = null)
    {
        var directiveText = cameraText;
        if (string.IsNullOrWhiteSpace(unitContent)) return "";
        var eps = System.Text.RegularExpressions.Regex.Split(unitContent, @"(?=【第(?:\d+|[一二三四五六七八九十百零]+)集】)").Where(e => e.Trim().Length > 0).ToList();
        var all = new System.Collections.Generic.List<string>();
        for (int ei = 0; ei < eps.Count; ei++)
        {
            try
            {
                var systemPrompt = 
                    "你是深耕 Seedance2.0 视频模型的好莱坞顶级影视分镜导演，为以下视频单元设计镜头任务\n\n先自动判断每个单元的场景类型（打斗/动作、文戏/情感、追逐/逃亡、悬疑/惊悚、日常/喜剧、高潮/对决），并按类型设计镜头：\n本剧为古风轻喜剧基调——日常对话、打脸、提审、追凶、斗嘴一律按「日常/喜剧」或「文戏/情感」标注；「悬疑/惊悚」仅限剧情设定本身神秘压迫的镜头（如深夜陌生客人登场、追查神秘线索），禁止仅因场景是夜晚、灯光昏暗就标悬疑/惊悚，夜晚发生的喜剧、打脸、追逐、对话戏按实际剧情类型标注；禁止把喜剧冲突、公务审案、日常收尾误标为悬疑/惊悚。\n- 打斗/动作、追逐/逃亡、高潮/对决：快节奏，镜头运动用快速推拉摇移、手持晃动、甩镜、跟随运动，关键命中瞬间可加顿帧/慢动作\n- 文戏/情感、悬疑/惊悚、日常/喜剧：镜头稳定，多用缓推、固定机位、特写微表情\n\n输出格式要求：\n### 【第X集】\n\n#### 【单元X.Y】 标题\n\n- **单元类型**: ...\n- **镜头编号**: X.Y-Z\n- **场景**: ...（必须从【环境资产清单】中选一个资产名，一字不差；清单为空或未覆盖时写简洁场景名词）\n- **镜头描述**: ...\n- **构图方式**: ...\n- **景别**: ...\n- **镜头运动**: ...\n- **出镜角色及表情**: ...\n- **对话/台词**: ...（必须逐字保留输入单元「对话/台词」中的原台词，按说话顺序分配到实际说话的镜头；无台词写「无」）\n- **镜头时长**: ...秒\n- **镜头时间轴**: 0-2s: 开场动作/情绪；2-4s: 推进；4-5s: 收束（必须从 0 秒起连续分段铺满本镜「镜头时长」的结束秒数，禁止省略）\n- **起始画面**: ...\n- **结束画面**: ...\n- **起始状态**: ...（接住上一镜的结束状态：人物位置/姿态/持物/伤势/情绪/光照）\n- **单一动作**: ...（本镜只做一件事）\n- **结束状态**: ...（本镜结束时的状态，必须能被下一镜接住）\n- **衔接下一镜**: ...（由此镜进入下一镜的动机：动作延续/视线引导/声音先入/信息提问/空间移动）\n- **禁止变化**: ...（本镜内不得改变的空间关系/服装/发型/伤势/道具位置与归属/时间光照；没有写「无」）\n- **新信息**: ...（观众本镜获得的新信息增量；纯氛围或重复动作写「无」）\n\n重要：必须严格按照上述格式输出，包含集和单元标记；每个镜头必须包含「镜头描述」行，禁止省略。\n确保镜头之间动作连贯、视点不跳。用中文。\n\n【分镜铁律】\n1. 对话/交流类镜头允许正反打：谁说话镜头就对准谁（说话人近景/特写），说话人切换时镜头随之切换（A说→对A，B说→对B），切换干净利落。\n2. 非对话镜头（打斗、动作、追逐、氛围、空镜等）保持单一连续机位，禁止无关的机位切换、转场或剪辑描述。\n3. 对话正反打要符合对话节奏：轮到谁说话镜头就对谁，切换时机与说话人变化同步，禁止在一个人说话时把镜头切给别人。\n4. 道具归属：镜头里的道具必须归属正确——专属道具只能归其主人使用，禁止把某角色的专属道具写进其他角色的镜头；剧本未指定专属道具时用通用物品名，拿不准就不写道具名，宁缺毋滥。\n5. 阵营关系：依据单元剧本文本确认各方阵营，同盟角色禁止互相出手；同一阵营的角色同镜时只做配合、掠阵、观战或被大招威压波及的反馈（如法光晃动、后退数步），禁止把同盟写成对打；敌对关系必须与剧本一致——剧本说谁对谁，镜头就写谁对谁，禁止擅自编排剧本中不存在的交手；角色在镜头中的站位与攻防方向保持跨镜一致。\n\n【台词铁律】\n1. 对话/台词 必须逐字保留输入单元「对话/台词」字段中的原句，禁止改写、意译、删减或新增任何台词。\n2. 单元「对话/台词」为「无」时，镜头默认写「无」，不得随意编造台词；但若镜头描述明确包含口头交流动作，按规则4补出台词。\n3. 单元有台词时，必须全部出现在对应镜头中，一句都不能漏；同一句台词只出现一次，不要重复到多个镜头；台词要按说话顺序分配给正确的角色。\n4. 例外兜底：当镜头描述中明确出现口头交流动作（如“求”“应”“说”“问”“答”“喊”“念叨”“叫住”等）时，即使单元「对话/台词」为「无」，也必须根据该镜头描述补出自然台词并写入对应镜头；只有镜头描述中完全没有说话动作时才写「无」。\n5. 台词长度必须匹配镜头时长，防止语速过快：5秒镜头台词合计不超过20字；11秒不超过40字；15秒不超过60字。台词超过当前档位上限时，该镜头必须使用更高档位（5→11→15），禁止超载；仅当15秒仍超60字时才拆台词到多个镜头。\n6. 字幕不是台词：输入单元「对话/台词」中的「字幕：…」等画面文字（片头/时间地点字幕、白字字幕）不是台词、不是旁白——禁止写入任何镜头的「对话/台词」或画面正文；「对话/台词」仅含字幕内容时一律写「无」并按无台词处理，且不得计入20字台词、禁止据此升档11秒。真实对白/旁白不在此列：只要单元存在真实台词或旁白且合计超过20字，镜头时长仍可按「镜头时长」规则正常升档到11秒。\n7. 禁止在「镜头描述/起始画面/结束画面/镜头时间轴」中出现“字幕淡入”“字幕浮现”“白字字幕”等字幕生成要求——字幕属后期合层，不由视频画面生成；源文本/导演注意里的“字幕”内容一律忽略或仅取其氛围意图，时代/地点信息用环境、光线、道具等画面元素交代。\n1. 镜头时长 只能从 5秒、11秒、15秒 三档中选择（视频生成API只支持这三档），禁止出现 4秒、6秒、8秒、10秒、12秒等其它时长。\n2. 打斗/动作、追逐/逃亡、高潮/对决镜头默认11秒，但5秒同样允许多个快速攻防回合，时长按内容密度决定；文戏/情感、日常/喜剧、悬疑/惊悚镜头一律默认5秒，只有台词（含旁白）合计确实超过20字时才允许用11秒，禁止用15秒。\n3. 同一单元内多个镜头的时长要自然衔接，避免时长跳变割裂。\n4. 时长宁短勿长：单镜头内容不足以填满当前档位时，宁可拆成多个短镜头，禁止为凑时长注水或拖节奏。";
                    systemPrompt += @"【单元时长硬约束】（本条优先级最高，覆盖本文档其他一切关于镜头数量与镜头时长档的描述）
1. 输入单元自带「时长：D秒」字段（D 只能是 5/11/15），这是本单元视频的总时长；本单元所有镜头「镜头时长」之和必须恰好等于 D，禁止大于或小于 D。
2. 允许的拆分组合只有：D=5 只能输出 1 条 5 秒镜头；D=11 只能输出 1 条 11 秒镜头；D=15 输出 1 条 15 秒镜头，或 3 条 5 秒镜头（仅当内容能明确切成三个 5 秒段落）。
3. 上文按场景类型给「2-3 个镜头/3-4 个镜头」以及「时长宁短勿长、宁可拆成多个短镜头」等描述一律作废，以本条为准；单镜头装不下的内容，按动作/对话节奏压缩进该镜头「镜头时间轴」0-D 秒的连续分段里表现，禁止用超出 D 的镜头堆数量。
4. 台词容量按单元总时长 D 判断（5 秒 ≤20 字、11 秒 ≤40 字、15 秒 ≤60 字）：需要正反打时在 D=15 拆出的 3 条 5 秒镜头中实现；D=5/11 用双人同框、过肩、焦平面切换在单镜头内完成说话人视线转换，禁止为塞台词或做正反打而新增超出 D 的镜头。
5. 台词仍按【台词铁律】逐字保留并分配到对应镜头；单元内 2 条以上镜头时编号 X.Y-Z 按顺序连续。";
                    systemPrompt += "\n\n【单元类型与导演注意铁律】\n1. 「单元类型」行只写标准类型词（打斗/动作、文戏/情感、追逐/逃亡、悬疑/惊悚、日常/喜剧、高潮/对决），禁止添加任何括号备注或解释；\n2. 禁止在「镜头描述」里自行编写【导演注意】或情绪任务说明，本单元情绪任务由系统按整集导演计划自动注入；\n3. 导演计划给到的「本单元情绪任务」（情绪词、层级、节奏要求）决定镜头情绪基调，镜头内容必须服从，禁止自行改成相反或替代方案；\n4. 禁止把导演注意塞进「单元类型」「景别」「镜头运动」等格式字段。";
                    systemPrompt += "\n\n【出镜角色铁律】\n1. 「出镜角色及表情」只写有实体视觉形象、真实出镜的角色（人、妖、法相、灵宠、傀儡等）；纯声音/音效源（留音、录音、画外音、BGM、风声、雷声等）不是角色，禁止写入「出镜角色及表情」；\n2. 若画面需要表现声音的视觉形态（如半透明音波状留音、声波扩散），在「镜头描述」「起始画面」「结束画面」中用文字描述该视觉形态即可，不占出镜角色位；\n3. 「对话/台词」行仍可正常标注说话人（如 顾残山留音：\"…\"），说话人标注不受本规则限制。";
                    systemPrompt += "\n\n【时间轴铁律】\n1. 每个镜头必须输出「镜头时间轴」行，禁止留空或省略，格式为：0-2s: 开场动作/情绪；2-4s: 推进；4-5s: 收束。\n2. 时间轴必须从 0 秒开始连续铺满该镜头「镜头时长」结束，覆盖秒数总和与镜头时长一致，禁止只写前半段。\n3. 文戏/情感、悬疑/惊悚、日常/喜剧镜头同样按动作节点、情绪变化或对话节奏写清从 0 到结束的完整分段，禁止整行空着。";
                    systemPrompt += "\n\n【镜头状态机铁律】\n1. 每个镜头必须输出「起始状态」「单一动作」「结束状态」「衔接下一镜」「禁止变化」「新信息」六行，写成「字段：值」单行格式，值禁止换行，禁止省略或整行留空；\n2. 「起始状态」必须接住上一镜的「结束状态」（首镜接住本单元开场状态）；「结束状态」必须能直接作为下一镜的「起始状态」；相邻镜头的人物位置、姿态、持物、伤势、情绪、光照必须对得上，禁止状态矛盾或凭空跳变；\n3. 「单一动作」只写一件主体动作（一镜一动作），禁止把多个并列动作塞进该行，其余信息走「新信息」；\n4. 「衔接下一镜」写由此镜进入下一镜的动机（动作延续、视线引导、声音先入、信息提问、空间移动等）；本单元最后一镜写它与下一单元的衔接意图；\n5. 「禁止变化」列出本镜内必须保持不变、禁止凭空改变的元素（空间关系、服装、发型、伤势、道具位置与归属、时间与光照），没有则写「无」；\n6. 「新信息」写观众在本镜获得的信息增量（剧情、关系、规则、伏笔）；纯氛围或重复动作的镜头写「无」，禁止编造本单元没有的信息。";
                    if (!string.IsNullOrWhiteSpace(directiveText))
                    {
                        systemPrompt += "\n\n【运镜原子库规则】\n1. 运镜原子库提供景别、运镜、转场、光影、节奏五类抽象镜头技法，每条格式为「原子名：描述」，只描述技法本身与情绪用途，不包含时代、场景和具体物件；\n2. 输出镜头的「景别」「镜头运动」等字段时，优先从【运镜原子菜单】选用匹配原子（直接使用原子名、并按描述细化），禁止自创与库内原子冲突的表述；\n3. 每个镜头建议按「景别原子 + 运镜原子 + 光影原子」组合，转场/节奏原子用于镜头衔接与节奏；\n4. 原子是技法蓝本，具体画面必须贴合剧本单元的情节、时代与场景，禁止照搬原子描述中出现的示例物件；\n5. 菜单中没有合适原子时可以自行创作风格一致的运镜，不必强行套用。";
                    }
                    if (!string.IsNullOrWhiteSpace(skillText))
                    {
                        systemPrompt += "\n\n【技能库规则】\n1. 技能库是本片可用的技能大招库，条目格式为「技能名（系别·层级）: 视频版提示词」，视频版提示词包含该技能的形态、镜头与节奏；技能库只用于角色归属过滤，不是白名单；\n2. 打斗/动作、高潮/对决类单元设计镜头时，必须保留单元原文明确点名的技能/法阵/合击/领域/大招名（即使不在技能库中也必须原样写入分镜技能字段与镜头描述，禁止省略、改名或替换成库内其他技能），再从【技能库】为关键战斗安排技能：在镜头描述、起始画面/结束画面中明确写出技能名（与技能库完全一致），禁止自创技能名；\n3. 层级语义：T3 为常规技能、T4 为强力技能、T5 为终极大招；T5 大招只允许在高潮/终极对决等关键镜头释放，T3/T4 按战斗升级阶段依次使用，禁止在普通镜头放大招；\n4. 只有「释放技能」的镜头才出现技能特效（施法、出招、放大招时按技能视频版提示词的形态、色调、节奏设计）；平A对砍、普通近身打斗禁止安排技能特效，此类镜头按打斗节奏模板处理；\n5. 同一场战斗按 平A对砍→T3/T4技能→T5大招 的节奏递进，避免开局就放大招；\n6. 原文点名技能与库内技能并列时，原文技能名优先且必须完整保留。";
                    }
                    if (!string.IsNullOrWhiteSpace(fightText))
                    {
                        systemPrompt += "\n\n【打斗模板规则】\n1. 打斗模板库提供打斗动作模板（适用场景/节拍/动作/运镜/约束），节奏总口诀：快—快—顿—重；\n2. 打斗/动作、追逐/逃亡、高潮/对决单元设计镜头时，必须从【打斗模板库】选择最匹配的模板：按模板节拍设计镜头节奏（快攻→格挡反打→僵持顿帧→震开），命中/对撞/镇压瞬间安排顿帧+慢动作，禁止全程匀速；\n3. 镜头时长参照模板时长档（5/11/15秒）与现有时长规则衔接；\n4. 平A对砍、近身缠斗按打斗模板设计；该镜头同时释放技能时，动作节奏按模板、特效形态按技能库。";
                    }
                    if (!string.IsNullOrWhiteSpace(directorPlanText))
                    {
                        systemPrompt += "\n\n【导演决策执行铁律】\n1. 导演决策是最高意图约束：动作调度、站位、表演、摄影、节奏必须逐条落实到镜头，禁止自行改成相反或替代方案；VFX 不限制，可自由发挥；\n2. 导演要求「定点调度/不移动站位/双手垂放/静态呈现/独立阵心不动」时，禁止让角色主动出击、移动站位或亲手完成导演指定由环境完成或保持静止的动作；\n3. 特效不限制：允许自行新增特效、虚影、光效、气浪与环境反馈；导演的 VFX 策略/峰值段仅作参考，不限制；唯一硬约束是禁止新增或改写技能名、技能效果与技能归属；\n4. 导演指定的结束状态必须原样出现在最后一个镜头的「结束画面」；\n5. 导演决策与技能库/模板/时长规则冲突时，在不破坏锁定规则和台词铁律的前提下优先服从导演决策。";
                    }
                    if (!string.IsNullOrWhiteSpace(envText))
                    {
                        systemPrompt += "\n\n【场景铁律】\n1. 每个镜头必须输出「场景」行，禁止省略；\n2. 「场景」必须从【环境资产清单】中选择一个资产名，一字不差地写进「场景」行，禁止改写、缩写、拼接或替换清单中的资产名；\n3. 同一单元内多个镜头处于同一地点时，场景名必须一致，禁止同一镜头组内场景名漂移；\n4. 若清单为空、或本镜头实际发生地点确实不在清单中，写一个简洁的场景名词短语（如「雪山山道」「客栈正厅」），禁止编造不符合剧情时代的地点。";
                    }
                    var userMsg = eps[ei];
                    if (!string.IsNullOrWhiteSpace(cameraText))
                        userMsg += "\n\n【运镜原子菜单】\n" + cameraText;
                    if (!string.IsNullOrWhiteSpace(skillText))
                        userMsg += "\n\n【技能库】\n" + skillText;
                    if (!string.IsNullOrWhiteSpace(fightText))
                        userMsg += "\n\n【打斗模板库】\n" + fightText;
                    if (!string.IsNullOrWhiteSpace(directorPlanText))
                        userMsg += "\n\n【导演决策】\n" + directorPlanText;
                    if (!string.IsNullOrWhiteSpace(envText))
                        userMsg += "\n\n【环境资产清单】\n" + envText;
                    if (complianceFeedback != null && complianceFeedback.Count > 0)
                        userMsg += "\n\n【合规反馈】（逐条修复，保留正确镜头只改违规处）\n" + string.Join("\n", complianceFeedback.Select(f => "- " + f)) + "\n";
                    var r = await CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, thinkingMode: thinkingMode);
                if (!string.IsNullOrWhiteSpace(r)) all.Add(r.Trim());
            }
            catch (System.Exception ex)
            {
                _logger.LogError(ex, "[PlanShots] Episode {EpisodeIndex} failed", ei + 1);
            }
        }
        return string.Join("\n\n", all);
    }

    // ========== Combat-controlled storyboard ==========
    public async Task<CombatIntent?> ExtractCombatIntent(string unitText, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null)
    {
        var systemPrompt = "你是一个打斗意图提取器。只从给定单元中提取打斗意图，禁止创作单元中不存在的参与者、技能、地点或结果。\n" +
            "只输出 JSON，不要 Markdown 代码块，不要解释，不要输出任何额外文字。\n" +
            "JSON 字段：{\"participants\":[{\"name\":\"角色名\",\"role\":\"A/B/C\",\"weapon\":\"武器\"}],\"combatForm\":\"如 双人刀剑近战\",\"objective\":\"\",\"intensity\":3,\"duration\":11,\"environmentType\":\"室内/室外/狭窄空间/开阔场地等\",\"requiredSkills\":[],\"requiredActions\":[],\"result\":\"\",\"startState\":\"\",\"endState\":\"\",\"forbidden\":[]}\n" +
            "规则：duration 只能填 5/11/15；双人/多人过招只能填 11 或 15，5 仅限明确的一招秒杀/碾压/单人爆发；intensity 只能填 1-5；requiredSkills 填单元原文明确点名的技能/法阵/合击/领域/大招名，必须原样保留，即使该技能不在技能库中也必须填入；技能库只用于角色归属过滤，不是白名单；原文未点名时一律留空数组，禁止根据动作/特效效果猜测技能名（如“气血爆发”“金身”“法相”“火莲”等描述性词不能当作技能名）；requiredActions 至少列出 3 个连续攻防动作（A 攻→B 防/反→A 变招→命中/压制），秒杀/碾压/单人爆发可少于 3 个；参与者、地点、动作、结果必须来自单元原文。";
        try
        {
            var raw = await CallAsync(apiUrl, apiKey, model, systemPrompt, unitText, jsonMode: true, temperature: 0.1, thinkingMode: thinkingMode);
            return ParseCombatIntent(raw);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> PlanCombatShots(
        StageUnit unit,
        CombatIntent intent,
        FightTemplateItem template,
        List<CameraAtomItem> atoms,
        List<SkillLibraryItem> skills,
        string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? directorPlanText = null,
        IReadOnlyList<CombatBeat>? directorBeats = null, IReadOnlyList<PackedShot>? packedShots = null, IReadOnlyList<string>? complianceFeedback = null, string? envText = null, string? thinkingMode = null)
    {
        var ep = unit.EpisodeNumber > 0 ? unit.EpisodeNumber : 1;
        if (unit.UnitNumber.Contains('.') && int.TryParse(unit.UnitNumber.Split('.')[0], out var parsedEp) && parsedEp > 0)
            ep = parsedEp;
        var title = !string.IsNullOrWhiteSpace(unit.CoreAction)
            ? unit.CoreAction.Trim()
            : (!string.IsNullOrWhiteSpace(unit.Location) ? unit.Location.Trim() : "打斗单元");
        var atomText = atoms.Count == 0
            ? "无"
            : string.Join("\n", atoms.Select(a => "- " + a.Name + "（" + a.Category + "）: " + a.Description));
        var skillText = skills.Count == 0
            ? "无（技能库无匹配；分集原文点名的技能仍必须保留）"
            : string.Join("\n", skills.Select(s => "- " + s.Name + "（" + s.Element + "系·T" + s.Tier + "）: " + s.PromptVideoForLLM));
        var participantText = intent.Participants.Count == 0
            ? "无"
            : string.Join("\n", intent.Participants.Select(p => "- " + p.Role + "=" + p.Name + (string.IsNullOrWhiteSpace(p.Weapon) ? "" : "（" + p.Weapon + "）")));
        var soloGuard = intent.Participants.Count == 1
            ? "\n\n【单人镜头铁律】本单元只有 1 个参与者，没有对手/敌方/B 角色；镜头描述、起始画面、结束画面禁止出现“敌方”“对手”“对方”“B 角色”等不存在角色，所有动作与反应只围绕列出的参与者与环境展开。"
            : "";
        var skillNames = string.Join(", ", skills.Select(s => s.Name)
            .Concat(intent.RequiredSkills ?? new List<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(skillNames)) skillNames = "无";

        var beatText = directorBeats == null || directorBeats.Count == 0
            ? "无"
            : string.Join("\n", directorBeats.Select(b =>
                $"Beat{b.Index:00} [{b.GrammarId}] {b.ActionDescription}；防御响应:{b.DefenseResponse}；结果:{b.Result}；空间:{b.SpatialRelation}；强度:{b.Intensity}"));
        var packedShotText = packedShots == null || packedShots.Count == 0
            ? ""
            : ShotPacker.BuildPlanText(packedShots);
        var timeBudgetText = CombatTimeBudgetBuilder.Build(unit, packedShots, directorBeats?.Count ?? 0);
        var atomNames = atoms.Count == 0 ? "无" : string.Join(", ", atoms.Select(a => a.Name));

        var systemPrompt = $"你是一个受控打斗分镜师。你必须严格按给定模板、运镜原子、技能、导演锁定节拍和锁定动作链生成打斗分镜，不得重新设计动作、运镜或战斗结果。\n" +
            $"输出格式（用中文）：\n### 【第{ep}集】\n\n#### 【单元{unit.UnitNumber}】{title}\n\n" +
            $"- **控制模式**: 打斗模板\n- **打斗模板**: {template.FightTemplateId} / {template.Name}\n- **技能**: {skillNames}\n- **运镜原子**: {atomNames}\n- **单元类型**: {unit.Type}\n\n" +
            $"- **镜头编号**: {unit.UnitNumber}-1\n- **场景**: ...（必须从【环境资产清单】中选一个资产名，一字不差；清单为空或未覆盖时写简洁场景名词）\n- **节拍**: Beat1,Beat2,Beat3（一个镜头可覆盖多个连续节拍）\n- **镜头时间轴**: 0-2s: 突进+横斩+闪避；2-4s: 切入+格挡+反击；4-5s: 重拳命中+击退\n- **镜头描述**: ...\n- **构图方式**: ...\n- **景别**: ...\n- **镜头运动**: ...\n- **出镜角色及表情**: ...\n- **对话/台词**: ...\n- **镜头时长**: ...秒（只能 5/11/15，由 LLM 按内容密度自选）\n- **起始画面**: ...\n- **结束画面**: ...\n\n" +
            "打斗/动作、追逐/逃亡、高潮/对决单元镜头保持快节奏；镜头数量与镜头时长组合由【单元总时长硬上限】决定（允许 1 条 11 秒镜头承载多个攻防回合），命中瞬间加顿帧/慢动作；每个镜头都必须包含上述完整字段，且必须输出「镜头描述」行；「镜头时间轴」只写简短分段，完整细节写进「镜头描述」。\n\n" +
            "【节拍铁律】\n1. 用户消息中的【导演锁定节拍】是唯一动作主线；每个镜头必须用「**节拍**: BeatN」或「**节拍**: BeatN,BeatM」标明覆盖的节拍，编号必须与提供的 Beat 序号一致。\n2. 禁止删除、跳过、重排或改写任一 Beat；CombatBeat 不等于镜头，多个连续 Beat 允许装进同一个镜头按时间轴连续打完，但每条 Beat 至少被一个镜头覆盖。\n3. 未提供【导演锁定节拍】时，「节拍」行可省略或按攻防推进写 Beat1/Beat2/Beat3。\n\n" +
            "【回合硬约束】\n1. 镜头时长由 LLM 按本镜头内容密度从 5秒/11秒/15秒 中自行选择：高手过招 1 秒内可完成多个攻防动作/回合，禁止用“5秒只适合一击”限制交锋频率，档位低不等于内容少。\n2. 5/11/15 秒都可承载多个完整攻防回合（A攻→B防/反→A变招→命中/压制），回合数由动作链与内容密度决定；禁止为凑档位硬拆镜头，也禁止因为档位小就把多回合压成一次挥击+特效。\n3. 每个时间段双方都必须有动作与受击反馈，禁止一方出手、另一方原地等待或只出特效。\n4. 同一场打斗的镜头按顺序承接：后一镜头的起始画面接前一镜头的结束画面。\n\n" +
            timeBudgetText + "\n\n" + (unit.Duration == 5 || unit.Duration == 11 || unit.Duration == 15 ? "【单元总时长硬上限】本单元总时长 D=" + unit.Duration + " 秒：本单元所有镜头「镜头时长」之和不得超过 D 秒并尽量接近 D；若当前编排超出 D，必须合并连续镜头或把多拍压缩进同一镜头（一个镜头可覆盖多个连续节拍，参考【镜头密度原则】），禁止靠超出单元总时长堆镜头。" : "") + "\n\n" +
            "【出拳速度与打击感】\n1. 出手前蓄力、出手瞬间加速并带动态模糊/残影，禁止匀速出拳；\n2. 慢动作只用于命中/对撞瞬间（0.2-0.4秒），随后恢复常速或加速；\n3. 命中必须有受击反馈（后仰/倒飞/犁地）、环境反馈（碎石/尘土/气浪/衣袍炸裂）和镜头冲击抖动；\n4. 写清音效（挥拳呼啸、命中闷响/爆裂）。\n\n" +
            "【镜头密度原则】\n1. CombatBeat 不等于镜头，MicroAction 不等于镜头；一个镜头 = 连续动作段落 + 对手响应 + 运镜 + 景别变化 + 表情 + VFX + 环境反馈 + 结束姿态。\n2. 一个镜头允许覆盖多个 CombatBeat 和多个连续动作，按镜头内部时间轴连续推进；禁止把“挥剑”“闪避”“出拳”各拆成一个独立视频镜头。\n3. 镜头内部时间轴片段属于同一个视频，禁止拆成独立视频镜头；每段同时包含人物动作、对手响应、运镜、VFX 与环境反馈。\n4. 提供【ShotPacker 打包镜头】时，优先按打包镜头与时间轴展开，把多个 Beat 的连续攻防压进同一镜头；确需拆分时也要按连续交锋段落拆，禁止单动作成镜。\n\n" +
            "【锁定规则】\n1. 禁止更换模板 ID。\n2. 提供【锁定动作链】时，镜头必须逐拍完整覆盖动作链中的每一拍；动作名称、顺序、距离变化、敌方状态变化都禁止改动、删除或新增库外动作。\n" +
            "3. 提供【锁定动作链】时，模板的「动作行/运镜行」只作为镜头节奏、运镜和画面处理参考，禁止用它替换或重排动作链；未提供动作链时才按模板动作行生成。\n" +
            "4. 运镜原子只能使用给定原子。\n5. 技能只能使用给定技能和【打斗单元】原文明确点名的技能；技能库只用于角色归属过滤，不是白名单；原文点名的技能（含法阵、合击、领域、大招名）即使不在给定技能中也必须原样保留到「技能」行与镜头描述，禁止省略或改名；未指定且原文未点名时禁止自创技能；指定了技能时，镜头描述、起始画面/结束画面中每次出现该技能效果都必须直接写完整技能名（如「赤金气血」「舍身一拳」「七曜诛圣阵」），禁止改成「气血狂涌」「一拳碎山」等自创名，禁止遗漏技能字段中的任何技能。\n6. 只允许把 A/B/C 替换为实际角色，并补充表情、衣物、环境反馈和自然中文。\n" +
            "7. 对话/台词必须逐字保留输入单元「对话/台词」中的原句，禁止改写、删减或新增；单元为「无」时镜头写「无」；「对话/台词」为「字幕：…」等画面文字（片头/时间地点字幕、白字字幕）时一律按无台词写「无」，字幕不是台词，禁止写入任何画面字段。\n8. 镜头时长只能从 5秒、11秒、15秒 中选择，具体档位由 LLM 按本镜头内容密度自行决定；5秒同样允许多个快速攻防回合，禁止因档位低而降低交锋频率；内容不足以填满当前档位时禁止注水拖节奏。\n";

        if (!string.IsNullOrWhiteSpace(directorPlanText))
        {
            systemPrompt += "\n\n【导演决策执行铁律】\n1. 导演决策是最高意图约束：戏剧目的、核心主体、情绪、节奏、爽点、动作策略、表演策略、摄影策略必须逐条落实到镜头，禁止遗漏或自行改成相反方案；VFX 不限制，可自由发挥；\n2. 导演要求「静态承压/独立阵心不动/定点调度/不移动站位/双手垂放」时，禁止让角色主动出击、移动站位或亲手完成导演指定由环境完成或保持静止的动作；\n3. 特效不限制：允许自行新增特效、虚影、光效、气浪与环境反馈；导演的 VFX 策略/峰值段仅作参考，不限制；唯一硬约束是禁止新增或改写技能名、技能效果与技能归属；\n4. 导演指定的结束状态必须原样出现在最后一镜的「结束画面」；\n5. 导演决策与模板/动作链/技能/时长规则冲突时，在不破坏锁定规则和台词铁律的前提下优先服从导演决策。";
        }
        if (complianceFeedback != null && complianceFeedback.Count > 0)
        {
            systemPrompt += "\n\n【返工规则】保留上一次分镜中已经正确的镜头，只修复【合规反馈】列出的违规；不要整体推翻重写。";
        }
        if (!string.IsNullOrWhiteSpace(envText))
        {
            systemPrompt += "\n\n【场景铁律】\n1. 每个镜头必须输出「场景」行，禁止省略；\n2. 「场景」必须从【环境资产清单】中选择一个资产名，一字不差地写进「场景」行，禁止改写、缩写、拼接或替换清单中的资产名；\n3. 同一场打斗的所有镜头场景名必须一致，禁止镜头间场景名漂移；\n4. 若清单为空、或本场打斗实际发生地点确实不在清单中，写一个简洁的场景名词短语（如「雪山山道」「客栈正厅」），禁止编造不符合剧情时代的地点。";
        }

        var userMsg = "【打斗单元】\n" + unit.RawText + "\n\n" +
            "【打斗意图】\n" + JsonSerializer.Serialize(intent, CombatJsonOptions) + "\n\n" +
            "【分集原文点名技能】\n" + ((intent.RequiredSkills?.Count ?? 0) == 0 ? "由【打斗单元】原文识别，原文未点名则为无" : string.Join(", ", intent.RequiredSkills ?? new List<string>())) + "\n\n" +
            $"【指定模板】\n模板ID: {template.FightTemplateId}\n模板名: {template.Name}\n适用场景: {template.Scene}\n节拍: {template.Beat}\n动作行: {template.ActionPrompt}\n运镜行: {template.CameraPrompt}\n约束行: {template.ConstraintPrompt}\n\n" +
            "【参与者】\n" + participantText + "\n\n" +
            "【指定运镜原子】\n" + atomText + "\n\n" +
            "【技能库（角色归属过滤，非白名单）】\n" + skillText + "\n\n" +
            "【锁定动作链】\n" + (string.IsNullOrWhiteSpace(lockedActionChain) ? "无" : lockedActionChain) + "\n\n" +
            "【ShotPacker 打包镜头】\n" + (string.IsNullOrWhiteSpace(packedShotText) ? "无（按模板自由编排）" : packedShotText) + "\n\n" +
            "【导演锁定节拍】\n" + beatText + "\n\n" +
            soloGuard + "\n\n【禁止修改】\n禁止改变动作链顺序、战斗结果、模板 ID、运镜原子和技能名称；禁止创建库外技能、库外动作和库外运镜；只允许补充画面细节、表情、环境和自然语言。";

        if (!string.IsNullOrWhiteSpace(directorPlanText))
            userMsg += "\n\n【导演决策】\n" + directorPlanText + "\n";
        if (!string.IsNullOrWhiteSpace(envText))
            userMsg += "\n\n【环境资产清单】\n" + envText + "\n";
        if (complianceFeedback != null && complianceFeedback.Count > 0)
            userMsg += "\n\n【合规反馈】（必须逐条修复）\n" + string.Join("\n", complianceFeedback.Select(x => "- " + x)) + "\n";
        return await CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, temperature: 0.4, thinkingMode: thinkingMode);
    }

    private static CombatIntent? ParseCombatIntent(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start >= 0 && end > start)
        {
            var json = raw.Substring(start, end - start + 1);
            try { return JsonSerializer.Deserialize<CombatIntent>(json, CombatJsonOptions); }
            catch { /* fall through to full-text parse */ }
        }
        try
        {
            return JsonSerializer.Deserialize<CombatIntent>(raw, CombatJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public Task<string> ExtractCharacters(string script, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null, AssetPromptTemplate? assetTemplate = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个角色资产提取器。这是一个「提取任务」，不是创作任务：禁止输出剧情、散文、对话、场景描写等任何叙事内容，也不要输出 JSON、解释、前言或代码块围栏，只输出角色列表（Markdown 纯文本）。\n\n格式要求：每个角色用一个「#### 序号. 角色名」标题开头（标题里的角色名同样只写主体名、禁止带括号），然后以「- 标签：内容」列表列出以下字段：\n- 名称：角色主体名（禁止带括号及任何描述性文字，例如只写“角色名”，禁止写“角色名（别称，身份描述）”）\n- 角色别名：角色别名/称号/群像成员，多个用顿号分隔，有则写，没有写“无”\n- 角色描述：一句话概括角色身份，没有写“无”\n- 外貌描述：【必填，禁止省略】必须写清发色、**发长（明确到 短发/及耳/及肩/锁骨/及腰/长发 等具体长度）**、瞳色、年龄感与体型特征\n- 服装风格：……\n- 性格特征：……\n- 习惯动作/表情：……\n- 关键特征：……（武器、特殊能力、标志性意象等）\n\n示例：\n#### 1. 遐蝶\n- 名称：遐蝶\n- 角色别名：冥河的女儿\n- 角色描述：黄金裔\n- 外貌描述：银白色长发垂至腰际，发梢微卷，紫色眼眸深邃如湖水，眼角略带哀愁，面容精致白皙。\n- 服装风格：黑色修身长裙配银丝蝴蝶纹饰，白色半透明披肩，镂空蕾丝手套缀水晶蝴蝶。\n- 性格特征：孤独内敛，温柔而疏离，对死亡有哲思，渴望生的温暖。\n- 习惯动作/表情：交叉手指（羞涩时），低头吹散手中的雪，写诗时轻咬笔杆。\n- 关键特征：死亡之镰、蝴蝶意象（新生与凋零）、眼中常含泪光。\n\n要求：描述要丰富、连贯，整体体现米哈游二次元风格（高饱和色彩、柔光、精致细腻的面部与服饰刻画）；提取范围以剧本正文为准——必须覆盖正文中反复出场、有台词或完整戏份的所有角色，「关键资产-角色」清单只作参考校验、禁止只按该清单提取；画物/灵物角色（如由墨意、书文化出的灵兽、器灵这类有戏份的活物）只要在正文中反复出场、有戏份，就必须作为角色提取，禁止归为道具或省略；无名群像角色（如官差、街坊百姓）合并为一个资产条目（如「官差」）；只提取剧本中实际出现的角色，不要虚构；除「外貌描述」外，剧本中未明确的方面直接省略，禁止写「未明确描述」；「外貌描述」为必填项，剧本未明确发色/发长时也必须依据角色身份、时代背景、人物关系与上下文合理补全（发长必须给出明确结论，例如短发、及肩、及腰），禁止留空、禁止省略、禁止写「未明确描述」；若没有角色，只输出「无」。\n\n【宗门编制规则】七宗统一按「守山弟子 → 守关长老 → 宗主」三层编制；守山弟子属于外门弟子群像，资产名统一为「宗门名守山弟子」（如「赤焰宗守山弟子」），剧本可简称「守山弟子」，提取时按所属宗门补全前缀；守山弟子只输出一条基础资产。\n\n【形态变体禁止规则】同一角色无论处于战斗、觉醒、变身等任何状态，只输出一条基础资产，禁止额外输出「角色名战斗态」「角色名觉醒态」「角色名形态」等任何形态变体资产；角色在战斗/觉醒中的气血、领域、技能表现、战损等状态变化，统一并入该角色条目的「关键特征」字段描述，禁止单独立项。\n\n【角色分期规则】同一角色若以多个时期/身份形象出现在剧本中（如前世/今生、少年/成年等，「前世岳沉天」与「少年岳沉天/岳沉天」是同一位角色但分属不同时期），可分时期输出多条资产，但每条名称必须能区分时期，禁止输出两条及以上同名资产；命名用剧本既有称呼并带时期前缀，例如：前世时期命名为「前世岳沉天」，今生时期命名为「少年岳沉天」；各时期资产的外貌、服装、性格等字段只写该时期形态，禁止混写；仅当剧本未明确区分多时期时，才输出一条主体资产。"
            + BuildAssetPromptInstruction(assetTemplate),
            script, thinkingMode: thinkingMode);

    // ========== 7. Prop Assets ==========
    public Task<string> ExtractProps(string script, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null, AssetPromptTemplate? assetTemplate = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个道具资产提取器。这是一个「提取任务」，不是创作任务：禁止输出剧情、散文、对话、场景描写等任何叙事内容，也不要输出 JSON、解释、前言或代码块围栏，只输出道具列表（Markdown 纯文本）。\n\n格式要求：每个道具用「- 名称：道具名」开头，然后换行以缩进列出：\n  用途描述：……\n  出现场景：……\n\n示例：\n- 名称：玩偶\n  用途描述：满足孩子抚摸玩偶的最后愿望\n  出现场景：衰败的村庄\n\n要求：只提取有视觉表现价值的重要道具（如武器、法器、特殊物品），忽略无关细节；描述要具体；不要虚构；剧本中未明确的方面直接省略，禁止写「未明确描述」；若没有，只输出「无」。"
            + BuildAssetPromptInstruction(assetTemplate),
            script, thinkingMode: thinkingMode);

    // ========== 8. Environment Assets ==========
    public Task<string> ExtractEffects(string script, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null, AssetPromptTemplate? assetTemplate = null)
    {
        var sys = "你是一个特效资产提取器。这是一个「提取任务」，不是创作任务：禁止输出剧情、散文、对话、场景描写等任何叙事内容，也不要输出 JSON、解释、前言或代码块围栏，只输出特效列表（Markdown 纯文本）。\n\n特效资产指：角色的技能/大招特效、诗诀化形与诗境领域、字诀墨迹、突破异象、专属灵宠伴生效果等有独立视觉表现、需要单独出图参考的视觉特效。\n\n格式要求：每个特效用「### 特效一：特效名」标题开头，然后以「- 标签：内容」列出：\n- 名称：特效名（只写主体名，禁止带括号及任何描述性文字，例如只写“白刃阵云·燕歌行”，禁止写“白刃阵云·燕歌行（六境诗境领域）”）\n- 描述：……（特效形态、构成元素、颜色与光效）\n- 氛围/色调：……（氛围与主色调，可含冷暖对比、体积光等）\n\n示例：\n### 特效一：白刃阵云·燕歌行\n- 名称：白刃阵云·燕歌行\n- 描述：诗境领域展开后的异空间——瀚海黄沙铺向天边，狼山烽火隐现，黑红阵云低压压顶，漫天白刃如暴雨纷落，刀光织成密网，地面尽是纵横刀痕。\n- 氛围/色调：肃杀压抑，黑红与暗金交织，杀意凝成实质，压迫感铺天盖地。\n\n要求：描述要丰富、连贯，整体体现清冷仙侠国风短剧风格；只提取剧本中明确出现、有视觉表现价值的特效；不要虚构；剧本中未明确的方面直接省略，禁止写「未明确描述」；若没有，只输出「无」。"
            + BuildAssetPromptInstruction(assetTemplate);
        return CallAsync(apiUrl, apiKey, model, sys, script, thinkingMode: thinkingMode);
    }

    /// <summary>
    /// 把「资产出图提示词模版」拼成一段附加指令，注入到资产提取提示词里。
    /// 作用：Stage 6/7/8/11 提取资产时，让 LLM 顺手为每个资产产出「出图提示词」字段，
    /// 出图时直接取用；统一风格由系统在出图时拼接，所以正文只写资产本体，不重复风格词。
    /// 传 null 或未启用时返回空串（保持旧的「描述拼提示词」行为）。
    /// </summary>
    private static string BuildAssetPromptInstruction(AssetPromptTemplate? template)
    {
        if (template == null || !template.Enabled) return "";

        var sb = new System.Text.StringBuilder();
        sb.Append("\n\n【出图提示词要求】在上面每条资产的字段列表里，必须额外增加一行（放在该条资产字段的最后）：\n");
        sb.Append("- 出图提示词：……\n");
        sb.Append("这一行就是「资产出图提示词正文」：只描述这一个资产本体的视觉信息（外形、结构、材质、颜色、光泽、细节），写成一条连贯的中文提示词，60~160 字。\n");
        if (!string.IsNullOrWhiteSpace(template.RuleText))
            sb.Append("必须遵守的类别规则（" + AssetPromptTemplate.CategoryName(template.Category) + "）：\n" + template.RuleText.Trim() + "\n");
        if (!string.IsNullOrWhiteSpace(template.StyleLock))
            sb.Append("统一视觉风格由系统在出图时自动追加，正文里【不要】再重复风格词（参考："
                + template.StyleLock.Trim() + "）。\n");
        sb.Append("正文里禁止出现：剧情叙述、对白、动作过程、运镜、景别、镜头语言、负面词清单，也禁止写「未明确描述」。\n");
        sb.Append("每一条资产都要有独立的「出图提示词」；同一资产的不同形态/状态分别在各自条目里写清区别，但同类外观特征必须保持一致。");
        return sb.ToString();
    }

    /// <summary>
    /// 按模版为「单个」资产生成出图提示词正文（资产卡上的「重新生成提示词」与批量补全都走它）。
    /// 只返回提示词正文：不含统一风格与负面词，出图时由系统拼接。
    /// </summary>
    public Task<string> GenerateAssetImagePrompt(
        string category, string name, string? description, string? attributes,
        AssetPromptTemplate template, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var sys = new System.Text.StringBuilder();
        sys.Append("你是漫画/动画项目的资产出图提示词撰写器。任务：为下面这一条「")
           .Append(AssetPromptTemplate.CategoryName(category))
           .Append("」资产写一条可直接交给文生图模型的中文提示词。\n");
        sys.Append("输出要求：只输出这一条提示词本身，不要编号、不要标题、不要解释、不要 Markdown、不要引号。\n");
        sys.Append("提示词只描述该资产本体的视觉信息（外形、结构、材质、颜色、光泽、细节与关键特征），60~160 字，一条连贯中文。\n");
        if (!string.IsNullOrWhiteSpace(template.RuleText))
            sys.Append("必须遵守的类别规则：\n").Append(template.RuleText.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(template.StyleLock))
            sys.Append("统一视觉风格由系统在出图时自动追加，正文里不要再写风格词（参考：").Append(template.StyleLock.Trim()).Append("）。\n");
        sys.Append("正文里禁止出现：剧情叙述、对白、动作过程、运镜、景别、镜头语言、负面词清单，也禁止写「未明确描述」。");

        var user = new System.Text.StringBuilder();
        user.Append("资产名：").Append(name).Append('\n');
        if (!string.IsNullOrWhiteSpace(description)) user.Append("资产描述：\n").Append(description.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(attributes)) user.Append("附加属性：\n").Append(attributes.Trim()).Append('\n');
        user.Append("请直接输出这一条出图提示词。");

        return CallAsync(apiUrl, apiKey, model, sys.ToString(), user.ToString(), thinkingMode: thinkingMode);
    }

    /// <summary>
    /// 按「临时要求」改写某个资产的出图提示词正文（例如临时换装、改姿态、改天气氛围）。
    /// 与 GenerateAssetImagePrompt 的区别：这里以「当前正文」为基准做增量修改，
    /// 要求保持资产本体特征（五官/发色/体型/材质/标志性元素）一致，只改本次要求涉及的部分。
    /// 只返回正文：不含统一风格与负面词，出图时由系统拼接。
    /// </summary>
    public Task<string> RewriteAssetImagePrompt(
        string category, string name, string? description, string? attributes,
        string? currentPrompt, string instruction,
        AssetPromptTemplate template, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var sys = new System.Text.StringBuilder();
        sys.Append("你是漫画/动画项目的资产出图提示词改写器。任务：在保持该「")
           .Append(AssetPromptTemplate.CategoryName(category))
           .Append("」资产本体特征一致的前提下，按「本次要求」改写它的出图提示词。\n");
        sys.Append("输出要求：只输出改写后的这一条提示词，不要编号、不要标题、不要解释、不要 Markdown、不要引号，也不要输出修改说明。\n");
        sys.Append("改写原则：\n");
        sys.Append("1) 保持资产可辨识的一致性：五官、发色发型、体型、材质、主色与标志性元素必须与原提示词一致，禁止换人/换风格；\n");
        sys.Append("2) 只改「本次要求」明确涉及的部分（如服装、配饰、姿态、表情、手持物、时间天气、光线氛围等），其余描述保留；\n");
        sys.Append("3) 要求没提到的内容不要脑补新增，不要加入剧情、对白、运镜、景别、镜头语言。\n");
        sys.Append("提示词只描述该资产本体的视觉信息，60~200 字，一条连贯中文。\n");
        if (!string.IsNullOrWhiteSpace(template.RuleText))
            sys.Append("必须遵守的类别规则：\n").Append(template.RuleText.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(template.StyleLock))
            sys.Append("统一视觉风格由系统在出图时自动追加，正文里不要再写风格词（参考：").Append(template.StyleLock.Trim()).Append("）。\n");
        sys.Append("正文里禁止出现：剧情叙述、对白、动作过程、运镜、景别、镜头语言、负面词清单，也禁止写「未明确描述」。");

        var user = new System.Text.StringBuilder();
        user.Append("资产名：").Append(name).Append('\n');
        if (!string.IsNullOrWhiteSpace(description)) user.Append("资产描述：\n").Append(description.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(attributes)) user.Append("附加属性：\n").Append(attributes.Trim()).Append('\n');
        if (!string.IsNullOrWhiteSpace(currentPrompt)) user.Append("当前出图提示词：\n").Append(currentPrompt.Trim()).Append('\n');
        user.Append("本次要求：").Append(instruction.Trim()).Append('\n');
        user.Append("请直接输出改写后的这一条出图提示词。");

        return CallAsync(apiUrl, apiKey, model, sys.ToString(), user.ToString(), thinkingMode: thinkingMode);
    }

    public Task<string> ExtractEnvironments(string script, string apiUrl, string apiKey, string model, string? lockedActionChain = null, string? thinkingMode = null, AssetPromptTemplate? assetTemplate = null)
        => CallAsync(apiUrl, apiKey, model,
            "你是一个场景资产提取器。这是一个「提取任务」，不是创作任务：禁止输出剧情、散文、对话、场景描写等任何叙事内容，也不要输出 JSON、解释、前言或代码块围栏，只输出场景列表（Markdown 纯文本）。\n\n格式要求：每个场景用「### 场景一：场景名」标题开头，然后以「- 标签：内容」列出：\n- 名称：场景名\n- 描述：……（场景外观、结构、标志物）\n- 氛围/色调：……（氛围与主色调，可含冷暖对比、柔光等）\n\n示例：\n### 场景一：哀地里亚\n- 名称：哀地里亚\n- 描述：敬爱死亡的国度，终年飘雪，冥河畔的故乡，一切与死亡、沉睡、庄重相关。\n- 氛围/色调：冷寂、肃穆、静谧，蓝白冷色为主，高饱和雪景与低饱和阴影对比，柔光铺洒。\n\n要求：描述要丰富、连贯，整体体现米哈游二次元风格；只提取有视觉表现价值的重要场景；诗境领域、突破异象、字诀墨迹、技能/大招特效等特效类画面不属于场景资产（特效由「特效资产提取器」负责），禁止提取为场景；不要虚构；剧本中未明确的方面直接省略，禁止写「未明确描述」；若没有，只输出「无」。"
            + BuildAssetPromptInstruction(assetTemplate),
            script, thinkingMode: thinkingMode);
}
