using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using ManhuaPipeline.Services.Director;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Linq;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Controllers;

[ApiController]
[Route("api/project/{projectId}/stage")]
public class StageController : ControllerBase
{
    private static readonly ConcurrentDictionary<(int ProjectId, int StageNumber), byte> ProcessingStages = new();
    private static readonly ConcurrentDictionary<(int ProjectId, int StageNumber), StageProgressState> StageProgress = new();

    private readonly DbService _db;
    private readonly LLMService _llm;
    private readonly AgentService _agent;
    private readonly StoryboardPlanningService _storyboardPlanning;
    private readonly StoryboardAutoFixer _storyboardAutoFixer;
    private readonly DirectorService _director;
    private readonly ContinuityExtractionService _continuity;
    private readonly KeyframePlanService _keyframes;
    private readonly ILogger<StageController> _logger;

    public StageController(DbService db, LLMService llm, AgentService agent, StoryboardPlanningService storyboardPlanning, StoryboardAutoFixer autoFixer, DirectorService director, ContinuityExtractionService continuity, KeyframePlanService keyframes, ILogger<StageController> logger)
    {
        _db = db; _llm = llm; _agent = agent; _storyboardPlanning = storyboardPlanning; _storyboardAutoFixer = autoFixer; _director = director; _continuity = continuity; _keyframes = keyframes; _logger = logger;
    }

    private int GetUserId() => HttpContext.Session.GetInt32("UserId") ?? 0;

    private async Task<(string apiUrl, string apiKey, string model, string provider, string? thinkingMode)> GetLLMConfig()
    {
        var uid = GetUserId();
        var active = _db.GetActiveLLMProvider(uid);
        if (active != "qwen" && active != "gpt") active = "deepseek";

        // 优先使用开关指定的大模型；该家没配置时自动回退到其他已配置的
        var providers = new List<string> { active };
        foreach (var p in new[] { "deepseek", "qwen", "gpt" })
            if (!providers.Contains(p)) providers.Add(p);
        LLMConfig? config = null;
        var usedProvider = "";
        foreach (var p in providers)
        {
            config = _db.GetActiveConfig(uid, p);
            if (config != null) { usedProvider = p; break; }
        }
        if (config == null) throw new Exception("请先配置 DeepSeek、Qwen 或 Chat-GPT 的 API Key");

        var defaultUrls = new Dictionary<string, string>
        {
            ["deepseek"] = "https://api.deepseek.com/chat/completions",
            // 必须是 OpenAI 兼容模式地址：代码发出的请求体是 OpenAI 格式（messages/choices）。
            // 原生地址 .../api/v1/services/aigc/text-generation/generation 需要 input.messages + parameters
            // 结构，且被下面的补后缀逻辑拼成 .../generation/chat/completions 必然 404。
            ["qwen"] = "https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions",
            ["gpt"] = "https://api.openai.com/v1/chat/completions"
        };

        var url = config.ApiUrl;
        // 兼容 OpenAI 协议的 provider（deepseek/qwen/gpt）都必须以 /chat/completions 结尾。
        // qwen 的 DashScope 兼容模式地址常被写成 .../compatible-mode/v1（少了 /chat/completions），
        // 旧逻辑只给 deepseek/gpt 补后缀，qwen 会直接 POST 到 /compatible-mode/v1 得到 404，
        // 表现为阶段一开始就立刻失败（约 0.05 秒返回，容易被误判成"模型/超时"问题）。
        if (!string.IsNullOrEmpty(url) && usedProvider is "deepseek" or "qwen" or "gpt")
        {
            if (!url.Contains("/chat/completions"))
                url = url.TrimEnd('/') + "/chat/completions";
        }
        return (
            string.IsNullOrEmpty(url) ? defaultUrls.GetValueOrDefault(usedProvider, "") : url,
            config.ApiKey,
            string.IsNullOrEmpty(config.ModelName) ? (usedProvider == "deepseek" ? "deepseek-chat" : usedProvider == "gpt" ? "gpt-4o-mini" : "qwen-max") : config.ModelName,
            usedProvider,
            // 思考模式开关对 deepseek 与 qwen 都适用。qwen3 系列默认就开思考链，
            // 不透传该值等于让模型跑满推理预算，Stage 4 长任务会被 HttpClient 超时掐断。
            usedProvider is "deepseek" or "qwen" ? config.ThinkingMode : null
        );
    }

    [HttpGet("all")]
    public IActionResult GetAllStages(int projectId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        var proj = _db.GetProject(projectId, uid);
        if (proj == null) return NotFound(new { message = "项目不存在" });
        var stages = _db.GetAllStageData(projectId);
        var result = new List<object>();
        for (int i = 1; i <= 11; i++)
        {
            var s = stages.FirstOrDefault(x => x.StageNumber == i);
            result.Add(new {
                stageNumber = i,
                name = PipelineStage.Name(i),
                status = s?.Status ?? "pending",
                content = i == 4 ? SeedancePromptParser.NormalizeEpisodeMarkers(s?.Content) : s?.Content,
                llmResponse = i == 4 ? SeedancePromptParser.NormalizeEpisodeMarkers(s?.LlmResponse) : s?.LlmResponse
            });
        }
        return Ok(new { projectId, currentStage = proj.CurrentStage, stageOrder = PipelineStage.Order, stages = result });
    }

    /// <summary>返回某项目分集细化落库的「单元 ↔ 资产」绑定，供分集细化表格展示“引用资产”列。</summary>
    [HttpGet("unit-bindings")]
    public IActionResult GetUnitBindings(int projectId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        try
        {
            return Ok(new { bindings = _db.GetUnitAssetBindings(projectId) });
        }
        catch (Exception)
        {
            // UnitAssetBindings 表尚未创建等场景：给空列表，前端正常降级显示
            return Ok(new { bindings = new List<UnitAssetBinding>() });
        }
    }

    /// <summary>返回某项目分镜落库的「帧 ↔ 资产」绑定，供分镜表格逐镜头展示“引用资产”列。
    /// 每条绑定附带其所属帧的集/单元/镜头号，便于前端把“文本分镜表格行”也关联到绑定。</summary>
    [HttpGet("frame-bindings")]
    public IActionResult GetFrameBindings(int projectId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        try
        {
            var bindings = _db.GetFrameAssetBindings(projectId);
            var frames = _db.GetAllFrames(projectId);
            var byFrame = frames.ToDictionary(f => f.FrameId);
            var items = bindings.Select(b =>
            {
                byFrame.TryGetValue(b.FrameId, out var fr);
                return new
                {
                    frameId = b.FrameId,
                    episodeNumber = fr?.EpisodeNumber,
                    unitNumber = fr?.UnitNumber,
                    shotNumber = fr?.ShotNumber,
                    category = b.Category,
                    assetId = b.AssetId,
                    name = b.Name,
                    hasImage = b.HasImage
                };
            }).ToList();
            return Ok(new { bindings = items });
        }
        catch (Exception)
        {
            // FrameAssetBindings 表尚未创建等场景：给空列表，前端正常降级显示
            return Ok(new { bindings = new List<FrameAssetBinding>() });
        }
    }

    [HttpGet("{stageNumber}/progress")]
    public IActionResult GetStageProgress(int projectId, int stageNumber)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!PipelineStage.Order.Contains(stageNumber))
            return BadRequest(new { message = "无效的阶段编号" });
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        if (StageProgress.TryGetValue((projectId, stageNumber), out var sp))
        {
            return Ok(new
            {
                stageNumber,
                status = sp.Status,
                live = true,
                done = sp.Done,
                total = sp.Total,
                currentUnit = sp.CurrentUnit,
                currentPhase = sp.CurrentPhase,
                message = sp.Message,
                startedAt = sp.StartedAt,
                updatedAt = sp.UpdatedAt
                , history = _db.GetStageProgressLogs(projectId, stageNumber)
            });
        }

        var done = 0;
        var total = 0;
        var status = "pending";
        string? currentUnit = null;
        string? currentPhase = null;
        string? message = null;
        DateTime? startedAt = null;
        DateTime? updatedAt = null;
        if (stageNumber == 5)
        {
            var stage5 = _db.GetStageData(projectId, 5);
            status = stage5?.Status ?? "pending";
            var stage4 = _db.GetStageData(projectId, 4);
            total = StageUnitParser.Parse(stage4?.LlmResponse ?? stage4?.Content ?? "").Count;
            done = _db.CountFrameUnits(projectId);
            message = done > 0
                ? $"已写入 {done}/{total} 个单元"
                : (status == "processing" ? "正在初始化..." : "未开始");
            startedAt = stage5?.UpdatedAt;
            updatedAt = stage5?.UpdatedAt;
        }
        else if (stageNumber == 9)
        {
            var stage9 = _db.GetStageData(projectId, 9);
            status = stage9?.Status ?? "pending";
            done = _db.CountDistinctPromptUnits(projectId);
            var stage5 = _db.GetStageData(projectId, 5);
            var plan = stage5?.LlmResponse ?? stage5?.Content ?? "";
            total = Regex.Matches(plan, @"【单元[\d.]+[a-zA-Z]?】").Count;
            if (total == 0)
                total = Regex.Matches(plan, @"【第\d+集】").Count;
        }
        return Ok(new { stageNumber, status, done, total, currentUnit, currentPhase, message, startedAt, updatedAt, live = false, history = _db.GetStageProgressLogs(projectId, stageNumber) });
    }

    [HttpPost("{stageNumber}/process")]
    public async Task<IActionResult> ProcessStage(int projectId, int stageNumber)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!PipelineStage.Order.Contains(stageNumber))
            return BadRequest(new { message = "无效的阶段编号" });
        var proj = _db.GetProject(projectId, uid);
        if (proj == null) return NotFound(new { message = "项目不存在" });

        if (string.IsNullOrEmpty(proj.ScriptContent))
            return BadRequest(new { message = "请先生成剧本内容" });

        var processingKey = (projectId, stageNumber);
        if (!ProcessingStages.TryAdd(processingKey, 0))
            return Conflict(new { message = PipelineStage.Name(stageNumber) + "正在处理中，请勿重复提交" });

        StageProgressState? progress = null;

        try
        {
            var (apiUrl, apiKey, model, llmProvider, thinkingMode) = await GetLLMConfig();
            _logger.LogInformation("[Stage{StageNumber}] 使用大模型: {Provider} / {Model}", stageNumber, llmProvider, model);

            // 续写模式：在 SaveStageData 清空前先读取旧数据
            string? _oldStageContent = null;
            if (proj.CurrentBatch > 1 && (stageNumber == 1 || stageNumber == 2))
            {
                var _existing = _db.GetStageData(projectId, stageNumber);
                _oldStageContent = _existing?.LlmResponse ?? _existing?.Content;
            }

            _db.SaveStageData(projectId, stageNumber, null, null, "processing");
            // L1：阶段 1 重跑时先作废旧的结构化故事基线，避免新产出失败后阶段 2/3 复用陈旧内容
            if (stageNumber == 1)
                _db.SaveStageStructuredJson(projectId, 1, null);

            progress = new StageProgressState
            {
                ProjectId = projectId,
                StageNumber = stageNumber,
                Status = "processing",
                StartedAt = DateTime.Now,
                UpdatedAt = DateTime.Now,
                CurrentPhase = "准备",
                Message = PipelineStage.Name(stageNumber) + " 处理中"
            };
            StageProgress[processingKey] = progress;
            _db.ClearStageProgressLogs(projectId, stageNumber);
            AppendProgressLog(progress, "准备", null, PipelineStage.Name(stageNumber) + " 处理中");

            string? result = null;
            var stage5FixCount = 0;
            switch (stageNumber)
            {
                case 1:
                    // L1：阶段 1/2/3 合并为一次调用——先尝试一次产出结构化故事基线，再分别写入阶段 1/2/3。
                    // 续写模式（CurrentBatch > 1）语义是「只分析新增内容并追加到已有结果」，仍走原逐阶段路径。
                    if (proj.CurrentBatch <= 1)
                    {
                        var storyFoundation = await _llm.GenerateStoryFoundation(proj.ScriptContent, proj.EpisodeCount, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                        if (storyFoundation != null)
                        {
                            result = PersistStoryFoundation(projectId, uid, proj, storyFoundation, progress);
                            break;
                        }
                        AppendProgressLog(progress, "故事基线", null, "结构化产出解析失败，回退为「创意构思」单阶段调用");
                    }

                    // 续写模式：只分析新增内容，追加到已有结果
                    if (proj.CurrentBatch > 1 && !string.IsNullOrEmpty(proj.ScriptContent))
                    {
                        var marker = "### 以下为续写内容";
                        var idx = proj.ScriptContent.LastIndexOf(marker);
                        if (idx >= 0)
                        {
                            // 使用预读取的旧数据
                            var oldContent = _oldStageContent ?? "";
                            var newPart = proj.ScriptContent.Substring(idx);
                            var newResult = await _llm.AnalyzeScript(newPart, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                            result = oldContent + "\n\n---\n\n### 续写部分创意构思\n\n" + newResult;
                        }
                        else
                        {
                            result = await _llm.AnalyzeScript(proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                        }
                    }
                    else
                    {
                        result = await _llm.AnalyzeScript(proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                    }
                    break;
                case 2:
                    // L1：阶段 1 已产出结构化故事基线时直接复用渲染，不再单独调用大模型。
                    if (proj.CurrentBatch <= 1)
                    {
                        var reusedFoundation = StoryFoundationRenderer.TryLoad(_db.GetStageData(projectId, 1)?.StructuredJson);
                        if (reusedFoundation != null)
                        {
                            result = StoryFoundationRenderer.RenderStage2(reusedFoundation);
                            AppendProgressLog(progress, "故事分析", null, "复用阶段 1 的结构化故事基线（观众必须看懂的信息 + 情绪曲线），未重复调用大模型");
                            break;
                        }
                    }

                    // 续写模式：只分析新增内容，追加到已有结果
                    if (proj.CurrentBatch > 1 && !string.IsNullOrEmpty(proj.ScriptContent))
                    {
                        var marker = "### 以下为续写内容";
                        var idx = proj.ScriptContent.LastIndexOf(marker);
                        if (idx >= 0)
                        {
                            // 使用预读取的旧数据
                            var oldContent = _oldStageContent ?? "";
                            var newPart = proj.ScriptContent.Substring(idx);
                            var prev1 = _db.GetStageData(projectId, 1);
                            var analysisForNew = (prev1?.LlmResponse ?? "") + "\n\n---\n\n### 续写内容\n" + newPart;
                            var newResult = await _llm.AnalyzeStoryStructure(analysisForNew, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                            result = oldContent + "\n\n---\n\n### 续写部分故事分析\n\n" + newResult;
                        }
                        else
                        {
                            var prev1 = _db.GetStageData(projectId, 1);
                            result = await _llm.AnalyzeStoryStructure(prev1?.LlmResponse ?? proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                        }
                    }
                    else
                    {
                        var prev1 = _db.GetStageData(projectId, 1);
                        result = await _llm.AnalyzeStoryStructure(prev1?.LlmResponse ?? proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                    }
                    break;
                case 3:
                    // L1：同上——视觉锚点 + 分集大纲直接复用阶段 1 的结构化故事基线。
                    if (proj.CurrentBatch <= 1)
                    {
                        var reusedBlueprint = StoryFoundationRenderer.TryLoad(_db.GetStageData(projectId, 1)?.StructuredJson);
                        if (reusedBlueprint != null)
                        {
                            result = StoryFoundationRenderer.RenderStage3(reusedBlueprint);
                            SaveEpisodesFromBlueprint(projectId, result, uid, proj.CurrentBatch);
                            AppendProgressLog(progress, "全局蓝图", null, "复用阶段 1 的结构化故事基线（视觉锚点 + 分集大纲），未重复调用大模型");
                            break;
                        }
                    }

                    var prev2 = _db.GetStageData(projectId, 2);
                    result = await _llm.GenerateBlueprint(prev2?.LlmResponse ?? proj.ScriptContent, proj.EpisodeCount, 0, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                    SaveEpisodesFromBlueprint(projectId, result, uid, proj.CurrentBatch);
                    break;
                case 4:
                    var splitSkills = _db.GetSkills(uid, tags: proj.Tags);
                    var splitSkillNames = splitSkills.Count == 0 ? null : string.Join("、", splitSkills.Select(s => s.Name + (string.IsNullOrWhiteSpace(s.Element) ? "" : "（" + s.Element + "）")));
                    var stage4AssetCatalog = BuildAssetCatalogText(projectId);
                    // [已停用 2026-09-16] L2 连续性层：不再调用 LLM 抽取六类表，避免阶段 4 静默多一次大模型请求。
                    // 原实现（恢复时解除注释即可）：
                    // var stage4Continuity = (await _continuity.EnsureContinuityTablesAsync(
                    //         projectId, apiUrl, apiKey, model, assetCatalog: stage4AssetCatalog, thinkingMode: thinkingMode))
                    //     ? _continuity.BuildContinuityText(projectId)
                    //     : null;
                    string? stage4Continuity = null;
                    AppendProgressLog(progress, "连续性表", null, "已停用，按原逻辑拆分");
                    // 全片目标时长区间：显式设置优先（Stage4 界面的"目标总时长"），其次剧本头部"建议时长"自动识别
                    var durationBudget = DurationBudgetParser.TryParse(proj.TargetDurationText) ?? DurationBudgetParser.TryParse(proj.ScriptContent);
                    result = await _llm.SplitIntoUnits(proj.ScriptContent, proj.EpisodeCount, apiUrl, apiKey, model, skillList: splitSkillNames, thinkingMode: thinkingMode, assetCatalog: stage4AssetCatalog, totalDurationMinSeconds: durationBudget?.MinSeconds, totalDurationMaxSeconds: durationBudget?.MaxSeconds, totalDurationDisplay: durationBudget?.Display, continuityText: stage4Continuity);
                    result = SeedancePromptParser.NormalizeEpisodeMarkers(result);
                    string? repairedSplit = null;
                    try
                    {
                        repairedSplit = await _llm.RepairSplitCoverage(proj.ScriptContent, result, splitSkillNames, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                    }
                    catch (Exception) { /* 校验修复失败不影响主拆分结果 */ }
                    if (!string.IsNullOrWhiteSpace(repairedSplit) && StageUnitParser.Parse(repairedSplit).Count > StageUnitParser.Parse(result).Count)
                        result = SeedancePromptParser.NormalizeEpisodeMarkers(repairedSplit);

                    // 全片目标时长区间双向对齐：低于下限自动补足（升档/放细），超过上限自动压缩（只调时长档位，台词/技能/关键点逐字保留）
                    if (durationBudget.HasValue)
                    {
                        var budget = durationBudget.Value;
                        // 单点目标（如"200秒"）只约束上限，避免要求总时长"恰好等于"某值而无解
                        var enforceFloor = budget.MinSeconds < budget.MaxSeconds;
                        int Distance(int total) => total > budget.MaxSeconds ? total - budget.MaxSeconds
                            : enforceFloor && total < budget.MinSeconds ? budget.MinSeconds - total : 0;
                        for (var attempt = 1; attempt <= 3; attempt++)
                        {
                            var unitList = StageUnitParser.Parse(result);
                            var totalSecs = unitList.Sum(u => u.Duration);
                            var prevDev = unitList.Count == 0 ? 0 : Distance(totalSecs);
                            if (unitList.Count == 0 || prevDev == 0) break;
                            string? alignSplit = null;
                            if (totalSecs > budget.MaxSeconds)
                            {
                                AppendProgressLog(progress, "时长预算", null, $"单元总时长 {totalSecs} 秒超过上限 {budget.MaxSeconds} 秒，第 {attempt} 次自动压缩");
                                try
                                {
                                    alignSplit = await _llm.RepairSplitBudget(proj.ScriptContent, result, budget.MaxSeconds, budget.Display, splitSkillNames, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                                }
                                catch (Exception) { break; }
                            }
                            else
                            {
                                AppendProgressLog(progress, "时长预算", null, $"单元总时长 {totalSecs} 秒低于目标下限 {budget.MinSeconds} 秒，第 {attempt} 次自动补足时长");
                                try
                                {
                                    alignSplit = await _llm.RepairSplitFloor(proj.ScriptContent, result, budget.MinSeconds, budget.Display, splitSkillNames, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                                }
                                catch (Exception) { break; }
                            }
                            if (string.IsNullOrWhiteSpace(alignSplit)) break;
                            alignSplit = SeedancePromptParser.NormalizeEpisodeMarkers(alignSplit);
                            var alignUnits = StageUnitParser.Parse(alignSplit);
                            var alignSecs = alignUnits.Sum(u => u.Duration);
                            if (alignUnits.Count == 0) break;
                            if (Distance(alignSecs) >= prevDev) break; // 未向目标区间靠拢则放弃本轮，保留原结果
                            result = alignSplit;
                        }
                        var finalSecs = StageUnitParser.Parse(result).Sum(u => u.Duration);
                        string checkText;
                        if (finalSecs > budget.MaxSeconds)
                            checkText = $"单元总时长自检 {finalSecs} 秒仍超上限 {budget.MaxSeconds} 秒，请人工复核后重跑";
                        else if (enforceFloor && finalSecs < budget.MinSeconds)
                            checkText = $"单元总时长自检 {finalSecs} 秒仍低于目标下限 {budget.MinSeconds} 秒（目标 {budget.Display}），可能剧本内容量不足，请人工复核";
                        else
                            checkText = $"单元总时长自检 {finalSecs} 秒达标（目标 {budget.Display}）";
                        AppendProgressLog(progress, "时长预算", null, checkText);
                    }
                    RebindUnitsAfterStage4(projectId, result);
                    break;
                case 5:
                    var prev4 = _db.GetStageData(projectId, 4);
                    StoryboardPlanningLogger.StartRun(projectId, stageNumber);
                    var stage4Text = prev4?.LlmResponse ?? proj.ScriptContent;
                    // 进分镜前先用当前口径重算 Stage 4「单元↔资产」绑定（不重新生成 Stage 4 文本）：
                    // 每个镜头都要继承所属单元的绑定，单元绑定若停留在旧口径（如漏绑别名资产），
                    // 只靠镜头自身字段补不全，同一件东西就会在首尾两个镜头用上两套视觉来源。
                    RebindUnitsAfterStage4(projectId, stage4Text);
                    var storyboardSkills = _db.GetSkills(uid, tags: proj.Tags);
                    var storyboardTemplates = _db.GetFightTemplates(uid);
                    var storyboardAtoms = _db.GetCameraAtoms(uid);
                    var storyboardUnits = StageUnitParser.Parse(stage4Text);
                    _db.ClearEpisodeDirectorPlans(projectId);
                    _db.ClearEpisodeDirectorStates(projectId);
                    _db.ClearEpisodeUnitStateSnapshots(projectId);
                    _db.ClearDirectorPlans(projectId);
                    if (progress != null)
                    {
                        progress.Total = storyboardUnits.Count;
                        progress.CurrentPhase = "导演决策";
                        progress.Message = $"共 {storyboardUnits.Count} 个单元";
                    }
                    if (progress != null)
                    {
                        progress.CurrentPhase = "整集导演";
                        progress.Message = "正在生成整集导演计划";
                    }
                    await _director.EnsureEpisodeDirectorPlansAsync(
                        projectId, storyboardUnits, apiUrl, apiKey, model,
                        onProgress: update => UpdateStageProgress(progress!, update),
                        thinkingMode: thinkingMode);
                    if (progress != null)
                    {
                        progress.Total = storyboardUnits.Count;
                        progress.CurrentPhase = "导演决策";
                        progress.Message = $"共 {storyboardUnits.Count} 个单元";
                    }
                    await _director.EnsureDirectorPlansAsync(
                        projectId, uid, storyboardUnits, apiUrl, apiKey, model,
                        storyboardSkills, storyboardTemplates, storyboardAtoms,
                        onProgress: update => UpdateStageProgress(progress!, update),
                        thinkingMode: thinkingMode);
                    // 文戏/打斗分流：打斗单元走受控模板，文戏保持自由分镜
                    var characters = _db.GetCharacterAssets(projectId);
                    var envAssets = _db.GetEnvAssets(projectId);
                    var envText = envAssets.Count == 0
                        ? null
                        : string.Join("\n", envAssets.Select(a => "- " + a.Name + (string.IsNullOrWhiteSpace(a.Description) ? "" : "：" + a.Description)));
                    result = await _storyboardPlanning.PlanAsync(
                        projectId, stage4Text, apiUrl, apiKey, model,
                        storyboardAtoms, storyboardSkills, storyboardTemplates,
                        characters.Select(c => c.Name).ToList(),
                        characters,
                        envText: envText,
                        onProgress: update => UpdateStageProgress(progress!, update),
                        thinkingMode: thinkingMode,
                        onChunk: (chunk, unitNumber, episodeNumber, unitType, unitOrder) =>
                        {
                            var fixedChunk = _storyboardAutoFixer.Fix(chunk, stage4Text, storyboardSkills, out _);
                            SaveStoryboardFramesFromResult(projectId, fixedChunk, incremental: true, defaultUnitNumber: unitNumber, defaultEpisodeNumber: episodeNumber, defaultUnitType: unitType, unitOrder: unitOrder);
                        });
                    result = _storyboardAutoFixer.Fix(result, stage4Text, storyboardSkills, out var stage5Fixes);
                    stage5FixCount = stage5Fixes.Count;
                    _logger.LogInformation("[Stage5] 自动校验修正 {FixCount} 处，项目 {ProjectId}", stage5FixCount, projectId);
                    if (stage5FixCount > 0)
                        _logger.LogInformation("[Stage5] 修正明细：{Fixes}", string.Join("；", stage5Fixes));
                    StoryboardPlanningLogger.Append($"===== Stage5 自动校验修正 项目ID={projectId} 修正 {stage5FixCount} 处 =====\n");
                    if (stage5FixCount > 0)
                        StoryboardPlanningLogger.Append(string.Join("\n", stage5Fixes.Select(f => "- " + f)) + "\n");
                    // Stage 5 完成：按最终入库的分镜帧重算「帧 ↔ 资产」绑定，供 Stage 9 直接引用
                    RebindFramesAfterStage5(projectId);
                    // 字段完整性自检：缺「镜头时间轴」的镜头在 Stage 9 只能按时长均匀切分，节奏全丢，
                    // 这里显性报出来，避免像以前一样静默存成 NULL、到出片阶段才发现。
                    CheckMissingTimelines(projectId, progress);
                    break;
                case 6:
                    var charPromptTpl = _db.GetAssetPromptTemplate(uid, projectId, AssetPromptTemplate.CategoryCharacters);
                    result = await _llm.ExtractCharacters(proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode, assetTemplate: charPromptTpl);
                    ParseAndSaveCharacters(projectId, result);
                    break;
                case 7:
                    var propPromptTpl = _db.GetAssetPromptTemplate(uid, projectId, AssetPromptTemplate.CategoryProps);
                    result = await _llm.ExtractProps(proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode, assetTemplate: propPromptTpl);
                    ParseAndSaveProps(projectId, result);
                    break;
                case 8:
                    var envPromptTpl = _db.GetAssetPromptTemplate(uid, projectId, AssetPromptTemplate.CategoryEnvironments);
                    result = await _llm.ExtractEnvironments(proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode, assetTemplate: envPromptTpl);
                    ParseAndSaveEnvironments(projectId, result);
                    break;
                case 9:
                    StoryboardPlanningLogger.StartRun(projectId, stageNumber);
                    StoryboardPlanningLogger.Append($"===== Stage9 提示词生成 项目ID={projectId} 开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss} =====\n");
                    // 清空旧提示词，开始逐单元增量生成（AgentService 内已逐单元入库，此处不再全量重插）
                    _db.ClearSeedancePrompts(projectId);
                    // Stage 9 直接引用分镜资产：先按当前分镜帧把绑定表刷新到位（表缺失/异常由方法内部兜底，不影响生成）
                    RebindFramesAfterStage5(projectId);
                    // L3 关键帧自动前置：保证 Stage 9 一定带【关键帧锚定】，不再依赖人工先点「生成关键帧」。
                    // 已有关键帧跳过（不覆盖）；生成失败只降级为无锚定，不阻断 Stage 9。
                    await EnsureKeyframesBeforePromptGeneration(projectId, apiUrl, apiKey, model, thinkingMode);
                    if (progress != null)
                    {
                        var stage5ForProgress = _db.GetStageData(projectId, 5);
                        var planForProgress = stage5ForProgress?.LlmResponse ?? stage5ForProgress?.Content ?? "";
                        var promptUnitCount = Regex.Matches(planForProgress, @"【单元[\d.]+[a-zA-Z]?】").Count;
                        if (promptUnitCount == 0)
                            promptUnitCount = Regex.Matches(planForProgress, @"【第\d+集】").Count;
                        progress.Total = promptUnitCount;
                        progress.CurrentPhase = "提示词生成";
                        progress.Message = $"共 {promptUnitCount} 个单元";
                    }
                    result = await _agent.BuildPrompt(projectId, apiUrl, apiKey, model,
                        onProgress: update => UpdateStageProgress(progress!, update),
                        thinkingMode: thinkingMode);
                    break;
                case 10:
                    result = await _agent.CheckCoherence(projectId, apiUrl, apiKey, model, thinkingMode: thinkingMode);
                    _db.SaveCoherenceCheck(projectId, result, "completed");
                    break;
                case 11:
                    var fxPromptTpl = _db.GetAssetPromptTemplate(uid, projectId, AssetPromptTemplate.CategoryEffects);
                    result = await _llm.ExtractEffects(proj.ScriptContent, apiUrl, apiKey, model, thinkingMode: thinkingMode, assetTemplate: fxPromptTpl);
                    ParseAndSaveEffects(projectId, result);
                    break;
            }

            _db.SaveStageData(projectId, stageNumber, null, result, "completed");
            var completionMessage = PipelineStage.Name(stageNumber) + " 已完成";
            if (stageNumber == 5 && stage5FixCount > 0)
                completionMessage += $"（自动修正 {stage5FixCount} 处）";
            if (progress != null)
            {
                progress.Status = "completed";
                progress.Done = progress.Total > 0 ? progress.Total : progress.Done;
                progress.CurrentPhase = "已完成";
                progress.Message = completionMessage;
                progress.UpdatedAt = DateTime.Now;
                AppendProgressLog(progress, "已完成", null, completionMessage);
                if (stageNumber == 9)
                    StoryboardPlanningLogger.Append($"===== Stage9 完成 {DateTime.Now:HH:mm:ss} =====\n");
            }
            var nextStage = PipelineStage.Next(stageNumber);
            if (nextStage.HasValue)
                _db.UpdateProjectStage(projectId, uid, nextStage.Value);
            return Ok(new { result = result, stageNumber = stageNumber, message = completionMessage });
        }
        catch (Exception ex)
        {
            try
            {
                _db.SaveStageData(projectId, stageNumber, null, null, "failed");
            }
            catch (Exception statusEx)
            {
                _logger.LogError(statusEx, "Failed to persist failure status for stage {StageNumber}, project {ProjectId}", stageNumber, projectId);
            }
            var errorId = HttpContext.TraceIdentifier;
            _logger.LogError(ex, "Stage processing failed. ProjectId={ProjectId}, StageNumber={StageNumber}, UserId={UserId}, ErrorId={ErrorId}", projectId, stageNumber, GetUserId(), errorId);
            if (stageNumber == 9)
                StoryboardPlanningLogger.Append($"===== Stage9 失败 {DateTime.Now:HH:mm:ss} ErrorId={errorId} =====\n{ex}\n");
            if (progress != null)
            {
                progress.Status = "failed";
                progress.CurrentPhase = "失败";
                progress.Message = "处理失败，请稍后重试";
                progress.UpdatedAt = DateTime.Now;
                AppendProgressLog(progress, "失败", null, "处理失败，请稍后重试");
            }
            return StatusCode(500, new { message = "处理失败，请稍后重试", errorId });
        }
        finally
        {
            ProcessingStages.TryRemove(processingKey, out _);
        }
    }

    /// <summary>单分镜独立生成提示词（只处理该镜头，不影响整集 Stage 9 流程）。</summary>
    [HttpPost("prompt/frame/{frameId}")]
    public async Task<IActionResult> GenerateFramePrompt(int projectId, int frameId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        var frame = _db.GetFrameById(frameId);
        if (frame == null || frame.ProjectId != projectId) return NotFound(new { message = "分镜不存在" });

        try
        {
            var (apiUrl, apiKey, model, _, thinkingMode) = await GetLLMConfig();
            var (parsed, raw) = await _agent.GenerateFramePromptAsync(projectId, frameId, apiUrl, apiKey, model, thinkingMode);

            // 单镜头"原地覆盖"：保留该镜头已有行的 PromptId 与参考图/视频绑定，只刷新文本内容，避免 ID 漂移。
            var unitName = frame.UnitNumber?.Trim();
            var shotNo = string.IsNullOrWhiteSpace(frame.ShotNumber) ? frame.FrameNumber.ToString() : frame.ShotNumber;
            if (parsed.Count > 0)
            {
                _db.UpsertSeedancePromptsForShot(projectId, unitName, shotNo, parsed);
                return Ok(new { message = "提示词重新生成成功（已原地覆盖该镜头，PromptId 保持不变）", count = parsed.Count, raw });
            }
            // 生成结果为空：保留原提示词不动，避免误删已有内容
            return Ok(new { message = "提示词生成结果为空，已保留原有提示词", count = 0, raw });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateFramePrompt failed. ProjectId={ProjectId}, FrameId={FrameId}", projectId, frameId);
            return StatusCode(500, new { message = "提示词生成失败：" + ex.Message });
        }
    }

    // ========== L3 关键帧层（每剧情节点一张，人工触发）==========

    /// <summary>
    /// 生成关键帧方案（8-16 张/集，覆盖全集）：系统确定性选点 → 模型写构图与锁定项 → 整表覆盖落库。
    /// 原有语义：人工触发，未生成时阶段 9 行为与以前完全一致；生成后阶段 9 会按集注入【关键帧锚定】。
    /// 现改为：阶段 9 执行时自动前置（<see cref="EnsureKeyframesBeforeStage9"/>），本接口保留为「手动重生成/覆盖」入口。
    /// </summary>
    // [已停用 2026-09-16] L3 关键帧层整体停用：不再调用 LLM。
    // 前端入口（项目页左栏「关键帧」）已隐藏；原实现保留在 git 历史中，需要时取回。
    [HttpPost("keyframes/generate")]
    public IActionResult GenerateKeyframes(int projectId)
        => BadRequest(new { message = "关键帧功能已停用（不再调用 LLM）" });

    /// <summary>清空关键帧方案（重新生成前或不想让阶段 9 注入锚定时使用）。</summary>
    [HttpDelete("keyframes")]
    public IActionResult ClearKeyframes(int projectId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        _db.ClearKeyframes(projectId);
        return Ok(new { message = "关键帧方案已清空" });
    }

    /// <summary>
    /// 提示词生成（Stage 9 与 H3 批量两条线共用）前置：自动补齐 L3 关键帧方案，保证提示词一定带【关键帧锚定】。
    /// 判定口径：无关键帧 → 生成；关键帧已落后于分镜（Stage 5 重跑过 / 锚定的镜头已不存在）→ 整表覆盖重建；
    /// 否则沿用（不浪费一次全量 LLM 调用，也不冲掉人工确认过的关键帧）。
    /// 表不存在或生成失败只记日志，降级为无锚定（行为同未生成关键帧），绝不阻断 Stage 9。
    /// </summary>
    private async Task EnsureKeyframesBeforePromptGeneration(int projectId, string apiUrl, string apiKey, string model, string? thinkingMode)
    {
        // [已停用 2026-09-16] L3 关键帧层整体停用：阶段 9 不再自动前置生成关键帧，避免静默调用 LLM。
        // 恢复方法：解除下方注释块，并删除本行 return。
        await Task.CompletedTask;
        return;
        // try
        // {
        //     if (!_db.KeyframeTableExists())
        //     {
        //         _logger.LogWarning("[关键帧层] ProjectKeyframes 表不存在，Stage 9 降级为无【关键帧锚定】：请先执行 Database/Upgrade_关键帧层.sql");
        //         return;
        //     }
        //     var existing = _db.GetKeyframes(projectId);
        //     if (existing.Count > 0 && !KeyframesStale(projectId, existing)) return;
        //     if (existing.Count > 0)
        //         _logger.LogInformation("[关键帧层] 检测到分镜/分集细化已更新，提示词生成前自动重建关键帧 ProjectId={ProjectId} 待替换旧关键帧={Count}", projectId, existing.Count);
        //
        //     var (written, message) = await _keyframes.GenerateAsync(projectId, apiUrl, apiKey, model, thinkingMode);
        //     var line = $"[关键帧层] 前置生成结果：写入={written} {message}";
        //     _logger.LogInformation("[关键帧层] 自动前置生成完成 ProjectId={ProjectId} 写入={Count} {Message}", projectId, written, message);
        //     StoryboardPlanningLogger.Append(line + "\n");
        //     AppendKeyframeProgressLog(projectId, line);
        // }
        // catch (Exception ex)
        // {
        //     var detail = $"{ex.GetType().Name} {ex.Message}";
        //     var inner = ex.GetBaseException();
        //     if (inner != ex) detail += " | 内部异常：" + inner.GetType().Name + " " + inner.Message;
        //     _logger.LogWarning(ex, "[关键帧层] 自动前置失败，降级为无锚定 ProjectId={ProjectId}", projectId);
        //     StoryboardPlanningLogger.Append("[关键帧层] 前置生成异常：" + detail + "\n");
        //     AppendKeyframeProgressLog(projectId, "[关键帧层] 前置生成异常：" + detail);
        // }
    }

    /// <summary>把关键帧结果写进阶段 9 进度日志（页面「进度日志」可见，也方便事后查库定位）。</summary>
    private void AppendKeyframeProgressLog(int projectId, string text)
    {
        try { _db.AppendStageProgressLog(projectId, 9, $"{DateTime.Now:HH:mm:ss}  " + text); }
        catch { /* 日志写失败不影响主流程 */ }
    }

    /// <summary>关键帧是否已落后于当前分镜：分镜/分集细化重跑时间晚于关键帧生成时间，或关键帧锚定的镜头已不在分镜里。</summary>
    private bool KeyframesStale(int projectId, List<ProjectKeyframe> keyframes)
    {
        var newest = keyframes.Max(k => k.UpdatedAt > k.CreatedAt ? k.UpdatedAt : k.CreatedAt);

        // 关键帧完全由分镜帧推导（KeyframePlanService.SelectCandidates），所以分镜重跑即视为失效；
        // 分集细化（Stage 4）是分镜的上游：它重跑后单元拆分/时长/关键元素都变了，分镜迟早要跟着重跑，
        // 这里一并视为失效，避免「只重跑了分集细化就回来生成 H3」时继续沿用基于旧单元的关键帧锚点。
        foreach (var stageNumber in new[] { PipelineStage.ShotPlanning, PipelineStage.SceneSplit })
        {
            var stage = _db.GetStageData(projectId, stageNumber);
            if (stage != null && stage.UpdatedAt > newest) return true;
        }

        var frames = _db.GetAllFrames(projectId);
        if (frames.Count == 0) return false;
        var shotLabels = frames
            .Select(f => (f.ShotNumber ?? f.FrameNumber.ToString()).Trim())
            .Where(s => s.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        return keyframes.Any(k => !string.IsNullOrWhiteSpace(k.ShotLabel) && !shotLabels.Contains(k.ShotLabel.Trim()));
    }

    // ========== H3 提示词生成（独立于 SD 提示词，按需触发）==========

    private sealed class H3BatchProgress
    {
        public bool Running { get; set; } = true;
        public int Done { get; set; }
        public int Total { get; set; }
        public string Message { get; set; } = "";
        public string? Error { get; set; }
        /// <summary>本次批量失败的镜头（镜头号 + 原因）：页面提示与「只重跑失败镜头」都用它。</summary>
        public List<H3FailureInfo> Failures { get; set; } = new();
        /// <summary>失败镜头号清单，重跑时回灌。</summary>
        public List<string> FailedShots { get; set; } = new();
        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    private sealed record H3FailureInfo(string ShotLabel, string UnitName, string Reason);

    private static readonly ConcurrentDictionary<int, H3BatchProgress> H3BatchStates = new();

    /// <summary>单镜头生成 H3 提示词：优先从 Stage 5 分镜脚本按镜头直接生成，不依赖 SD 提示词；分镜缺失时退化为基于已有 SD 提示词改写。</summary>
    [HttpPost("prompt/h3/frame/{promptId}")]
    public async Task<IActionResult> GenerateFrameH3Prompt(int projectId, int promptId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        var p = _db.GetPrompt(promptId);
        if (p == null || p.ProjectId != projectId) return NotFound(new { message = "提示词不存在" });

        try
        {
            var (apiUrl, apiKey, model, _, thinkingMode) = await GetLLMConfig();
            var h3 = await _agent.GenerateH3PromptForFrameAsync(promptId, apiUrl, apiKey, model, thinkingMode);
            return Ok(new { message = "H3 提示词生成成功", promptId, h3Text = h3 });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateFrameH3Prompt failed. ProjectId={ProjectId}, PromptId={PromptId}", projectId, promptId);
            return StatusCode(500, new { message = "H3 提示词生成失败：" + ex.Message });
        }
    }

    /// <summary>整项目批量生成 H3 提示词（异步执行）：遍历 Stage 5 分镜脚本中的镜头，逐个按分镜原文直接生成 H3 格式。overwrite=true 时全部重新生成，否则只补齐缺失的。</summary>
    [HttpPost("prompt/h3/project")]
    public Task<IActionResult> GenerateProjectH3Prompts(int projectId, [FromQuery] bool overwrite = false)
        => StartH3BatchAsync(projectId, overwrite, onlyShotLabels: null);

    /// <summary>
    /// 只重跑上一次批量生成失败的镜头（失败清单来自上一次批量的内存状态）；
    /// 应用重启后内存状态会丢，此时退化为「补齐缺失镜头」，结果一致（失败镜头本来就没落库）。
    /// </summary>
    [HttpPost("prompt/h3/retry-failed")]
    public Task<IActionResult> RetryFailedH3Prompts(int projectId)
    {
        var failedShots = (H3BatchStates.TryGetValue(projectId, out var prev) && prev.FailedShots.Count > 0)
            ? prev.FailedShots.ToList()
            : null;
        return StartH3BatchAsync(projectId, overwrite: false, onlyShotLabels: failedShots);
    }

    /// <summary>H3 批量生成（补齐 / 覆盖 / 定向重跑失败镜头三条入口共用）。</summary>
    private async Task<IActionResult> StartH3BatchAsync(int projectId, bool overwrite, IReadOnlyCollection<string>? onlyShotLabels)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });

        if (H3BatchStates.TryGetValue(projectId, out var cur) && cur.Running)
            return Conflict(new { message = "该项目 H3 提示词正在生成中，请稍候" });

        var onlySet = onlyShotLabels == null || onlyShotLabels.Count == 0
            ? null
            : new HashSet<string>(onlyShotLabels, StringComparer.OrdinalIgnoreCase);

        // 在请求线程内解析 LLM 配置与目标清单（后台任务线程无法访问 HttpContext/Session）
        var (apiUrl, apiKey, model, _, thinkingMode) = await GetLLMConfig();

        // H3 独立生成线：直接从 Stage 5 分镜脚本按镜头拆解，不依赖 SD 提示词
        var stage5 = _db.GetStageData(projectId, 5);
        var shotPlan = stage5?.LlmResponse ?? stage5?.Content;
        if (string.IsNullOrWhiteSpace(shotPlan))
            return BadRequest(new { message = "项目还没有分镜脚本，请先完成 Stage 5 生成分镜脚本后再生成 H3 提示词" });
        var shots = AgentService.ParseShotsFromShotPlan(shotPlan);
        var existing = _db.GetPrompts(projectId);
        var total = shots.Count(s =>
        {
            // 定向重跑：目标就是清单里那些上一次失败的镜头（强制重生成）
            if (onlySet != null) return onlySet.Contains(s.ShotLabel);
            if (!overwrite)
            {
                var row = existing.FirstOrDefault(e =>
                    e.EpisodeNumber == s.EpisodeNumber &&
                    string.Equals(e.UnitName?.Trim(), s.UnitName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(e.ShotLabel?.Trim(), s.ShotLabel, StringComparison.OrdinalIgnoreCase));
                if (row != null && !string.IsNullOrWhiteSpace(row.PromptTextH3)) return false;
            }
            return true;
        });

        // 没有任何待生成镜头时，直接给出明确提示，不启动后台任务
        if (total == 0)
        {
            var noneState = new H3BatchProgress();
            noneState.Running = false;
            noneState.Total = 0;
            noneState.Message = onlySet != null
                ? "没有需要重跑的失败镜头（失败清单可能已随应用重启清空）"
                : "该项目镜头已全部生成 H3 提示词（如需重新生成请开启覆盖模式）";
            H3BatchStates[projectId] = noneState;
            return Ok(new { message = noneState.Message, running = false });
        }

        var state = new H3BatchProgress();
        H3BatchStates[projectId] = state;
        lock (state) { state.Total = total; state.Message = $"共 {total} 个镜头待生成"; state.UpdatedAt = DateTime.Now; }
        _ = Task.Run(async () =>
        {
            try
            {
                // H3 与 Stage 9 是两条独立的提示词生成线，但共用同一套分镜资产绑定与关键帧锚定：
                // 分镜重跑后必须按当前口径刷新绑定，并补/重建关键帧，否则 H3 提示词会拿到旧绑定、且完全没有关键帧锚定。
                // 放在后台任务开头：「生成 H3 提示词」「覆盖重新生成 H3」「只重跑失败镜头」三个入口共用，
                // 行为一致 —— 任一入口点下去都会先保证关键帧锚定到位。
                RebindFramesAfterStage5(projectId);
                lock (state) { state.Message = "正在生成/校验 L3 关键帧锚定…"; state.UpdatedAt = DateTime.Now; }
                await EnsureKeyframesBeforePromptGeneration(projectId, apiUrl, apiKey, model, thinkingMode);
                lock (state) { state.Message = $"共 {total} 个镜头待生成"; state.UpdatedAt = DateTime.Now; }

                // 覆盖模式：删除项目全部旧提示词记录，再逐镜头重新生成（全部走插入）
                if (overwrite) _db.DeleteProjectPrompts(projectId);
                var result = await _agent.GenerateH3PromptsForProjectAsync(projectId, shotPlan, apiUrl, apiKey, model, thinkingMode, overwrite,
                    onProgress: (d, t) => { lock (state) { state.Done = d; state.Total = t; state.UpdatedAt = DateTime.Now; } },
                    onlyShotLabels: onlyShotLabels);
                lock (state)
                {
                    state.Done = result.Done; state.Total = total; state.Running = false;
                    state.Failures = result.Failures.Select(f => new H3FailureInfo(f.ShotLabel, f.UnitName, f.Reason)).ToList();
                    state.FailedShots = result.FailedShotLabels;
                    if (result.Failed == 0) state.Message = $"生成完成：成功 {result.Done}/{total} 条";
                    else
                    {
                        // 把失败镜头和原因直接写进提示文案（原来只有「首个原因」，且一晃而过，事后无从追溯）
                        var detail = string.Join("；", result.Failures.Take(3).Select(f => $"{f.ShotLabel}（{ShortenReason(f.Reason)}）"));
                        if (result.Failures.Count > 3) detail += $"；等共 {result.Failures.Count} 个";
                        state.Message = (result.Done == 0 ? "生成失败：" : $"生成完成：成功 {result.Done}/{total} 条，") +
                                        $"失败 {result.Failed} 条 —— {detail}";
                    }
                    state.UpdatedAt = DateTime.Now;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GenerateProjectH3Prompts failed. ProjectId={ProjectId}", projectId);
                lock (state)
                {
                    state.Running = false; state.Error = ex.Message;
                    state.Message = "生成失败：" + ex.Message; state.UpdatedAt = DateTime.Now;
                }
            }
        });
        return Ok(new { message = $"开始生成 H3 提示词，共 {total} 个镜头", running = true });
    }

    /// <summary>截断失败原因，用于页面提示（完整原因已写进 Stage 9 进度日志）。</summary>
    private static string ShortenReason(string reason, int max = 80)
        => string.IsNullOrEmpty(reason) ? "未知原因" : (reason.Length > max ? reason.Substring(0, max) + "…" : reason);

    /// <summary>查询整项目 H3 提示词批量生成进度（含上一次的失败镜头清单）。</summary>
    [HttpGet("prompt/h3/progress")]
    public IActionResult GetH3PromptProgress(int projectId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        if (H3BatchStates.TryGetValue(projectId, out var sp))
            return Ok(new
            {
                running = sp.Running,
                done = sp.Done,
                total = sp.Total,
                message = sp.Message,
                error = sp.Error,
                failed = sp.FailedShots.Count,
                failedShots = sp.FailedShots,
                failures = sp.Failures.Select(f => new { shotLabel = f.ShotLabel, unitName = f.UnitName, reason = f.Reason }),
                startedAt = sp.StartedAt,
                updatedAt = sp.UpdatedAt
            });
        return Ok(new { running = false, done = 0, total = 0, message = "尚未生成过 H3 提示词", failed = 0, failedShots = Array.Empty<string>() });
    }

    private void UpdateStageProgress(StageProgressState state, StageProgressUpdate update)
    {
        lock (state)
        {
            state.Done = update.Done;
            state.Total = update.Total;
            state.CurrentUnit = update.CurrentUnit;
            state.CurrentPhase = update.CurrentPhase;
            state.Message = update.Message;
            state.UpdatedAt = DateTime.Now;
            AppendProgressLog(state, update.CurrentPhase, update.CurrentUnit, update.Message);
            if (state.StageNumber == 9)
                StoryboardPlanningLogger.Append($"{DateTime.Now:HH:mm:ss} [{update.CurrentPhase}] {(string.IsNullOrEmpty(update.CurrentUnit) ? "" : update.CurrentUnit + " ")}{update.Message}\n");
        }
    }

    private void AppendProgressLog(StageProgressState state, string? phase, string? unit, string? message)
    {
        lock (state)
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(phase)) parts.Add(phase);
            if (!string.IsNullOrWhiteSpace(unit)) parts.Add("单元" + unit);
            if (!string.IsNullOrWhiteSpace(message)) parts.Add(message);
            var line = string.Join(" · ", parts);
            if (string.IsNullOrWhiteSpace(line)) return;
            var last = state.ProgressLog.Count == 0 ? null : state.ProgressLog[^1];
            if (last != null && last.EndsWith(line, StringComparison.Ordinal)) return;
            state.ProgressLog.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + line);
            if (state.ProgressLog.Count > 200)
                state.ProgressLog.RemoveRange(0, state.ProgressLog.Count - 200);
            try
            {
                _db.AppendStageProgressLog(state.ProjectId, state.StageNumber, state.ProgressLog[^1]);
            }
            catch (Exception dbEx)
            {
                _logger.LogWarning(dbEx, "Failed to persist stage progress log. ProjectId={ProjectId}, StageNumber={StageNumber}", state.ProjectId, state.StageNumber);
            }
        }
    }

    [HttpPost("{stageNumber}/save")]
    public IActionResult SaveStage(int projectId, int stageNumber, [FromBody] SaveStageRequest req)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!PipelineStage.Order.Contains(stageNumber))
            return BadRequest(new { message = "无效的阶段编号" });
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        var processingKey = (projectId, stageNumber);
        if (!ProcessingStages.TryAdd(processingKey, 0))
            return Conflict(new { message = PipelineStage.Name(stageNumber) + "正在处理中，暂时不能手动保存" });
        try
        {
            _db.SaveStageData(projectId, stageNumber, null, req.Content, "completed");
            // L1：人工改写阶段 1 后，结构化故事基线与文本产物不再一致，作废它让阶段 2/3 走逐阶段调用
            if (stageNumber == 1)
                _db.SaveStageStructuredJson(projectId, 1, null);
            // 手动保存分集细化 / 分镜文本时同步重算绑定，保证下游直接引用的是最新内容对应的资产
            if (stageNumber == PipelineStage.SceneSplit)
                RebindUnitsAfterStage4(projectId, req.Content ?? "");
            else if (stageNumber == PipelineStage.ShotPlanning)
                RebindFramesAfterStage5(projectId);
            var nextStage = PipelineStage.Next(stageNumber);
            if (nextStage.HasValue)
                _db.UpdateProjectStage(projectId, uid, nextStage.Value);
            return Ok(new { message = "保存成功" });
        }
        finally
        {
            ProcessingStages.TryRemove(processingKey, out _);
        }
    }

    /// <summary>
    /// 重算全项目「分镜帧 ↔ 项目资产」绑定并落库，返回资产/分镜引用缺口报告。
    /// 纯诊断 + 落库动作，不触发任何 LLM，不改分镜与提示词。
    /// </summary>
    [HttpPost("asset-bindings/rebuild")]
    public IActionResult RebuildFrameAssetBindings(int projectId)
    {
        var uid = GetUserId();
        if (uid == 0) return Unauthorized();
        if (!_db.ProjectBelongsToUser(projectId, uid)) return NotFound(new { message = "项目不存在" });
        try
        {
            var frames = _db.GetAllFrames(projectId);
            if (frames.Count == 0)
                return Ok(new { totalFrames = 0, totalBindings = 0, message = "项目还没有分镜帧，无需解析绑定" });

            var characters = _db.GetCharacterAssets(projectId);
            var environments = _db.GetEnvAssets(projectId);
            var props = _db.GetPropAssets(projectId);
            var effects = _db.GetEffectAssets(projectId);
            var unitBindingsByUnit = LoadUnitBindingsByUnit(projectId);

            var all = new List<FrameAssetBinding>();
            var issues = new List<string>();
            var categoryCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Character"] = 0, ["Environment"] = 0, ["Prop"] = 0, ["Effect"] = 0
            };
            var missingImageAssets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var frame in frames)
            {
                var r = FrameAssetBindingResolver.Resolve(frame, characters, environments, props, effects, UnitBindingsForFrame(unitBindingsByUnit, frame));
                foreach (var b in r.Bindings)
                {
                    all.Add(b);
                    if (categoryCount.ContainsKey(b.Category)) categoryCount[b.Category]++;
                    if (!b.HasImage) missingImageAssets.Add($"[{b.Category}] {b.Name}");
                }
                issues.AddRange(r.Issues);
            }

            _db.ReplaceFrameAssetBindings(projectId, all);

            const int issueCap = 120;
            var boundImages = all.Count(b => b.HasImage);
            return Ok(new
            {
                totalFrames = frames.Count,
                totalBindings = all.Count,
                boundImages,
                byCategory = categoryCount,
                missingImageAssets = missingImageAssets.ToList(),
                issueCount = issues.Count,
                issues = issues.Take(issueCap).ToList(),
                truncated = issues.Count > issueCap,
                message = $"已为 {frames.Count} 个分镜帧重算绑定：共 {all.Count} 条资产引用（有图 {boundImages}）。发现 {issues.Count} 条问题。"
            });
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            return BadRequest(new { message = "FrameAssetBindings 表不存在，请先执行 ManhuaPipeline/Database/Upgrade_FrameAssetBindings.sql 后再试。" });
        }
    }

    // ========== L1 故事基线落库（阶段 1/2/3 一次调用产出） ==========

    /// <summary>
    /// L1：把一次调用产出的「节 1-5」故事基线渲染后分别写入阶段 1/2/3，并把结构化 JSON 落在阶段 1 行上，
    /// 供阶段 2/3 复用渲染（不再重复调用大模型）；同时按阶段 3 文本落库分集列表。
    /// 返回值即阶段 1 的产物文本，调用方仍走统一的 SaveStageData / 返回逻辑。
    /// </summary>
    private string PersistStoryFoundation(int projectId, int userId, Project proj, StoryFoundation foundation, StageProgressState? progress)
    {
        var stage1Text = StoryFoundationRenderer.RenderStage1(foundation);
        var stage2Text = StoryFoundationRenderer.RenderStage2(foundation);
        var stage3Text = StoryFoundationRenderer.RenderStage3(foundation);

        _db.SaveStageData(projectId, 1, null, stage1Text, "completed");
        _db.SaveStageData(projectId, 2, null, stage2Text, "completed");
        _db.SaveStageData(projectId, 3, null, stage3Text, "completed");
        _db.SaveStageStructuredJson(projectId, 1, StoryFoundationRenderer.Serialize(foundation));
        SaveEpisodesFromBlueprint(projectId, stage3Text, userId, proj.CurrentBatch);

        if (progress != null)
        {
            progress.CurrentPhase = "故事基线";
            AppendProgressLog(progress, "故事基线", null,
                "一次调用产出「故事核心 / 主线因果链 / 观众必须看懂的信息 / 情绪曲线 / 视觉锚点 / 分集大纲」，已分别写入阶段 1、2、3（未重复调用大模型）");
        }
        return stage1Text;
    }

    // ========== Parse episodes from LLM blueprint output ==========

    private void SaveEpisodesFromBlueprint(int projectId, string text, int userId, int batchNumber)
    {
        var episodes = BlueprintEpisodeParser.Parse(text);
        if (episodes.Count == 0) return;

        foreach (var episode in episodes)
        {
            episode.ProjectId = projectId;
            episode.UserId = userId;
            episode.BatchNumber = batchNumber;
            episode.SortOrder = episode.EpisodeNumber;
        }

        _db.SaveEpisodes(projectId, userId, episodes);
    }

    // ========== 资产绑定自动落库（P2/P3：分集细化 & 分镜完成后整体重算） ==========

    /// <summary>把当前四类资产拼成规范名目录，注入 Stage 4 分集细化 prompt（让「地点/关键元素」沿用资产规范名）。</summary>
    private string? BuildAssetCatalogText(int projectId)
    {
        try
        {
            var parts = new List<string>();
            void Append(string header, IEnumerable<string> names)
            {
                var list = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct().Take(120).ToList();
                if (list.Count > 0) parts.Add(header + "：" + string.Join("；", list));
            }
            Append("人物", _db.GetCharacterAssets(projectId).Select(c => c.Name));
            Append("环境", _db.GetEnvAssets(projectId).Select(e => e.Name));
            Append("道具", _db.GetPropAssets(projectId).Select(p => p.Name));
            Append("特效", _db.GetEffectAssets(projectId).Select(f => f.Name));
            return parts.Count == 0 ? null : string.Join("\n", parts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "组装 Stage4 资产目录失败，项目 {ProjectId}，将继续按原逻辑拆分", projectId);
            return null;
        }
    }

    /// <summary>Stage 5 完成后检查哪些镜头没有「镜头时间轴」：缺了它 Stage 9 只能按时长均匀切分，节奏信息全丢。</summary>
    private void CheckMissingTimelines(int projectId, StageProgressState? progress)
    {
        try
        {
            var missing = _db.GetAllFrames(projectId)
                .Where(f => string.IsNullOrWhiteSpace(f.Timeline))
                .Select(f => f.ShotNumber ?? f.UnitNumber)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Distinct()
                .ToList();
            if (missing.Count == 0) return;
            var text = $"{missing.Count} 个镜头缺少「镜头时间轴」，Stage 9 将按时长均匀切分（节奏信息丢失）：{string.Join("、", missing.Take(20))}";
            _logger.LogWarning("[Stage5字段自检] 项目 {ProjectId}：{Text}", projectId, text);
            StoryboardPlanningLogger.Append($"[Stage5字段自检] {text}\n");
            if (progress != null) AppendProgressLog(progress, "字段自检", null, text);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stage5字段自检] 时间轴完整性检查失败，不影响分镜结果。项目 {ProjectId}", projectId);
        }
    }

    /// <summary>Stage 4 完成（或手动保存分集细化）后：解析每个【单元X.Y】，把单元↔资产绑定整体重算写入 UnitAssetBindings。</summary>
    private void RebindUnitsAfterStage4(int projectId, string stage4Text)
    {
        if (string.IsNullOrWhiteSpace(stage4Text)) return;
        try
        {
            var units = StageUnitParser.Parse(stage4Text);
            if (units.Count == 0) return;
            var characters = _db.GetCharacterAssets(projectId);
            var environments = _db.GetEnvAssets(projectId);
            var props = _db.GetPropAssets(projectId);
            var effects = _db.GetEffectAssets(projectId);
            var all = new List<UnitAssetBinding>();
            var issues = new List<string>();
            foreach (var unit in units)
            {
                var unitBindings = UnitAssetBindingResolver.Resolve(projectId, unit, characters, environments, props, effects, out var unitIssues);
                all.AddRange(unitBindings);
                issues.AddRange(unitIssues);
            }
            _db.ReplaceUnitAssetBindings(projectId, all);
            var catCount = string.Join(", ", all.GroupBy(b => b.Category).Select(g => $"{g.Key}={g.Count()}"));
            _logger.LogInformation("[Stage4绑定] 项目 {ProjectId} 已为 {UnitCount} 个单元重算资产绑定：共 {BindingCount} 条（有图 {ImageCount}）[{Category}]",
                projectId, units.Count, all.Count, all.Count(b => b.HasImage), catCount);
            if (issues.Count > 0)
            {
                _logger.LogWarning("[Stage4绑定] 项目 {ProjectId} 有 {IssueCount} 处角色引用待确认：{Issues}",
                    projectId, issues.Count, string.Join(" | ", issues.Take(20)));
            }
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            _logger.LogWarning(ex, "[Stage4绑定] UnitAssetBindings 表不存在，已跳过自动绑定。请先执行 ManhuaPipeline/Database/Upgrade_UnitAssetBindings.sql。项目 {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stage4绑定] 重算单元资产绑定失败，不影响分集细化结果。项目 {ProjectId}", projectId);
        }
    }

    /// <summary>Stage 5 完成（或手动保存分镜）后：按最终入库的分镜帧整体重算「帧↔资产」绑定。
    /// 每个镜头先继承其所属单元的 Stage 4 绑定（单元引用几个资产，该单元拆出的每个镜头就引用这几个），
    /// 再由本镜自身字段补充单元绑定未覆盖的资产。</summary>
    private void RebindFramesAfterStage5(int projectId)
    {
        try
        {
            var frames = _db.GetAllFrames(projectId);
            if (frames.Count == 0) return;
            // 先自愈历史错位编号（镜头号前缀 ≠ 单元号），再重算绑定，避免按错位编号去匹配单元资产
            var normalized = 0;
            try { normalized = _db.NormalizeFrameShotNumbers(projectId); }
            catch (Exception ex) { _logger.LogWarning(ex, "[Stage5绑定] 镜头号规范化失败，按原编号继续。项目 {ProjectId}", projectId); }
            if (normalized > 0)
            {
                _logger.LogWarning("[Stage5绑定] 项目 {ProjectId} 修正了 {Count} 条镜头号与单元号不一致的分镜帧", projectId, normalized);
                frames = _db.GetAllFrames(projectId);
            }
            var characters = _db.GetCharacterAssets(projectId);
            var environments = _db.GetEnvAssets(projectId);
            var props = _db.GetPropAssets(projectId);
            var effects = _db.GetEffectAssets(projectId);
            var unitBindingsByUnit = LoadUnitBindingsByUnit(projectId);
            var all = new List<FrameAssetBinding>();
            foreach (var frame in frames)
                all.AddRange(FrameAssetBindingResolver.Resolve(frame, characters, environments, props, effects, UnitBindingsForFrame(unitBindingsByUnit, frame)).Bindings);
            _db.ReplaceFrameAssetBindings(projectId, all);
            var catCount = string.Join(", ", all.GroupBy(b => b.Category).Select(g => $"{g.Key}={g.Count()}"));
            _logger.LogInformation("[Stage5绑定] 项目 {ProjectId} 已按 {FrameCount} 个分镜帧重算资产绑定：共 {BindingCount} 条（有图 {ImageCount}）[{Category}]，继承 {UnitCount} 个单元的绑定",
                projectId, frames.Count, all.Count, all.Count(b => b.HasImage), catCount, unitBindingsByUnit.Count);
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            _logger.LogWarning(ex, "[Stage5绑定] FrameAssetBindings 表不存在，已跳过自动绑定。请先执行 ManhuaPipeline/Database/Upgrade_FrameAssetBindings.sql。项目 {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stage5绑定] 重算帧资产绑定失败，不影响分镜结果。项目 {ProjectId}", projectId);
        }
    }

    /// <summary>按单元号索引 Stage 4 落库的「单元↔资产」绑定（单元号形如 1.3，已含集号），供 Stage 5 逐镜头继承。
    /// 表缺失或读取失败时返回空字典，帧绑定自动退回「只按本镜字段解析」的旧行为。</summary>
    private Dictionary<string, List<UnitAssetBinding>> LoadUnitBindingsByUnit(int projectId)
    {
        var map = new Dictionary<string, List<UnitAssetBinding>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var b in _db.GetUnitAssetBindings(projectId))
            {
                var key = (b.UnitNumber ?? "").Trim();
                if (key.Length == 0) continue;
                if (!map.TryGetValue(key, out var list)) { list = new List<UnitAssetBinding>(); map[key] = list; }
                list.Add(b);
            }
            foreach (var list in map.Values)
                list.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        }
        catch (SqlException ex) when (ex.Number is 208 or 2812)
        {
            _logger.LogWarning(ex, "[Stage5绑定] UnitAssetBindings 表不存在，镜头绑定不继承单元资产。请先执行 ManhuaPipeline/Database/Upgrade_UnitAssetBindings.sql。项目 {ProjectId}", projectId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Stage5绑定] 读取单元资产绑定失败，镜头绑定不继承单元资产。项目 {ProjectId}", projectId);
        }
        return map;
    }

    /// <summary>取某镜头所属单元的 Stage 4 绑定；单元号缺失或单元未绑定时返回 null（退回旧行为）。</summary>
    private static IReadOnlyList<UnitAssetBinding>? UnitBindingsForFrame(
        IReadOnlyDictionary<string, List<UnitAssetBinding>> unitBindingsByUnit, StoryboardFrame frame)
    {
        var key = (frame.UnitNumber ?? "").Trim();
        return key.Length > 0 && unitBindingsByUnit.TryGetValue(key, out var list) ? list : null;
    }

    // ========== Parse storyboard frames from LLM shot-plan output ==========
    private void SaveStoryboardFramesFromResult(
        int projectId,
        string text,
        bool incremental = false,
        string? defaultUnitNumber = null,
        int defaultEpisodeNumber = 0,
        string? defaultUnitType = null,
        int unitOrder = 0)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = SeedancePromptParser.NormalizeEpisodeNumbersOnly(text);
        var episodes = _db.GetEpisodes(projectId);
        if (episodes.Count == 0) return;
        var episodeMap = episodes.ToDictionary(e => e.EpisodeNumber, e => e.EpisodeId);
        var episodeDirectorPlans = _db.GetEpisodeDirectorPlans(projectId);

        var lines = text.Split('\n');
        int currentEpisodeNum = defaultEpisodeNumber;
        string? currentUnitNumber = string.IsNullOrWhiteSpace(defaultUnitNumber) ? null : defaultUnitNumber.Trim();
        string? currentUnitType = string.IsNullOrWhiteSpace(defaultUnitType) ? null : defaultUnitType.Trim();
        string? currentUnitNote = null;
        string? currentUnitSkills = null;
        var currentFrames = new List<StoryboardFrame>();
        StoryboardFrame? currentFrame = null;
        int frameCounter = 0;
        int nextUnitOrder = unitOrder;
        int currentUnitOrder = unitOrder;
        var continuation = new StoryboardFrameFieldParser.ContinuationState();

        // 导演注意以整集导演计划的「本单元情绪任务」为准，避免 LLM 自行改写情绪和节奏。
        string? BuildAuthoritativeUnitNote(int? episodeNumber, string? unitNumber)
        {
            if (episodeNumber == null || string.IsNullOrWhiteSpace(unitNumber)) return null;
            var plan = episodeDirectorPlans.FirstOrDefault(p => p.EpisodeNumber == episodeNumber.Value);
            var emotion = plan?.UnitEmotionCurve?.FirstOrDefault(x =>
                string.Equals(x.UnitNumber?.Trim(), unitNumber.Trim(), StringComparison.OrdinalIgnoreCase));
            if (emotion == null || string.IsNullOrWhiteSpace(emotion.Emotion)) return null;
            var note = "本单元情绪任务：" + emotion.Emotion + "（" + emotion.Level + "/10）";
            if (!string.IsNullOrWhiteSpace(emotion.DirectingNote))
                note += "；" + emotion.DirectingNote;
            return note;
        }

        void AddCurrentFrame()
        {
            if (currentFrame == null) return;
            // LLM 偶发省略「镜头描述」，但时间轴里写了完整动作时兜底复用，避免页面镜头描述为空。
            if (string.IsNullOrWhiteSpace(currentFrame.Description) &&
                !string.IsNullOrWhiteSpace(currentFrame.Timeline))
                currentFrame.Description = currentFrame.Timeline;
            currentFrames.Add(currentFrame);
            currentFrame = null;
        }

        void FlushCurrentUnit()
        {
            AddCurrentFrame();
            if (currentFrames.Count == 0) return;
            if (currentEpisodeNum <= 0 || !episodeMap.ContainsKey(currentEpisodeNum)) return;
            foreach (var f in currentFrames) f.UnitOrder = currentUnitOrder;
            if (incremental)
                _db.SaveFramesIncremental(episodeMap[currentEpisodeNum], projectId, currentFrames);
            else
                _db.SaveFrames(episodeMap[currentEpisodeNum], projectId, currentFrames);
            currentFrames = new List<StoryboardFrame>();
        }

        void FlushCurrentEpisode()
        {
            FlushCurrentUnit();
            frameCounter = 0;
        }

        foreach (var rawLine in lines)
        {
            var l = rawLine.Trim();
            if (string.IsNullOrEmpty(l)) continue;

            var epMatch = Regex.Match(l, @"【?第\s*(\d+)\s*集】?");
            if (epMatch.Success)
            {
                if (incremental) FlushCurrentUnit(); else FlushCurrentEpisode();
                currentEpisodeNum = int.Parse(epMatch.Groups[1].Value);
                continue;
            }

            var unitMatch = Regex.Match(l, @"【?单元\s*([\d.]+[a-zA-Z]?)\s*】?");
            if (unitMatch.Success)
            {
                var newUnitNumber = unitMatch.Groups[1].Value.Trim();
                if (incremental)
                {
                    // 权威单元号由流水线（Stage 4 单元列表）传入：分镜文本里写错的单元号一律不采信。
                    // 否则「单元1.14」被 LLM 写成「单元1.4」时，SaveFramesIncremental 会按单元号整单元删除重写，
                    // 把真正的 1.4 覆盖掉，只留下一行 UnitNumber=1.4 / ShotNumber=1.14-1 的错位帧。
                    if (!string.IsNullOrWhiteSpace(defaultUnitNumber) &&
                        !string.Equals(newUnitNumber, defaultUnitNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogWarning("[Stage5解析] 项目 {ProjectId}：分镜文本单元号「{TextUnit}」与权威单元号「{AuthoritativeUnit}」不一致，已忽略文本中的单元号",
                            projectId, newUnitNumber, defaultUnitNumber);
                        continue;
                    }
                    if (!string.Equals(currentUnitNumber, newUnitNumber, StringComparison.OrdinalIgnoreCase))
                    {
                        FlushCurrentUnit();
                        currentUnitNumber = newUnitNumber;
                        if (!string.IsNullOrWhiteSpace(defaultUnitType)) currentUnitType = defaultUnitType.Trim();
                        currentUnitOrder = unitOrder;
                        currentUnitNote = null;
                        currentUnitSkills = null;
                    }
                }
                else
                {
                    AddCurrentFrame();
                    currentUnitNumber = newUnitNumber;
                    if (!string.IsNullOrWhiteSpace(defaultUnitType)) currentUnitType = defaultUnitType.Trim();
                    currentUnitOrder = nextUnitOrder++;
                    currentUnitNote = null;
                    currentUnitSkills = null;
                }
                continue;
            }

            var shotNumber = StoryboardFrameParser.TryGetShotNumber(l);
            if (shotNumber != null)
            {
                // 镜头号一律以所属单元号为准重写前缀（LLM 常写成单元号的笔误，或只写序号）。
                shotNumber = NormalizeShotNumber(shotNumber, currentUnitNumber, projectId);
                AddCurrentFrame();
                continuation.TimelineOpen = false;
                continuation.CharacterListOpen = false;
                continuation.DialogueOpen = false;
                var frameNumber = frameCounter + 1;
                if (int.TryParse(shotNumber, out var parsedNumber) && parsedNumber > 0)
                    frameNumber = parsedNumber;
                var firstShotNote = BuildAuthoritativeUnitNote(currentEpisodeNum, currentUnitNumber) ?? currentUnitNote;
                // LLM 已自带【导演注意】时不再叠加权威版，避免同一镜头出现两条注意。
                firstShotNote = StoryboardFrameFieldParser.StripBracketedNotes(firstShotNote)?.Trim();
                currentUnitNote = null;
                currentFrame = new StoryboardFrame
                {
                    ProjectId = projectId,
                    EpisodeId = 0,
                    EpisodeNumber = currentEpisodeNum > 0 ? currentEpisodeNum : null,
                    FrameNumber = frameNumber,
                    SortOrder = frameCounter++,
                    UnitNumber = currentUnitNumber,
                    UnitType = currentUnitType,
                    Description = firstShotNote == null ? null : "【导演注意】" + firstShotNote,
                    ShotNumber = shotNumber,
                    Skills = currentUnitSkills
                };
                continue;
            }

            if (currentFrame == null)
            {
                // 单元级「技能」行（LLM 按规范输出，AutoFixer 已修正为正式名）→ 作为该单元可用技能声明写入每个镜头。
                var unitSkillLine = Regex.Match(l, @"^-\s*\*\*技能\*\*\s*[:：]\s*(.*)$");
                if (unitSkillLine.Success)
                {
                    var skillVal = unitSkillLine.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(skillVal))
                        currentUnitSkills = StoryboardFrameFieldParser.NormalizeSkillList(skillVal);
                }
                continue;
            }

            var unitNote = StoryboardFrameFieldParser.TryGetUnitNote(l);
            if (unitNote != null) currentUnitNote = unitNote;

            if (StoryboardFrameFieldParser.TryApplyField(currentFrame, l, continuation, ref currentUnitType))
                continue;

            StoryboardFrameFieldParser.TryAppendContinuation(currentFrame, l, continuation);


        }

        if (incremental)
        {
            FlushCurrentUnit();
            if (currentFrames.Count > 0 && currentEpisodeNum <= 0 && episodeMap.ContainsKey(1))
                _db.SaveFramesIncremental(episodeMap[1], projectId, currentFrames);
        }
        else
        {
            if (currentEpisodeNum > 0)
                FlushCurrentEpisode();
            else
            {
                AddCurrentFrame();
                if (currentFrames.Count > 0 && episodeMap.ContainsKey(1))
                    _db.SaveFrames(episodeMap[1], projectId, currentFrames);
            }
        }
    }

    /// <summary>把镜头号规范化为「单元号-序号」。LLM 常见的三种写法偏差：
    /// 1) 前缀写成单元号笔误（单元1.14 下写出 1.4-1）→ 以单元号为准重写前缀；
    /// 2) 只写序号（1、2、3）→ 补成「单元号-序号」；
    /// 3) 与单元号一致 → 原样返回。
    /// 不规范化会让下游（提示词 / 关键帧 / 衔接检查）继承错位编号，导致镜头对不上。</summary>
    private string NormalizeShotNumber(string shot, string? unitNumber, int projectId)
    {
        var raw = (shot ?? "").Trim();
        var unit = (unitNumber ?? "").Trim();
        if (unit.Length == 0 || raw.Length == 0) return raw;

        var m = Regex.Match(raw, @"^(?<prefix>\d+(?:\.\d+)*)-(?<idx>\d+)$");
        if (m.Success)
        {
            if (string.Equals(m.Groups["prefix"].Value, unit, StringComparison.OrdinalIgnoreCase)) return raw;
            var fixedShot = unit + "-" + m.Groups["idx"].Value;
            _logger.LogWarning("[Stage5解析] 项目 {ProjectId}：镜头号「{Shot}」与所属单元「{Unit}」不一致，已纠正为「{Fixed}」",
                projectId, raw, unit, fixedShot);
            return fixedShot;
        }
        if (Regex.IsMatch(raw, @"^\d+$"))
        {
            var fixedShot = unit + "-" + raw;
            _logger.LogInformation("[Stage5解析] 项目 {ProjectId}：镜头号「{Shot}」补全单元前缀为「{Fixed}」", projectId, raw, fixedShot);
            return fixedShot;
        }
        return raw;
    }

    // ========== Parse character assets ==========
    private void ParseAndSaveCharacters(int projectId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var assets = new List<CharacterAsset>();
        foreach (var (name, desc, prompt) in ParseTextAssets(text))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            // 兜底过滤：角色形态变体（如「XX战斗态」）并入基础角色，不单独生成参考卡
            if (name.EndsWith("战斗态", StringComparison.Ordinal)
                || name.EndsWith("觉醒态", StringComparison.Ordinal)
                || name.EndsWith("完全体", StringComparison.Ordinal)
                || name.EndsWith("变身态", StringComparison.Ordinal))
                continue;
            assets.Add(new CharacterAsset
            {
                ProjectId = projectId,
                Name = name.Trim(),
                Description = desc,
                Attributes = null,
                ImagePrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim()
            });
        }
        if (assets.Count > 0) _db.SaveCharacterAssets(projectId, assets);
    }

    // ========== Parse prop assets ==========
    private void ParseAndSaveProps(int projectId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var assets = new List<PropAsset>();
        foreach (var (name, desc, prompt) in ParseTextAssets(text))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            assets.Add(new PropAsset
            {
                ProjectId = projectId,
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(desc) ? null : desc.Trim(),
                ImagePrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim()
            });
        }
        if (assets.Count > 0) _db.SavePropAssets(projectId, assets);
    }

    // ========== Parse effect assets ==========
    private void ParseAndSaveEffects(int projectId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var assets = new List<EffectAsset>();
        foreach (var (name, desc, prompt) in ParseTextAssets(text))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            assets.Add(new EffectAsset
            {
                ProjectId = projectId,
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(desc) ? null : desc.Trim(),
                ImagePrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim()
            });
        }
        if (assets.Count > 0) _db.SaveEffectAssets(projectId, assets);
    }

    // ========== Parse environment assets ==========
    private void ParseAndSaveEnvironments(int projectId, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var assets = new List<EnvironmentAsset>();
        foreach (var (name, desc, prompt) in ParseTextAssets(text))
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            assets.Add(new EnvironmentAsset
            {
                ProjectId = projectId,
                Name = name.Trim(),
                Description = string.IsNullOrWhiteSpace(desc) ? null : desc.Trim(),
                ImagePrompt = string.IsNullOrWhiteSpace(prompt) ? null : prompt.Trim()
            });
        }
        if (assets.Count > 0) _db.SaveEnvAssets(projectId, assets);
    }

    // 解析纯文本资产：支持「#### 序号. 角色名」「### 场景一：场景名」「- 名称：xxx」三种块开头
    // 「出图提示词：…」会被单独抽出（不混进描述），出图时直接使用
    private static List<(string Name, string Description, string ImagePrompt)> ParseTextAssets(string text)
    {
        var result = new List<(string, string, string)>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        string? pendingHeader = null;
        string? currentName = null;
        var fields = new List<string>();
        var promptLines = new List<string>();
        var inPrompt = false;

        void Flush()
        {
            var name = !string.IsNullOrWhiteSpace(currentName) ? currentName : pendingHeader;
            if (!string.IsNullOrWhiteSpace(name) || fields.Count > 0 || promptLines.Count > 0)
                result.Add((name ?? "", string.Join("\n", fields), string.Join("\n", promptLines).Trim()));
            pendingHeader = null;
            currentName = null;
            fields.Clear();
            promptLines.Clear();
            inPrompt = false;
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            // 标题：#### 序号. 角色名 / ### 场景一：场景名
            var m = Regex.Match(line, @"^#{3,4}\s*(.*)$");
            if (m.Success)
            {
                var h = m.Groups[1].Value.Trim();
                var isAssetHeader = line.StartsWith("####") || h.StartsWith("场景") || h.StartsWith("特效") || Regex.IsMatch(h, @"^\d+[\.、]");
                if (!isAssetHeader) continue; // 章节标题，忽略
                h = Regex.Replace(h, @"^\d+[\.、]?\s*", "");
                var ci = h.IndexOf('：');
                if (ci < 0) ci = h.IndexOf(':');
                if (ci >= 0) h = h.Substring(ci + 1).Trim();
                Flush();
                pendingHeader = h;
                continue;
            }

            // 字段行：- 标签：内容 或 缩进 标签：内容
            var fm = Regex.Match(line, @"^[-*]?\s*([^:：]{1,20}?)\s*[:：]\s*(.*)$");
            if (fm.Success && (currentName != null || pendingHeader != null || line.StartsWith("-") || line.StartsWith("*")))
            {
                var label = fm.Groups[1].Value.Trim();
                var value = fm.Groups[2].Value.Trim();
                if (label == "名称")
                {
                    if (currentName == null)
                    {
                        currentName = value;
                    }
                    else
                    {
                        // 新的「名称」行 = 新资产块
                        Flush();
                        currentName = value;
                    }
                }
                else if (IsImagePromptLabel(label))
                {
                    // 出图提示词：单独收，不进描述
                    inPrompt = true;
                    if (!string.IsNullOrEmpty(value)) promptLines.Add(value);
                }
                else if (!string.IsNullOrEmpty(value))
                {
                    inPrompt = false;
                    fields.Add(label + "：" + value);
                }
                continue;
            }

            // 无标签的续行：正在收「出图提示词」就继续并入提示词（提示词可能被模型换行）
            if (inPrompt) promptLines.Add(line);
        }
        Flush();
        return result;
    }

    /// <summary>「出图提示词」字段的多种写法（提取时按模版要求输出，模型偶尔会换个说法）。</summary>
    private static bool IsImagePromptLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label)) return false;
        var l = label.Trim();
        if (l.Contains("负面") || l.Contains("negative", StringComparison.OrdinalIgnoreCase)) return false;
        return l.Contains("出图提示词") || l.Contains("画图提示词") || l.Contains("提示词")
            || l.Contains("图像提示词") || l.Contains("图片提示词") || l.Contains("出图Prompt");
    }


    private static string ExtractValue(string line)
    {
        var idx = line.IndexOfAny(new[] { ':', '：' });
        if (idx >= 0 && idx < line.Length - 1)
            return line.Substring(idx + 1).Trim();
        return line;
    }


}

public class SaveStageRequest
{
    public string? Content { get; set; }
}
