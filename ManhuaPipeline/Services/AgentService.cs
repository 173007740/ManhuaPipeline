using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using Microsoft.Extensions.Logging;

namespace ManhuaPipeline.Services;

/// <summary>
/// Agent 链式推理服务 — 多步顺序调用 + 内嵌交叉检查
/// </summary>
public class AgentService
{
    private readonly HttpClient _http;
    private readonly LLMService _llm;
    private readonly DbService _db;
    private readonly ILogger<AgentService>? _logger;

    public AgentService(HttpClient http, LLMService llm, DbService db, ILogger<AgentService>? logger = null)
    {
        _http = http;
        _llm = llm;
        _db = db;
        _logger = logger;
    }

    /// <summary>缺省视频风格：与前端“默认（星穹铁道PV风）”选项一致。</summary>
    private const string DefaultStylePrompt = "崩坏星穹铁道PV风格：电影级日系动画电影质感，厚涂插画与精致渲染结合，画面完成度极高；角色肤色通透，发丝、衣物、瞳孔高光刻画细腻；背景大气透视+景深虚化，场景有厚重材质感；电影级布光，强调侧逆光、轮廓光、体积光，明暗对比强烈但整体色调统一；高饱和、低对比的统一色板，带轻微胶片颗粒与辉光；镜头语言富有张力，广角大透视、快速推拉、镜头光晕，强调氛围与情绪。";

    /// <summary>获取项目绑定视频风格（缺省用星穹铁道PV风），供 H3 提示词「整体要求补充」拼接。</summary>
    private string GetStylePrompt(int projectId)
    {
        var proj = _db.GetProjectById(projectId);
        if (proj != null && proj.StyleId.HasValue)
        {
            var style = _db.GetVideoStyle(proj.StyleId.Value);
            if (style != null && !string.IsNullOrWhiteSpace(style.StylePrompt))
                return style.StylePrompt;
        }
        return DefaultStylePrompt;
    }

    // ========== Stage 9: 提示词生成（包装 ChainGeneratePrompts）==========
        /// <summary>
    /// 场景名归一化：去掉时间/氛围修饰（如“书房（清晨）→书房”），场景名只保留地点主体。
    /// </summary>
    private static string NormalizeSceneName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        var timeWords = new[] { "凌晨", "拂晓", "清晨", "早晨", "上午", "中午", "正午", "午后", "下午", "傍晚", "黄昏", "夜晚", "晚上", "夜间", "深夜", "半夜", "白天", "日间" };
        foreach (var w in timeWords)
        {
            var patterns = new[] { "（" + w + "）", "(" + w + ")", "-" + w, "·" + w, " " + w, w };
            foreach (var p in patterns)
            {
                int idx = name.IndexOf(p, StringComparison.Ordinal);
                if (idx > 0)
                {
                    var trimmed = name.Substring(0, idx).Trim();
                    if (trimmed.Length > 0) return trimmed;
                }
            }
        }
        return name;
    }

    /// <summary>
    /// 角色名归一化：去掉括号内的身份/描述性修饰（如“主角（身份描述）→主角”），角色名只保留主体名。
    /// </summary>
    private static string NormalizeCharacterName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name;
        int idx = name.IndexOf('（');
        if (idx <= 0) idx = name.IndexOf('(');
        if (idx > 0)
        {
            var trimmed = name.Substring(0, idx).Trim();
            if (trimmed.Length > 0) return trimmed;
        }
        return name;
    }

public async Task<string> BuildPrompt(int projectId, string apiUrl, string apiKey, string model, Action<StageProgressUpdate>? onProgress = null, string? thinkingMode = null)
    {
        // 从数据库获取相关数据
        var proj = _db.GetProjectById(projectId) ?? throw new Exception("项目不存在");
        var characters = _db.GetCharacterAssets(projectId);

        // 视频风格：优先取项目绑定风格，缺省用星穹铁道PV风（与前端“默认（星穹铁道PV风）”选项一致）
        var stylePrompt = "崩坏星穹铁道PV风格：电影级日系动画电影质感，厚涂插画与精致渲染结合，画面完成度极高；角色肤色通透，发丝、衣物、瞳孔高光刻画细腻；背景大气透视+景深虚化，场景有厚重材质感；电影级布光，强调侧逆光、轮廓光、体积光，明暗对比强烈但整体色调统一；高饱和、低对比的统一色板，带轻微胶片颗粒与辉光；镜头语言富有张力，广角大透视、快速推拉、镜头光晕，强调氛围与情绪。";
        if (proj.StyleId.HasValue)
        {
            var style = _db.GetVideoStyle(proj.StyleId.Value);
            if (style != null && !string.IsNullOrWhiteSpace(style.StylePrompt))
                stylePrompt = style.StylePrompt;
        }
        var props = _db.GetPropAssets(projectId);
        var effects = _db.GetEffectAssets(projectId);
        var environments = _db.GetEnvAssets(projectId);

        // 优先使用分镜脚本（stage 5）的输出，它包含完整的单元标记
        var stage5 = _db.GetStageData(projectId, 5);
        var shotPlan = stage5?.LlmResponse ?? stage5?.Content ?? "";

        // 如果 stage 5 没有数据，回退到从 StoryboardFrames 构造
        if (string.IsNullOrWhiteSpace(shotPlan))
        {
            var sb = new StringBuilder();
            var frames = _db.GetAllFrames(projectId);
            var episodes = _db.GetEpisodes(projectId);
            foreach (var ep in episodes)
            {
                sb.AppendLine("【第" + ep.EpisodeNumber + "集】");
                var epFrames = frames
                    .Where(f => f.EpisodeId == ep.EpisodeId)
                    .OrderBy(f => f.EpisodeNumber ?? ep.EpisodeNumber)
                    .ThenBy(f => f.UnitNumber)
                    .ThenBy(f => f.SortOrder)
                    .ToList();
                string? lastUnit = null;
                foreach (var f in epFrames)
                {
                    var unitKey = string.IsNullOrWhiteSpace(f.UnitNumber) ? null : f.UnitNumber.Trim();
                    if (!string.IsNullOrWhiteSpace(unitKey) && unitKey != lastUnit)
                    {
                        sb.AppendLine("【单元" + unitKey + "】");
                        if (!string.IsNullOrWhiteSpace(f.UnitType)) sb.AppendLine("  单元类型：" + f.UnitType);
                        lastUnit = unitKey;
                    }
                    var shotNo = string.IsNullOrWhiteSpace(f.ShotNumber) ? f.FrameNumber.ToString() : f.ShotNumber;
                    sb.AppendLine("  【镜头" + shotNo + "】");
                    if (!string.IsNullOrWhiteSpace(f.ShotSize)) sb.AppendLine("    景别：" + f.ShotSize);
                    if (!string.IsNullOrEmpty(f.Description)) sb.AppendLine("    镜头描述：" + f.Description);
                    if (!string.IsNullOrEmpty(f.Composition)) sb.AppendLine("    构图方式：" + f.Composition);
                    if (!string.IsNullOrEmpty(f.Characters)) sb.AppendLine("    出镜角色及表情：" + f.Characters);
                    if (!string.IsNullOrEmpty(f.Dialogue)) sb.AppendLine("    对话/台词：" + f.Dialogue);
                    if (!string.IsNullOrEmpty(f.Camera)) sb.AppendLine("    镜头运动：" + f.Camera);
                    if (!string.IsNullOrEmpty(f.Duration)) sb.AppendLine("    镜头时长：" + f.Duration);
                    if (!string.IsNullOrEmpty(f.CombatBeatIds)) sb.AppendLine("    节拍：" + f.CombatBeatIds);
                    if (!string.IsNullOrEmpty(f.Skills)) sb.AppendLine("    技能：" + f.Skills);
                    if (!string.IsNullOrEmpty(f.Timeline)) sb.AppendLine("    镜头时间轴：" + f.Timeline);
                    if (!string.IsNullOrEmpty(f.StartScene)) sb.AppendLine("    起始画面：" + f.StartScene);
                    if (!string.IsNullOrEmpty(f.EndScene)) sb.AppendLine("    结束画面：" + f.EndScene);
                }
            }
            shotPlan = sb.ToString();
        }

        // 构造角色资产文本
        // 打斗模板库：分镜含打斗关键词时注入（动作/运镜/约束模板）
        var fightText = "";
        if (!string.IsNullOrWhiteSpace(shotPlan))
        {
            var templates = _db.GetFightTemplates(proj.UserId);
            shotPlan = EnsureShotDescriptions(shotPlan, _db.GetAllFrames(projectId));
            fightText = BuildLockedFightText(shotPlan, templates);
            if (string.IsNullOrWhiteSpace(fightText) && HasFightKeywords(shotPlan) && templates.Count > 0)
                fightText = string.Join("\n", templates.Select(t => "- 模板" + t.Name + "（T" + t.Tier + " · " + t.Duration + "s）: 适用:" + t.Scene + "；节拍:" + t.Beat + "；动作行:" + t.ActionPrompt + "；运镜行:" + t.CameraPrompt + "；约束行:" + t.ConstraintPrompt));
        }

        // 技能库：按单元注入，Stage 9 内逐单元匹配该单元实际涉及的技能
        var skills = _db.GetSkills(proj.UserId, tags: proj.Tags);
        // 分镜出场但资产库缺失的角色（群像/配角）自动补入提示词名单，避免视频漏人
        var knownAssetNames = props.Select(p => NormalizeSceneName(p.Name))
            .Concat(effects.Select(e => NormalizeSceneName(e.Name)))
            .Concat(environments.Select(e => NormalizeSceneName(e.Name)))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        characters = EnsureShotCharacters(shotPlan, characters, knownAssetNames, out var autoSupplementedNames);
        var charText = string.Join("\n", characters.Select(c => "- " + NormalizeCharacterName(c.Name) + ": " + (c.Description ?? "") + " " + (c.Attributes ?? "")));
        var propText = string.Join("\n", props.Select(p => "- " + p.Name + ": " + (p.Description ?? "")));
        var envText = string.Join("\n", environments.Select(e => "- " + e.Name + ": " + (e.Description ?? "")));
        var effectText = string.Join("\n", effects.Select(e => "- " + e.Name + ": " + (string.IsNullOrWhiteSpace(e.ImageUrl) ? (e.Description ?? "") : "形态、色调、氛围以参考图为准")));

        // 参考图名单只认资产库：自动补入的临时角色（分镜配角/群像）不进 @图 名单，避免无图可传的对象被引用
        var charNames = string.Join("、", characters
            .Where(c => !autoSupplementedNames.Contains(c.Name))
            .Select(c => NormalizeCharacterName(c.Name)).Distinct());
        var propNames = string.Join("、", props.Select(p => p.Name));
        var envNames = string.Join("、", environments.Select(e => NormalizeSceneName(e.Name)).Distinct());
        var effectNames = string.Join("、", effects.Select(e => NormalizeSceneName(e.Name)).Distinct());
        var charNameList = characters.Select(c => c.Name).ToList();
        var propNameList = props.Select(p => p.Name).ToList();
        var envNameList = environments.Select(e => e.Name).ToList();
        var effectNameList = effects.Select(e => e.Name).ToList();
        return await ChainGeneratePrompts(shotPlan, charText, envText, propText, effectText, proj.ScriptContent ?? "", stylePrompt, charNames, propNames, envNames, effectNames, projectId, charNameList, propNameList, envNameList, effectNameList, skills, fightText, apiUrl, apiKey, model, onProgress, thinkingMode, characterAssets: characters, autoSupplementedNames: autoSupplementedNames);
    }

    /// <summary>单分镜独立生成提示词：复用整集逐单元链路（关键词提取→衔接分析→技能锁定→生成→全套Guard→解析入库）。</summary>
    public async Task<(List<SeedancePrompt> Prompts, string RawText)> GenerateFramePromptAsync(
        int projectId, int frameId, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var proj = _db.GetProjectById(projectId) ?? throw new Exception("项目不存在");
        var frame = _db.GetFrameById(frameId) ?? throw new Exception("分镜不存在");
        if (frame.ProjectId != projectId) throw new Exception("分镜不属于该项目");

        // 视频风格：与整集 BuildPrompt 一致
        var stylePrompt = "崩坏星穹铁道PV风格：电影级日系动画电影质感，厚涂插画与精致渲染结合，画面完成度极高；角色肤色通透，发丝、衣物、瞳孔高光刻画细腻；背景大气透视+景深虚化，场景有厚重材质感；电影级布光，强调侧逆光、轮廓光、体积光，明暗对比强烈但整体色调统一；高饱和、低对比的统一色板，带轻微胶片颗粒与辉光；镜头语言富有张力，广角大透视、快速推拉、镜头光晕，强调氛围与情绪。";
        if (proj.StyleId.HasValue)
        {
            var style = _db.GetVideoStyle(proj.StyleId.Value);
            if (style != null && !string.IsNullOrWhiteSpace(style.StylePrompt))
                stylePrompt = style.StylePrompt;
        }

        // 构造单镜头分镜文本（集/单元/镜头标记齐全，供解析器与 Guard 使用，格式对齐整集分镜）
        var sb = new StringBuilder();
        if (frame.EpisodeNumber.HasValue) sb.AppendLine("【第" + frame.EpisodeNumber + "集】");
        if (!string.IsNullOrWhiteSpace(frame.UnitNumber))
        {
            sb.AppendLine("【单元" + frame.UnitNumber.Trim() + "】");
            if (!string.IsNullOrWhiteSpace(frame.UnitType)) sb.AppendLine("  单元类型：" + frame.UnitType);
        }
        var shotNo = string.IsNullOrWhiteSpace(frame.ShotNumber) ? frame.FrameNumber.ToString() : frame.ShotNumber;
        sb.AppendLine("  【镜头" + shotNo + "】");
        if (!string.IsNullOrWhiteSpace(frame.ShotSize)) sb.AppendLine("    景别：" + frame.ShotSize);
        if (!string.IsNullOrEmpty(frame.Description)) sb.AppendLine("    镜头描述：" + frame.Description);
        if (!string.IsNullOrEmpty(frame.Composition)) sb.AppendLine("    构图方式：" + frame.Composition);
        if (!string.IsNullOrEmpty(frame.Characters)) sb.AppendLine("    出镜角色及表情：" + frame.Characters);
        if (!string.IsNullOrEmpty(frame.Dialogue)) sb.AppendLine("    对话/台词：" + frame.Dialogue);
        if (!string.IsNullOrEmpty(frame.Camera)) sb.AppendLine("    镜头运动：" + frame.Camera);
        if (!string.IsNullOrEmpty(frame.Duration)) sb.AppendLine("    镜头时长：" + frame.Duration);
        if (!string.IsNullOrEmpty(frame.CombatBeatIds)) sb.AppendLine("    节拍：" + frame.CombatBeatIds);
        if (!string.IsNullOrEmpty(frame.Skills)) sb.AppendLine("    技能：" + frame.Skills);
        if (!string.IsNullOrEmpty(frame.Timeline)) sb.AppendLine("    镜头时间轴：" + frame.Timeline);
        if (!string.IsNullOrEmpty(frame.StartScene)) sb.AppendLine("    起始画面：" + frame.StartScene);
        if (!string.IsNullOrEmpty(frame.EndScene)) sb.AppendLine("    结束画面：" + frame.EndScene);
        // L4 镜头状态机六字段：交给阶段 9，让提示词必须承接上一镜的结束状态、只做一个动作、并遵守禁止变化项
        if (!string.IsNullOrEmpty(frame.StartState)) sb.AppendLine("    起始状态：" + frame.StartState);
        if (!string.IsNullOrEmpty(frame.SingleAction)) sb.AppendLine("    单一动作：" + frame.SingleAction);
        if (!string.IsNullOrEmpty(frame.EndState)) sb.AppendLine("    结束状态：" + frame.EndState);
        if (!string.IsNullOrEmpty(frame.NextConnection)) sb.AppendLine("    衔接下一镜：" + frame.NextConnection);
        if (!string.IsNullOrEmpty(frame.ForbiddenChanges)) sb.AppendLine("    禁止变化：" + frame.ForbiddenChanges);
        if (!string.IsNullOrEmpty(frame.NewInformation)) sb.AppendLine("    新信息：" + frame.NewInformation);
        var unitText = sb.ToString();

        // 资产与打斗模板（与整集 BuildPrompt 相同）
        var characters = _db.GetCharacterAssets(projectId);
        var props = _db.GetPropAssets(projectId);
        var effects = _db.GetEffectAssets(projectId);
        var environments = _db.GetEnvAssets(projectId);
        var fightText = "";
        if (!string.IsNullOrWhiteSpace(unitText))
        {
            var templates = _db.GetFightTemplates(proj.UserId);
            fightText = BuildLockedFightText(unitText, templates);
            if (string.IsNullOrWhiteSpace(fightText) && HasFightKeywords(unitText) && templates.Count > 0)
                fightText = string.Join("\n", templates.Select(t => "- 模板" + t.Name + "（T" + t.Tier + " · " + t.Duration + "s）: 适用:" + t.Scene + "；节拍:" + t.Beat + "；动作行:" + t.ActionPrompt + "；运镜行:" + t.CameraPrompt + "；约束行:" + t.ConstraintPrompt));
        }

        // 技能库：按镜头匹配
        var skills = _db.GetSkills(proj.UserId, tags: proj.Tags);
        var knownAssetNames = props.Select(p => NormalizeSceneName(p.Name))
            .Concat(effects.Select(e => NormalizeSceneName(e.Name)))
            .Concat(environments.Select(e => NormalizeSceneName(e.Name)))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        characters = EnsureShotCharacters(unitText, characters, knownAssetNames, out var autoSupplementedNames);
        var charText = string.Join("\n", characters.Select(c => "- " + NormalizeCharacterName(c.Name) + ": " + (c.Description ?? "") + " " + (c.Attributes ?? "")));
        var propText = string.Join("\n", props.Select(p => "- " + p.Name + ": " + (p.Description ?? "")));
        var envText = string.Join("\n", environments.Select(e => "- " + e.Name + ": " + (e.Description ?? "")));
        var effectText = string.Join("\n", effects.Select(e => "- " + e.Name + ": " + (string.IsNullOrWhiteSpace(e.ImageUrl) ? (e.Description ?? "") : "形态、色调、氛围以参考图为准")));
        var charNames = string.Join("、", characters
            .Where(c => !autoSupplementedNames.Contains(c.Name))
            .Select(c => NormalizeCharacterName(c.Name)).Distinct());
        var propNames = string.Join("、", props.Select(p => p.Name));
        var envNames = string.Join("、", environments.Select(e => NormalizeSceneName(e.Name)).Distinct());
        var effectNames = string.Join("、", effects.Select(e => NormalizeSceneName(e.Name)).Distinct());
        var charNameList = characters.Select(c => c.Name).ToList();
        var propNameList = props.Select(p => p.Name).ToList();
        var envNameList = environments.Select(e => e.Name).ToList();
        var effectNameList = effects.Select(e => e.Name).ToList();

        // Step 1-3：单镜头关键词提取 + 衔接分析
        var charKeywords = await ExtractCharKeywords(charText, unitText, apiUrl, apiKey, model, thinkingMode);
        var envKeywords = await ExtractEnvKeywords(envText, propText, unitText, apiUrl, apiKey, model, thinkingMode);
        var propKeywords = await ExtractPropKeywords(propText, unitText, apiUrl, apiKey, model, thinkingMode);
        var effectKeywords = await ExtractEffectKeywords(effectText, unitText, apiUrl, apiKey, model, thinkingMode);
        var shotAnalysis = await AnalyzeShotTransition(unitText, apiUrl, apiKey, model, thinkingMode);

        // Step 4：生成 + Guard（与整集逐单元链路完全一致）
        var currentForm = PromptNameNormalizer.DefaultCurrentForm(charNameList);
        // 分镜帧参考绑定：把本镜头 FrameAssetBindings 作为 @图N 行的确定资产来源提示给 LLM（空镜只绑定场景，不出现镜头外的角色/道具）
        string? frameHint = null;
        try
        {
            var frameBindingsForShot = _db.GetFrameAssetBindings(projectId, frameId);
            if (frameBindingsForShot.Count > 0)
            {
                var oneShotMap = new Dictionary<string, List<FrameAssetBinding>>(StringComparer.OrdinalIgnoreCase);
                var oneKey = ShotFrameKey(frame.EpisodeNumber, frame.UnitNumber, shotNo);
                if (oneKey != null)
                {
                    frameBindingsForShot.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
                    oneShotMap[oneKey] = frameBindingsForShot;
                    frameHint = BuildFrameBindingHintText(oneShotMap, unitText, frame.EpisodeNumber, frame.UnitNumber?.Trim());
                }
            }
        }
        catch { frameHint = null; }
        var (unitSkillText, unitLockedSkills) = BuildUnitSkillText(unitText, skills, charNameList, characters);
        var prompts = await GenerateSeedancePrompts(
            unitText, charKeywords, propKeywords, envKeywords, effectKeywords, shotAnalysis, stylePrompt, charNames, propNames, envNames, effectNames, unitSkillText, fightText,
            apiUrl, apiKey, model, thinkingMode, frameHint, BuildKeyframeAnchor(projectId, unitText));
        prompts = PromptDialogueInjectGuard.Apply(prompts, unitText);
        prompts = PromptSkillGuard.FilterUnlockedSkills(prompts, skills.Select(s => s.Name ?? "").ToList(), unitLockedSkills.Select(s => s.Name ?? "").ToList());
        prompts = PromptNameNormalizer.Normalize(prompts, unitText, charNameList, ref currentForm);
        prompts = PromptCharacterRefGuard.Apply(prompts, charNameList);
        prompts = PromptCombatStateGuard.Apply(prompts, unitText, charNameList);
        prompts = EnsureSkillRefImages(prompts, skills, unitLockedSkills, effectNameList, unitText);
        prompts = PromptPropRefGuard.Apply(prompts, unitText, propNameList);
        prompts = PromptRefLineGuard.Apply(prompts, charNameList.Where(n => !autoSupplementedNames.Contains(n)).Concat(propNameList).Concat(envNameList).Concat(effectNameList).Concat(skills.Select(s => s.Name ?? "")).Concat(unitLockedSkills.Select(s => s.Name ?? "")));
        prompts = EnsureMaxNineRefImages(prompts);
        prompts = EnsureDialogueLines(prompts);
        prompts = EnsureActionCameraEmbedded(prompts);
        prompts = NormalizeTimeSegmentHeaders(prompts);
        prompts = NormalizeCompactTimeBlocks(prompts);
        prompts = PromptDialogueVerbatimGuard.Apply(prompts, unitText);
        prompts = PromptSkillShoutGuard.Apply(prompts, unitLockedSkills, charNameList);
        prompts = PromptSkillEntityGuard.Apply(prompts);
        prompts = EnsureNegativePromptLines(prompts);
        prompts = PromptInnerMonologueGuard.Apply(prompts);
        prompts = PromptPropUnseenGuard.Apply(prompts);
        prompts = PromptFacingGuard.Apply(prompts, charNameList);
        prompts = PromptPostOverlayGuard.Apply(prompts);

        var parsed = SeedancePromptParser.Parse(prompts, projectId, charNameList, propNameList, envNameList, effectNameList);
        return (parsed, prompts);
    }

    private static bool HasFightKeywords(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var fightKeywords = new[] { "打斗", "战斗", "对战", "对砍", "厮杀", "围攻", "围杀", "突围", "追杀", "追逐", "奔逃", "反杀", "秒杀", "过招", "较量", "比武", "切磋", "斗法", "交手", "混战", "群战", "缠斗", "追击", "逃杀", "对峙", "袭击", "突袭", "迎战", "应战", "攻防" };
        return fightKeywords.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 分镜“出镜角色及表情”里出现、但角色资产缺失的群像/配角，自动补入提示词角色名单。
    /// </summary>
    private static List<CharacterAsset> EnsureShotCharacters(
        string shotPlan,
        List<CharacterAsset> characters,
        List<string> knownAssetNames,
        out List<string> autoSupplementedNames)
    {
        autoSupplementedNames = new List<string>();
        var result = characters == null ? new List<CharacterAsset>() : new List<CharacterAsset>(characters);
        if (string.IsNullOrWhiteSpace(shotPlan)) return result;

        var knownRaw = result.Select(c => (c.Name ?? "").Trim()).Where(n => n.Length > 0).ToList();
        var known = new HashSet<string>(
            result.Select(c => NormalizeCharacterName(c.Name).Trim()).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var pattern = new System.Text.RegularExpressions.Regex(
            @"^\s*(?:[-*]+\s*)?(?:\*\*)?(?:出镜角色及表情|出镜角色)(?:\*\*)?\s*[:：]\s*(.+)$",
            System.Text.RegularExpressions.RegexOptions.Multiline);

        foreach (System.Text.RegularExpressions.Match m in pattern.Matches(shotPlan))
        {
            var segments = m.Groups[1].Value.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in segments)
            {
                var name = ExtractShotCharacterName(raw);
                if (string.IsNullOrWhiteSpace(name)) continue;
                name = NormalizeCharacterName(name).Trim();
                name = StripLeadingCharacterModifiers(name);
                if (name.Length < 2 || known.Contains(name)) continue;
                if (name.IndexOfAny(new[] { '、', '，', ',' }) >= 0) continue;
                if (IsKnownAssetName(name, knownAssetNames)) continue;
                if (IsGenericCharacterName(name)) continue;
                // 裸名/修饰名如果已经是某个正式资产名的组成部分（如“岳沉天”属于“前世岳沉天/少年岳沉天”，
                // “顾残山留音”属于“顾残山”），交给 Stage 9 归一化器映射，不能再补成独立资产，否则归一化和战斗态双卡会短路。
                if (IsPartOfExistingCharacterName(name, knownRaw)) continue;
                result.Add(new CharacterAsset
                {
                    Name = name,
                    Description = "分镜出场角色（自动补全）：按镜头规划实际出镜，群像/配角保持数量与站位",
                    Attributes = "配角/群像"
                });
                autoSupplementedNames.Add(name);
                known.Add(name);
            }
        }
        return result;
    }

    private static string ExtractShotCharacterName(string segment)
    {
        var s = segment.Trim();
        var idx = s.IndexOf("——", StringComparison.Ordinal);
        if (idx < 0) idx = s.IndexOf('：');
        if (idx < 0) idx = s.IndexOf(':');
        if (idx < 0) idx = s.IndexOf('-');
        if (idx < 0) idx = s.IndexOf('—');
        return (idx > 0 ? s.Substring(0, idx) : s).Trim();
    }

    private static readonly string[] CharacterPrefixModifiers =
    {
        "被击中的", "被", "其余", "剩下的", "几位", "数名", "多名", "所有", "在场",
        "远处的", "前方的", "身后的", "周围的", "另一边的", "敌方", "我方", "众"
    };

    private static string StripLeadingCharacterModifiers(string name)
    {
        var current = name;
        bool changed;
        do
        {
            changed = false;
            foreach (var p in CharacterPrefixModifiers)
            {
                if (current.StartsWith(p, StringComparison.Ordinal) && current.Length > p.Length)
                {
                    current = current.Substring(p.Length).Trim();
                    changed = true;
                }
            }
        } while (changed);
        return current;
    }

    private static bool IsKnownAssetName(string name, List<string> assetNames)
    {
        foreach (var a in assetNames)
        {
            if (string.IsNullOrWhiteSpace(a)) continue;
            var n = a.Trim();
            if (name.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
            if (name.Length >= 2 && n.Length >= 2 &&
                (name.Contains(n, StringComparison.OrdinalIgnoreCase) || n.Contains(name, StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    private static bool IsGenericCharacterName(string name)
    {
        var stop = new[] { "无", "旁白", "众人", "弟子们", "宗主", "长老", "弟子", "观众", "画面", "镜头", "背景", "远景", "全景", "近景", "特写", "上方", "下方", "远处", "近处", "在场" };
        return stop.Any(s => string.Equals(name, s, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsPartOfExistingCharacterName(string name, List<string> existingNames)
    {
        if (string.IsNullOrWhiteSpace(name) || existingNames.Count == 0) return false;
        foreach (var existing in existingNames)
        {
            var n = existing.Trim();
            if (n.Length <= name.Length) continue;
            if (n.Contains(name, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static string BuildLockedFightText(string shotPlan, List<FightTemplateItem> templates)
    {
        if (string.IsNullOrWhiteSpace(shotPlan) || templates == null || templates.Count == 0) return "";
        var unitMatches = System.Text.RegularExpressions.Regex.Matches(shotPlan, @"【单元[\d.]+[a-zA-Z]?】");
        if (unitMatches.Count == 0) return "";
        var seen = new HashSet<string>();
        var entries = new List<string>();
        for (var i = 0; i < unitMatches.Count; i++)
        {
            var m = unitMatches[i];
            var blockStart = m.Index;
            var blockEnd = i + 1 < unitMatches.Count ? unitMatches[i + 1].Index : shotPlan.Length;
            var block = shotPlan.Substring(blockStart, blockEnd - blockStart);
            var modeMatch = System.Text.RegularExpressions.Regex.Match(block, @"(?:\*\*)?控制模式(?:\*\*)?\s*[:：]\s*([^\r\n]+)");
            if (!modeMatch.Success || !modeMatch.Groups[1].Value.Contains("打斗")) continue;
            var tmplMatch = System.Text.RegularExpressions.Regex.Match(block, @"(?:\*\*)?打斗模板(?:\*\*)?\s*[:：]\s*(\d+)\s*[/／]\s*([^\r\n]+)");
            FightTemplateItem? template = null;
            if (tmplMatch.Success && int.TryParse(tmplMatch.Groups[1].Value, out var templateId))
                template = templates.FirstOrDefault(t => t.FightTemplateId == templateId);
            if (template == null && tmplMatch.Success)
                template = templates.FirstOrDefault(t => t.Name.Contains(tmplMatch.Groups[2].Value.Trim(), StringComparison.OrdinalIgnoreCase));
            if (template == null && tmplMatch.Success)
                template = CombatPlanSelector.TryResolveDefensiveTemplateByName(tmplMatch.Groups[2].Value.Trim());
            if (template == null) continue;
            if (!seen.Add(template.FightTemplateId + "|" + template.Name)) continue;
            var atomLine = ExtractMetaLine(block, "运镜原子");
            if (string.IsNullOrWhiteSpace(atomLine))
                atomLine = ExtractMetaLine(block, "分镜指令");
            var skillLine = ExtractMetaLine(block, "技能");
            var entry = "【锁定模板】\n" +
                "模板ID: " + template.FightTemplateId + "\n" +
                "模板名: " + template.Name + "\n" +
                "适用: " + template.Scene + "\n" +
                "节拍: " + template.Beat + "\n" +
                "动作行: " + template.ActionPrompt + "\n" +
                "运镜行: " + template.CameraPrompt + "\n" +
                "约束行: " + template.ConstraintPrompt + "\n" +
                "运镜原子: " + (string.IsNullOrWhiteSpace(atomLine) ? "无" : atomLine) + "\n" +
                "技能: " + (string.IsNullOrWhiteSpace(skillLine) ? "无" : skillLine);
            entries.Add(entry);
        }
        return string.Join("\n\n", entries);
    }

    private static string ExtractMetaLine(string block, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(block, @"\*\*" + label + @"\*\*\s*[:：]\s*([^\r\n]+)");
        return match.Success ? match.Groups[1].Value.Trim() : "";
    }

    private static string EnsureShotDescriptions(string shotPlan, List<StoryboardFrame> frames)
    {
        if (string.IsNullOrWhiteSpace(shotPlan) || frames == null || frames.Count == 0)
            return shotPlan;

        var descByShot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in frames)
        {
            if (f == null) continue;
            var key = string.IsNullOrWhiteSpace(f.ShotNumber) ? f.FrameNumber.ToString() : f.ShotNumber.Trim();
            if (key.Length == 0 || descByShot.ContainsKey(key)) continue;
            var desc = string.IsNullOrWhiteSpace(f.Description) ? f.Timeline : f.Description;
            if (!string.IsNullOrWhiteSpace(desc))
                descByShot[key] = desc;
        }
        if (descByShot.Count == 0) return shotPlan;

        var regex = new System.Text.RegularExpressions.Regex(@"- \*\*镜头编号\*\*\s*[：:]\s*([^\r\n]+)");
        var matches = regex.Matches(shotPlan);
        if (matches.Count == 0) return shotPlan;

        var sb = new StringBuilder();
        var pos = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var afterShot = m.Index + m.Length;
            var nextShotIndex = i + 1 < matches.Count ? matches[i + 1].Index : shotPlan.Length;
            var block = shotPlan.Substring(afterShot, nextShotIndex - afterShot);
            var shotId = m.Groups[1].Value.Trim();

            sb.Append(shotPlan, pos, afterShot - pos);
            if (descByShot.TryGetValue(shotId, out var desc) && block.IndexOf("**镜头描述**", StringComparison.Ordinal) < 0)
            {
                sb.AppendLine();
                sb.Append("- **镜头描述**: ");
                sb.Append(desc);
            }
            pos = afterShot;
        }
        sb.Append(shotPlan, pos, shotPlan.Length - pos);
        return sb.ToString();
    }

    private static Dictionary<string, int> CountShotsByUnit(string text)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text)) return result;
        var matches = System.Text.RegularExpressions.Regex.Matches(text, @"(?:\-\s*)?\*\*镜头编号\*\*\s*[:：]\s*([\d.]+[a-zA-Z]?-\d+)");
        if (matches.Count == 0)
            matches = System.Text.RegularExpressions.Regex.Matches(text, @"【镜头([\d.]+[a-zA-Z]?-\d+)】");
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            var shot = m.Groups[1].Value;
            var dash = shot.LastIndexOf('-');
            var unit = dash > 0 ? shot.Substring(0, dash) : shot;
            if (unit.Length > 0)
                result[unit] = result.TryGetValue(unit, out var count) ? count + 1 : 1;
        }
        return result;
    }

    // ========== Stage 10: 衔接检查 ==========
    public async Task<string> CheckCoherence(int projectId, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var prompts = _db.GetPrompts(projectId);
        // 提示词正文可能在 PromptText（SD）或 PromptTextH3（H3）里，两边都要取，
        // 只取 PromptText 会在「只跑过 H3」的项目上误判为无数据。
        // 每个镜头前加「单元-镜头」编号头，让模型能按编号定位，而不是自己编「片段N」。
        var blocks = new List<string>();
        foreach (var p in prompts)
        {
            var sd = p.PromptText ?? "";
            var h3 = p.PromptTextH3 ?? "";
            string body;
            if (!string.IsNullOrWhiteSpace(sd) && !string.IsNullOrWhiteSpace(h3)) body = sd + "\n" + h3;
            else body = string.IsNullOrWhiteSpace(sd) ? h3 : sd;
            if (string.IsNullOrWhiteSpace(body)) continue;

            var label = string.IsNullOrWhiteSpace(p.UnitName)
                ? (string.IsNullOrWhiteSpace(p.ShotLabel) ? p.PromptId.ToString() : p.ShotLabel)
                : (string.IsNullOrWhiteSpace(p.ShotLabel) ? p.UnitName : p.UnitName + "-" + p.ShotLabel);
            if (p.EpisodeNumber > 0) label = "第" + p.EpisodeNumber + "集/" + label;
            blocks.Add($"===== 镜头 {label} =====\n{body}");
        }
        var promptText = string.Join("\n\n", blocks);
        // 兜底：SeedancePrompts 没落库（解析失败/逐单元插入为空）时，退回 Stage 9 的完整输出，
        // 避免 Stage 10 变成「必然无数据」的死路。
        var labeled = blocks.Count > 0;
        if (!labeled)
        {
            var stage9 = _db.GetStageData(projectId, 9);
            promptText = stage9?.LlmResponse ?? stage9?.Content ?? "";
        }
        if (string.IsNullOrWhiteSpace(promptText))
            return "暂无提示词数据，无法进行衔接检查。";

        var systemPrompt = "你是一个视频衔接检查专家。下面按播放顺序给出若干镜头的提示词，" +
            (labeled ? "每个镜头以「===== 镜头 编号 =====」开头，编号形如「1.5-1」或「第1集/1.5-1」。\n\n" : "\n\n") +
            "硬性要求：\n" +
            (labeled
                ? "1. 每个问题必须写明涉及的镜头编号（用上面给出的编号原文），禁止自己另编「片段N」「Shot N」这类编号。\n" +
                  "2. 开头先输出一行「已检查镜头清单：<编号列表>」，并核对数量与下文镜头数一致；数量对不上说明你漏读了，必须重新通读。\n"
                : "1. 提示词未标注镜头编号，涉及具体镜头时请引用该镜头的原文片段作为依据。\n" +
                  "2. 开头先输出一行「已检查内容：<一句话概括范围与规模>」。\n") +
            "3. 只报能在给定提示词文本里找到依据的问题；找不到依据的不要臆测、不要编造镜头内容或剧情。\n" +
            "4. 输出分两类，不要混在一起：\n" +
            "   【确认问题】文本中存在明确矛盾，必须修。每条写清：涉及镜头 / 矛盾点原文摘录 / 具体修改建议。\n" +
            "   【可选优化】仅为提升观感的建议，可忽略。\n" +
            "5. 只有当你能指出具体两个镜头的前后状态冲突时，才可建议调整镜头顺序，并写明建议的新顺序与理由；否则不要提。\n" +
            "6. 禁止输出排查过程：不要写「重新审视发现…」「寻找绝对矛盾…」「无直接矛盾」这类自问自答，也不要复述你排查过哪些镜头。只输出结论。\n\n" +
            "检查维度：\n" +
            "1. 角色外貌、服装及状态（干湿/伤痕/持物）跨镜头是否连续\n" +
            "2. 场景空间布局与光照（色温、光源方向、雨势）是否一致\n" +
            "3. 道具与特效的出现/消失位置是否连贯\n" +
            "4. 镜头运动与空间关系是否合理\n" +
            "5. 角色情绪弧线是否连贯\n" +
            "6. 数值/计数类状态（剩余次数、倒计时、金额、数量、屏幕上的数字）跨镜头是否单调且可解释；同一数值被重复消耗、倒退或出现两次「归零」必须报为确认问题\n\n" +
            "用中文输出。没有问题就明确写「未发现确认问题」，不要为了凑数硬编建议。";

        var userMsg = (labeled ? $"本次共 {blocks.Count} 个镜头，按播放顺序如下：\n\n" : "") + promptText;

        return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, thinkingMode: thinkingMode);
    }

    // ========== Chain: Seedance 2.0 提示词生成（Stage 9 增强版）==========
    public async Task<string> ChainGeneratePrompts(
        string shotPlan,
        string characters,
        string environments,
        string props,
        string effects,
        string scriptContext,
        string stylePrompt,
        string charNames,
        string propNames,
        string envNames,
        string effectNames,
        int projectId,
        List<string> charNameList,
        List<string> propNameList,
        List<string> envNameList,
        List<string> effectNameList,
        List<SkillLibraryItem> skills,
        string fightText,
        string apiUrl, string apiKey, string model,
        Action<StageProgressUpdate>? onProgress = null,
        string? thinkingMode = null,
        IReadOnlyList<CharacterAsset>? characterAssets = null,
        List<string>? autoSupplementedNames = null)
    {
        // Step 1: 提取角色外貌关键词（用于提示词中保持一致）
        var charKeywords = await ExtractCharKeywords(characters, shotPlan, apiUrl, apiKey, model, thinkingMode);

        // Step 2: 提取场景关键词
        var envKeywords = await ExtractEnvKeywords(environments, props, shotPlan, apiUrl, apiKey, model, thinkingMode);

        // Step 2.5: 提取道具关键词
        var propKeywords = await ExtractPropKeywords(props, shotPlan, apiUrl, apiKey, model, thinkingMode);

        // Step 2.6: 提取特效关键词
        var effectKeywords = await ExtractEffectKeywords(effects, shotPlan, apiUrl, apiKey, model, thinkingMode);

        // Step 3: 分析镜头衔接
        var shotAnalysis = await AnalyzeShotTransition(shotPlan, apiUrl, apiKey, model, thinkingMode);

        // Step 4: 按单元分批生成提示词（避免超出 token 限制）
        var unitRegex = new System.Text.RegularExpressions.Regex(@"【单元[\d.]+[a-zA-Z]?】");
        var unitMatches = unitRegex.Matches(shotPlan);
        var unitPromptsList = new List<string>();
        var currentForm = PromptNameNormalizer.DefaultCurrentForm(charNameList);
        var unitTextMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        // Stage 9 直接引用分镜资产：读取 Stage 4 落库的「单元资产绑定」，逐单元把资产名单收窄为绑定资产，LLM 无法引用绑定外的对象
        var unitBindingScopes = LoadUnitBindingScopes(projectId);
        // 分镜帧资产绑定（FrameAssetBindings）：按「集|单元|镜头」索引，用于逐镜头提示 LLM 的 @图N 行只能绑定帧绑定清单资产
        var frameBindingsByShot = LoadFrameBindingsByShot(projectId);

        if (unitMatches.Count == 0)
        {
            // 没有单元标记，按集拆分
            var epRegex = new System.Text.RegularExpressions.Regex(@"【第\d+集】");
            var epMatches = epRegex.Matches(shotPlan);
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = 0,
                Total = epMatches.Count,
                CurrentPhase = "提示词生成",
                Message = $"共 {epMatches.Count} 个单元"
            });
            for (int i = 0; i < epMatches.Count; i++)
            {
                var m = epMatches[i];
                int startIdx = m.Index;
                int endIdx = (i + 1 < epMatches.Count) ? epMatches[i + 1].Index : shotPlan.Length;
                var epText = shotPlan.Substring(startIdx, endIdx - startIdx).Trim();
                if (string.IsNullOrEmpty(epText)) continue;

                var (unitSkillText, unitLockedSkills) = BuildUnitSkillText(epText, skills, charNameList, characterAssets);
                var prompts = await GenerateSeedancePrompts(
                    epText, charKeywords, propKeywords, envKeywords, effectKeywords, shotAnalysis, stylePrompt, charNames, propNames, envNames, effectNames, unitSkillText, fightText,
                    apiUrl, apiKey, model, thinkingMode, null, BuildKeyframeAnchor(projectId, epText));
                prompts = PromptDialogueInjectGuard.Apply(prompts, epText);
                prompts = PromptSkillGuard.FilterUnlockedSkills(prompts, skills.Select(s => s.Name ?? "").ToList(), unitLockedSkills.Select(s => s.Name ?? "").ToList());
                prompts = PromptNameNormalizer.Normalize(prompts, epText, charNameList, ref currentForm);
                prompts = PromptCharacterRefGuard.Apply(prompts, charNameList);
                prompts = PromptCombatStateGuard.Apply(prompts, epText, charNameList);
                prompts = EnsureSkillRefImages(prompts, skills, unitLockedSkills, effectNameList, epText);
                prompts = PromptPropRefGuard.Apply(prompts, epText, propNameList);
                prompts = PromptRefLineGuard.Apply(prompts, charNameList.Where(n => autoSupplementedNames == null || !autoSupplementedNames.Contains(n)).Concat(propNameList).Concat(envNameList).Concat(effectNameList).Concat(skills.Select(s => s.Name ?? "")).Concat(unitLockedSkills.Select(s => s.Name ?? "")));
                prompts = EnsureMaxNineRefImages(prompts);
                prompts = EnsureDialogueLines(prompts);
                prompts = EnsureActionCameraEmbedded(prompts);
                prompts = NormalizeTimeSegmentHeaders(prompts);
                prompts = NormalizeCompactTimeBlocks(prompts);
                prompts = PromptDialogueVerbatimGuard.Apply(prompts, epText);
                prompts = PromptSkillShoutGuard.Apply(prompts, unitLockedSkills, charNameList);
                prompts = PromptSkillEntityGuard.Apply(prompts);
                prompts = EnsureNegativePromptLines(prompts);
                prompts = PromptInnerMonologueGuard.Apply(prompts);
                prompts = PromptPropUnseenGuard.Apply(prompts);
                prompts = PromptFacingGuard.Apply(prompts, charNameList);
                prompts = PromptPostOverlayGuard.Apply(prompts);
                unitPromptsList.Add(prompts);

                // 逐单元增量入库：跑完一个单元立即写入，页面可实时看到进度
                var parsed = SeedancePromptParser.Parse(prompts, projectId, charNameList, propNameList, envNameList, effectNameList);
                if (parsed.Count > 0)
                    _db.InsertSeedancePrompts(projectId, parsed);
                var epLabel = epMatches[i].Value.Trim('【', '】');
                onProgress?.Invoke(new StageProgressUpdate
                {
                    Done = i + 1,
                    Total = epMatches.Count,
                    CurrentUnit = epLabel,
                    CurrentPhase = "提示词生成",
                    Message = $"已完成 {epLabel}"
                });
            }
        }
        else
        {
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = 0,
                Total = unitMatches.Count,
                CurrentPhase = "提示词生成",
                Message = $"共 {unitMatches.Count} 个单元"
            });
            for (int i = 0; i < unitMatches.Count; i++)
            {
                var m = unitMatches[i];
                int startIdx = m.Index;
                int endIdx = (i + 1 < unitMatches.Count) ? unitMatches[i + 1].Index : shotPlan.Length;
                var unitText = shotPlan.Substring(startIdx, endIdx - startIdx).Trim();
                if (string.IsNullOrEmpty(unitText)) continue;

                // Find which episode this unit belongs to
                var beforeUnit = shotPlan.Substring(0, m.Index);
                var epMatch = System.Text.RegularExpressions.Regex.Match(beforeUnit, @"【第\s*(\d+)\s*集】");
                string episodePrefix = "";
                while (epMatch.Success) { episodePrefix = $"【第{epMatch.Groups[1].Value}集】"; epMatch = epMatch.NextMatch(); }
                if (!string.IsNullOrEmpty(episodePrefix))
                    unitText = episodePrefix + "\n" + unitText;

                var unitKey = System.Text.RegularExpressions.Regex.Match(unitMatches[i].Value, @"[\d.]+[a-zA-Z]?").Value;
                if (unitKey.Length > 0) unitTextMap[unitKey] = unitText;

                var unitScope = ScopeUnitAssetNames(unitKey, unitBindingScopes, charNames, propNames, envNames, effectNames, charNameList, propNameList, envNameList, effectNameList, autoSupplementedNames);

                int? epNumber = null;
                var epNumM = Regex.Match(episodePrefix, @"【第\s*(\d+)\s*集】");
                if (epNumM.Success && int.TryParse(epNumM.Groups[1].Value, out var parsedEp)) epNumber = parsedEp;
                var frameHint = BuildFrameBindingHintText(frameBindingsByShot, unitText, epNumber, unitKey);

                var (unitSkillText2, unitLockedSkills2) = BuildUnitSkillText(unitText, skills, charNameList, characterAssets);
                var prompts = await GenerateSeedancePrompts(
                    unitText, charKeywords, propKeywords, envKeywords, effectKeywords, shotAnalysis, stylePrompt, unitScope.CharNames, unitScope.PropNames, unitScope.EnvNames, unitScope.EffectNames, unitSkillText2, fightText,
                    apiUrl, apiKey, model, thinkingMode, frameHint, BuildKeyframeAnchor(projectId, unitText));
                prompts = PromptDialogueInjectGuard.Apply(prompts, unitText);
                prompts = PromptSkillGuard.FilterUnlockedSkills(prompts, skills.Select(s => s.Name ?? "").ToList(), unitLockedSkills2.Select(s => s.Name ?? "").ToList());
                prompts = PromptNameNormalizer.Normalize(prompts, unitText, unitScope.CharNameList, ref currentForm);
                prompts = PromptCharacterRefGuard.Apply(prompts, unitScope.CharNameList);
                prompts = PromptCombatStateGuard.Apply(prompts, unitText, unitScope.CharNameList);
                prompts = EnsureSkillRefImages(prompts, skills, unitLockedSkills2, unitScope.EffectNameList, unitText);
                prompts = PromptPropRefGuard.Apply(prompts, unitText, unitScope.PropNameList);
                prompts = PromptRefLineGuard.Apply(prompts, unitScope.CharNameList.Where(n => autoSupplementedNames == null || !autoSupplementedNames.Contains(n)).Concat(unitScope.PropNameList).Concat(unitScope.EnvNameList).Concat(unitScope.EffectNameList).Concat(skills.Select(s => s.Name ?? "")).Concat(unitLockedSkills2.Select(s => s.Name ?? "")));
                prompts = EnsureMaxNineRefImages(prompts);
                prompts = EnsureDialogueLines(prompts);
                prompts = EnsureActionCameraEmbedded(prompts);
                prompts = NormalizeTimeSegmentHeaders(prompts);
                prompts = NormalizeCompactTimeBlocks(prompts);
                prompts = PromptDialogueVerbatimGuard.Apply(prompts, unitText);
                prompts = PromptSkillShoutGuard.Apply(prompts, unitLockedSkills2, unitScope.CharNameList);
                prompts = PromptSkillEntityGuard.Apply(prompts);
                prompts = EnsureNegativePromptLines(prompts);
                prompts = PromptInnerMonologueGuard.Apply(prompts);
                prompts = PromptPropUnseenGuard.Apply(prompts);
                prompts = PromptFacingGuard.Apply(prompts, unitScope.CharNameList);
                prompts = PromptPostOverlayGuard.Apply(prompts);
                unitPromptsList.Add(prompts);

                // 逐单元增量入库：跑完一个单元立即写入，页面可实时看到进度
                var parsed = SeedancePromptParser.Parse(prompts, projectId, unitScope.CharNameList, unitScope.PropNameList, unitScope.EnvNameList, unitScope.EffectNameList);
                if (parsed.Count > 0)
                    _db.InsertSeedancePrompts(projectId, parsed);
                var unitLabel = unitMatches[i].Value.Trim('【', '】');
                onProgress?.Invoke(new StageProgressUpdate
                {
                    Done = i + 1,
                    Total = unitMatches.Count,
                    CurrentUnit = unitLabel,
                    CurrentPhase = "提示词生成",
                    Message = $"已完成 {unitLabel}"
                });
            }
        }

        var finalPrompts = string.Join("\n\n", unitPromptsList);

        // Stage 5 镜头与 Stage 9 实际镜头对照，缺失或漏镜头单元补跑，避免静默缺单元
        var expectedUnitShots = CountShotsByUnit(shotPlan);
        var generatedUnitShots = CountShotsByUnit(finalPrompts);
        var incompleteUnits = expectedUnitShots
            .Where(kv => generatedUnitShots.TryGetValue(kv.Key, out var got) ? got != kv.Value : true)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var missingUnit in incompleteUnits)
        {
            if (!unitTextMap.TryGetValue(missingUnit, out var unitText)) continue;

            var repairScope = ScopeUnitAssetNames(missingUnit, unitBindingScopes, charNames, propNames, envNames, effectNames, charNameList, propNameList, envNameList, effectNameList, autoSupplementedNames);

            int? repairEpNumber = null;
            var repairEpM = Regex.Match(unitText, @"【第\s*(\d+)\s*集】");
            if (repairEpM.Success && int.TryParse(repairEpM.Groups[1].Value, out var repairParsedEp)) repairEpNumber = repairParsedEp;
            var repairFrameHint = BuildFrameBindingHintText(frameBindingsByShot, unitText, repairEpNumber, missingUnit);

            string? repaired = null;
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var (unitSkillTextRepair, unitLockedSkillsRepair) = BuildUnitSkillText(unitText, skills, charNameList, characterAssets);
                var retry = await GenerateSeedancePrompts(
                    unitText, charKeywords, propKeywords, envKeywords, effectKeywords, shotAnalysis, stylePrompt,
                    repairScope.CharNames, repairScope.PropNames, repairScope.EnvNames, repairScope.EffectNames, unitSkillTextRepair, fightText,
                    apiUrl, apiKey, model, thinkingMode, repairFrameHint, BuildKeyframeAnchor(projectId, unitText));
                retry = PromptDialogueInjectGuard.Apply(retry, unitText);
                retry = PromptSkillGuard.FilterUnlockedSkills(retry, skills.Select(s => s.Name ?? "").ToList(), unitLockedSkillsRepair.Select(s => s.Name ?? "").ToList());
                retry = PromptNameNormalizer.Normalize(retry, unitText, repairScope.CharNameList, ref currentForm);
                retry = PromptCharacterRefGuard.Apply(retry, repairScope.CharNameList);
                retry = PromptCombatStateGuard.Apply(retry, unitText, repairScope.CharNameList);
                retry = EnsureSkillRefImages(retry, skills, unitLockedSkillsRepair, repairScope.EffectNameList, unitText);
                retry = PromptPropRefGuard.Apply(retry, unitText, repairScope.PropNameList);
                retry = PromptRefLineGuard.Apply(retry, repairScope.CharNameList.Where(n => autoSupplementedNames == null || !autoSupplementedNames.Contains(n)).Concat(repairScope.PropNameList).Concat(repairScope.EnvNameList).Concat(repairScope.EffectNameList).Concat(skills.Select(s => s.Name ?? "")).Concat(unitLockedSkillsRepair.Select(s => s.Name ?? "")));
                retry = EnsureMaxNineRefImages(retry);
                retry = EnsureDialogueLines(retry);
                retry = EnsureActionCameraEmbedded(retry);
                retry = NormalizeTimeSegmentHeaders(retry);
                retry = NormalizeCompactTimeBlocks(retry);
                retry = PromptDialogueVerbatimGuard.Apply(retry, unitText);
                retry = PromptSkillShoutGuard.Apply(retry, unitLockedSkillsRepair, repairScope.CharNameList);
                retry = PromptSkillEntityGuard.Apply(retry);
                retry = EnsureNegativePromptLines(retry);
                retry = PromptInnerMonologueGuard.Apply(retry);
                retry = PromptPostOverlayGuard.Apply(retry);

                var unitMarker = new System.Text.RegularExpressions.Regex(@"【单元" + System.Text.RegularExpressions.Regex.Escape(missingUnit) + @"】");
                if (!unitMarker.IsMatch(retry)) continue;

                var repairedParsed = SeedancePromptParser.Parse(retry, projectId, repairScope.CharNameList, repairScope.PropNameList, repairScope.EnvNameList, repairScope.EffectNameList);
                if (repairedParsed.Count == 0) continue;
                _db.DeleteSeedancePromptsByUnit(projectId, missingUnit);
                _db.InsertSeedancePrompts(projectId, repairedParsed);
                repaired = retry;
                break;
            }

            if (repaired == null)
                throw new InvalidOperationException("提示词生成缺少单元 " + missingUnit + "，重试后仍未生成完整单元");

            unitPromptsList.Add(repaired);
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = unitMatches.Count,
                Total = unitMatches.Count,
                CurrentUnit = "单元" + missingUnit,
                CurrentPhase = "提示词生成",
                Message = "已补生成缺失单元 单元" + missingUnit
            });
        }

        finalPrompts = string.Join("\n\n", unitPromptsList);
        return finalPrompts;
    }

    private sealed class UnitBindingScope
    {
        public HashSet<string> Characters = new(StringComparer.Ordinal);
        public HashSet<string> Environments = new(StringComparer.Ordinal);
        public HashSet<string> Props = new(StringComparer.Ordinal);
        public HashSet<string> Effects = new(StringComparer.Ordinal);
    }

    private sealed class UnitNameScope
    {
        public string CharNames = "", PropNames = "", EnvNames = "", EffectNames = "";
        public List<string> CharNameList = new(), PropNameList = new(), EnvNameList = new(), EffectNameList = new();
    }

    // ========== 分镜帧 ↔ 资产绑定（FrameAssetBindings）：SD / H3 统一以「帧绑定」作为每个镜头参考图/素材的唯一来源 ==========

    /// <summary>镜头键：集|单元|镜头（与 StoryboardFrames 的 EpisodeNumber/UnitNumber/ShotNumber、SeedancePrompts 的集/单元/镜头编号对齐）。</summary>
    private static string? ShotFrameKey(int? episodeNumber, string? unitNumber, string? shotNumber)
    {
        if (!episodeNumber.HasValue) return null;
        var unit = unitNumber?.Trim();
        var shot = shotNumber?.Trim();
        if (string.IsNullOrWhiteSpace(unit) || string.IsNullOrWhiteSpace(shot)) return null;
        return episodeNumber.Value + "|" + unit + "|" + shot;
    }

    /// <summary>
    /// 读取某项目落库的 FrameAssetBindings，按「集|单元|镜头」键索引（同一镜头命中多帧时合并，绑定行按 SortOrder 升序），
    /// 替代旧逻辑的「镜头文本子串匹配 + 匹配失败退化为全量资产名单」。
    /// 缺表或读取失败返回空字典，调用方仍走各自旧回退，不影响无绑定表的老数据。
    /// </summary>
    private Dictionary<string, List<FrameAssetBinding>> LoadFrameBindingsByShot(int projectId)
    {
        var result = new Dictionary<string, List<FrameAssetBinding>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var keyByFrameId = new Dictionary<int, string>();
            foreach (var f in _db.GetAllFrames(projectId))
            {
                var key = ShotFrameKey(f.EpisodeNumber, f.UnitNumber, f.ShotNumber);
                if (key != null && !keyByFrameId.ContainsKey(f.FrameId)) keyByFrameId[f.FrameId] = key;
            }
            foreach (var b in _db.GetFrameAssetBindings(projectId))
            {
                if (!keyByFrameId.TryGetValue(b.FrameId, out var key)) continue;
                if (!result.TryGetValue(key, out var list)) { list = new List<FrameAssetBinding>(); result[key] = list; }
                list.Add(b);
            }
            foreach (var list in result.Values)
                list.Sort((a, b) => a.SortOrder.CompareTo(b.SortOrder));
        }
        catch { /* 表缺失/读取失败：返回空，调用方自行回退 */ }
        return result;
    }

    /// <summary>按绑定顺序截取最多 9 个参考素材（与 SD 提示词 @图N 上限保持一致），顺序即 @图片N 顺序。</summary>
    private static List<FrameAssetBinding> CapFrameBindings(IReadOnlyList<FrameAssetBinding> bindings) =>
        bindings.OrderBy(b => b.SortOrder).Take(9).ToList();

    /// <summary>把帧绑定转成 H3 锁定表 (编号, 资产名)：编号按绑定顺序从 1 连续编号。</summary>
    private static List<(int N, string Name)> NumberFrameBindings(IReadOnlyList<FrameAssetBinding> bindings) =>
        bindings
            .Select(b => b.Name?.Trim() ?? "")
            .Where(n => n.Length > 0)
            .Select((name, i) => (i + 1, name))
            .ToList();

    /// <summary>
    /// 为 SD 逐单元文本构造「分镜帧参考绑定」提示行：逐镜头列出该镜头帧绑定清单里的参考资产，
    /// 供 LLM 生成每个镜头 @图N 行时只绑定本镜绑定清单中的资产。绑定表里没有的镜头不输出
    /// （SD 仍可引用单元名单，绝不因缺表而空绑定、也绝不回退全量自选）。
    /// </summary>
    private static string? BuildFrameBindingHintText(
        IReadOnlyDictionary<string, List<FrameAssetBinding>> byShot,
        string? unitText, int? episodeNumber, string? unitKey)
    {
        if (byShot.Count == 0 || string.IsNullOrWhiteSpace(unitText)) return null;
        var labels = new List<string>();
        // 兼容两种镜头编号写法：单帧构造文本用「【镜头1.1-1】」标题行；Stage 5 整集文本用「- **镜头编号**: 1.1-1」字段行
        foreach (System.Text.RegularExpressions.Match m in Regex.Matches(unitText, @"(?:【镜头\s*([\d]+\.[\d]+-[\d]+[a-zA-Z]?)\s*】|\*\*镜头编号\*\*\s*[:：]\s*([\d]+\.[\d]+-[\d]+[a-zA-Z]?))"))
        {
            var label = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
            if (labels.Contains(label)) continue;
            if (Regex.IsMatch(label, @"^[\d]+\.[\d]+-[\d]+[a-zA-Z]?$")) labels.Add(label);
        }
        if (labels.Count == 0) return null;
        var sb = new StringBuilder();
        foreach (var label in labels)
        {
            var key = ShotFrameKey(episodeNumber, unitKey, label);
            if (key == null || !byShot.TryGetValue(key, out var bindings) || bindings.Count == 0) continue;
            var chars = bindings.Where(b => b.Category == "Character").Select(b => b.Name).Distinct().ToList();
            var envs = bindings.Where(b => b.Category == "Environment").Select(b => b.Name).Distinct().ToList();
            var props = bindings.Where(b => b.Category == "Prop").Select(b => b.Name).Distinct().ToList();
            var fx = bindings.Where(b => b.Category == "Effect").Select(b => b.Name).Distinct().ToList();
            sb.Append("【镜头").Append(label).Append("】")
              .Append("人物参考：").Append(chars.Count > 0 ? string.Join("、", chars) : "无").Append("；")
              .Append("场景参考：").Append(envs.Count > 0 ? string.Join("、", envs) : "无").Append("；")
              .Append("道具参考：").Append(props.Count > 0 ? string.Join("、", props) : "无").Append("；")
              .Append("特效参考：").Append(fx.Count > 0 ? string.Join("、", fx) : "无").AppendLine();
        }
        return sb.Length > 0 ? sb.ToString().TrimEnd() : null;
    }

    /// <summary>读取 Stage 4 落库的「单元资产绑定」，按单元号索引（只取规范资产名集合）。缺表/查询失败时返回空字典，调用方自动回退全项目名单。</summary>
    private Dictionary<string, UnitBindingScope> LoadUnitBindingScopes(int projectId)
    {
        var result = new Dictionary<string, UnitBindingScope>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var b in _db.GetUnitAssetBindings(projectId))
            {
                if (!result.TryGetValue(b.UnitNumber, out var scope))
                {
                    scope = new UnitBindingScope();
                    result[b.UnitNumber] = scope;
                }
                switch (b.Category)
                {
                    case "Character": scope.Characters.Add(b.Name); break;
                    case "Environment": scope.Environments.Add(b.Name); break;
                    case "Prop": scope.Props.Add(b.Name); break;
                    case "Effect": scope.Effects.Add(b.Name); break;
                }
            }
        }
        catch { /* 表缺失或读取失败时不生效，回落全项目名单，保证旧流程不回退 */ }
        return result;
    }

    /// <summary>把某单元的资产名单收窄为该单元「绑定资产」，使提示词生成的 @图N/@角色引用 等只能引用分集细化/分镜绑定的资产，杜绝 LLM 从全项目资产里自选。</summary>
    private UnitNameScope ScopeUnitAssetNames(string unitKey, IReadOnlyDictionary<string, UnitBindingScope> scopes,
        string charNames, string propNames, string envNames, string effectNames,
        List<string> charNameList, List<string> propNameList, List<string> envNameList, List<string> effectNameList,
        List<string>? autoSupplementedNames)
    {
        var global = new UnitNameScope
        {
            CharNames = charNames, PropNames = propNames, EnvNames = envNames, EffectNames = effectNames,
            CharNameList = charNameList, PropNameList = propNameList, EnvNameList = envNameList, EffectNameList = effectNameList
        };
        if (string.IsNullOrEmpty(unitKey) || !scopes.TryGetValue(unitKey, out var scope)) return global;

        bool IsSupplement(string n) => autoSupplementedNames != null && autoSupplementedNames.Contains(n);
        var charList = charNameList.Where(n => scope.Characters.Contains(n) || IsSupplement(n)).ToList();
        var propList = propNameList.Where(n => scope.Props.Contains(n)).ToList();
        var envList = envNameList.Where(n => scope.Environments.Contains(n)).ToList();
        var effectList = effectNameList.Where(n => scope.Effects.Contains(n)).ToList();
        // 绑定存在但和当前资产库完全对不上（空绑定/资产被删）时回退全局名单，宁可多给不可漏资产
        if (charList.Count == 0 && propList.Count == 0 && envList.Count == 0 && effectList.Count == 0) return global;

        return new UnitNameScope
        {
            CharNames = string.Join("、", charList.Where(n => !IsSupplement(n)).Select(NormalizeCharacterName).Distinct()),
            PropNames = string.Join("、", propList),
            EnvNames = string.Join("、", envList.Select(NormalizeSceneName).Distinct()),
            EffectNames = string.Join("、", effectList.Select(NormalizeSceneName).Distinct()),
            CharNameList = charList,
            PropNameList = propList,
            EnvNameList = envList,
            EffectNameList = effectList
        };
    }


    private async Task<string> ExtractCharKeywords(string characters, string shotPlan, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var systemPrompt = "你是一个角色描述提取专家。从角色资产中提取每个角色的关键描述词。\n只输出纯文本关键词，每条格式：角色名: 外貌关键词| 服装关键词| 表情关键词| 动作关键词\n不要额外解释。";
        var userMsg = "【角色资产】\n" + characters + "\n\n" + "【镜头规划】\n" + shotPlan;
        return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, thinkingMode: thinkingMode);
    }

    private async Task<string> ExtractEnvKeywords(string environments, string props, string shotPlan, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var systemPrompt = "你是一个场景描述提取专家。从场景资产和道具资产中提取每个场景的关键描述词。\n只输出纯文本关键词，每条格式：场景名: 空间关键词| 色调关键词| 光关键词| 道具关键词\n不要额外解释。";
        var userMsg = "【场景资产】\n" + environments + "\n\n" + "【道具资产】\n" + props + "\n\n" + "【镜头规划】\n" + shotPlan;
        return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, thinkingMode: thinkingMode);
    }

    private async Task<string> ExtractPropKeywords(string props, string shotPlan, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var systemPrompt = "你是一个道具描述提取专家。从道具资产中提取每个道具的关键描述词。\n只输出纯文本关键词，每条格式：道具名: 外观关键词| 材质关键词| 尺寸关键词| 使用方式关键词\n不要额外解释。";
        var userMsg = "【道具资产】\n" + props + "\n\n" + "【镜头规划】\n" + shotPlan;
        return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, thinkingMode: thinkingMode);
    }

    private async Task<string> ExtractEffectKeywords(string effects, string shotPlan, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var systemPrompt = "你是一个特效描述提取专家。从特效资产中提取每个特效的关键描述词。\n只输出纯文本关键词，每条格式：特效名: 形态关键词| 色调关键词| 光效关键词| 触发时机关键词\n不要额外解释。";
        var userMsg = "【特效资产】\n" + effects + "\n\n" + "【镜头规划】\n" + shotPlan;
        return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, thinkingMode: thinkingMode);
    }

    private async Task<string> AnalyzeShotTransition(string shotPlan, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var systemPrompt = "你是一个镜头衔接状态分析专家。分析镜头规划中每个镜头（含第一个）的开头与结尾画面状态，解决多镜连续生成视频时镜头接不上、站位突变的问题。\n对每个镜头输出一个结构化衔接块，格式严格如下：\n【镜头X.Y-Z 衔接块】\n上一镜结尾:（本镜的前一镜头结束时：主要角色各在画面什么方位（画面左/右/中央/近前景/远背景）、朝向、双方距离、景别、最后动作定格；第一镜写\"无\"）\n本镜开头:（本镜开始时的初始画面状态：主要角色站位/朝向/双方距离/景别，必须与上一镜结尾吻合，禁止无来源瞬移或双方互换位置）\n本镜结尾:（本镜结束时的定格状态：主要角色方位/朝向/双方距离/景别，作为下一镜的衔接依据；第一镜也要写）\n规则：同一镜头内站位从开头到结尾必须连续走位过渡，走位写明起止位置；相邻镜头（尤其打斗/动作类）必须保持出镜角色不变、相对方位延续、景别可切换但画面主体连续；正文没交代位置的角色，按场景空间与出场逻辑推导合理站位。\n只输出各镜头的衔接块，禁止额外解释，每个块之间用空行分隔。";
        var userMsg = "【镜头规划】\n" + shotPlan;
        return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMsg, thinkingMode: thinkingMode);
    }

    /// <summary>
    /// L3 关键帧锚定文本：该项目该集已生成关键帧时返回渲染文本，否则返回 null。
    /// 未生成关键帧的项目返回 null —— 阶段 9 提示词与以前逐字一致（关键帧是可选增强，不是必经环节）。
    /// </summary>
    private string? BuildKeyframeAnchor(int projectId, string unitText)
    {
        try
        {
            var rows = _db.GetKeyframes(projectId);
            if (rows.Count == 0) return null;

            var epMatch = Regex.Match(unitText ?? "", @"【第\s*(\d+)\s*集】");
            var episode = epMatch.Success && int.TryParse(epMatch.Groups[1].Value, out var ep) ? ep : 0;

            var scoped = rows
                .Where(k => k.EpisodeNumber == episode || k.EpisodeNumber == 0)
                .OrderBy(k => k.EpisodeNumber).ThenBy(k => k.SortOrder)
                .ToList();
            if (scoped.Count == 0) return null;

            return RenderKeyframeAnchor(scoped);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按集号渲染关键帧锚定（H3 逐镜头生成线用：H3 是单镜头调用，没有「单元文本」可供解析集号）。</summary>
    private string? BuildKeyframeAnchorForEpisode(int projectId, int episode)
    {
        try
        {
            var rows = _db.GetKeyframes(projectId);
            if (rows.Count == 0) return null;

            var scoped = rows
                .Where(k => k.EpisodeNumber == episode || k.EpisodeNumber == 0)
                .OrderBy(k => k.EpisodeNumber).ThenBy(k => k.SortOrder)
                .ToList();
            return scoped.Count == 0 ? null : RenderKeyframeAnchor(scoped);
        }
        catch
        {
            return null;
        }
    }

    private static string RenderKeyframeAnchor(List<ProjectKeyframe> scoped)
    {
        var sb = new StringBuilder();
        sb.AppendLine("本集已锁定 " + scoped.Count + " 张关键帧。关键帧定死的东西，后续镜头禁止漂移：角色站位与朝向、道具状态、场景朝向、线索可见性、镜头衔接一律以本表为准；镜头时间块正文的站位、道具状态与线索呈现必须与对应节点一致。");
        foreach (var k in scoped)
        {
            sb.AppendLine("◆ " + (k.NodeLabel ?? "关键帧") + "（镜头 " + (k.ShotLabel ?? "-") + "）");
            void Line(string label, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value)) sb.AppendLine("  " + label + "：" + value.Trim());
            }
            Line("构图", k.Composition);
            Line("锁定站位", k.LockedCharacters);
            Line("锁定道具", k.LockedProps);
            Line("场景朝向", k.LockedSceneDirection);
            Line("线索可见性", k.ClueVisible);
            Line("衔接下一帧", k.NextConnection);
        }
        return sb.ToString().TrimEnd();
    }

    private async Task<string> GenerateSeedancePrompts(
        string shotPlan,
        string charKeywords,
        string propKeywords,
        string envKeywords,
        string effectKeywords,
        string shotAnalysis,
        string stylePrompt,
        string charNames,
        string propNames,
        string envNames,
        string effectNames,
        string skillText,
        string fightText,
        string apiUrl, string apiKey, string model,
        string? thinkingMode = null,
        string? frameBindingHint = null,
        string? keyframeAnchor = null)
    {
        // Stage 9 使用紧凑 @图N + 大块时间戳模板；下方旧 systemPrompt 构建已不参与最终调用，保留以兼容历史逻辑。
        var seedancePrompt = BuildSeedanceSystemPrompt(stylePrompt, skillText, fightText, charNames);
        var systemPrompt = "你是一位顶级的 AI 提示词架构师，精通所有大模型 prompt 逻辑，善于把需求转化为高质量、可执行的提示词；同时是专业的 Seedance 2.0 视频生成提示词工程师。要求：清晰结构化排版，无废话、不闲聊、不凑字数；严格依据给定的【镜头规划】与角色/道具/场景资产名单输出，禁止自行增删剧情、角色、道具、场景与台词。\n\n严格按照以下格式输出每个镜头的提示词（不要额外解释，直接输出格式内容）。每个镜头必须完整包含以下全部行，一行都不能少：标题行、类型、参考图、[组合]、@角色引用、@道具引用、场景锚定引用、景别、场景、灯光、画质、约束、禁止、视频风格，以及每个时间段的完整字段：\n\n【第X集】【单元X.Y】【镜头X.Y-Z】（镜头编号必须与单元编号一致，例如单元2.3的镜头写【镜头2.3-1】，禁止只写【镜头1】）\n类型:场景类型（从：打斗/动作、文戏/情感、追逐/逃亡、悬疑/惊悚、日常/喜剧、高潮/对决 中选择最匹配的一项；日常对话、打脸、斗嘴、审问、追查一律按「日常/喜剧」或「文戏/情感」标注，「悬疑/惊悚」仅限剧情设定本身神秘压迫的镜头（如深夜神秘来客、暗中窥视、阴森据点），禁止仅因场景是夜晚、灯光昏暗、月光冷色就标悬疑/惊悚，夜晚发生的喜剧、打脸、追逐、对话戏按实际剧情类型标注；禁止把喜剧冲突、公务审案、日常收尾误标为悬疑/惊悚）\n参考图:第一张(@图1)为[角色A]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[角色B]形象参考，保持外貌、发型、服装、气质一致（每名出镜角色按@角色引用顺序各占一张）；随后按@道具引用顺序为每个出镜道具各占一张（格式“第X张(@图X)为[道具名]道具参考，保持外观、材质、大小一致”）；若本镜头涉及【特效资产名单】中的特效，再按@特效引用顺序为每个特效各占一张（格式“第X张(@图X)为[特效名]特效参考，保持形态、色调、氛围一致”）；涉及【技能库】技能的镜头，按特效参考格式为每个技能各占一张（格式“第X张(@图X)为[技能名]特效参考，保持形态、色调、氛围一致”，技能名必须与【技能库】完全一致）；倒数第二张(@图N-1)为[场景名]场景参考，保持空间布局、色调、氛围一致；最后一张(@图N)为画面风格参考（光影、色调、材质质感）\n[组合:场景名-时间段-时长s]（必须带方括号 [ ]；时间段用清晨/上午/白天/午后/黄昏/夜等词，禁止写成 0-5秒 这类区间；场景名必须与下方「场景锚定引用」完全一致）\n@角色引用:[角色名][角色名]\n@道具引用:[道具名][道具名]（本镜头实际出镜的道具，无道具时省略该行）\n【道具归属约束】@道具引用只能列本镜头真实出镜且归属正确的道具：专属道具只能归其主人使用，禁止把某角色的专属道具写进其他角色的镜头；剧本未指定专属道具时用通用物品名，拿不准就省略该道具，宁缺毋滥\n场景锚定引用:场景名\n景别:景别描述（远景/全景/中景/近景/特写，可含过渡如“远景→大远景”）\n场景:场景描述\n灯光:灯光描述\n画质:电影级色彩，浅景深，4K，24fps，柔化高光\n约束:运镜强度匹配场景类型——文戏/情感保持稳定镜头缓推；打斗/动作、追逐、高潮对决使用快速推拉摇移、手持晃动、甩镜、跟随运动，命中瞬间加顿帧/慢动作；禁止BGM，禁止字幕\n禁止:禁止事项\n视频风格:" + stylePrompt + "\n\n0-4秒[1]\n时长:X秒\n主体:主体描述\n动作:@角色名:动作描述\n对话:角色名说\"台词\"；角色名说\"台词\"（该时间段无人说话写\"无\"）\n姿态:姿态描述\n景别:景别描述（远景/全景/中景/近景/特写，可含过渡如“远景→大远景”）\n场景:场景描述\n运镜:运镜描述（根据场景类型匹配：文戏平稳缓推+微表情特写；打斗/动作使用快速推拉摇移、手持晃动、甩镜、命中顿帧；追逐用跟随环绕；悬疑用缓慢推进变焦）\n灯光:灯光描述\n画质:画质描述\n约束:约束条件\n\n每个镜头单元按上述模板输出，用中文。每两个镜头之间用空行分隔。\n\n【时长规则】\n1. 每条提示词的总时长只能从 5秒、11秒、15秒 三档中选择（视频生成API只支持这三档）；[组合:场景名-时间-时长s] 的时长s 必须等于各时间段 时长 之和，且与各时间段 0-X秒[N] 的区间完全一致，禁止出现 4秒、6秒、8秒、10秒、12秒等其它总时长。\n2. 打斗/动作、高潮/对决类镜头：默认按11秒设计（可拆两段，如 0-5秒[1] 时长5秒 + 5-11秒[2] 时长6秒），动作连贯不割裂，突出攻防回合、打击感与速度感；仅重大打斗/终极对决才按15秒三段设计——0-5秒[1]（起手/试探）、5-10秒[2]（对拼/连击）、10-15秒[3]（收招/大招），每段时长写5秒，总时长15秒，动作必须足够密集饱满，禁止为凑时长注水。\n3. 文戏/情感、日常/喜剧、悬疑/惊悚等其他类型镜头：一律默认5秒（单段）；先统计本镜头全部台词（含旁白）字数，合计不超过20字用5秒，只有合计确实超过20字时才允许用11秒（可拆两段，如 0-5秒[1] 时长5秒 + 5-11秒[2] 时长6秒）；禁止文戏/情感、日常/喜剧、悬疑/惊悚类镜头使用15秒；时间段必须连续覆盖整个总时长，不得有空隙或重叠。\n4. 台词长度必须匹配总时长，防止语速过快：5秒镜头全片台词合计不超过20字（约1句）；11秒镜头合计不超过40字（约2-3句）；15秒镜头合计不超过60字（约3-4句）；台词超过当前档位上限时，该镜头必须升级到更高时长档位（5→11→15），禁止超载；只有15秒镜头台词仍超60字时，才允许把台词拆分到多个时间段；每个有台词的时间段在「约束」行写明「语速从容自然，贴合时长，禁止赶拍」。\n5. 时长宁短勿长：单镜头内容不足以填满当前档位时，宁可拆成多个短镜头，禁止为凑时长注水、加无关内容或拖慢节奏。\n1. @角色引用 行必须严格使用 [角色名][角色名] 格式（例如 [角色名1][角色名2][角色名3]），多个角色名必须用方括号分隔，禁止连写，禁止用 @、顿号、空格代替方括号。\n2. @角色引用 中的角色名只能从【角色资产名单】中选择且只写主体名，禁止改名或自创名字，禁止带括号内的身份或描述性修饰（例如必须写名单中的主体名，禁止写“角色名（身份描述）”这类带括号的变体）。\n3. 场景锚定引用 行只能从【场景资产名单】中选择场景名；场景名只写地点主体（如“书房”“文试场”“放榜处”），禁止附加时间段、天气、氛围等修饰词（禁止写“书房（清晨）”“书房重开”这类名字），时间段一律写在「场景」「灯光」字段里；若镜头实际场景不在名单中，选择语义最接近的名单场景名。\n4. [组合:场景名-时间段-时长s] 的场景名必须与「场景锚定引用」完全一致，且只能从【场景资产名单】中选择，禁止使用名单外的场景名（如“隔壁铺子柜台前”），场景名只写地点主体、禁止带时间或氛围修饰（时间段单独写在“-时间段-”位置）；时间段用清晨/上午/白天/午后/黄昏/夜等词，禁止写时间区间。\n5. 必须严格依据【镜头规划】中对应镜头的镜头描述生成内容：主体、动作、台词、场景、道具均以镜头描述为准，禁止新增分镜外的人物、情节或道具。\n6. 参考图 行必须完整覆盖本镜头：先按@角色引用顺序逐张列出出镜角色（每名角色一张，格式“第一张(@图1)为[角色名]形象参考，保持外貌、发型、服装、气质一致”），再按@道具引用顺序列出出镜道具（每个道具一张，格式“第X张(@图X)为[道具名]道具参考，保持外观、材质、大小一致”），再列出本镜头涉及的特效或技能库技能（特效格式“第X张(@图X)为[特效名]特效参考，保持形态、色调、氛围一致”，特效名必须与【特效资产名单】一致且只写主体名、禁止带括号描述；技能格式“第X张(@图X)为[技能名]特效参考，保持形态、色调、氛围一致”，技能名必须与【技能库】一致），再列出所在场景（格式“倒数第二张(@图N-1)为[场景名]场景参考，保持空间布局、色调、氛围一致”），最后一张(@图N)为画面风格参考（光影、色调、材质质感）；角色名必须与【角色资产名单】一致且只写主体名（禁止“角色名（身份描述）”这类带括号描述的名字），道具名必须与【道具资产名单】一致，场景名必须与【场景资产名单】一致且只写地点主体（禁止“场景名（清晨）”这类带时间/氛围修饰的名字），场景与场景锚定引用一致；只有一名角色时省略中间角色行，无角色出镜时直接从道具/场景参考开始，无道具出镜时跳过道具参考行，无特效出镜时跳过特效参考行；整条参考图列表不得超过9张，超出时按角色优先、道具其次、特效再次、场景第四的优先级取舍（场景参考原则上保留），多个道具可合并为一张参考图。\n7. 每个时间段必须输出 对话 行，格式：对话:角色名说\"台词\"；角色名说\"台词\"。有台词的时间段必须完整复述【镜头规划】中该镜头的【对话/台词】并归属到正确角色，禁止改写、删减或新增台词；该时间段无人说话必须写 对话:无；若【镜头规划】中该镜头对话/台词为「无」，但镜头描述中明确包含口头交流动作（求/应/说/问/答/喊等），必须按描述补出自然台词，禁止写「无」。\n8. 每个时间段必须描述所有在场角色的动作、姿态与站位，站位必须写明角色间的相对朝向（如“面对面、视线相对”“侧身相对”“背对镜头”等）；对话/对峙类镜头中，参与对话的角色必须正面相对、目光接触，具体距离依镜头描述与场景而定，禁止硬性写死步数或距离；禁止背对背站立，禁止背对镜头说话或做主要动作（除非分镜明确要求背面出场）；不参与本时间段主要动作的角色必须写\"站在原地、双脚着地、保持位置\"，禁止无原因悬空或漂移；打斗/动作/追逐/高潮类镜头中，动作角色允许跳跃、腾空、翻滚，但必须有明确的起跳与落地逻辑，落地时双脚踩实地面，禁止全程无支撑悬浮；旁观与文戏角色始终保持地面锚定。\n9. @道具引用 行必须严格使用 [道具名][道具名] 格式（例如 [道具名1][道具名2]），只能从【道具资产名单】中选择本镜头实际出镜的道具，禁止自创道具名或列出分镜中没有出现的道具；无道具出镜的镜头不输出该行。\n10. 对话/交流类镜头允许正反打：谁说话镜头对准谁（说话人近景/特写），说话人切换时镜头随之切换（A说→对A，B说→对B），切换干净利落；非对话镜头保持单一连续机位，禁止无关的机位切换、转场或剪辑描述。\n11. 镜头编号必须为 X.Y-Z 格式（如【镜头2.3-1】），与单元编号一致，禁止只写【镜头1】【镜头2】。\n12. 每个镜头必须完整输出「视频风格」行（原样复制给定风格）与「禁止」行（列出本镜头的禁止事项），禁止遗漏这两行。\n13. 每个镜头必须在标题行（【镜头X.Y-Z】）之后立即输出「类型:」行，格式为「类型:场景类型」，从规定类型中选择最匹配的一项（打斗/动作、文戏/情感、追逐/逃亡、悬疑/惊悚、日常/喜剧、高潮/对决），禁止遗漏、禁止用其他词代替或省略。\n14. 每个镜头的标题行必须完整输出「【第X集】【单元X.Y】【镜头X.Y-Z】」，禁止省略「【第X集】【单元X.Y】」前缀、禁止只写「【镜头X.Y-Z】」；相邻镜头即使属于同一单元，也必须各自独立输出完整标题行，禁止合并或复用上一个镜头的标题。\n15. 动作/姿态/主体 描述禁止用「应了一声」「应声」「哼了一声」「嗯了一声」「欲言又止」「张了张嘴」「刚要开口」等暗示发声却无具体台词的表述；角色确需发声（应答、吟诗、惊呼、嘟囔等）时，必须把具体内容写入该时间段「对话」行（如 顾九霄说\"嗯\"），动作行只写肢体动作、不再写发声；纯无声的欲言/憋气/抿嘴等表情必须明确写「未出声」「无声」「闭嘴」，禁止有口型无台词的描述；「不敢出声」「未出声」等明确无声表述允许；群体惊叫、音效等环境声不作台词处理。";
        systemPrompt += "\n\n【打斗回合硬约束】\n1. 打斗/动作、高潮/对决镜头必须按 Stage 5 锁定的动作链与回合数展开，禁止把多回合压缩成一次挥击+特效；\n2. 回合密度由动作链决定：5秒镜头同样允许多个快速攻防回合（A攻→B防/反→A变招→命中/压制），11秒/15秒可承载更多回合，禁止因档位低而减少交锋频率；\n3. 每个时间段双方都必须有动作与受击反馈（格挡、闪避、后仰、倒退、倒地、武器脱手等），禁止一方出手、另一方原地等待或只出特效；\n4. 镜头首尾动作与下一镜头衔接，禁止无来源瞬移或双方突然互换位置。";
        systemPrompt += "\n\n【出拳速度与打击感】\n1. 出手前必须有明确蓄力（沉腰、后脚蹬地、肩背发力、拳锋后拉），出手瞬间加速度爆发，拳锋/刃锋带动态模糊与残影，禁止匀速出拳；\n2. 慢动作只允许用于命中/对撞/受击瞬间（约0.2-0.4秒），随后立即恢复常速或加速，禁止整段慢放、禁止全程高速模糊；\n3. 命中必须同时给出三层反馈：受击者身体反应（后仰/离地/倒飞/犁地）、环境反应（碎石、尘土、气浪、衣袍炸裂、武器脱手）、镜头反应（命中瞬间轻微冲击抖动）；\n4. 写清音效：挥拳呼啸、命中闷响/爆裂、碎石落地声；\n5. 禁止双方隔空比划、禁止拳锋未到敌人先倒、禁止命中后无反馈直接切镜。\n6. 武器与攻击必须自带视觉强化：每记出招刃锋/拳锋带明显光效与轨迹（剑芒、刀气、斩击弧光、罡气、属性流光），挥砍轨迹带残影拖尾，禁止裸武器干挥、禁止无光效的纯动作对砍。\n7. 燃度要求：出手快、连招密、冲击猛——攻防转换节奏紧凑，命中瞬间力量感拉满（武器劈裂装甲、冲击波荡开、地面碎裂、尘土飞扬），禁止轻飘飘接触、禁止软绵绵收招。";
        systemPrompt += "\n\n【文戏节奏例外】\n1. 当镜头描述以「【导演注意】」开头或明确写明快切、顿帧、节奏爆发、情绪高潮等节奏指令时，即使类型为文戏/情感，也必须按该指令执行：允许快切、顿帧、短镜切换，爆发瞬间可用顿帧或慢动作；\n2. 此时约束行必须写明具体节奏（如“快切+顿帧营造情绪爆发”），禁止按“稳定镜头缓推”处理；\n3. 未出现此类指令的文戏仍按默认稳定缓推执行。";
        systemPrompt += "\n\n【打斗时间轴动作流（优先于【时长规则】第2条）】\n1. 打斗/动作、高潮/对决镜头：连续机位只写一个时间段（如 0-11秒[1] 时长:11秒），禁止把完整交锋拆成两个静态时间段；只有镜头中途明确切换机位/景别（如从贴身对拼切到全景大招）时才允许拆段，拆出的每段仍必须是密集时间轴动作流；\n2. 单段内「动作」「运镜」「姿态」「约束」行必须以【镜头规划】的镜头时间轴/动作链为骨架，按 0-1秒、1-2秒、2-3秒……的小节逐段写出双方攻防、机位运动、受击反馈与物理细节，小节间隔约1秒，镜头时间轴为空时按动作链自然展开，禁止用一句笼统描述概括整个时间段；\n3. 每小节写清攻防双方：谁出手、什么招式、对手如何反应（侧闪/格挡/后仰/倒飞），禁止一方出手、另一方原地等待；\n4. 运镜与动作绑定：突进用低角度极速推镜或贴身跟拍，对拼用环绕或横移，命中用短促震动、顿帧/慢动作0.2-0.4秒，禁止整段匀速；\n5. 物理细节必须跟上：火星/水花/沙尘/衣袍发丝翻飞/碎布飞散/冲击波，命中瞬间给足环境与镜头反馈；\n6. 时间轴小节必须连续铺满整个时间段（如11秒镜头从0写到11秒），禁止只写半程、留空或时间重叠；\n7. 5秒打斗镜头同样按时间轴动作流写满4-5个小节，禁止因时长短而压缩成一次挥击+特效。\n示例（连续机位打斗单段）：\n0-11秒[1]\n时长:11秒\n动作:0-1秒 A爆发灵力持剑突进；1-2秒 A连斩两剑，B侧身后仰连续闪过，剑锋擦过衣袍；2-3秒 A第三剑劈落，B侧步切入剑势内侧；3-4秒 B左手扣住A持剑手腕，右拳蓄起气血；4-5秒 B一拳轰中A胸口，气血炸开，A倒飞；5-8秒 A翻身再战，双方双剑高频交格，火星四溅；8-11秒 A变招横扫，B后跃拉开距离，镜头急停\n运镜:0-1秒 低角度极速推镜；1-2秒 贴身平行极速横移；2-3秒 快速推近；3-4秒 特写扣腕蓄力；4-5秒 镜头短促震动跟随冲击；5-8秒 环绕跟拍；8-11秒 横扫轨迹跟拍末端急停";
        systemPrompt += "\n\n【动作行镜头流（机位嵌入动作，半秒级颗粒度）】\n1. 打斗/动作、高潮/对决镜头的「动作」行必须按 0-0.5秒、0.5-1秒、1-1.5秒……的半秒小节连续书写，镜头内容密集时不按整秒停顿；每个小节至少包含机位/透视、角色动作、物理反馈三层信息，禁止只写镜头叙事或只写静态场面；\n2. 机位与透视直接嵌入动作行（如 固定机位虫视贴地仰拍、低角度极速推镜、剑身特写锁焦、贴身平行极速横移、横扫轨迹跟拍末端急停），并用“急推、环绕、锁焦、震动”等词写明镜头响应；运镜行仍保留整段镜头轨迹总述，动作行里的机位词不得与运镜行冲突；\n3. 每个半秒小节都要推进事件：角色出招/位移/防御、对手反应（侧闪/格挡/后仰/倒飞）、环境反馈（沙尘/火星/水花/气浪/衣袍发丝翻飞/碎布飞散）、特效形态（发光烟雾对冲、冲击波掀起地面、剑鸣震颤），禁止两个相邻小节内容重复或原地等待；\n4. 所有在场角色都要有动作或明确站位（如 麻布细绳绷紧、插沙剑剧烈震动、盘腿闭目坐于画布中央），禁止某角色无来源消失或凭空出现；\n5. 4-5秒的短镜头至少写满4个小节，11秒镜头至少写满8个小节，15秒镜头至少写满12个小节，小节时间必须连续铺满全程，禁止留空或重叠；\n参考写法（0-2秒示例）：\n动作:0-0.5秒 固定机位虫视贴地仰拍，镜头位于画布左侧倾斜夸张透视，远景沙漠驿站沙尘飞扬，红衣少年盘腿闭目坐于画布中央，插沙的剑剧烈震动，麻布细绳绷紧，身后破败红帐被大风吹得翻飞；0.5-1秒 感应震得沙粒四溅，少年猛然睁眼瞳孔骤缩，大风卷起沙尘扑向镜头，毛边碎布四散，剑鸣震颤，沙土大团扬起；1-1.5秒 固定机位锁焦急推，一团黑烟自画面右下方急速冲来，插沙银剑剑身特写锁焦，银剑自动震出松软沙土，麻布细绳崩裂成不规则；1.5-2秒 低角度极速推镜，少年握剑猛然起身旋身斩向黑烟，黑烟中白发少年凝聚成形黑雾缭绕，双剑凌空首撞，发光烟雾与黑色烟雾进发对冲，脚下松软沙土被冲击波掀起";

        systemPrompt += "\n【动作行机位硬性开头】动作行每个时间小节必须以机位/透视词开头（如 \"0-0.5秒 低角度极速推镜，...\"、\"0.5-1秒 手持贴身跟拍，...\"），禁止出现没有机位词的时间小节；运镜行只保留整段轨迹总述，不能替代动作行内的机位。";
        if (!string.IsNullOrWhiteSpace(skillText))
        {
            systemPrompt += "\n\n【技能库规则】\n1. 技能库是本片可用的技能大招库，条目格式为「技能名（系别·层级）: 视频版提示词」，视频版提示词包含该技能的形态、镜头与节奏；技能库只用于角色归属过滤，不是白名单，分镜锁定的库外技能同样有效；设定了参考图的技能条目不附视频版提示词，而标注「形态、色调、氛围以参考图为准」；\n2. 仅当【镜头规划】中该镜头明确涉及技能库技能或分镜锁定技能（施法、招式、大招、法阵、合击、领域、特效爆发）时，才可引用该技能：该镜头的特效、动作、运镜与节奏必须严格以技能库中该技能的「视频版提示词」为蓝本（形态、色调、镜头、节奏）；条目标注「形态、色调、氛围以参考图为准」的技能，其形态与色调以 @图N 参考图为唯一依据，禁止再按任何文字描述改写；分镜锁定但库外无视频版提示词的技能，形态、色调与节奏以分镜描述为准，禁止自行改写成其他效果；\n3. 参考图行中该技能按特效参考格式占一张：第X张(@图X)为[技能名]特效参考，保持形态、色调、氛围一致；技能名必须与【技能库】或分镜「技能」行完全一致，禁止加括号描述或改名；\n4. 镜头规划未涉及任何技能时，禁止擅自加入技能或技能特效参考；普通特效/氛围光效允许自由补充；\n5. 技能与【特效资产名单】同规格处理：不替代角色、道具、场景参考；参考图超过9张时按原优先级取舍（技能归入特效一类）；\n6. 层级语义：T3 为常规技能、T4 为强力技能、T5 为终极大招；T5 大招只允许在高潮/终极对决等关键镜头释放，T3/T4 按战斗升级阶段依次使用，禁止在普通镜头放大招；\n7. 只有「释放技能」的镜头才出现技能特效（施法、出招、放大招时，特效严格按技能视频版提示词生成；条目标注「形态、色调、氛围以参考图为准」的技能，特效形态与色调按 @图N 参考图）；平A对砍、普通近身打斗禁止出现技能库大招级特效（法相、剑柱、领域、万剑归宗等），此类镜头按【打斗模板库】处理，但武器与攻击的「基础视觉强化」必须写足：剑芒刀气、刃锋光晕、斩击弧光、拳风腿影、命中火花、气浪冲击、残影拖尾；武器视觉强化属于表现层、不算新增道具，不受【内容规则】「禁止新增道具」限制，禁止把武器挥砍写成无光效的干巴巴动作。\n8. 本次镜头规划已锁定以下技能：见【技能库】列表；只要某个技能名出现在本镜头的镜头描述、动作、起始画面或结束画面中，该技能的「[技能名]特效参考」行就必须同时出现在参考图行中，禁止只写动作文字却漏掉技能参考图；未在参考图行中出现的技能名不得出现在动作描述中。\n9. 分镜「技能」行明确点名的技能即使不在技能库中，也必须原样保留到参考图行与动作/特效描述，禁止省略、改名或替换成库内其他技能。";
        }
        if (!string.IsNullOrWhiteSpace(fightText))
        {
            systemPrompt += "\n\n【打斗模板规则】\n1. 打斗模板库是本片可用的打斗动作模板（动作行/运镜行/约束行），条目格式为「模板名（T层级 · 时长s）: 适用;节拍;动作行;运镜行;约束行」，节奏总口诀：快—快—顿—重；\n2. 打斗/动作、追逐/逃亡、高潮/对决类镜头，必须从【打斗模板库】中选择最匹配的模板：该镜头的「动作」「运镜」「约束」三行以模板对应行为蓝本，角色名替换为实际角色（A=我方/主角，B=敌方/反派，C=灵宠/帮手），单段时间轴动作流的小节按模板节拍展开（连续机位不拆段），只有机位切换才拆段；\n3. 命中/对撞/镇压瞬间必须顿帧+慢动作，禁止全程匀速；\n4. 非打斗镜头禁止套用打斗模板；\n5. 打斗镜头同时涉及技能库技能时：动作/运镜/约束按打斗模板，特效形态与节奏按技能的「视频版提示词」（技能条目标注「形态、色调、氛围以参考图为准」的，特效形态与色调按 @图N 参考图）。\n6. 【打斗效果词库】（打击感弹药库，打斗镜头写反馈时选用，禁止全部堆砌）：武器/攻击强化——剑芒刀气、斩击弧光、罡气护体、属性流光、残影拖尾、音爆白环；命中反馈——冲击波荡开、气浪掀飞、火星四溅、碎石飞溅、尘土炸起；受击反馈——倒飞犁地、撞塌岩壁、甲胄碎裂、武器脱手、连滚数圈；环境破坏——地面龟裂、裂纹蔓延、石柱崩断、冰晶炸裂、烟尘弥漫；镜头反馈——镜头震颤、短促顿帧、动态模糊、失焦回焦。命中瞬间至少从「命中反馈+受击反馈」中各取1个、按需补环境破坏，禁止全程只用同一组反馈词。";
        }
        if (charNames.IndexOf("战斗态", StringComparison.Ordinal) >= 0)
        {
            systemPrompt += "\n\n【角色战斗态规则】\n1. 若角色资产名单同时存在「角色名」与「角色名战斗态」：文戏、日常、常态对峙、步行赶路等非战斗镜头只引用基础名「[角色名]」一张参考图；打斗/动作、追逐/逃亡、高潮/对决、技能释放、战损镜头必须同时引用「[角色名]」和「[角色名战斗态]」两张参考图，顺序为基础卡在前、战斗态卡在后；\n2. 战斗镜头中基础卡负责锁定脸型、发型、服装与气质，战斗态卡负责锁定气血、技能、衣损等状态，禁止只引用战斗态卡而丢失基础卡；\n3. 战斗镜头中@角色引用必须同时列出「[角色名]」和「[角色名战斗态]」，参考图行按同一顺序连续列出两张；\n4. 参考图位不足时，按【参考图容量规则】压缩：优先保证核心交战角色双卡，非核心角色只保留一张或并入群像；\n5. 战斗镜头中，若分镜锁定技能里存在该角色的专属状态技（状态爆发/气血类，如「赤金气血」），即使本镜头未释放该技能，也必须把该状态技写入参考图行（[技能名]特效参考）作为战斗态视觉锚定，动作/姿态可表现为护体收敛、隐约透光等低强度状态；禁止在「禁止」行写该状态技「不得出现」。";
        }
        systemPrompt += "\n\n【参考图容量规则】\n1. 每条提示词参考图最多9张，最后一张固定为画面风格参考，因此内容参考最多8张；\n2. 每个出镜角色每名占一张参考图，群演合并为群像参考；\n3. 当内容参考超过8张时，按以下顺序处理：保留主角、说话人、动作/技能执行者和有独立戏份的核心角色；其余角色合并为「角色群像参考」，每组最多4人一张；背景群演不再占参考图，只通过主体、动作、姿态和场景文本描述；\n4. 仍超过8张时，先合并或舍弃道具与特效参考，再舍弃无台词背景角色参考；场景参考原则上保留；\n5. 群像参考名必须与资产库一致，使用固定群像名（如「赤焰宗守山弟子群像」「玄冰宗守山弟子群像」「七宗宗主群像」），禁止在参考图行里把多个角色名拼成一个长名称；\n6. 10人战斗镜头默认结构：3名核心交战角色双卡（6张）+ 1张群像参考（其余4人）+ 1张场景参考 = 8张内容参考，最后加1张风格参考；10人文戏镜头默认结构：6名核心角色个人参考 + 1张群像参考 + 1张场景参考 = 8张内容参考；\n7. 被合并或舍弃的角色仍必须保留在@角色引用和动作/姿态文本中，禁止让角色从镜头里消失；\n8. 战斗镜头中的群像参考与对应角色外观一致，名称使用名单中的正式群像名；\n9. 守山弟子只出基础卡单体代表与「宗门名守山弟子群像」。";
        systemPrompt += "\n\n【出镜角色完整性规则】\n1. 【镜头规划】的『出镜角色及表情』中出现的每个角色（含群像，如七宗宗主、宗门弟子群像）都必须出现在 @角色引用 行，并在参考图行中占一张参考图，群像角色可合并为一张群像参考；\n2. 参考图超过9张时，按 角色 > 道具 > 特效 > 场景 的顺序取舍，先合并或舍弃特效/道具参考，禁止删除任何出镜角色；\n3. 群像名必须使用【角色资产名单】中的名称（如七宗宗主），禁止改名或自创。";

        var userMsg = "【镜头规划】\n" + shotPlan + "\n\n【角色资产名单】\n" + charNames + "\n\n【道具资产名单】\n" + propNames + "\n\n【场景资产名单】\n" + envNames + "\n\n【特效资产名单】\n" + effectNames + "\n\n【角色关键词】\n" + charKeywords + "\n\n【道具关键词】\n" + propKeywords + "\n\n【场景关键词】\n" + envKeywords + "\n\n【特效关键词】\n" + effectKeywords + "\n\n【镜头衔接分析】\n" + shotAnalysis;
        if (!string.IsNullOrWhiteSpace(fightText) && fightText.Contains("【锁定模板】"))
        {
            systemPrompt += "\n\n【锁定规则】\n1. 当前打斗模板已由 Stage 5 锁定。\n2. Stage 9 只能把分镜扩写成 Seedance 提示词，禁止更换模板 ID、改变动作顺序、运镜指令、技能和战斗结果。\n3. 只允许补充画面细节、镜头节奏和角色表演。";
        }
        if (!string.IsNullOrWhiteSpace(skillText))
            userMsg += "\n\n【技能库/分镜锁定技能】\n" + skillText;
        if (!string.IsNullOrWhiteSpace(fightText))
            userMsg += "\n\n【打斗模板库】\n" + fightText;
        if (!string.IsNullOrWhiteSpace(frameBindingHint))
            userMsg += "\n\n【分镜帧参考绑定（系统确定，最高优先级）】\n下面为当前单元/镜头每个镜头的分镜帧资产绑定清单（来源：FrameAssetBindings，分镜帧→项目资产的确定解析，非全量名单）。每个镜头的 @图N 行只能绑定该镜头清单中实际列出的实体资产（人物/场景/道具/特效），清单中该镜头没有的资产——即使出现在上方的【角色/道具/场景/特效资产名单】中——也禁止写入该镜头的 @图N 行（画面需要时可在时间块正文用文字交代）；画面风格「@图N [光影质感]光影质感参考」卡不受本表约束、照常写在每镜最后；「场景锚定/场景名+时间段」的风格行语义场景名从该镜头清单的场景参考中挑选。\n" + frameBindingHint;
        if (!string.IsNullOrWhiteSpace(keyframeAnchor))
            userMsg += "\n\n【关键帧锚定（L3，系统确定，优先级高于模型自由发挥）】\n" + keyframeAnchor;

        systemPrompt += "\n\n【台词逐字照抄规则】\n1. 「对话」行引号内的台词必须逐字照抄【镜头规划】中该镜头的【对话/台词】原文，禁止把台词里的人称或角色名替换成资产名；例如分镜写「太虚圣主：岳沉天，你一人再强，也强不过天下法统。」，对话行必须写 太虚圣主说“岳沉天，你一人再强，也强不过天下法统。”，禁止写成“前世岳沉天，你一人再强...”。\n2. 只有说话者标签（如 顾残山留音说）才允许规范化为正式角色名（顾残山说），台词正文一个字都不能改。";

        return await _llm.CallAsync(apiUrl, apiKey, model, seedancePrompt, userMsg, thinkingMode: thinkingMode);
    }


    private static string BuildSeedanceSystemPrompt(string stylePrompt, string skillText, string fightText, string charNames)
    {
        var sb = new StringBuilder();
        sb.Append("你是一位好莱坞顶级影视提示词架构师，精通所有大模型 prompt 逻辑，善于把需求转化为高质量、可执行的提示词；同时是专业的 Seedance 2.0 视频生成提示词工程师。要求：清晰结构化排版，无废话、不闲聊、不凑字数；严格依据给定的【镜头规划】与角色/道具/场景资产名单输出，禁止自行增删剧情、角色、道具、场景与台词。\n\n");
        sb.Append("严格按照以下紧凑模板输出每个镜头的提示词（不要额外解释，直接输出格式内容）。每个镜头必须完整包含：标题行、类型行、@图N 参考图绑定行、风格行、2-5 个 [X-Ys] 时间块、灯光行、约束行、视频风格行。禁止输出 参考图:、[组合]、@角色引用、@道具引用、场景锚定引用、景别、画质、禁止 等旧字段行；禁止输出 时长:、主体:、动作:、对话:、姿态:、运镜: 等字段行，全部画面信息写进时间块正文。\n\n");
        sb.Append("模板：\n【第X集】【单元X.Y】【镜头X.Y-Z】（镜头编号必须与单元编号一致，例如单元2.3的镜头写【镜头2.3-1】，禁止只写【镜头1】）\n类型:场景类型（从：打斗/动作、文戏/情感、追逐/逃亡、悬疑/惊悚、日常/喜剧、高潮/对决 中选择最匹配的一项；日常对话、打脸、斗嘴、审问、追查一律按「日常/喜剧」或「文戏/情感」标注，「悬疑/惊悚」仅限剧情设定本身神秘压迫的镜头，禁止仅因夜晚、灯光昏暗、月光冷色就标悬疑/惊悚；禁止把喜剧冲突、公务审案、日常收尾误标为悬疑/惊悚）\n@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致；@图2 [七宗宗主]群像参考，保持袍服各异、七色法光缠身、冷漠森严一致；@图3 [七曜诛圣阵]特效参考，保持形态、色调、阵纹氛围一致；@图4 [赤金气血]特效参考，保持赤金色调、能量形态、压迫氛围一致；@图5 [葬天台]场景参考，保持空间布局、黑石台面、阵柱位置、云海氛围一致；@图6 [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致。\n场景名+时间段。\n[0-3s]大块时间戳正文：1组机位+1组核心动作+环境反馈，直接写画面，禁止再写任何字段名。\n[3-7s]……\n[7-11s]……\n灯光：全局灯光描述。\n约束：4K，24fps，浅景深，无字幕无BGM，人物比例自然、肢体完整；再按本镜头补充1-2条镜头技术/动作约束（禁止写负面词，禁止写「干净通透、不泛黄、低饱和、电影级、体积光」等画面风格词）。\n视频风格：完整引用「给定视觉方向」原文，禁止缩写、浓缩、删减或改写。\n\n每个镜头单元按上述模板输出，用中文。每两个镜头之间用空行分隔。\n\n");
        if (!string.IsNullOrWhiteSpace(stylePrompt))
        {
            sb.Append("风格行规则：风格行只写「场景名+时间段」，禁止再写风格描述或 4K/24fps 等固定参数；「视频风格：」行必须是每个镜头的最后一行，完整引用给定视觉方向原文，禁止缩写、浓缩、删减或改写；「约束：」行固定以「4K，24fps，浅景深，无字幕无BGM，人物比例自然、肢体完整」开头，再按本镜头补充镜头技术/动作约束；禁止写「无变形、无畸形」等负面词，禁止写「干净通透、不泛黄、低饱和、电影级、体积光」等画面风格词——画面风格一律只由「视频风格：」行承载，禁止在「约束：」行重复；禁止出现「负向提示词：」行；无台词镜头在「约束：」行补「无台词，无口型表演，无对白」，禁止写「语速无台词」这类矛盾表述；给定视觉方向：" + stylePrompt + "\n\n");
        }
        sb.Append("【@图N 绑定行规则】\n1. @图N 行是参考图绑定说明，模型据此把参考图对应到角色/群像/特效/场景/风格；必须只写一行，多个绑定用中文分号「；」分隔，禁止换行或写成 参考图: 字段。\n2. 每段格式为「@图N [资产名]类别说明」：资产名必须用方括号写在 @图N 后，禁止省略或改写；类别只能从：人物、群像、特效、道具、场景、光影质感 中选择；说明紧跟在类别后，如「人物形象参考，保持外貌、发型、服装、气质一致」「群像参考，保持整体造型一致」「特效参考，保持形态、色调、氛围一致」「道具参考，保持外观、材质、大小一致」「场景参考，保持空间布局、色调一致」「光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致」，每段说明控制在 6-15 字，禁止展开成长句或重复正文细节。\n3. 资产名必须使用名单中的正式全名并用方括号写在类别前，禁止省略或自创：人物写「@图1 [前世岳沉天]人物形象参考，保持外貌、发型、服装、气质一致」；群像写「@图2 [七宗宗主]群像参考，保持袍服各异、七色法光缠身、冷漠森严一致」或名单群像名；特效写「@图3 [赤金气血]特效参考，保持赤金色调、能量形态、压迫氛围一致」「@图X [七曜诛圣阵]特效参考，保持形态、色调、阵纹氛围一致」；道具写「@图X [镇岳拳带]道具参考，保持外观、材质、大小一致」；场景写「@图5 [葬天台]场景参考，保持空间布局、黑石台面、阵柱位置、云海氛围一致」。\n4. 顺序固定：角色人物参考在前，再群像、道具、特效/技能、场景，最后一张必须是 光影质感；最多 9 张，超出按 角色>道具>特效>场景 取舍，光影质感与场景原则上保留。\n5. 每个出镜角色必须出现在 @图N 行，每名角色给一张「[角色名]人物形象参考」；群像角色合并为一张群像。\n6. 技能锁定：只要镜头描述、动作、起始画面或结束画面出现技能名（如 七曜诛圣阵、赤金气血），@图N 行就必须有对应「[技能名]特效参考」段；未锁定技能禁止出现；分镜点名的库外技能原样保留。\n7. 整行 @图N（含说明）控制在 180 字以内，与时间块正文合计仍遵守【时长与时间块规则】的字数上限。\n8. 道具段绑定条件：只有本镜头时间块正文中实际出现/使用的道具（被手持、挥动、携带、展示或作为交互对象）才能写入 @图N 行「[道具名]道具参考」段；正文未出镜的道具禁止绑定，禁止仅因某角色在场就把其标志性武器/配饰绑定为道具参考（如角色未拔剑，禁止绑剑参考）；特效/技能段同理，正文未出现的特效禁止绑定。\n\n");
        sb.Append("【时长与时间块规则】\n1. 每条提示词总时长只能从 5秒、11秒、15秒 三档中选择（视频生成API只支持这三档）；总时长 = 最后一个时间块的结束秒数，禁止出现 4秒、6秒、8秒、10秒、12秒等其它总时长。\n2. 时间块使用大块秒级区间，格式 [0-3s]、[3-7s]、[7-11s]；5秒镜头写 2-3 块，11秒镜头写 3-4 块，15秒镜头写 4-5 块；时间戳统一为整数秒，禁止小数（如 2-3.5s、3.5-5s）；禁止写成 0-1秒、1-2秒、0-0.5秒 等高密度小段，禁止在块内再写子时间（如 [3-7s] 内写 3-4s）。\n3. 每个时间块只写 1 组机位 + 1 组核心动作：机位词放块首（如 大远景固定机位、低角度极速推镜、贴身平行横移、特写锁焦），随后写人物动作、对手反应与物理反馈；连续攻防可以写进同一块（A突进→B格挡→A变招），但禁止把一个动作拆成多个时间块。\n4. 时间块必须从 0 秒开始连续铺满总时长，禁止留空、重叠或跳跃；每个块都要推进事件，禁止两个相邻块内容重复或原地等待。\n5. 全部正文（含 @图N 行）控制在 500 个中文字以内；打斗/动作、高潮/对决镜头放宽至 1200 字以内（按【打斗镜头语言规则】的镜头语言式写法，画面密度优先），优先删减灯光、约束的重复措辞。\n6. 非打斗镜头单个时间块动作必须克制：只写 1 组核心动作与 1 组机位；多人/群像调度写整体趋势（如「七人从正前方、左右侧翼、后方同步压近，形成环形合围」），禁止把每个角色的复杂走位逐一列出；打斗/动作、高潮/对决镜头不受此限，按【打斗镜头语言规则】写足动作密度。\n\n");
        sb.Append("【打斗时间块动作流】\n1. 打斗/动作、高潮/对决镜头按 Stage 5 锁定的动作链与回合数展开，禁止把多回合压缩成一次挥击+特效；回合密度由动作链决定，5秒镜头同样允许多个快速攻防回合，禁止因档位低而减少交锋频率。\n2. 时间块按大块秒级切分（5秒2-3块、11秒3-4块、15秒4-5块），每个块内机位与动作绑定：突进用低角度极速推镜或贴身跟拍，对拼用环绕或横移，命中用短促震动、顿帧/慢动作0.2-0.4秒，禁止整段匀速。\n3. 每个时间块双方都必须有动作与受击反馈（格挡、闪避、后仰、倒退、倒地、武器脱手等），禁止一方出手、另一方原地等待或只出特效。\n4. 物理细节必须跟上：火星/水花/沙尘/衣袍发丝翻飞/碎布飞散/冲击波，命中瞬间给足环境与镜头反馈。\n5. 镜头首尾动作与下一镜头衔接，禁止无来源瞬移或双方突然互换位置。\n\n");
        sb.Append("【打斗镜头语言规则（仅 打斗/动作、高潮/对决 镜头适用，优先级高于上方克制类规则与【打斗时间块动作流】；文戏/情感、日常/喜剧、悬疑/惊悚 镜头禁止使用）】\n1. 时间块采用「主题+镜头语言」写法，格式：[X-Ys]【四字主题名】正文长句。正文禁止清单式短句、禁止罗列字段；必须把站位、动作、技能形态、命中反馈、环境破坏、镜头运动、光影、情绪融合进连续画面叙述，对标史诗仙战风格。\n2. 站位融入叙述：角色方位直接写进动作句（如「自画面左侧踏碎冰尘冲入」「立于画面中央挥剑下劈」），禁止单独站位字段；【镜头衔接与站位规则】的相邻镜头延续依然生效，禁止瞬移或互换位置。\n3. 节奏弧必须完整：起手蓄势→对拼连击→爆发大招→收束定格。15秒大招/高潮镜头写4-5个主题块（如 0-3s/3-6s/6-10s/10-13s/13-15s），情绪逐层递进（压迫感递增、战场归于沉寂等）；5秒/11秒打斗按档位压缩主题块数（5秒2-3块、11秒3-4块），但必须保留蓄势-爆发-收束的节奏。\n4. 破坏力允许夸张写足：地面冻裂崩塌、冲击波席卷四方、敌阵掀飞、甲胄碎片飞溅、寒霜封冻碎裂等，禁止轻描淡写。\n5. 镜头运动允许节奏变化：高速贴地跟拍、极速穿梭、环绕推进、低角度仰拍压迫、俯拍全景、径向拉远；大招/高潮镜头允许一次流畅的镜头视角变化（如特写拉升至全景、仰拍转俯拍）与自然转场（前景遮挡划镜、冰屑飞溅遮屏），必须是连续镜头运动，禁止生硬跳切、禁止剪辑术语；运镜必须随动作联动：突进/冲锋用低角度极速推镜或贴地跟拍，对拼/交格用环绕横移，上挑/腾空用仰拍快速拉升，下劈/砸落用俯冲压镜跟拍，爆发/大招用径向拉远+镜头震颤，命中瞬间短促震动/顿帧强调；同一镜头内相邻时间块的运镜不得重复，禁止全程固定机位、禁止运镜与动作脱节。\n6. 命中/大招瞬间用顿帧或慢动作强调（约0.2-0.4秒），其余时段保持高速密集，禁止全程匀速。\n7. 技能/特效形态必须写足具体视觉（法相、剑柱、冰凰虚影、霜雾、冰晶、冲击波等），禁止只写「技能释放」四个字；技能绑定按【技能库规则】。\n8. 写法示范（仅示范写法风格，角色/技能/场景必须替换为本镜头实际内容，禁止照抄示范剧情）：\n[0-3s]【冰刃突袭】自画面左侧踏碎冰尘冲入敌阵，长剑挥出蓝白冰芒，身形化作冰晶残影高速穿梭斩击，剑刃所过敌人被寒霜冻结倒飞，冰碴与冷雾同步炸开，地面冻出连片裂痕，镜头高速贴地跟拍，动作残影叠加冰蓝拖尾，动态模糊拉满，刺穿瞬间升格定格冰晶爆裂细节。\n[3-6s]【霜域冰封】立于阵中长剑点地，冰蓝灵力波纹环形扩散，地面瞬间升起数十根冰晶柱，寒雾与冰丝交织成密网，踏冰柱腾空翻转挥剑劈出扇形冰刃冲击波，成片敌人被寒霜封冻后碎裂倒飞，甲胄碎片与冰晶四溅，镜头在冰柱间隙极速穿梭，前景冰屑划镜完成自然转场。\n[6-10s]【冰凰法相】凌空跃起身形暴涨，身后浮现巨型冰凰法相，蓝白冰羽缠绕周身，长剑化为千米冰刃裹挟极寒之力轰然劈落，地面冻裂崩塌，环形寒潮冲击波席卷四方，无边敌阵被冰浪掀飞至半空，低角度仰拍冰凰巨影压迫感，随即流畅拉升至上帝视角俯拍冰封全景，天地尽被蓝白寒光覆盖。\n[10-13s]【万晶归寒】将长剑抛向天穹崩解为漫天蓝白冰晶光点，双手结印手势刚猛凌厉，天际四面八方浮现无数冰剑虚影遮天蔽日，万剑汇聚成一道贯穿天地的冰寒剑柱，剑柱表面冰凰虚影盘旋，寒光如银河倾泻而下，镜头从结印手部特写极速拉升至天穹全景，环绕冰柱螺旋推进，压迫感逐层递增。\n[13-15s]【霜烬尘凝】冰寒剑柱轰然砸落，炽白寒光瞬间吞噬全场，敌阵在极寒中寸寸封冻化为冰晶碎片消散，主角持剑静立于冰封废墟中心，战裙与发丝被寒风吹动，身后残霜与冰凰虚影交相辉映，镜头径向快速拉远，战场归于沉寂，仅余零星冰屑在冻土上缓缓飘落。\n\n");
        sb.Append("【镜头衔接与站位规则】\n1. 每个时间块正文开头必须先交代主要角色的画面站位：用标准方位词写明「画面左侧/画面右侧/画面中央/画面近前景/画面远背景」+ 朝向 + 景别，再写动作；禁止只写动作不写位置，禁止用「对面」「两侧」「对峙」「分列」等不指明方位的模糊词。\n2. 相邻镜头站位必须严格依据【镜头衔接分析】中对应镜头的衔接块：本镜头开头直接采用该块「上一镜结尾」的画面状态（谁在左、谁在右、双方距离、景别、定格姿态），镜头末尾状态即该块「本镜结尾」，禁止无来源瞬移或双方突然互换位置；【镜头衔接分析】缺失时按上一条规则自行延续。\n3. 多人/群像镜头写清主要角色之间的相对位置关系（如「岳沉天立于画面左侧，七宗宗主居画面右侧呈扇形对峙」），禁止只罗列人物不交代布局。\n4. 单镜头内主要角色的画面方位必须稳定：除明确写出的走位外，角色不得在镜头中途无来源改变画面位置（禁止从画面左侧漂移到右侧再漂移回来）；走位必须写明起止位置（如「岳沉天从画面左侧沿弧线逼至画面中央」）。\n5. 站位与布局描述写进时间块正文，禁止另起字段行；单角色镜头也要写明角色在画面中的方位与景别。\n\n");
        sb.Append("【出拳速度与打击感】\n1. 出手前必须有明确蓄力（沉腰、后脚蹬地、肩背发力、拳锋后拉），出手瞬间加速度爆发，拳锋/刃锋带动态模糊与残影，禁止匀速出拳。\n2. 慢动作只允许用于命中/对撞/受击瞬间（约0.2-0.4秒），随后立即恢复常速或加速，禁止整段慢放、禁止全程高速模糊。\n3. 命中必须同时给出三层反馈：受击者身体反应（后仰/离地/倒飞/犁地）、环境反应（碎石、尘土、气浪、衣袍炸裂、武器脱手）、镜头反应（命中瞬间轻微冲击抖动）。\n4. 写清音效：挥拳呼啸、命中闷响/爆裂、碎石落地声。\n5. 禁止双方隔空比划、禁止拳锋未到敌人先倒、禁止命中后无反馈直接切镜。\n6. 武器与攻击必须自带视觉强化：每记出招刃锋/拳锋带明显光效与轨迹（剑芒、刀气、斩击弧光、罡气、属性流光），挥砍轨迹带残影拖尾，禁止裸武器干挥、禁止无光效的纯动作对砍。\n7. 燃度要求：出手快、连招密、冲击猛——攻防转换节奏紧凑，命中瞬间力量感拉满（武器劈裂装甲、冲击波荡开、地面碎裂、尘土飞扬），禁止轻飘飘接触、禁止软绵绵收招。\n\n");
        sb.Append("【文戏节奏例外】\n1. 当镜头描述以「【导演注意】」开头或明确写明快切、顿帧、节奏爆发、情绪高潮等节奏指令时，即使类型为文戏/情感，也必须按该指令执行：允许快切、顿帧、短镜切换，爆发瞬间可用顿帧或慢动作。\n2. 此时约束行必须写明具体节奏（如“快切+顿帧营造情绪爆发”），禁止按“稳定镜头缓推”处理。\n3. 未出现此类指令的文戏仍按默认稳定缓推执行。\n\n");
        if (!string.IsNullOrWhiteSpace(skillText))
        {
        sb.Append("【技能库规则】\n1. 技能库是本片可用的技能大招库，条目格式为「技能名（系别·层级）: 视频版提示词」，视频版提示词包含该技能的形态、镜头与节奏；技能库只用于角色归属过滤，不是白名单，分镜锁定的库外技能同样有效；设定了参考图的技能条目不附视频版提示词，而标注「形态、色调、氛围以参考图为准」。\n2. 仅当【镜头规划】中该镜头明确涉及技能库技能或分镜锁定技能（施法、招式、大招、法阵、合击、领域、特效爆发）时，才可引用该技能：该镜头的特效、动作、运镜与节奏必须严格以技能库中该技能的「视频版提示词」为蓝本；条目标注「形态、色调、氛围以参考图为准」的技能，其形态与色调以 @图N 参考图为唯一依据，禁止再按任何文字描述改写；分镜锁定但库外无视频版提示词的技能，形态、色调与节奏以分镜描述为准，禁止自行改写成其他效果。\n3. @图N 行中该技能占一段「[技能名]特效参考，保持形态、色调、氛围一致」（如 @图4 [七曜诛圣阵]特效参考，保持形态、色调、阵纹氛围一致），技能名必须与【技能库】或分镜「技能」行完全一致且用方括号括起，禁止省略、禁止改名。\n4. 镜头规划未涉及任何技能时，禁止擅自加入技能或技能特效参考；普通特效/氛围光效允许自由补充。\n5. 技能与【特效资产名单】同规格处理：不替代角色、道具、场景参考；@图N 行超过9段时按原优先级取舍（技能归入特效一类）。\n6. 层级语义：T3 为常规技能、T4 为强力技能、T5 为终极大招；T5 大招只允许在高潮/终极对决等关键镜头释放，T3/T4 按战斗升级阶段依次使用，禁止在普通镜头放大招。\n7. 只有「释放技能」的镜头才出现技能特效（施法、出招、放大招时，特效严格按技能视频版提示词生成；条目标注「形态、色调、氛围以参考图为准」的技能，特效形态与色调按 @图N 参考图）；平A对砍、普通近身打斗禁止出现技能库大招级特效（法相、剑柱、领域、万剑归宗等），此类镜头按【打斗模板库】处理，但武器与攻击的「基础视觉强化」必须写足：剑芒刀气、刃锋光晕、斩击弧光、拳风腿影、命中火花、气浪冲击、残影拖尾；武器视觉强化属于表现层、不算新增道具，不受【内容规则】「禁止新增道具」限制，禁止把武器挥砍写成无光效的干巴巴动作。\n8. 本次镜头规划已锁定以下技能：见【技能库】列表；只要某个技能名出现在本镜头的镜头描述、动作、起始画面或结束画面中，@图N 行就必须有对应「[技能名]特效参考」段，禁止只写动作文字却漏掉技能绑定；未在 @图N 行中出现的技能名不得出现在正文中。\n9. 分镜「技能」行明确点名的技能即使不在技能库中，也必须原样保留到 @图N 行与正文特效描述，禁止省略、改名或替换成库内其他技能。\n10. 单元级「技能」列表只代表本单元可用技能：若某技能未出现在本镜头的镜头描述、动作、起始画面或结束画面中，禁止写入该镜头 @图N 行；只有战斗镜头中角色专属状态技（如赤金气血）允许例外。\n11. 【技能实体关系标准】人物+法相+技能特效类技能（如 法天象地·武圣法相）三者关系统一为：人物本尊立于画面前景/中央，法相巨相立于人物身后同轴，人物与法相同步复刻同一动作（人物挥拳则法相同步出拳、人物结印则法相同步结印），技能特效/气血缠人物本体拳臂；禁止「法相立于眉心」「法相从眉心显现」「法相在前、本尊在后」等错误空间关系；若该技能参考图或视频版提示词描述的空间关系与此冲突，一律按本规则执行。\n\n");
        }
        if (!string.IsNullOrWhiteSpace(fightText))
        {
            sb.Append("【打斗模板规则】\n1. 打斗模板库是本片可用的打斗动作模板（动作行/运镜行/约束行），条目格式为「模板名（T层级 · 时长s）: 适用;节拍;动作行;运镜行;约束行」，节奏总口诀：快—快—顿—重。\n2. 打斗/动作、追逐/逃亡、高潮/对决类镜头，必须从【打斗模板库】中选择最匹配的模板：该镜头的时间块按模板对应节拍展开，角色名替换为实际角色（A=我方/主角，B=敌方/反派，C=灵宠/帮手），模板的动作/运镜/约束蓝本写进时间块正文。\n3. 命中/对撞/镇压瞬间必须顿帧+慢动作，禁止全程匀速。\n4. 非打斗镜头禁止套用打斗模板。\n5. 打斗镜头同时涉及技能库技能时：动作/运镜/约束按打斗模板，特效形态与节奏按技能的「视频版提示词」（技能条目标注「形态、色调、氛围以参考图为准」的，特效形态与色调按 @图N 参考图）。\n6. 【打斗效果词库】（打击感弹药库，打斗镜头写反馈时选用，禁止全部堆砌）：武器/攻击强化——剑芒刀气、斩击弧光、罡气护体、属性流光、残影拖尾、音爆白环；命中反馈——冲击波荡开、气浪掀飞、火星四溅、碎石飞溅、尘土炸起；受击反馈——倒飞犁地、撞塌岩壁、甲胄碎裂、武器脱手、连滚数圈；环境破坏——地面龟裂、裂纹蔓延、石柱崩断、冰晶炸裂、烟尘弥漫；镜头反馈——镜头震颤、短促顿帧、动态模糊、失焦回焦。命中瞬间至少从「命中反馈+受击反馈」中各取1个、按需补环境破坏，禁止全程只用同一组反馈词。\n\n");
        }
        if (charNames.IndexOf("战斗态", StringComparison.Ordinal) >= 0)
        {
            sb.Append("【角色战斗态规则】\n1. 若角色资产名单同时存在「角色名」与「角色名战斗态」：文戏、日常、常态对峙、步行赶路等非战斗镜头只在 @图N 行给基础「[角色名]人物形象参考」一张；打斗/动作、追逐/逃亡、高潮/对决、技能释放、战损镜头必须同时给「[角色名]人物形象参考」和「[角色名战斗态]战斗姿态参考」两张，顺序为基础卡在前、战斗态卡在后。\n2. 战斗镜头中基础卡负责锁定脸型、发型、服装与气质，战斗态卡负责锁定气血、技能、衣损等状态，禁止只给战斗态卡而丢失基础卡。\n3. @图N 行中战斗态段必须写「[角色名战斗态]战斗姿态参考，保持外貌、发型、服装、气质一致」（方括号内写资产全名），禁止只写「战斗态」。\n4. 参考图位不足时，按【@图N 绑定行规则】压缩：优先保证核心交战角色双卡，非核心角色只保留一张或并入群像。\n5. 战斗镜头中，若分镜锁定技能里存在该角色的专属状态技（状态爆发/气血类，如「赤金气血」），即使本镜头未释放该技能，也必须把该状态技写入 @图N 行（「[技能名]特效参考」）作为战斗态视觉锚定，动作/姿态可表现为护体收敛、隐约透光等低强度状态；禁止在约束行写该状态技「不得出现」。\n\n");
        }
        sb.Append("【参考图容量规则】\n1. @图N 行最多 9 段，最后一段固定为 光影质感，因此内容参考最多 8 段。\n2. 每个出镜角色每名占一段（人物），群演合并为群像。\n3. 当内容参考超过 8 段时，按以下顺序处理：保留主角、说话人、动作/技能执行者和有独立戏份的核心角色；其余角色合并为「角色群像」；背景群演不再占段，只通过时间块正文描述。\n4. 仍超过 8 段时，先合并或舍弃道具与特效段，再舍弃无台词背景角色段；场景段原则上保留。\n5. 群像名必须与资产库一致，使用固定群像名（如「赤焰宗守山弟子群像」「七宗宗主群像」），禁止在 @图N 行里把多个角色名拼成一个长名称。\n6. 被合并或舍弃的角色仍必须保留在时间块正文的动作/姿态描述中，禁止让角色从镜头里消失。\n7. 战斗镜头中的群像参考与对应角色外观一致，名称使用名单中的正式群像名。\n8. 守山弟子只出基础卡单体代表与「宗门名守山弟子群像」。\n\n");
        sb.Append("【出镜角色完整性规则】\n1. 【镜头规划】的『出镜角色及表情』中出现的每个角色（含群像，如七宗宗主、宗门弟子群像）都必须出现在 @图N 行或时间块正文中，群像角色可合并为一张群像。\n2. 参考图超过 9 段时，按 角色 > 道具 > 特效 > 场景 的顺序取舍，先合并或舍弃特效/道具段，禁止删除任何出镜角色。\n3. 群像名必须使用【角色资产名单】中的名称（如七宗宗主），禁止改名或自创。\n\n");
        sb.Append("【参考图对象约束】\n1. @图N 行每个段的对象（人物/群像/道具/特效/技能/场景）必须来自给定名单：【角色资产名单】【道具资产名单】【场景资产名单】【特效资产名单】或【技能库】，禁止自创或引用名单外对象。\n2. 纯声音/音效类对象（留音、录音、话音、回声、BGM、风声、雷声、呼喊声、心跳声等）不是视觉特效，禁止为其生成「特效参考」段；此类声音若需画面化（如半透明音波状留音），只在时间块正文用文字描述即可，禁止占用 @图N 段。\n3. 分镜『出镜角色及表情』中出现的纯声音声源（如「顾残山留音」）不是实体角色，禁止写入 @图N 行。\n\n");
        sb.Append("【内容规则】\n1. 必须严格依据【镜头规划】中对应镜头的镜头描述生成内容：主体、动作、台词、场景、道具均以镜头描述为准，禁止新增分镜外的人物、情节或道具。\n2. 台词必须逐字照抄【镜头规划】中该镜头的【对话/台词】原文，写入对应时间块正文（如 太虚圣主说\"岳沉天，你一人再强，也强不过天下法统。\"），禁止改写、删减或新增台词，禁止输出 对话: 字段行；只有说话者标签（如 顾残山留音说）才允许规范化为正式角色名（顾残山说），台词正文一个字都不能改。\n3. 台词长度必须匹配总时长，防止语速过快：5秒镜头全片台词合计不超过20字（约1句）；11秒镜头合计不超过40字（约2-3句）；15秒镜头合计不超过60字（约3-4句）；台词超过当前档位上限时必须升级到更高时长档位（5→11→15）；每个含台词的时间块在约束行写明「语速从容自然，贴合时长，禁止赶拍」。\n4. 每个时间块必须描述所有在场角色的动作、姿态与站位，站位必须写明角色间的相对朝向；对话/对峙类镜头参与对话的角色必须正面相对、目光接触；禁止背对背站立，禁止背对镜头说话或做主要动作（除非分镜明确要求背面出场）；不参与本时间块主要动作的角色必须写\"站在原地、双脚着地、保持位置\"；打斗/动作角色允许跳跃、腾空、翻滚，但必须有明确的起跳与落地逻辑，落地时双脚踩实地面，禁止全程无支撑悬浮。\n5. 非对话镜头保持单一连续机位，禁止无关的机位切换、转场或剪辑描述；对话/交流类镜头允许正反打：谁说话镜头对准谁，说话人切换时镜头随之切换，切换干净利落；打斗/动作、高潮/对决镜头按【打斗镜头语言规则】允许节奏型镜头运动（高速跟拍、穿梭、环绕、仰拍、俯拍、拉远及一次流畅视角变化），禁止生硬跳切。\n6. 镜头编号必须为 X.Y-Z 格式（如【镜头2.3-1】），标题行必须完整输出「【第X集】【单元X.Y】【镜头X.Y-Z】」，相邻镜头即使属于同一单元也必须各自独立输出完整标题行，禁止合并或复用上一个镜头的标题。\n7. 动作/姿态/主体 描述禁止用「应了一声」「应声」「哼了一声」「嗯了一声」「欲言又止」「张了张嘴」「刚要开口」等暗示发声却无具体台词的表述；角色确需发声时必须把具体内容写入时间块正文（如 顾九霄说\"嗯\"），动作行只写肢体动作、不再写发声；纯无声的欲言/憋气/抿嘴等表情必须明确写「未出声」「无声」「闭嘴」；「不敢出声」「未出声」等明确无声表述允许；群体惊叫、音效等环境声不作台词处理。\n\n");
        sb.Append("【无台词与站桩约束】\n1. 无台词镜头禁止写「语速无台词」「语速无对白」等矛盾表述，统一写「无台词，无口型表演，无对白」；「语速从容自然」只用于有台词的镜头。\n2. 站桩抵抗/对峙镜头必须写明可执行的具体约束：「双脚稳固、身躯不后退、不出手攻击、不倒地、不腾空」，禁止只写「站立不动」这类抽象词。\n3. 时间块正文禁止临时新增与 @图N 参考卡不一致的服装/战痕细节，禁止临时发明新服装状态。\n\n");
        if (!string.IsNullOrWhiteSpace(fightText) && fightText.Contains("【锁定模板】"))
        {
            sb.Append("【锁定规则】\n1. 当前打斗模板已由 Stage 5 锁定。\n2. Stage 9 只能把分镜扩写成 Seedance 提示词，禁止更换模板 ID、改变动作顺序、运镜指令、技能和战斗结果。\n3. 只允许补充画面细节、镜头节奏和角色表演。");
        }
        sb.Append("【新增必需字段】\n1. 视频模型会自动生成音效，禁止输出独立的「音效：」字段行；需要强调的环境音/动作音（如拳风、闷响、碎裂）直接写进对应 [X-Ys] 时间块正文。\n2. 「视频风格：」行是每个镜头必须输出的最后一行：完整引用给定视觉方向原文（前缀「视频风格：」），禁止缩写、浓缩、删减、改写或省略；「4K，24fps，浅景深，无字幕无BGM，人物比例自然、肢体完整」不在该行出现，必须写在「约束：」行。\n3. 「约束：」行只放技术参数与镜头技术/动作约束，禁止写「干净通透、不泛黄、低饱和、电影级、体积光」等画面风格词——画面风格由「视频风格：」行唯一承载，禁止在约束行重复或硬加。\n4. 禁止输出「负向提示词：」行：Seedance 不支持 negative prompt，负面词会诱导画面畸变，需要避免的问题一律以正面表述写入「约束：」行。\n\n");
        sb.Append("【音效写入规则】\n1. 视频模型默认会自动生成音效，不需要在提示词中单独输出「音效：」字段，也不要把音效信息写成时间块之外的单独段落；本规则只是不单独写音效字段，不是关闭音效。\n2. 打斗/高潮/对决镜头的命中、对撞、碎裂等关键声音（拳风、闷响、爆裂、碎石落地）直接写进对应 [X-Ys] 时间块正文，作为动作反馈的一部分，帮助模型生成更贴合的自动音效。\n3. 文戏/情感镜头可写环境音（风声、更鼓声、脚步声、心跳声），也可不写；音效描述保持简短，避免撑爆500字上限。\n\n");
        sb.Append("【技能呐喊规则】\n1. 技能释放镜头（@图N 行含 [技能名]特效参考 且 Tier≥3）中，施法角色必须在释放技能的时间块开口喊出技能名（格式：角色名怒吼\"技能名！\"，如 前世岳沉天怒吼\"法天象地·武圣法相！\"）；该呐喊属于技能释放表演，不受台词逐字照抄限制，但计入台词字数上限，语气铿锵有力、配合蓄力爆发节奏。\n2. 若分镜【对话/台词】已含喊技能名的台词，则逐字照抄，不得重复追加。\n3. 含技能呐喊的镜头，约束行禁止写「无台词，无口型表演，无对白」。\n\n");
        sb.Append("【内心独白与旁白规则】\n1. 【镜头规划】台词字段中的「旁白/画外音」一律转成「内心独白-角色名」，按旁白信息归属到最贴合的在场角色主观视角（感受、回忆、判断归该角色；客观环境交代归镜头主体角色）；禁止输出「旁白」「画外音」字样的说话人。\n2. 内心独白格式：内心独白-角色名:\"内容\"（不用「说」、不写口型同步）；对白保持 角色名说\"内容\"。\n3. 含内心独白的时间块，该角色必须写明「嘴巴闭合/嘴唇紧抿/未开口/不开口」，仅用表情、眼神与动作传达情绪；禁止出现该角色开口说话的画面。\n4. 内心独白必须第一人称、贴合角色当下动作表情场景，禁止第三人称解说；台词正文仍必须逐字保留【镜头规划】原文。\n\n");
        sb.Append("【灯光与明暗规则】\n1. 灯光行写具体光源与受光对象，如「冷月光为底光铺满云层，七色法光自阵柱冲天做主光源，强明暗对比，人物与阵柱被法光照亮，远处云海保持冷暗」。\n2. 禁止只写「主体亮背景暗」「明暗强对比」这类抽象词，避免模型把整个画面压黑或压暗。\n\n");
        sb.Append("【字段顺序】每个镜头按以下顺序输出：标题行 → 类型行 → @图N 行 → 风格行 → [X-Ys] 时间块 → 灯光行 → 约束行 → 视频风格行（每个镜头的最后一行）。\n\n");
        sb.Append("【风格卡固定铁律】\n1. @图N 行最后一段是风格卡，名称固定为「光影质感」，禁止改写成 冷月雨夜、葬天台冷暗光影、冷月七色法光、冷月光 等场景化名称，只允许写「@图N [光影质感]光影质感参考，保持冷月光、体积光、强明暗对比、电影级材质一致」。\n\n");
        sb.Append("【后期叠加规则（屏幕/文件类文字一律留白）】\n1. 手机屏幕、短信、聊天记录、来电显示、新闻推送、头条、文件、卷宗、书信、纸条、告示、榜单、地图、监控画面、时间码、系统面板等「文字类画面元素」，画面只画洁净留白（无字符）的屏幕/纸面/面板，文字内容一律后期叠加，禁止在画面中生成任何可读文字或字符。\n2. 含这类元素的镜头，「约束：」行必须写明「视频中只保留屏幕留白，文字后期叠加」。\n3. 正文只描述设备材质、反光、持握方式与人物看屏幕的反应（如「指尖停在屏幕上」「屏幕冷光映亮侧脸」「纸页被指腹压出褶皱」），禁止把短信/新闻/文件的具体文字写进时间块正文。\n4. 与剧情相关的信息内容一律用人物反应、对话或内心独白承载，不依赖画面中的文字；需要后期叠加的清单条目由系统单独维护，不要在提示词里补写文字内容。\n\n");
        sb.Append("【质感层规则（Mx-Shell 式五段，融入既有行，禁止新增字段行）】\n1. 质感锚点只能写进既有行：机位/镜头/景深/材质/瑕疵写进 [X-Ys] 时间块正文，光色写进「灯光：」行，技术参数写进「约束：」行；禁止因此新增字段行，禁止压缩时间块数量或牺牲动作密度。\n2. 镜头/胶片锚点：每个镜头至少给出一个具体的镜头或胶片锚点（如 IMAX 胶片机+Panavision C 系 35mm f/4、Sony Venice+Canon K-35、Kodak 35mm 漂白旁路、Canon EF 85mm f/1.2 人像镜头、超广角 18mm 低角度），禁止只写「电影级镜头」「高级感」这类空词。\n3. 光色锚点：「灯光：」行必须写具体光行为与色温倾向（低饱和灰蓝、低照度高反差、青橙对比、暖调实用光源、侧逆光、轮廓光、体积雾中的丁达尔光），禁止只写「光影唯美」。\n4. 材质锚点：画面元素写到物理材质（湿混凝土、磨损金属、油污关节、布料浮尘、皮肤细纹、裂纹玻璃、胶片颗粒、光雾、不完美反射），禁止一味写「干净」「锃亮」。\n5. 瑕疵锚点（反过度美化）：人物、服装、道具、环境各至少给出 2 处真实瑕疵（发丝翘起、领口褶皱、指节擦伤、衣角磨白、鞋面泥点、道具磨痕、墙面剥落、地面积水），禁止全身全道具无瑕的塑料感画面。\n6. 镜头浮动：手持、主观、跟拍类镜头写「手持拍摄，全程保持极其轻微的、如呼吸般的镜头浮动，增强临场感，禁止变成剧烈晃动」；固定机位、证据与线索特写一律用固定机位稳定呈现，禁止无理由晃动。\n7. 声音策略：每个镜头正文都要体现「无配乐，仅现场同期声」，并点出本镜头的具体制作音（呼吸、脚步、布料摩擦、玻璃裂响、雨声、远处警笛、手机震动、灯管嗡鸣、金属刮擦、低频轰鸣、门轴声、人群嘈杂），禁止写「热血BGM」「史诗配乐」「悲壮配乐」，也禁止为此单独输出「音效：」字段行。\n8. 反空洞褒义词：禁止「史诗、震撼、高级、完美、炫酷、大片感、电影级」等词单独出现——出现时必须与具体机位、光、材质或运动细节绑定，否则删掉；@图N 行的「光影质感」风格卡原文与「约束：」行的固定参数不受本条限制。\n9. 收尾克制：镜头结尾停在「变化之后的状态」上，环境声继续，保留破损、不完整、不安的细节，切在目光、声音、道具状态或未解决的威胁上；除分镜明确要求外，禁止堆爆炸、强光、胜利姿势或新增剧情动作。\n10. 与既有规则冲突时以既有规则为准：运镜强度仍按镜头类型执行（文戏稳定缓推、打斗快速推拉摇移）、@图N 绑定顺序与字数上限不变，质感层只是把描述写具体，不新增人物、道具、剧情与字段行。\n\n");
        return sb.ToString();
    }

    // ========== H3 提示词生成（独立于 SD 提示词，按 MiniMax H3 参考生视频模板规范）==========

    /// <summary>分镜脚本中单个镜头的原始段落（从 Stage 5 分镜脚本按「镜头编号」字段切出）。</summary>
    public sealed class ShotScriptBlock
    {
        public int EpisodeNumber { get; set; }
        public string UnitName { get; set; } = "";
        public string ShotLabel { get; set; } = "";
        public string? ShotType { get; set; }
        public int Duration { get; set; }
        public int RefImageCount { get; set; } = 1;
        public string RawText { get; set; } = "";
        public string? Scene { get; set; }
    }

    /// <summary>从 Stage 5 分镜脚本（shotPlan）中按「镜头编号」字段切出全部镜头段落，供 H3 独立生成使用。</summary>
    public static List<ShotScriptBlock> ParseShotsFromShotPlan(string? shotPlan)
    {
        var shots = new List<ShotScriptBlock>();
        if (string.IsNullOrWhiteSpace(shotPlan)) return shots;
        var lines = shotPlan.Replace("\r\n", "\n").Split('\n');

        string? currentUnitType = null;
        ShotScriptBlock? cur = null;
        var curLines = new List<string>();

        void Flush()
        {
            if (cur == null) return;
            var raw = string.Join("\n", curLines).Trim();
            cur.RawText = string.IsNullOrWhiteSpace(raw) ? "**镜头编号**: " + cur.ShotLabel : raw;
            if (cur.Duration <= 0)
            {
                var m = Regex.Match(cur.RawText, @"\*\*镜头时长\*\*\s*[:：]\s*(\d+)");
                if (m.Success) cur.Duration = int.Parse(m.Groups[1].Value);
            }
            if (cur.Duration <= 0) cur.Duration = 5;
            if (string.IsNullOrWhiteSpace(cur.ShotType)) cur.ShotType = currentUnitType;
            if (string.IsNullOrWhiteSpace(cur.Scene))
            {
                var sm2 = Regex.Match(cur.RawText, @"\*\*场景\*\*\s*[:：]\s*([^\n]+)");
                if (sm2.Success) cur.Scene = sm2.Groups[1].Value.Trim();
            }
            cur.RefImageCount = Math.Clamp(CountCharactersInShot(cur.RawText), 1, 9);
            shots.Add(cur);
            cur = null;
            curLines.Clear();
        }

        foreach (var line in lines)
        {
            var t = line.Trim();
            if (t.Length == 0)
            {
                if (cur != null) curLines.Add(t);
                continue;
            }

            // 集/单元标题行（### 【第X集】 / #### 【单元X.Y】 / #### 单元标题）：结束当前镜头，标题行本身不属于任何镜头
            if (Regex.IsMatch(t, @"^#{3,4}\s+\S"))
            {
                Flush();
                continue;
            }

            // 单元类型（单元级与镜头级均可，向后继承；行首允许 - 项目符号）
            var utm = Regex.Match(t, @"^[-*]?\s*\*\*单元类型\*\*\s*[:：]\s*(.+)$");
            if (utm.Success) currentUnitType = utm.Groups[1].Value.Trim();

            // 镜头编号行：- **镜头编号**: 1.1-1（行首允许 - 项目符号）
            var sm = Regex.Match(t, @"^[-*]?\s*\*\*镜头编号\*\*\s*[:：]\s*([\d]+\.[\d]+-[\d]+)\s*$");
            if (sm.Success)
            {
                Flush();
                cur = new ShotScriptBlock { ShotLabel = sm.Groups[1].Value };
                var parts = cur.ShotLabel.Split('-');
                if (parts.Length == 2 && int.TryParse(parts[0].Split('.')[0], out var ep))
                {
                    cur.EpisodeNumber = ep;
                    cur.UnitName = parts[0];
                }
                curLines.Add(t);
                continue;
            }
            if (cur == null) continue; // 尚未进入任何镜头，跳过
            curLines.Add(t);
        }
        Flush();
        return shots;
    }

    /// <summary>
    /// 从 Stage 4 分集细化文本解析「单元 → 地点」映射（如 单元1.1 → 办公室·男主工位）。
    /// 分镜脚本（Stage 5）未保留结构化地点，而场景/道具资产匹配依赖文本命中，故用分集细化地点补偿。
    /// </summary>
    private static Dictionary<string, string> ParseUnitLocationsFromStage4(string? stage4Text)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(stage4Text)) return map;
        var text = stage4Text.Replace("\r\n", "\n");
        var blocks = Regex.Matches(text, @"【单元\s*(\d+\.\d+)\s*】([\s\S]*?)(?=【单元\s*\d+\.\d+\s*】|【第\s*\d+\s*集】|$)");
        foreach (Match b in blocks)
        {
            var unitKey = b.Groups[1].Value.Trim();
            var body = b.Groups[2].Value;
            var loc = Regex.Match(body, @"(?m)^\s*地点\s*[:：]\s*([^\r\n]+)");
            if (loc.Success && !string.IsNullOrWhiteSpace(loc.Groups[1].Value.Trim()))
                map[unitKey] = loc.Groups[1].Value.Trim();
        }
        return map;
    }

    /// <summary>估算镜头段落中的出镜角色数（用于确定 H3 参考图数量）。</summary>
    private static int CountCharactersInShot(string raw)
    {
        var m = Regex.Match(raw, @"\*\*出镜角色及表情\*\*\s*[:：]\s*([^\n]+)");
        if (!m.Success) return 1;
        var seg = m.Groups[1].Value.Trim();
        var count = Regex.Matches(seg, @"[\u4e00-\u9fa5A-Za-z0-9]+（").Count;
        if (count == 0)
            count = seg.Split(new[] { '；', ';', '，', ',' }, StringSplitOptions.RemoveEmptyEntries).Length;
        return count;
    }

    /// <summary>单个镜头的 H3 生成失败记录（镜头号 + 原因），用于落日志、页面提示与「只重跑失败镜头」。</summary>
    public sealed class H3BatchFailure
    {
        public string ShotLabel { get; set; } = "";
        public string UnitName { get; set; } = "";
        public int EpisodeNumber { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>H3 批量生成结果（含失败镜头清单）。</summary>
    public sealed class H3BatchResult
    {
        public int Done { get; set; }
        public int Total { get; set; }
        public int Failed { get; set; }
        public string? FirstError { get; set; }
        public List<H3BatchFailure> Failures { get; set; } = new();
        /// <summary>失败镜头号清单，供「只重跑失败的镜头」逐条回灌。</summary>
        public List<string> FailedShotLabels => Failures.Select(f => f.ShotLabel).ToList();
    }

    /// <summary>
    /// 整项目批量生成 H3 提示词：直接基于 Stage 5 分镜脚本按镜头切块，逐个镜头独立生成 H3 参考生视频提示词，
    /// 不依赖 SD 提示词（两条生成线完全独立）。overwrite=false 时跳过已生成过 H3 的镜头（补齐模式）；
    /// <paramref name="onlyShotLabels"/> 非空时只处理清单里的镜头，并强制重生成（用于「只重跑上一次失败的镜头」）。
    /// 单个镜头失败不中断批量，继续生成其余镜头，并把失败清单（镜头号 + 原因）一并返回。
    /// </summary>
    public async Task<H3BatchResult> GenerateH3PromptsForProjectAsync(int projectId, string? shotPlan, string apiUrl, string apiKey, string model, string? thinkingMode = null, bool overwrite = false, Action<int, int>? onProgress = null, IReadOnlyCollection<string>? onlyShotLabels = null)
    {
        var stylePrompt = GetStylePrompt(projectId);
        var onlySet = onlyShotLabels == null || onlyShotLabels.Count == 0
            ? null
            : new HashSet<string>(onlyShotLabels, StringComparer.OrdinalIgnoreCase);
        // 与 SD 线共用同一套项目资产库与命名（角色/环境/道具/特效），参考素材按镜头匹配生成，不再使用独立的参考资产库
        var characters = _db.GetCharacterAssets(projectId);
        var props = _db.GetPropAssets(projectId);
        var effects = _db.GetEffectAssets(projectId);
        var environments = _db.GetEnvAssets(projectId);
        // 分集细化（Stage 4）含结构化「地点」字段，分镜脚本（Stage 5）未保留该字段；解析后用于补充 H3 场景/道具资产匹配文本，
        // 使「办公室·男主工位」「老板办公室」等地点的镜头能精确命中环境资产，避免场景参考整体丢失
        var stage4 = _db.GetStageData(projectId, 4);
        var unitLocations = ParseUnitLocationsFromStage4(stage4?.LlmResponse ?? stage4?.Content);
        var knownAssetNames = props.Select(x => NormalizeSceneName(x.Name))
            .Concat(effects.Select(x => NormalizeSceneName(x.Name)))
            .Concat(environments.Select(x => NormalizeSceneName(x.Name)))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        characters = EnsureShotCharacters(shotPlan ?? "", characters, knownAssetNames, out var autoSupplementedNames);
        var shots = ParseShotsFromShotPlan(shotPlan);
        var existing = _db.GetPrompts(projectId);
        // 分镜帧资产绑定（FrameAssetBindings）：按「集|单元|镜头」索引，作为每个镜头参考素材的权威来源
        var frameBindingsByShot = LoadFrameBindingsByShot(projectId);
        var targets = new List<(ShotScriptBlock Shot, SeedancePrompt? Row)>();
        foreach (var shot in shots)
        {
            var row = existing.FirstOrDefault(e =>
                e.EpisodeNumber == shot.EpisodeNumber &&
                string.Equals(e.UnitName?.Trim(), shot.UnitName, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(e.ShotLabel?.Trim(), shot.ShotLabel, StringComparison.OrdinalIgnoreCase));
            // 定向模式（onlyShotLabels 非空）：只处理清单里的镜头，且强制重生成（即使已有 H3 文本）
            if (onlySet != null)
            {
                if (!onlySet.Contains(shot.ShotLabel)) continue;
            }
            else if (!overwrite && row != null && !string.IsNullOrWhiteSpace(row.PromptTextH3)) continue;
            targets.Add((shot, row));
        }
        var total = targets.Count;
        var done = 0;
        var failures = new List<H3BatchFailure>();
        foreach (var (shot, row) in targets)
        {
            try
            {
                // 优先用分镜脚本里的「场景」字段，缺失时回退 Stage4 结构化地点
                var unitLocation = !string.IsNullOrWhiteSpace(shot.Scene)
                    ? shot.Scene!
                    : (unitLocations.TryGetValue(shot.UnitName ?? "", out var stage4Loc) ? stage4Loc : null);
                var fbKey = ShotFrameKey(shot.EpisodeNumber, shot.UnitName, shot.ShotLabel);
                var keptBindings = (fbKey != null && frameBindingsByShot.TryGetValue(fbKey, out var shotBindings) && shotBindings.Count > 0)
                    ? CapFrameBindings(shotBindings)
                    : null;
                var (refAssetText, shotRefCount) = BuildH3ReferenceMaterialList(shot, row, characters, props, effects, environments, autoSupplementedNames, unitLocation, keptBindings);
                var lockedBindings = keptBindings != null ? NumberFrameBindings(keptBindings) : null;
                var h3 = await GenerateH3PromptCoreAsync(shot, row, apiUrl, apiKey, model, thinkingMode, stylePrompt, refAssetText, shotRefCount, lockedBindings, projectId);
                if (!string.IsNullOrWhiteSpace(h3))
                {
                    var finalH3 = EnsureH3CompleteSections(h3);
                    if (row != null) _db.UpdatePromptTextH3(row.PromptId, finalH3);
                    else _db.InsertSeedancePromptWithH3(projectId, shot.EpisodeNumber, shot.UnitName, shot.ShotLabel, shot.ShotType, shot.Duration, finalH3);
                    done++;
                }
                else
                {
                    RecordFailure(projectId, shot, "LLM 返回内容为空", failures);
                }
            }
            catch (Exception ex)
            {
                RecordFailure(projectId, shot, ex.GetBaseException().Message, failures);
            }
            onProgress?.Invoke(done, total);
        }
        // 批量结束补一条汇总：失败镜头已逐条写进 Stage 9 进度日志，事后能查「哪个镜头、为什么失败」
        try
        {
            _db.AppendStageProgressLog(projectId, 9, failures.Count == 0
                ? $"{DateTime.Now:HH:mm:ss}  H3 提示词生成完成：成功 {done}/{total}"
                : $"{DateTime.Now:HH:mm:ss}  H3 提示词生成完成：成功 {done}/{total}，失败 {failures.Count} 个镜头（{string.Join("、", failures.Select(f => f.ShotLabel))}）");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[H3批量] 写汇总进度日志失败（不影响结果）：ProjectId={ProjectId}", projectId);
        }
        return new H3BatchResult
        {
            Done = done,
            Total = total,
            Failed = failures.Count,
            FirstError = failures.FirstOrDefault()?.Reason,
            Failures = failures
        };
    }

    /// <summary>记录单个镜头的 H3 生成失败：写应用日志 + Stage 9 进度日志（页面上可查），并计入失败清单。</summary>
    private void RecordFailure(int projectId, ShotScriptBlock shot, string reason, List<H3BatchFailure> failures)
    {
        var shotLabel = string.IsNullOrWhiteSpace(shot.ShotLabel) ? (shot.UnitName ?? "未知") : shot.ShotLabel;
        failures.Add(new H3BatchFailure
        {
            ShotLabel = shotLabel,
            UnitName = shot.UnitName ?? "",
            EpisodeNumber = shot.EpisodeNumber,
            Reason = reason
        });
        _logger?.LogWarning("[H3批量] 镜头 {Shot} 生成失败：{Reason}", shotLabel, reason);
        var brief = reason.Length > 200 ? reason.Substring(0, 200) + "…" : reason;
        try
        {
            _db.AppendStageProgressLog(projectId, 9, $"{DateTime.Now:HH:mm:ss}  失败 · 镜头{shotLabel} · {brief}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[H3批量] 写失败进度日志失败（不影响批量继续）：镜头 {Shot}", shotLabel);
        }
    }

    /// <summary>
    /// 单镜头生成 H3 提示词：基于该镜头现有 SD 提示词改写为 H3 格式，覆盖写入 PromptTextH3，返回 H3 全文。
    /// </summary>
    public async Task<string> GenerateH3PromptForFrameAsync(int promptId, string apiUrl, string apiKey, string model, string? thinkingMode = null)
    {
        var p = _db.GetPrompt(promptId) ?? throw new Exception("提示词不存在");
        var stylePrompt = GetStylePrompt(p.ProjectId);
        // 与 SD 线共用同一套项目资产库与命名，参考素材按镜头匹配生成，不再使用独立的参考资产库
        var characters = _db.GetCharacterAssets(p.ProjectId);
        var props = _db.GetPropAssets(p.ProjectId);
        var effects = _db.GetEffectAssets(p.ProjectId);
        var environments = _db.GetEnvAssets(p.ProjectId);
        // 优先从 Stage 5 分镜脚本取该镜头的原始段落直接生成；分镜缺失时退化为基于 SD 提示词改写
        var stage5 = _db.GetStageData(p.ProjectId, 5);
        var shotPlan = stage5?.LlmResponse ?? stage5?.Content;
        // 分集细化（Stage 4）含结构化「地点」，分镜脚本未保留；解析后用于补充该镜头场景/道具资产匹配文本
        var stage4 = _db.GetStageData(p.ProjectId, 4);
        var unitLocations = ParseUnitLocationsFromStage4(stage4?.LlmResponse ?? stage4?.Content);
        var shot = ParseShotsFromShotPlan(shotPlan)
            .FirstOrDefault(s => s.EpisodeNumber == p.EpisodeNumber &&
                                 string.Equals(s.UnitName, p.UnitName?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                                 string.Equals(s.ShotLabel, p.ShotLabel?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (shot == null && string.IsNullOrWhiteSpace(p.PromptText))
            throw new Exception("该镜头没有分镜脚本内容，也没有 SD 提示词，无法生成 H3");
        var knownAssetNames = props.Select(x => NormalizeSceneName(x.Name))
            .Concat(effects.Select(x => NormalizeSceneName(x.Name)))
            .Concat(environments.Select(x => NormalizeSceneName(x.Name)))
            .Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToList();
        characters = EnsureShotCharacters(shotPlan ?? "", characters, knownAssetNames, out var autoSupplementedNames);
        // 优先用分镜脚本里的「场景」字段，缺失时回退 Stage4 结构化地点
        var unitLocation = !string.IsNullOrWhiteSpace(shot?.Scene)
            ? shot!.Scene!
            : (unitLocations.TryGetValue(shot?.UnitName ?? "", out var stage4Loc) ? stage4Loc : null);
        // 分镜帧资产绑定（FrameAssetBindings）：作为该镜头参考素材的权威来源（无绑定时退回文本匹配，不退化全量）
        var frameBindingsByShot = LoadFrameBindingsByShot(p.ProjectId);
        var fbKey = ShotFrameKey(shot?.EpisodeNumber ?? p.EpisodeNumber, shot?.UnitName ?? p.UnitName, shot?.ShotLabel ?? p.ShotLabel);
        var keptBindings = (fbKey != null && frameBindingsByShot.TryGetValue(fbKey, out var shotBindings) && shotBindings.Count > 0)
            ? CapFrameBindings(shotBindings)
            : null;
        var (refAssetText, shotRefCount) = BuildH3ReferenceMaterialList(shot, p, characters, props, effects, environments, autoSupplementedNames, unitLocation, keptBindings);
        var lockedBindings = keptBindings != null ? NumberFrameBindings(keptBindings) : null;
        var h3 = await GenerateH3PromptCoreAsync(shot, p, apiUrl, apiKey, model, thinkingMode, stylePrompt, refAssetText, shotRefCount, lockedBindings);
        if (string.IsNullOrWhiteSpace(h3)) throw new Exception("H3 提示词生成失败");
        var finalH3 = EnsureH3CompleteSections(h3);
        _db.UpdatePromptTextH3(p.PromptId, finalH3);
        return finalH3;
    }

    /// <summary>H3 文末禁乐开关（官方唯一有效写法）：依据第 03 课《H3 提示词进阶详解》，不要背景音乐 = 在整条提示词最末尾按键值对写
    /// “非叙事性音乐: N/A”（英文写法 non_diegetic_music: N/A，键名不带引号），值必须为 N/A；视频模型只识别该固定语法才会真正关闭配乐。
    /// STRICTLY DISABLED 长段英文声明、「无配乐」「不要 BGM」等自创写法及带引号键名均非官方语法，实测无效仍会铺 BGM。</summary>
    private const string NoNonDiegeticMusicTrailer =
        "non_diegetic_music: N/A\n非叙事性音乐: N/A";

    /// <summary>
    /// H3 提示词缺节补全 + 文末禁乐开关归一化：LLM 未输出 overall_soundscape 时补齐默认节；
    /// 正文中只要已出现禁乐声明（中/英文键），一律从声明起始处截断到文末，改写为官方 N/A 开关置于全文最后一行。
    /// 仅作用于 H3 输出文本，不影响 SD 逻辑。
    /// </summary>
    private static string EnsureH3CompleteSections(string h3)
    {
        var text = h3?.Trim();
        if (string.IsNullOrWhiteSpace(text)) return text ?? "";
        // 官方语法只认末尾的 “键: N/A”（键为 non_diegetic_music / 非叙事性音乐）。旧式英文禁制节一律从键位置截断后重写为 N/A。
        var enIdx = text.IndexOf("non_diegetic_music", StringComparison.OrdinalIgnoreCase);
        var zhIdx = text.IndexOf("非叙事性音乐", StringComparison.OrdinalIgnoreCase);
        var cut = enIdx >= 0 && (zhIdx < 0 || enIdx < zhIdx) ? enIdx : zhIdx;
        if (cut >= 0)
        {
            var head = text.Substring(0, cut).TrimEnd(' ', '\t', '\r', '\n');
            return head + "\n\n" + NoNonDiegeticMusicTrailer;
        }
        text = text.TrimEnd();
        const string noMusic = "\n\n" + NoNonDiegeticMusicTrailer;
        if (text.IndexOf("overall_soundscape", StringComparison.OrdinalIgnoreCase) >= 0)
            return text + noMusic;
        return text + "\n\noverall_soundscape:\nAmbient environmental and foley sounds of the scene only; dialogue belongs to detailed_description, and one-off sound effects belong to their shot." + noMusic;
    }

    /// <summary>
    /// 从 SD 紧凑提示词（PromptText）解析参考图绑定行：标准行为“@图N [资产名]…”，兼容旧式整行
    /// “参考图:第一张(@图1)为[资产名]…”。返回按编号升序的 (编号, 资产名)。
    /// 识别不了资产名（如纯画面风格行）或编号重复的条目跳过，避免把无效行锁给 H3。
    /// </summary>
    private static List<(int N, string Name)> ParseSdReferenceBindings(string? text)
    {
        var result = new List<(int N, string Name)>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (!line.StartsWith("参考图", StringComparison.Ordinal) && !line.Contains("@图", StringComparison.Ordinal)) continue;
            var segments = System.Text.RegularExpressions.Regex.Split(line, @"；|;");
            foreach (var segRaw in segments)
            {
                var seg = segRaw.Trim();
                if (seg.Length == 0) continue;
                var m = System.Text.RegularExpressions.Regex.Match(seg, @"@图\s*(\d+)[^\[]*\[([^\]）]+)\]");
                if (!m.Success) continue;
                var n = int.Parse(m.Groups[1].Value);
                var name = m.Groups[2].Value.Trim();
                // 画风/光影质感行不锁为 H3 输入素材：H3 统一不用参考图做画风，风格一律以文字表达（见 BuildH3SystemPrompt）
                if (string.IsNullOrWhiteSpace(name) || name.Contains("光影质感", StringComparison.Ordinal)
                    || name.Contains("画面风格", StringComparison.Ordinal)
                    || seg.Contains("画面风格参考", StringComparison.Ordinal)) continue;
                if (result.Any(r => r.N == n)) continue;
                result.Add((n, name));
            }
        }
        return result.OrderBy(r => r.N).ToList();
    }

    /// <summary>
    /// H3 输出后的绑定一致性校验（把「Subject 编号铁律」从文字规则变成程序检查）：
    /// 素材绑定行（@图片N）、subject_definitions 的 &lt;Subject N&gt;、正文所有 &lt;Subject N&gt; 引用三者必须一一对应。
    /// 传 locked（系统从 SD 绑定行解析出的确定表）时按该表逐一核对编号与资产名。
    /// 返回问题列表，空列表表示通过。
    /// </summary>
    private static List<string> ValidateH3SubjectBindings(string h3, List<(int N, string Name)>? locked, out List<int>? missingDefNs, out bool hasBindingRows)
    {
        var issues = new List<string>();
        missingDefNs = null;
        hasBindingRows = false;
        if (string.IsNullOrWhiteSpace(h3)) { issues.Add("H3 文本为空"); return issues; }

        var bindNs = new List<int>();
        var styleBindLines = new List<string>();
        foreach (var line in h3.Split('\n'))
        {
            var lt = line.Trim();
            var m = System.Text.RegularExpressions.Regex.Match(lt, @"^@图片\s*(\d+)");
            if (!m.Success) continue;
            bindNs.Add(int.Parse(m.Groups[1].Value));
            // H3 统一不使用画面风格/光影质感参考图（画风以文字写入风格句）；出现风格绑定行即致命
            if (lt.Contains("画面风格参考", StringComparison.Ordinal) || lt.Contains("光影质感", StringComparison.Ordinal))
                styleBindLines.Add(lt);
        }
        if (styleBindLines.Count > 0)
            issues.Add("FATAL: 素材绑定出现画面风格/光影质感参考行（H3 禁止用参考图做画风，请把画风写成 detailed_description 首句文字）：" + string.Join(" / ", styleBindLines));
        bindNs = bindNs.Distinct().OrderBy(x => x).ToList();
        // 文本里只要写了素材绑定行（@图片1..N），素材说明本身即成绑定权威：<Subject N> 必须与 @图片N 一一对应，
        // 不依赖 SD 锁定表（独立生成线同样要硬校验，防止缺定义/自造主体/编号错位的文本静默入库）。
        hasBindingRows = bindNs.Count > 0;

        var defNs = new List<int>();
        var defPicNs = new List<int>();
        var inDefs = false;
        foreach (var line in h3.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("subject_definitions", StringComparison.OrdinalIgnoreCase)) { inDefs = true; continue; }
            if (inDefs && t.StartsWith("summary:", StringComparison.OrdinalIgnoreCase)) break;
            if (!inDefs) continue;
            var m = System.Text.RegularExpressions.Regex.Match(t, @"<Subject\s+(\d+)>");
            if (m.Success) defNs.Add(int.Parse(m.Groups[1].Value));
            foreach (System.Text.RegularExpressions.Match pm in System.Text.RegularExpressions.Regex.Matches(t, @"<Picture\s+(\d+)>"))
                defPicNs.Add(int.Parse(pm.Groups[1].Value));
        }
        defNs = defNs.Distinct().OrderBy(x => x).ToList();
        defPicNs = defPicNs.Distinct().OrderBy(x => x).ToList();

        // 正文（素材说明/definitions 之外，含 summary / retention_analysis / detailed_description）中出现的 <Subject N> 与 <Picture N> 引用
        var refNs = new List<int>();
        var refPicNs = new List<int>();
        var seenSection = false;
        foreach (var line in h3.Split('\n'))
        {
            var t = line.Trim();
            if (t.StartsWith("subject_definitions", StringComparison.OrdinalIgnoreCase)) { seenSection = true; continue; }
            if (t.StartsWith("summary:", StringComparison.OrdinalIgnoreCase)) { seenSection = false; continue; }
            if (seenSection) continue;
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t, @"<Subject\s+(\d+)>"))
                refNs.Add(int.Parse(m.Groups[1].Value));
            foreach (System.Text.RegularExpressions.Match pm in System.Text.RegularExpressions.Regex.Matches(t, @"<Picture\s+(\d+)>"))
                refPicNs.Add(int.Parse(pm.Groups[1].Value));
        }
        refNs = refNs.Distinct().ToList();
        refPicNs = refPicNs.Distinct().ToList();

        if (bindNs.Count == 0)
        {
            if (locked != null && locked.Count > 0) issues.Add("缺少素材绑定行（@图片N）");
            return issues; // 无锁定且无绑定行视为无参考图镜头，不强制
        }
        for (var i = 1; i <= bindNs.Count; i++)
        {
            if (!bindNs.Contains(i)) { issues.Add("素材绑定编号不连续，缺 @图片" + i); break; }
        }
        if (locked != null && locked.Count > 0)
        {
            var lockNs = locked.Select(x => x.N).Distinct().OrderBy(x => x).ToList();
            if (bindNs.Count != lockNs.Count || bindNs.SequenceEqual(lockNs) == false)
            {
                issues.Add("素材绑定行与系统参考图绑定表不一致（系统 " + string.Join(",", lockNs.Select(x => "@图片" + x)) + "，实际 " + string.Join(",", bindNs.Select(x => "@图片" + x)) + "）");
            }
            else
            {
                var lines = h3.Split('\n');
                foreach (var lockN in lockNs)
                {
                    var lockName = locked.First(l => l.N == lockN).Name;
                    var idx = Array.FindIndex(lines, l => l.Trim().StartsWith("@图片" + lockN));
                    if (idx < 0) continue;
                    // 资产名比对前做「去标点归一化」：LLM 会把手写资产名里的 “月亮鱼” 写成 "月亮鱼" 或直接省掉引号，
                    // 逐字符 Contains 会把它误判成「与系统绑定表不一致」而整镜作废（实测：凡是资产名含中文引号的镜头
                    // 必失败、不含引号的镜头全通过）。归一化后仍不一致才报错，并把模型实际写的段落回显进错误信息。
                    var windowEnd = lines.Length;
                    for (var j = idx + 1; j < lines.Length; j++)
                        if (lines[j].Trim().StartsWith("@图片", StringComparison.Ordinal)) { windowEnd = j; break; }
                    var windowText = string.Join(" ", lines.Skip(idx).Take(windowEnd - idx));
                    if (!H3AssetNameMatches(windowText, lockName))
                        issues.Add("@图片" + lockN + " 资产名应为 [" + lockName + "]，实际写作 " + DescribeActualBindingLine(lines[idx]) + "，与系统绑定表不一致");
                }
            }
        }
        if (defNs.Count != bindNs.Count || defNs.SequenceEqual(bindNs) == false)
        {
            var miss = bindNs.Where(n => !defNs.Contains(n)).ToList();
            if (miss.Count > 0) missingDefNs = miss;
            issues.Add("subject_definitions 的 <Subject N> 必须与素材绑定一一对应（绑定 " + string.Join(",", bindNs.Select(x => "S" + x)) + "，定义 " + string.Join(",", defNs.Select(x => "S" + x)) + "）");
        }
        var extra = refNs.Where(r => !defNs.Contains(r)).Distinct().ToList();
        if (extra.Count > 0)
            issues.Add("正文引用了未在 subject_definitions 定义的 <Subject " + string.Join(">, <Subject ", extra) + ">");
        // subject_definitions 自造了素材清单之外的 <Subject N>（幽灵主体）：必须删除越界定义，对应瞬间画面元素
        // 只能写成 detailed_description 的动作/环境描写，禁止占用参考图槽位
        var extraDefs = defNs.Where(n => !bindNs.Contains(n)).Distinct().OrderBy(x => x).ToList();
        if (extraDefs.Count > 0)
            issues.Add("subject_definitions 定义了素材清单之外的主体 <Subject " + string.Join(">, <Subject ", extraDefs) + ">（本镜输入素材只有 " + string.Join(",", bindNs.Select(x => "@图片" + x)) + "）：请删除这些越界定义，并把它们描述的尘土/光影/掉落物等瞬间画面元素改写进 detailed_description，禁止为其保留 <Picture N> 引用或保留行");
        // <Picture N> 越界引用（引用了未上传的参考图编号，常见于 retention_analysis / definitions 自造编号）
        var maxBind = bindNs.Max();
        var defPicOut = defPicNs.Where(n => !bindNs.Contains(n)).Distinct().OrderBy(x => x).ToList();
        if (defPicOut.Count > 0)
            issues.Add("subject_definitions 引用了不存在的参考图 <Picture " + string.Join(">, <Picture ", defPicOut) + ">（本镜输入素材为 @图片1~@图片" + maxBind + "）：请改为引用已上传的 <Picture N>，或删除引用了空槽位的 <Subject> 定义");
        var refPicOut = refPicNs.Where(n => !bindNs.Contains(n)).Distinct().OrderBy(x => x).ToList();
        if (refPicOut.Count > 0)
            issues.Add("retention_analysis/正文 引用了不存在的参考图 <Picture " + string.Join(">, <Picture ", refPicOut) + ">（本镜输入素材为 @图片1~@图片" + maxBind + "）：请删除这些越界引用行");
        return issues;
    }

    /// <summary>
    /// 资产名比对归一化：先做全角→半角，再去掉所有空白与标点（引号 “ ” " ' 「 」『 』、方括号【】[]、括号（）、
    /// 书名号、破折号、斜杠、顿号等），只保留字母、数字与汉字，最后转小写。
    /// 目的：LLM 抄写带标点的资产名时会把中文弯引号写成英文直引号或直接省略，逐字符比对会误杀整个镜头；
    /// 归一化后「深蓝色“月亮鱼”绘本」「深蓝色"月亮鱼"绘本」「深蓝色月亮鱼绘本」三者视为同一资产。
    /// </summary>
    private static string NormalizeForAssetNameCompare(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder(text.Length);
        foreach (var raw in text)
        {
            var ch = raw;
            if (ch >= '\uFF01' && ch <= '\uFF5E') ch = (char)(ch - 0xFEE0); // 全角 ASCII → 半角
            if (char.IsWhiteSpace(ch)) continue;
            if (!char.IsLetterOrDigit(ch)) continue; // 标点/引号/括号一律忽略
            if (ch >= 'A' && ch <= 'Z') ch = (char)(ch + 32);
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>资产名比对：归一化后做子串包含判断（保留原「名字出现在该行即通过」的语义，只消除标点差异）。</summary>
    private static bool H3AssetNameMatches(string? text, string? lockName)
    {
        var expected = NormalizeForAssetNameCompare(lockName);
        if (expected.Length == 0) return true;
        return NormalizeForAssetNameCompare(text).Contains(expected, StringComparison.Ordinal);
    }

    /// <summary>取素材绑定行里模型实际写的资产名（方括号/书名号/圆括号内的首段），用于把「实际写作什么」回显进校验错误。</summary>
    private static string DescribeActualBindingLine(string line)
    {
        var raw = (line ?? "").Trim();
        var shown = raw.Length > 80 ? raw.Substring(0, 80) + "…" : raw;
        var m = System.Text.RegularExpressions.Regex.Match(raw, @"\[\s*([^\]\r\n]{1,60})\s*\]|【\s*([^】\r\n]{1,60})\s*】");
        var actual = m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim() : shown;
        return "[" + actual + "]（该行原文：" + shown + "）";
    }

    /// <summary>
    /// 锁定表模式下，当模型在 subject_definitions 漏写了某条绑定（@图片N）对应的 &lt;Subject N&gt; 定义时，
    /// 用锁定表的资产名在节末补齐一行最小定义（外观以参考图为准），返回修补后的全文；节边界定位失败返回 null。
    /// 适用于“绑定/编号/资产名/正文引用全部正确、只是少定义一行”的纯漏写场景。
    /// </summary>
    private static string? RepairH3MissingSubjectDefs(string h3, List<(int N, string Name)> locked, List<int> missingDefNs)
    {
        var lines = h3.Split('\n').ToList();
        var defIdx = lines.FindIndex(l => l.TrimStart().StartsWith("subject_definitions", StringComparison.OrdinalIgnoreCase));
        if (defIdx < 0) return null;
        var endIdx = lines.FindIndex(defIdx + 1, l => l.TrimStart().StartsWith("summary:", StringComparison.OrdinalIgnoreCase));
        if (endIdx < 0) return null;
        var add = new List<string>();
        foreach (var n in missingDefNs.OrderBy(x => x))
        {
            var name = locked.FirstOrDefault(x => x.N == n).Name;
            if (string.IsNullOrWhiteSpace(name)) continue;
            add.Add("<Subject " + n + "> is the " + name.Trim() + " in <Picture " + n + ">, with appearance consistent with the reference image.");
        }
        if (add.Count == 0) return null;
        lines.InsertRange(endIdx, add);
        return string.Join('\n', lines);
    }

    private async Task<string> GenerateH3PromptCoreAsync(ShotScriptBlock? shot, SeedancePrompt? p, string apiUrl, string apiKey, string model, string? thinkingMode = null, string? stylePrompt = null, string? refAssetText = null, int shotRefCount = 1, List<(int N, string Name)>? frameLockedBindings = null, int projectId = 0)
    {
        // 素材源：优先分镜脚本镜头段落（H3 独立生成线）；无分镜段落时退化为 SD 提示词（老数据兜底）
        var fromScript = shot != null && !string.IsNullOrWhiteSpace(shot.RawText);
        var sourceText = fromScript ? shot!.RawText : (p?.PromptText ?? "");

        // 参考图绑定表（系统确定、最高优先级，锁死数量/编号/资产名，杜绝 LLM 重拟与“同资产复制 N 行”）：
        //   1. 该镜头存在分镜帧绑定 FrameAssetBindings 时，直接以帧绑定为锁定表（SD/H3 统一的权威来源）；
        //   2. 无帧绑定的老数据，才退而解析该镜头 SD 提示词已有的 @图N 绑定行。
        // 锁定表非空时素材行数量/编号/资产名全部照抄锁定表，LLM 无需再凭空猜第 N 张参考图是谁。
        var lockedBindings = (frameLockedBindings != null && frameLockedBindings.Count > 0)
            ? frameLockedBindings
            : ParseSdReferenceBindings(p?.PromptText);

        // 参考图数量：取用户预设 ReferenceImages 的有效数与调用方按镜头匹配项目资产算出的数量中的较大值，
        // 避免预设图数偏少时场景参考因名额不足被挤出（场景参考图由前端按资产库精确匹配补齐）
        var refCount = 0;
        if (!string.IsNullOrWhiteSpace(p?.ReferenceImages))
        {
            try { refCount = JsonSerializer.Deserialize<List<string>>(p.ReferenceImages)?.Count(s => !string.IsNullOrWhiteSpace(s)) ?? 0; }
            catch { refCount = 0; }
        }
        if (refCount < shotRefCount) refCount = shotRefCount;
        // 已解析出 SD 绑定行时以绑定行数为权威（而不是“预设图数取大”）：绑定行即参考图槽位的确定映射。
        // 画风/光影质感行已被剔除（@图5 光影质感不再作为 H3 素材），若仍按 ReferenceImages 的旧数（含风格图）
        // 提示“共 N 张”，会与仅含非风格绑定的锁定表自相矛盾，诱发输出错乱；参考图槽位由前端按新文本绑定行收敛，
        // 保证“文本绑定行数 == 实际参考图数”，不会出现提交闸门里文本与实传数量不一致。
        if (lockedBindings.Count > 0) refCount = lockedBindings.Count;

        var duration = (p?.Duration ?? 0) > 0 ? p!.Duration : (fromScript && shot!.Duration > 0 ? shot!.Duration : 5);
        var shotType = !string.IsNullOrWhiteSpace(p?.ShotType) ? p!.ShotType
            : (fromScript && !string.IsNullOrWhiteSpace(shot!.ShotType) ? shot.ShotType : "未标注");

        var userMsg = new StringBuilder();
        userMsg.AppendLine("【镜头信息】");
        userMsg.AppendLine("总时长：" + duration + " 秒");
        userMsg.AppendLine("镜头类型：" + shotType);
        userMsg.AppendLine("单元/镜头：" + (string.IsNullOrWhiteSpace(p?.UnitName) ? "未标注" : p.UnitName) + (string.IsNullOrWhiteSpace(p?.ShotLabel) ? "" : " / " + p.ShotLabel));
        userMsg.AppendLine();
        userMsg.AppendLine("【参考图数量】" + refCount + " 张（@图片1 ~ @图片" + refCount + "，必须按此顺序编号、一一对应参考图输入顺序，禁止超出、跳号或打乱）");
        userMsg.AppendLine();
        if (lockedBindings.Count > 0)
        {
            userMsg.AppendLine("【参考图绑定表（系统已确定，最高优先级：素材说明与正文的 @图片N / <Subject N> 必须逐条照抄此表，禁止增删行、换序或改资产名）】");
            foreach (var b in lockedBindings)
                userMsg.AppendLine("@图片" + b.N + " [" + b.Name + "]");
            userMsg.AppendLine();
        }
        if (!string.IsNullOrWhiteSpace(refAssetText))
        {
            userMsg.AppendLine(refAssetText);
            userMsg.AppendLine();
        }
        // 音色参考音频（可选）：按"镜头出场角色 → 项目音色绑定表"解析本镜要带的音色（最多 3 段）。
        // <Audio N> 编号与提交 ComfyUI 时 ref_audios 槽位的注入顺序由 VoiceRefResolver 统一产出，保证"编号 ↔ 实际音频"严格一致。
        var voiceRefs = VoiceRefResolver.Resolve(
            _db,
            projectId > 0 ? projectId : (p?.ProjectId ?? 0),
            p?.FrameId,
            lockedBindings.Select(b => b.Name).ToList());
        if (voiceRefs.Count > 0)
        {
            userMsg.AppendLine("【音色参考表（系统已确定，最高优先级：<Audio N> 与角色的对应关系必须逐条照抄，禁止改派、增删或换序）】");
            userMsg.AppendLine("本镜共 " + voiceRefs.Count + " 段音色参考音频（编号 <Audio 1> ~ <Audio " + voiceRefs.Count + ">）：");
            foreach (var v in voiceRefs)
                userMsg.AppendLine("<Audio " + v.AudioIndex + "> [" + v.CharacterName + "]");
            userMsg.AppendLine("表内角色在本镜头开口时，其说话句必须写成：<Subject N> (Sx) 以参考 <Audio M> 的音色和说话方式说道，<d>[Chinese] 台词原文</d>；台词内容仍以源文本为准，音频只提供音色与说话方式；未在表中开口的角色不写 <Audio M>；<Audio M> 仅可用于音色/说话方式参考，禁止写成音乐风格、节拍或配乐参考。");
            userMsg.AppendLine();
        }
        // L3 关键帧锚定：与 Stage 9 同一套锚定，保证 H3 提示词的站位/道具状态/线索呈现不漂移
        var h3ProjectId = projectId > 0 ? projectId : (p?.ProjectId ?? 0);
        if (h3ProjectId > 0)
        {
            var h3Anchor = BuildKeyframeAnchorForEpisode(h3ProjectId, p?.EpisodeNumber ?? shot?.EpisodeNumber ?? 0);
            if (!string.IsNullOrWhiteSpace(h3Anchor))
                userMsg.AppendLine("【关键帧锚定（L3，系统确定，优先级高于模型自由发挥）】\n" + h3Anchor);
        }
        if (fromScript)
        {
            userMsg.AppendLine("【分镜脚本】（Stage 5 分镜脚本中该镜头的原文，剧情、主体、动作、台词以此为准，请直接据此创作 H3 参考生视频提示词）");
            userMsg.AppendLine(sourceText);
        }
        else
        {
            userMsg.AppendLine("【已有 Seedance 分镜提示词（SD 紧凑格式，请将其完整改写成 H3 参考生视频提示词；剧情、主体、动作、台词不得改变）】");
            userMsg.AppendLine(sourceText);
        }
        var shotLabel = (p?.ShotLabel ?? shot?.ShotLabel) ?? "未知";
        var raw = await _llm.CallAsync(apiUrl, apiKey, model, BuildH3SystemPrompt(fromScript, stylePrompt ?? DefaultStylePrompt, !string.IsNullOrWhiteSpace(refAssetText), voiceRefs.Count > 0), userMsg.ToString(), thinkingMode: thinkingMode);
        // 绑定一致性程序校验：素材绑定行 / <Subject N> 定义 / 正文引用必须一一对应。
        // 素材说明只要写了绑定行（@图片1..N），自身即绑定权威，无论有无 SD 锁定表都硬校验；
        // 不一致时先自动补纯漏写的定义，其余错位携带校验错误让模型带着错误信息重生成。
        // 修正机会按参考图条数放宽：绑定 ≥5 条时模型要一次写全 5~7 条 <Subject N> 定义 + 正文引用，
        // 实测「两次都不过」的比例明显更高（批量里表现为某几个镜头反复失败、内容被丢弃），故给 2 次修正机会。
        string clean = "";
        List<string>? lastIssues = null;
        var maxAttempts = lockedBindings.Count >= 5 ? 3 : 2;
        // 已发生的 LLM 调用次数（首次 1 次 + 每次带错误信息的重生成），日志与异常里回报真实次数
        var llmCalls = 1;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var (cleanText, fixCount) = SanitizeH3VoiceReferences(raw);
            if (fixCount > 0)
                System.Diagnostics.Debug.WriteLine($"[H3对白合规兜底] 自动修正 {fixCount} 处暗示开口却无台词的人声描述（镜头：{shotLabel}）");
            clean = cleanText;
            var bindingIssues = ValidateH3SubjectBindings(clean, lockedBindings.Count > 0 ? lockedBindings : null, out var missingDefNs, out _);
            if (bindingIssues.Count == 0) { lastIssues = null; break; }

            var fatalIssues = bindingIssues.Where(i => i.StartsWith("FATAL:", StringComparison.Ordinal)).ToList();
            // 1) 锁定表下若仅是 subject_definitions 少定义了几条 <Subject N>（绑定/编号/资产名/正文引用都正确），
            //    属模型纯漏写：用锁定表资产名自动补齐即可，不必再消耗一次重生成。
            if (attempt == 0 && fatalIssues.Count == 0 && lockedBindings.Count > 0 && missingDefNs != null && missingDefNs.Count > 0
                && bindingIssues.Count == 1 && bindingIssues[0].StartsWith("subject_definitions 的 <Subject N> 必须与素材绑定一一对应", StringComparison.Ordinal))
            {
                var repaired = RepairH3MissingSubjectDefs(clean, lockedBindings, missingDefNs);
                if (repaired != null && ValidateH3SubjectBindings(repaired, lockedBindings, out _, out _).Count == 0)
                {
                    System.Diagnostics.Debug.WriteLine("[H3绑定一致性] 已自动补齐漏写的 <Subject> 定义：S" + string.Join(",S", missingDefNs.OrderBy(x => x)) + "（镜头：" + shotLabel + "）");
                    clean = repaired;
                    lastIssues = null;
                    break;
                }
            }
            // 2) 其余不一致（含独立生成线：素材说明自身有绑定行却不一一对应，如场景/道具漏定义、编号错位、
            //    自造素材里不存在的主体）记录问题并让模型带错误信息修正一次再校验。
            lastIssues = bindingIssues;
            // 每轮未通过都带错误信息重生成，直到用完 maxAttempts 次机会。
            // 原先只在 attempt==0 重发、attempt>=1 直接 break：绑定 ≥5 条时名义 3 次实际只调了 2 次 LLM，
            // 日志又把上限当次数打印，看起来像「重试 3 次都过不去」。
            if (attempt < maxAttempts - 1)
            {
                userMsg.AppendLine();
                userMsg.AppendLine("【修正要求】（上一次生成未通过参考图绑定一致性校验，请按规则修正后重新输出完整 H3 文本，不要输出任何解释）");
                userMsg.AppendLine(string.Join("\n", bindingIssues.Select(i => "- " + i)));
                raw = await _llm.CallAsync(apiUrl, apiKey, model, BuildH3SystemPrompt(fromScript, stylePrompt ?? DefaultStylePrompt, !string.IsNullOrWhiteSpace(refAssetText), voiceRefs.Count > 0), userMsg.ToString(), thinkingMode: thinkingMode);
                llmCalls++;
                continue;
            }
            break;
        }
        if (lastIssues != null)
        {
            var fatalIssues = lastIssues.Where(i => i.StartsWith("FATAL:", StringComparison.Ordinal)).ToList();
            var problemText = string.Join("；", fatalIssues.Count > 0 ? fatalIssues : lastIssues);
            _logger?.LogWarning("[H3绑定一致性] 镜头 {Shot} 连续 {Attempts} 次未通过：{Issues}", shotLabel, llmCalls, problemText);
            throw new Exception("H3 参考图绑定一致性校验未通过（已尝试 " + llmCalls + " 次，镜头 " + shotLabel + "）：" + problemText + "；请重新生成该镜头提示词");
        }
        return clean;
    }

    /// <summary>
    /// H3 对白合规自动兜底（硬约束，提示词规则 6 的代码级保证）：
    /// 当 H3 全文不含 <d> 标签（即该镜头无台词）时，把"吐槽声/说话声/对话声/抱怨声/议论声/叫喊声/低语声"等
    /// 暗示"人物开口且内容具体却未写出台词"的环境人声词，替换为规则允许的笼统人声描述（中文，与中文正文一致）。
    /// 全文含 <d> 标签时说明台词已逐字写入 <d>[Chinese] ...</d>，属于合规场景，不替换，避免误伤"吐槽/说话"等说话方式描述。
    /// </summary>
    private static (string Text, int FixCount) SanitizeH3VoiceReferences(string h3)
    {
        if (string.IsNullOrWhiteSpace(h3)) return (h3, 0);
        if (h3.Contains("<d>", StringComparison.OrdinalIgnoreCase)) return (h3, 0);

        var t = h3;
        var fix = 0;
        foreach (var (from, to) in new[]
        {
            ("吐槽声", "人群嘈杂声"),
            ("抱怨声", "人群嘈杂声"),
            ("议论声", "人群嘈杂声"),
            ("说话声", "背景低语"),
            ("对话声", "背景低语"),
            ("叫喊声", "远处含混的说话声"),
            ("低语声", "背景低语"),
        })
        {
            if (!t.Contains(from)) continue;
            fix += System.Text.RegularExpressions.Regex.Matches(t, System.Text.RegularExpressions.Regex.Escape(from)).Count;
            t = t.Replace(from, to);
        }
        return (t, fix);
    }

    /// <summary>把用户资产库整理成 H3 生成时的【可用参考资产】清单文本（按用途分组）。资产为空时返回空串。</summary>
    /// <summary>
    /// 按镜头从项目资产库构建 H3 参考素材清单（与 SD 线共用同一套项目资产与命名规则，参考图名单只认资产库，
    /// 资产名与 SD 的 @图 名单一致：角色用 NormalizeCharacterName、环境/道具/特效用 NormalizeSceneName），
    /// 并返回该镜头的参考图数量。
    /// 首选来源为分镜帧资产绑定 <paramref name="frameBindings"/>（FrameAssetBindings，SD/H3 统一的权威清单，
    /// 空镜只有场景时素材清单就只有场景一条，杜绝「同资产复制 N 行」）；
    /// 无绑定表的历史镜头才回退到镜头文本匹配，且匹配失败绝不退化全量——宁可自然语言概括（N=1）。
    /// <paramref name="unitLocation"/> 为分集细化（Stage 4）中该镜头所属单元的结构化「地点」，
    /// 分镜脚本（Stage 5）未保留该字段，拼接进匹配文本以补偿场景/道具资产命中。
    /// </summary>
    private static (string Text, int RefCount) BuildH3ReferenceMaterialList(
        ShotScriptBlock? shot, SeedancePrompt? p,
        List<CharacterAsset> characters, List<PropAsset> props, List<EffectAsset> effects, List<EnvironmentAsset> environments,
        List<string> autoSupplementedNames, string? unitLocation = null,
        IReadOnlyList<FrameAssetBinding>? frameBindings = null)
    {
        // —— 新路径（统一引用分镜帧绑定）：该镜头存在 FrameAssetBindings 时，可用参考素材完全以绑定为准。
        //    不再做镜头文本子串匹配、更不退化全量——空镜（无人物/无道具/无特效，仅场景）素材清单就只有场景一条，
        //    杜绝「同一种资产被复制 N 行」；绑定为空时仍以 N=1 自然语言概括处理。
        if (frameBindings != null && frameBindings.Count > 0)
        {
            var charNames = new List<string>();
            var envNames = new List<string>();
            var propNames = new List<string>();
            var effectNames = new List<string>();
            foreach (var b in frameBindings)
            {
                var name = b.Name?.Trim();
                if (string.IsNullOrEmpty(name)) continue;
                switch (b.Category)
                {
                    case "Character": if (!charNames.Contains(name)) charNames.Add(name); break;
                    case "Environment": if (!envNames.Contains(name)) envNames.Add(name); break;
                    case "Prop": if (!propNames.Contains(name)) propNames.Add(name); break;
                    case "Effect": if (!effectNames.Contains(name)) effectNames.Add(name); break;
                }
            }
            if (charNames.Count == 0 && envNames.Count == 0 && propNames.Count == 0 && effectNames.Count == 0)
                return ("", 1);
            var fbRefCount = charNames.Count + envNames.Count + propNames.Count + effectNames.Count;
            // 角色卡「属性」里声明的随身物品：已画在同一张角色卡内，随角色行一起交代，不单独占参考图槽
            var personalByChar = PersonalItemResolver.BuildByCharacter(characters);
            var sbB = new StringBuilder("【可用参考素材】（@图片N 括号内的 [资产名] 必须从下方清单中选择，名称一字不差；清单中没有所需资产时，不要用方括号，直接写该镜头的自然语言外观概括）");
            if (charNames.Count > 0)
                sbB.Append('\n').Append("角色（人物参考）：").Append(string.Join("、", charNames.Select(n => DescribeCharacterPersonalItems(n, personalByChar))));
            if (envNames.Count > 0)
                sbB.Append('\n').Append("环境（环境/场景参考）：").Append(string.Join("、", envNames));
            if (propNames.Count > 0)
                sbB.Append('\n').Append("道具（道具/特效/技能参考）：").Append(string.Join("、", propNames));
            if (effectNames.Count > 0)
                sbB.Append('\n').Append("特效（技能特效参考）：").Append(string.Join("、", effectNames));
            return (sbB.ToString(), fbRefCount);
        }

        var hay = new StringBuilder();
        if (shot != null && !string.IsNullOrWhiteSpace(shot.RawText)) hay.Append(shot.RawText).Append('\n');
        if (!string.IsNullOrWhiteSpace(unitLocation)) hay.Append(unitLocation).Append('\n');
        if (!string.IsNullOrWhiteSpace(p?.PromptText)) hay.Append(p.PromptText);
        var text = hay.ToString();

        var tempSupplemented = new HashSet<string>(autoSupplementedNames ?? new List<string>(), StringComparer.OrdinalIgnoreCase);

        List<CharacterAsset> MatchChars()
        {
            var matched = (characters ?? new List<CharacterAsset>())
                .Where(c => !tempSupplemented.Contains(c.Name))
                .Where(c =>
                {
                    var n = NormalizeCharacterName(c.Name).Trim();
                    return n.Length > 0 && text.Contains(n, StringComparison.Ordinal);
                })
                .DistinctBy(c => NormalizeCharacterName(c.Name).Trim())
                .ToList();
            // 明确不回退全量：本镜头文本没有命中的资产说明它不属于该镜头（可能为群像/配角/无资产镜头），
            // 宁可走自然语言概括（返回 N=1）也绝不把全量资产库塞进参考素材清单
            return matched;
        }

        var chars = MatchChars();
        var envs = (environments ?? new List<EnvironmentAsset>())
            .Where(e => { var n = NormalizeSceneName(e.Name).Trim(); return n.Length > 0 && text.Contains(n, StringComparison.Ordinal); })
            .DistinctBy(e => NormalizeSceneName(e.Name).Trim())
            .ToList();
        var propList = (props ?? new List<PropAsset>())
            .Where(x => { var n = NormalizeSceneName(x.Name).Trim(); return n.Length > 0 && text.Contains(n, StringComparison.Ordinal); })
            .DistinctBy(x => NormalizeSceneName(x.Name).Trim())
            .ToList();
        var effectList = (effects ?? new List<EffectAsset>())
            .Where(x => { var n = NormalizeSceneName(x.Name).Trim(); return n.Length > 0 && text.Contains(n, StringComparison.Ordinal); })
            .DistinctBy(x => NormalizeSceneName(x.Name).Trim())
            .ToList();

        // 该镜头没有任何可参考的项目资产时，走自然语言概括模式（与无清单时一致）
        if (chars.Count == 0 && envs.Count == 0 && propList.Count == 0 && effectList.Count == 0)
            return ("", 1);

        // 参考图数量：按镜头匹配到的资产数估算（上限 6，避免超出视频模型参考图限制）。
        // 本分支仅覆盖无帧绑定表的历史镜头：角色每人 1 张、环境/道具/特效各类各 1 张，不再有全量退化，
        // 因此数量不会虚高；用户预设的 ReferenceImages 由 GenerateH3PromptCoreAsync 与本值取较大值，
        // 保证场景参考在名额上不被预设图数挤掉
        var refCount = Math.Clamp(chars.Count + Math.Min(envs.Count, 1) + Math.Min(propList.Count, 1) + Math.Min(effectList.Count, 1), 1, 6);

        var sb = new StringBuilder("【可用参考素材】（@图片N 括号内的 [资产名] 必须从下方清单中选择，名称一字不差；清单中没有所需资产时，不要用方括号，直接写该镜头的自然语言外观概括）");
        if (chars.Count > 0)
            sb.Append('\n').Append("角色（人物参考）：").Append(string.Join("、", chars.Select(c => NormalizeCharacterName(c.Name).Trim() + "（" + (string.IsNullOrWhiteSpace(c.Description) ? "外观以参考图为准" : c.Description) + "）")));
        if (envs.Count > 0)
            sb.Append('\n').Append("环境（环境/场景参考）：").Append(string.Join("、", envs.Select(e => NormalizeSceneName(e.Name).Trim())));
        if (propList.Count > 0)
            sb.Append('\n').Append("道具（道具/特效/技能参考）：").Append(string.Join("、", propList.Select(x => NormalizeSceneName(x.Name).Trim())));
        if (effectList.Count > 0)
            sb.Append('\n').Append("特效（技能特效参考）：").Append(string.Join("、", effectList.Select(x => NormalizeSceneName(x.Name).Trim())));
        return (sb.ToString(), refCount);
    }

    /// <summary>
    /// 角色素材行渲染：角色卡「属性」里声明了随身物品的角色，在【可用参考素材】里直接带上物品清单，
    /// 供 LLM 把「同图附带其随身物品（…）」写进该角色那一行 @图片N；没有声明的角色原样返回。
    /// </summary>
    private static string DescribeCharacterPersonalItems(string name, Dictionary<string, List<string>> byCharacter)
    {
        var items = PersonalItemResolver.ForCharacter(byCharacter, name);
        if (items.Count == 0) return name;
        return name + "（随身物品：" + string.Join("、", items) + "，已画在本角色卡内、随本卡一起生效，禁止为它们单独占参考图槽）";
    }

    private static string BuildH3SystemPrompt(bool fromScript, string stylePrompt, bool hasRefAssets, bool hasVoiceRefs = false)
    {
        var sb = new StringBuilder();
        if (fromScript)
            sb.Append("你是一位顶级的 MiniMax H3 参考生视频提示词专家，精通「多张参考图 + 一段文本」的视频生成。你的任务：根据用户提供的【分镜脚本】中的单个镜头原文，直接创作一份 H3 参考生视频提示词（本流程不经过 SD 提示词，镜头的一切剧情与画面信息以分镜脚本为准）。\n\n");
        else
            sb.Append("你是一位顶级的 MiniMax H3 参考生视频提示词专家，精通「多张参考图 + 一段文本」的视频生成。你的任务：把用户提供的【已有 Seedance 分镜提示词】（SD 紧凑格式）完整改写成一份 H3 参考生视频提示词。\n\n");
        sb.Append("H3 与 SD 的关键区别：\n");
        sb.Append("1. H3 是「多张参考图 + 文本描述」生成视频：参考图按上传顺序自动编号注入，提示词中的 @图片N 必须与参考图输入顺序严格一一对应（系统已给出 @图片1~@图片N 编号，禁止超出、跳号或打乱顺序）；\n");
        sb.Append("2. H3 自动生成音效、对白口型：对话通过 <d> 标签逐字触发，台词必须逐字写入 <d>[Chinese] 台词原文</d>，绝不自行扩写、润色或编造；\n");
        sb.Append("3. H3 不需要写 4K/24fps/浅景深 等画质技术参数（由模型与工作流控制），禁止在提示词中出现这类参数；\n");
        sb.Append("4. H3 官方推荐「全能参考生视频」结构化模板（六节英文结构），以下模板是唯一输出格式，必须完整输出全部章节，禁止省略或自行增删章节。\n\n");
        sb.Append("【H3 标准输出模板】（严格按此格式输出；【参考素材说明】与正文六节一律用中文撰写，结构化标签与固定术语保留英文（见规则 7）；不要任何解释或代码块包裹）\n\n");
        sb.Append("【参考素材说明】\n");
        sb.Append("首行固定写：全能参考生视频模式（共 N 个输入素材，全部按上传顺序编号并指派用途）。\n");
        sb.Append("随后按 @图片1~@图片N 顺序，每个素材单独一行，行格式仿照 SD 参考素材行，紧凑简短：\n");
        sb.Append("@图片N [资产名]类型参考，保持…一致；\n");
        sb.Append("「类型参考」措辞与默认保持项：人物→人物形象参考，保持外貌、发型、服装、气质一致；道具→道具参考，保持外观、材质、大小一致；场景/环境→场景参考，保持空间布局、色调一致；特效→特效参考，保持形态、色调、氛围一致。\n");
        sb.Append("人物卡若在【可用参考素材】的角色条目里标注了「随身物品：…」（这些物件已画在同一张角色卡内），就在该 @图片N 行末追加「；同图附带其随身物品（A、B），一并保持外观一致」，物件名称逐一列出即可；示例：\n");
        sb.Append("@图片1 [玻吕茜亚]人物形象参考，保持外貌、发型、服装、气质一致；同图附带其随身物品（挎包、手机、怀表），一并保持外观一致；\n");
        if (hasVoiceRefs)
        {
            sb.Append("【音色参考（音频输入）】除参考图外，本镜还带若干音色参考音频，编号 <Audio 1>~<Audio M> 由系统指定（禁止跳号、超出或自行新增）：\n");
            sb.Append("<Audio N> [角色名]音色参考，只参考音色与说话方式，不复制原台词或原音乐；\n");
            sb.Append("音频行的用途只允许「参考音色 / 参考说话方式（音色、语速、语气）」；禁止写成音乐风格、节拍或配乐参考（本片从头到尾严禁任何配乐），禁止用它复制参考音频里的原台词或原音乐；音频行同样一行一条、紧凑格式，禁止外观复述。\n\n");
        }
        sb.Append("【禁止画面风格参考图（硬约束）】H3 输入素材只允许绑定镜头中实际出现且需保持一致的实体对象（人物/环境/道具/特效）。画面风格、光影质感、风格类一律禁止作为参考图绑定，禁止出现「@图片N [光影质感]」「光影质感参考」「是画面风格参考」这类行；画风/光影/色调需要时全部写成文字：以【项目风格】文本为准，并在 detailed_description 开头风格句原样完整引用【项目风格】全文（一字不改，禁止概括、缩写、删减或改写），不得占用任何输入素材槽位。\n");
        if (hasRefAssets)
        {
            sb.Append("[资产名] 从用户消息中的【参考图绑定表】（无绑定表时从【可用参考素材】清单）对应用途中选、逐字照抄（人物→角色类、场景→环境类、道具/特效→道具类；光影质感/风格类资产禁止选来占素材槽，画风写文字）；资产名内的引号“”、书名号、破折号、斜杠等标点必须原样保留，禁止换成英文引号或省略标点；示例：\n");
            sb.Append("@图片1 [陈默]人物形象参考，保持外貌、发型、服装、气质一致；\n");
            sb.Append("@图片2 [凌晨三点独居小屋]场景参考，保持空间布局、色调一致；\n");
            sb.Append("@图片3 [键盘]道具参考，保持外观、材质、大小一致；\n");
        }
        else
        {
            sb.Append("清单中没有所需资产时，用括号写一句极简外观概括代替 [资产名]（禁止逐条展开），再写类型参考与保持项；示例：\n");
            sb.Append("@图片1（银白长发少女，白金色调）人物形象参考，保持外貌、发型、服装、气质一致；\n");
            sb.Append("@图片2（暖光水汽弥漫的金色浴池）场景参考，保持空间布局、色调一致。\n");
        }
        sb.Append("【参考素材说明】只负责「绑定+用途」：写清每张参考图是什么、需要保持哪些方面一致即可，禁止出现整段外观复述（如「锁该角色的脸型、银白长发、瞳色、服装配色、配饰、身形与气质」）；参考对象的外观以参考图本身为唯一权威，全片任何章节（含 subject_definitions）一律禁止用文字复述参考对象的外观细节，需要指代对象时一律用 <Subject N> 编号引用、不重述。唯「同图附带其随身物品（A、B）」属归属说明：逐一列出物件名称即可，不算外观复述，但禁止展开描述这些物件的外观（颜色、材质、形状、尺寸一律不写）。\n");
        sb.Append("若源文本或【可用参考素材】含有本镜头的场景氛围行（如 金线的浴池+白天（暖光水汽弥漫）），原样保留在清单最后一行，不增删、不合并、不改写。\n\n");
        sb.Append("只输出素材清单本身，禁止输出以上任何规则、说明或示例文字。\n\n");
        sb.Append("subject_definitions:\n");
        sb.Append("<Subject 1> is the [身份一句话（仅名称/归属/用途/人物关系，禁止任何外观特征；角色卡「角色别名」若写明长幼或亲属关系，必须写进本句，如 the younger sister (妹妹) Bolixiya (玻吕茜亚)；无关系时写 the programmer Chen Mo (陈默)）] in <Picture 1>.\n");
        if (hasRefAssets)
            sb.Append("（每个输入参考图都必须定义一个 <Subject N>，N 与 @图片N 一一对应；每行只做「编号 → 参考图 → 身份」绑定：一句话点明图里的对象是什么即可（如 the programmer Chen Mo (陈默)、the white embroidered gloves、the golden-threaded bathhouse），中文专有名词首次定义时用 英文名 (中文名) 标注；本节禁止出现任何外观特征描述——发型/发色/服装/配色/材质/身形等一律不写，参考图已提供完整外观，以图为准；但角色卡「角色别名」给出的长幼/亲属关系（如姐姐/妹妹）属身份信息，必须写进身份句，用于同一镜头多人同框时区分人物，不算外观复述；全片其余章节同样禁止文字复述外观，只允许用 <Subject N> 编号引用；图片仅用于定义主体时，直接作为来源写在 <Subject N> 定义中，不单独创建 <Picture N> 行（<Picture N> 只在图片充当镜头帧锚点时单独列出，本流程默认不建））\n\n");
        else
            sb.Append("（每个输入参考图都必须定义一个 <Subject N>，N 与 @图片N 一一对应；每行只写一句身份（如 the programmer Chen Mo (陈默)，中文专有名词用 英文名 (中文名) 标注），禁止任何外观/服装/配色/材质/身形特征描述；角色卡「角色别名」给出的长幼/亲属关系（如姐姐/妹妹）属身份信息，必须写进身份句以区分同框人物，不算外观复述——外观以参考图本身为唯一权威，全片任何章节均不得用文字复述外观；图片仅用于定义主体时，直接作为来源写在 <Subject N> 定义中，不单独创建 <Picture N> 行）\n\n");
        sb.Append("（【特效非实体声明·硬约束】素材用途为「特效参考」时，该 <Subject N> 的定义句必须自带非实体声明，写成：<Subject N> is the atmospheric effect [特效名] in <Picture N>（非实体：no human form, no face, no body, no silhouette, not a person）；只有用途为「人物形象参考/群像参考」的 <Subject N> 才是人物，其余用途（特效/道具/场景）一律不是人物，严禁被渲染成人形或任何类人实体）\n");
        if (hasVoiceRefs)
            sb.Append("（音色参考音频在 subject_definitions 中单独定义一行，其编号体系独立于 <Subject N>：<Audio 1> is the voice/timbre reference of [角色名]，只用于参考音色与说话方式；音频不是画面主体、不提供外观，禁止把音频写成 <Subject N> 或当作外观来源）\n");
        sb.Append("summary:\n");
        sb.Append("[reference generation] 目标视频展示 [一句话概括：主体、核心动作、使用的参考关系]。\n");
        sb.Append("（任务类型固定为 [reference generation]：参考图仅提供人物/环境/道具/动作的生成指导，画面风格不来自参考图、以 detailed_description 开头风格句的文字描述为准；无帧锚点、无源视频编辑、无音频复用，禁止添加其他任务类型前缀；摘要一段话总结目标视频与参考关系，不引入未定义的新标签）\n\n");
        sb.Append("retention_analysis:\n");
        sb.Append("<Subject 1>（出现在 [Shot 1]、[Shot 2]）：fully_preserved - consistent with <Picture 1>；[如确需说明可写动作/状态/构图层面的保留要点]。\n");
        sb.Append("<Subject 2> ...: partially_preserved - ...。\n");
        sb.Append("（每个 <Subject N> 一行，标注出现镜头与保留方式（fully_preserved / partially_preserved / attribute_transfer / weak_reference）；保留方式只能在 subject_definitions 已定义的参考角色范围内选择，目标视频新增的动作、背景或情节事件不视为保真度损失；外观一致性一律用 consistent with <Picture N> 表述，禁止复述任何外观细节（发型、服装、配色、材质等））\n");
        sb.Append("（【特效保留行·硬约束】用途为「特效参考」的 <Subject N>：保留方式只能写 atmosphere_only，禁止 fully_preserved / partially_preserved；该行必须写明「非实体、无人物形态与面部与肢体，仅作环境与氛围层存在于背景或空气中，不占主体构图位」；特效形态以其参考图为准，禁止具象为人物或任何类人实体）\n");
        if (hasVoiceRefs)
            sb.Append("（音色参考音频在 retention_analysis 中单独列行，标注出现镜头，保留方式固定写 reference - 只参考音色与说话方式，不复制原台词或原音乐；严禁写成 music_style、音乐风格或节拍参考；音频一行一条，不得与 <Subject N> 混行）\n\n");
        sb.Append("detailed_description:\n");
        sb.Append("首句风格句：原样完整引用【项目风格】全文（一字不改，逐句照抄，禁止概括、缩写、删减或改写；风格句末尾紧接负向约束英文原文：no subtitles, no watermarks, no character-name overlays）。\n");
        sb.Append("[Shot 1] 以 [景别/构图] 开场，交代 [主体位置与画面构成]。[动作/状态变化]。[参考内容生效点]。<Subject 1> (S1) [说话方式]，<d>[Chinese] 台词原文</d>。[说完后的动作或反应]。\n");
        sb.Append("[Shot 2] At MM:SS.mmm，切换为 [景别/主体]。[运镜]。[当前声音]。<Subject N> (Sx) [说话方式]，<d>[Chinese] 台词原文</d>。\n");
        if (hasVoiceRefs)
            sb.Append("（说话角色若配有音色参考，其说话句必须写成：<Subject N> (Sx) 以参考 <Audio M> 的音色和说话方式说道，<d>[Chinese] 台词原文</d>；同一角色在多处说话始终引用同一个 <Audio M>，禁止换用其它 <Audio M> 或省略；未配音色参考的说话角色按原模板写法，不加 <Audio M>）\n\n");
        sb.Append("（风格句在 [Shot 1] 之前，必须原样完整引用【项目风格】全文——一字不改、逐句照抄，禁止概括、缩写、删减、改写，也禁止只写一两句摘要（不得写成「目标视频采用电影感、文学性风格，柔光…」这类缩略句），负向约束并入首句（no subtitles, no watermarks, no character-name overlays）；[Shot 1] 开头不写时间戳（默认从 00:00 开始），后续每个 [Shot N] 以 At MM:SS.mmm 标注切换点；时间轴连续覆盖总时长，禁止留空、重叠或跳跃；每个镜头必须包含：构图、主体与位置（主体一律用 <Subject N> 编号引用，禁止在逐镜头描述里重述外观；镜头含多名人物时，构图句必须同时写清各 <Subject N> 的画面方位与相对站位，双人互动必须双方同框入画，禁止某一 <Subject N> 只在文字中被提到而不实际入镜；同框人物之间存在长幼/亲属关系（姐姐/妹妹等）时，构图句与动作句必须用该关系词（如「姐姐」「妹妹」）指称对应 <Subject N>，防止模型把同框的两人混为同一人）、环境与光线、动作状态变化、运镜、当前声音、参考内容生效点；人物出现时带视觉标签 <Subject N>，说话时同时带说话人 ID (Sx)；<Subject N> 的外观以参考图本身为唯一权威，禁止在正文任何位置用文字重述外观，所有镜头沿用同一编号、只引用不描述；跨剪切对白用 <scenetrans> 衔接，语音中断用 <cutoff>；本段正文（不含开头原样引用的风格句）350–600 中文字，对白密集时优先完整容纳口播时间线）\n\n");
        sb.Append("声音纪律（必须遵守）：每个 [Shot N] 的『当前声音』必须写出画面内真实可闻声（对白 / 动作拟音 / 环境声，至少一项具体声音）；禁止以『静谧』『安静』『无声』『氛围安静』『与画面无声呼应』等抽象词充当声音描写——不含具体可闻声的措辞易诱发音频分支自动铺设情绪/氛围配乐；安静镜头改写为具体细微声（如 呼吸声、衣料摩擦、锅中细烟升腾的咝咝声）；有台词镜头一律由画面内说话人念出并写入 <d>[Chinese] 台词原文</d>，禁止把台词或旁白处理成配乐式吟唱。\n\n");
        sb.Append("overall_soundscape:\n");
        sb.Append("[只写画面内真实可闻且非音乐的声音：持续环境底噪 + 动作/特效拟音（脚步、器物碰撞、打击、爆炸、技能音等），中文，一至两句并至少写出一项具体声音。例：晨光厨房中，锅铲翻动带出轻微滋滋油响，蒸汽升腾细响，夹杂衣料与围裙的摩擦声。]本节列出的声音是整段音轨中除对白外唯一允许存在的声音内容。\n");
        sb.Append("（此节只准写画面内真实可闻且非音乐的声音：持续环境底噪与动作/特效拟音（如 脚步、器物碰撞、打击、爆炸、技能音）均可写入本节；禁止混入任何音乐性词汇（旋律/配乐/鼓点/哼唱/和弦/乐器/情绪氛围乐等），也禁止用『静谧』『安静』『无声』『氛围』等抽象词充当整节唯一内容——安静镜头也须写出具体细微声（呼吸、衣料摩擦、器物轻响等）；对白与歌词不写在此节，对白只完整写在 detailed_description 的 <d> 中）\n\n");
        sb.Append("整条提示词必须以如下两行原样收尾（官方禁乐开关，值必须是 N/A，键不带引号，置于全文最后）：\n");
        sb.Append("non_diegetic_music: N/A\n");
        sb.Append("非叙事性音乐: N/A\n\n");
        sb.Append("（依据 MiniMax H3 官方教程：不要背景音乐 = 文末键值对的值写 N/A，模型只认这个固定语法才会真正关闭配乐。本片从头到尾禁止任何配乐、旋律、节奏乐、情绪音乐、哼唱或演唱；但禁止音乐不等于删除声音——对白写进对应镜头的 <d>，持续环境底噪写进 overall_soundscape，动作/特效声写进对应 [Shot N] 的『当前声音』，这三类必须保留。禁止用 STRICTLY DISABLED 之类英文长声明、『无配乐』『不要BGM』等自创写法替代 N/A——实测无效，视频模型仍会自行铺 BGM）\n\n");
        sb.Append("【项目风格】（全片唯一画风来源，禁止改写语义、禁止另设画面风格/光影质感参考图；detailed_description 首句须原样完整引用该风格全文，一字不改，禁止概括、缩写、删减或改写）\n");
        sb.Append(stylePrompt + "\n\n");
        sb.Append("【音频纪律】风格文本含『PV/宣传片/电影感/恢弘/史诗/预告片』等词时，仅表示画面视觉基调与镜头语言，绝不授权音频侧加入配乐或情绪音乐。本片音频只允许保留三类画面内声：(1) 对白与人物非乐音语气（对话/惊呼/喘息/笑声），写入 detailed_description 对应镜头；(2) 持续环境底噪，写入 overall_soundscape；(3) 一次性动作/特效声（脚步、门响、碰撞、打击、爆炸、冲击、呼啸、技能音），写入对应 [Shot N] 的『当前声音』。严禁第四类：任何形式的音乐（旋律/和弦/节奏乐/配乐/哼唱/演唱/情绪音乐）——不得以「音效化」或「氛围化」名义夹带，出现即整条作废。\n\n");
        sb.Append("色温纪律（必须遵守）：detailed_description 首句的风格句必须原样完整引用【项目风格】全文（不得改动、不得删减其中的色温措辞）；除此之外的逐镜头描述，其光线与色温一律以参考图实测为准——画面中客观存在什么光线与色温就写什么（如 warm golden rim light、soft cool daylight、neutral indoor lighting），禁止在逐镜头描述里重复堆叠或夸大风格文本中的色温措辞，不得给参考图中不存在的强色调。\n\n");
        sb.Append("【转换规则】\n");
        if (fromScript)
        {
            sb.Append("1. 剧情、主体、动作、台词必须严格照搬【分镜脚本】中该镜头的『镜头描述』『出镜角色及表情』『对话/台词』『起始画面』『结束画面』，禁止新增、删减或改写剧情与台词；台词逐字保留，写入对应镜头的 <d>[Chinese] 台词原文</d>。\n");
            sb.Append("2. 【参考素材说明】按本镜头出镜的角色/场景/道具从【可用参考素材】确定参考图清单，每行写成 @图片N [资产名]类型参考，保持…一致 的紧凑格式（见上方模板），禁止把分镜『出镜角色及表情』与镜头描述中的外貌信息扩写成长句；『出镜角色及表情』或镜头描述明确含多名角色时，素材说明必须为每名在场实体角色各绑一条「[角色名]人物形象参考」，禁止只绑叙事主角而漏绑同框配角（漏绑者将没有 <Subject N>、无法入镜）；<Subject N>/<Picture N> 编号与 @图片N 严格一一对应；禁止为画风/光影质感/风格绑定输入素材（画风按【项目风格】以文字表达）。已并入角色卡的随身物品写在所属人物那一行，禁止为它们新增 @图片N 行（不单独占参考图槽）；只有本镜需要单独出镜、被特写或被交接的剧情道具才单独绑定一行。\n");
            sb.Append("3. Shot 分块：按分镜的『镜头时间轴』字段切分（如 0-3s:…；3-8s:…；8-11s:…），[Shot 1] 对应 0 秒起点、不写时间戳，后续镜头在 [Shot N] 后写 At MM:SS.mmm（如 At 00:03.000）；没有『镜头时间轴』字段时按 总时长/N 均匀切分；时间轴必须从 0 秒开始连续覆盖总时长，禁止留空、重叠或跳跃。\n");
            sb.Append("4. 运镜描述优先依据分镜的『构图方式』『景别』『镜头运动』字段展开；【项目风格】必须完整原样遵循（禁止改写语义）；detailed_description 在 [Shot 1] 之前原样完整引用【项目风格】全文（一字不改，禁止概括、缩写、删减或改写），保持全片风格统一。\n");
        }
        else
        {
            sb.Append("1. 剧情、主体、动作、台词必须严格照搬【已有 Seedance 分镜提示词】，禁止新增、删减或改写剧情与台词；台词逐字保留，写入对应镜头的 <d>[Chinese] 台词原文</d>。\n");
            sb.Append("2. 【参考素材说明】照搬 SD 提示词 @图N 行的资产名与参考类型，逐行改写为 @图片N [资产名]类型参考，保持…一致 的紧凑格式（见上方模板），资产名与用途保持一致；SD 绑定行中代表整片画面风格的「画面风格参考/光影质感」行是 SD 特例，H3 一律剔除、不搬为输入素材，对应风格需求写入 detailed_description 开头风格句；不得把时间块正文中的外貌描写扩写进素材行；源文本的场景氛围行（如 金线的浴池+白天（暖光水汽弥漫））原样附在清单末尾；<Subject N>/<Picture N> 编号与 @图片N 严格一一对应。\n");
            sb.Append("3. Shot 分块：按 SD 提示词的时间块（[X-Ys]）自然对应切分，[Shot 1] 对应 0 秒起点、不写时间戳，后续镜头在 [Shot N] 后写 At MM:SS.mmm（如 At 00:03.000）；多个时间块可合并成一个 Shot，时间轴必须连续覆盖总时长。\n");
            sb.Append("4. 【项目风格】必须完整原样遵循（禁止改写语义）；detailed_description 在 [Shot 1] 之前原样完整引用【项目风格】全文（一字不改，禁止概括、缩写、删减或改写）；SD「约束」行的技术参数（4K、24fps、浅景深等）不写入 H3，字幕/水印类禁止项并入首句负向约束（no subtitles, no watermarks, no character-name overlays）。\n");
        }
        sb.Append("5. 说话人 ID (S1)(S2)...：按角色在片中首次开口的顺序全局分配，同一角色始终用同一 ID；说话时同时保留视觉标签 <Subject N> 与说话人 ID (Sx)；画外音写 off-screen；台词语言标签统一用 [Chinese]。\n");
        if (hasVoiceRefs)
            sb.Append("5.4. 【音色参考编号铁律（最高优先级硬约束）】：<Audio M> 与角色的对应关系由用户消息里的【音色参考表】唯一确定，禁止改派、合并、增删；说话角色必须引用表中它自己那一行对应的 <Audio M>（例：表中写 <Audio 1> 陈默，则陈默每次开口都写「以参考 <Audio 1> 的音色和说话方式说道」，不得改用 <Audio 2> 或省略）；未出现在【音色参考表】中的角色一律不写 <Audio M>；<Audio M> 是独立的音频编号体系，与 <Subject N>、<Picture N> 互不换算、互不替代；台词仍以 <d>[Chinese] 台词原文</d> 为准，音频参考只提供音色与说话方式，绝不复制参考音频里的原话。\n");
        sb.Append("5.1. 【Subject 编号铁律（最高优先级硬约束）】：每个角色的视觉标签一律写作 <Subject N>，N 必须与该角色在【参考素材说明】中的 @图片N 编号严格一致（例：遐蝶若为 @图片1，则正文中遐蝶永远是 <Subject 1>，阿格莱雅若为 @图片2 则永远是 <Subject 2>），禁止按叙述先后、画面出场顺序或任何其它顺序改写编号；人物名字（英文名或中文专有名词）与 <Subject N> 必须一一对应同现，一个编号只属于一个角色；(Sx) 说话人 ID 只代表开口顺序，与视觉标签的 N 是两个独立体系，只有在角色实际开口的镜头里才为开口角色分配 (Sx)；无台词镜头正文一律禁止出现任何 (Sx) 说话 ID，画面中的角色只用 <Subject N> 视觉标签锚定；(Sx) 绝不与 <Subject N> 混用、互换或省略，出现「名字 A 却写 <Subject B>（A≠B）」即视为整条提示词作废。\n");
        sb.Append("5.2. 【主体完整出镜铁律（最高优先级硬约束）】：凡是【参考素材说明】中被指派为「人物/群像」的 <Subject N>，必须实际出现在 detailed_description 的成片画面中，禁止「绑了人物参考卡却不让其出镜」或「只定义不渲染」；镜头含两名及以上人物时，每个 [Shot N] 的构图句必须同时写清在场各 <Subject N> 的画面方位与相对站位（如 <Subject 1> 站在画面左侧，<Subject 2> 从右侧面对他），双人互动双方必须同框入画，非主动作一方也要有明确站位与反应（保持原位、注视、侧身回应），禁止任何时间点让在场角色凭空消失；若同一人物参考卡出现在同一画面但仅作远背景，也必须写明其位置与状态，禁止因主体遮挡、特写或转写而整体漏画；参考素材的人物卡数量必须覆盖分镜『出镜角色及表情』中的全部实体出场角色，同框画面 2-3 名主体属正常要求，禁止因同框难度删减角色或把两人合并成一人。\n");
        sb.Append("5.3. 【素材编号与主体定义边界铁律（最高优先级硬约束）】(a) @图片N 的总数即本镜输入素材总数，subject_definitions、retention_analysis 与 detailed_description 中出现的 <Subject N>、<Picture N> 编号一律不得超过该总数，禁止引用未上传的 <Picture N>，全部只允许使用 1..N 范围内的编号；(b) 每个 <Subject N> 只能对应 subject_definitions 中已定义、且真实存在于 @图片N 参考图内的实体，禁止为了把画面中的零散个体/物件都『留住』而凭空多建 <Subject N>；(c) 禁止把氛围/瞬间元素定义为主体：扬起的尘土与尘雾、碎裂的光影、被撞落的布帽/落叶、飘动的衣料等由动作或环境瞬时产生的元素，只能在 detailed_description 中作为动作与环境描写出现，严禁在 subject_definitions 中为其建立 <Subject N>，也严禁在 retention_analysis 中为其开保留行；(d) retention_analysis 只能逐行罗列 subject_definitions 已定义的 <Subject N>（一行一条，编号一一对应），禁止新增定义之外的主体行。(e) 已并入角色卡的人物随身物品不建 <Subject N>、不占素材槽，属于该人物 <Subject N> 的组成部分；正文需要它们出现时，直接在动作描写里点名其名称与用法（如 <Subject 1> 举起手机拍照、把挎包挂上栏杆），只写名称与动作，不描述其外观。\n");
        sb.Append("5.5. 【特效非实体铁律（最高优先级硬约束）】(a) 画面中可以被渲染为人物/角色的，仅限【参考素材说明】里用途标注为「人物形象参考」或「群像参考」的 <Subject N>，只有这些编号允许是人；(b) 所有用途为「特效参考」「道具参考」「场景参考」的 <Subject N> 一律不是人物，严禁被渲染成人形、类人轮廓、面部、五官、肢体或任何可辨认的人影，严禁占据主体构图位，严禁用人物名字去指称它们；(c) 特效必须保持其参考图的固有形态——光尘=稀疏漂浮的细小粒子、水雾=弥散的雾气、飘落花瓣=花瓣、蝴蝶=蝶形，只能作为环境与氛围层存在于背景、空气或前景虚化处，禁止具象为实体，更禁止具象为任何人；(d) 特效参考图与人物参考图在同一镜头并存时，必须在正文中明确点出「画面中只有 <Subject X>（人物）是人，其余为特效/道具/场景元素」，防止模型把特效图里的发光/柔化形态误生成第二个同角色人影；(e) 同一镜头内特效参考图原则上不超过 2 张，超出部分不建 <Subject N>，只在 detailed_description 中用文字描写其氛围。\n");
        sb.Append("6. 该镜头无台词时：正文不写 <d> 标签、不分配说话人 ID，环境人声只能用中文笼统描述（如 人群嘈杂声、背景低语、远处含混的说话声 等），禁止任何暗示人物开口且内容具体却未写出台词的表述。\n");
        if (hasRefAssets)
            sb.Append("6.1. 【参考素材说明】素材行第一个有效词写 [资产名]（从【参考图绑定表】/【可用参考素材】清单对应用途中选、逐字照抄，含其中的引号“”、书名号等标点原样保留），如 @图片1 [陈默]人物形象参考，保持外貌、发型、服装、气质一致；清单中无所需资产时不加方括号，用一句极简外观概括代替 [资产名]；素材行禁止逐条外貌复述；源文本给出的场景氛围行原样保留在清单末尾。\n");
        sb.Append("6.2. 【参考角色身份与位置不挪用铁律（最高优先级硬约束）】(a) 已绑定人物/群像参考的每个 <Subject N> 都带有固定身份与空间归属，正文中该角色的身份地位、座位归属、站位区域必须与其身份一致，禁止张冠李戴：身份为教师/长辈/授课者的角色（如徐先生）出现在教室/课堂时，必须始终处于讲台、黑板前、讲台侧等授课位置，保持站立授课或缓步巡视姿态，全程禁止坐入学生的课桌座位、禁止混入学生队列或替学生落座；若镜头需要该角色低头批阅、书写，只能在讲台处完成。(b) 画面中需要出现【参考素材说明】未绑定任何人物/群像参考的人群或个体（如教室里散坐的学生、窗外人群）时，一律只能以远景、背影、剪影、虚化、侧影等无法辨认个体身份的形式呈现，禁止出现清晰可辨的面孔、五官、正脸特写；若镜头拍摄方向天然无法拍清（如学生背对镜头望向讲台），必须在正文写明『背影/后脑勺』等不可辨表述。(c) 严禁把已绑定人物参考的形象挪用于无参考群体——尤其禁止把唯一人物参考的教师立绘复制成多名『坐在学生座位上的学生』；正文与构图句中，任何出现在学生座位区的人物若未被绑定群像参考，都必须明确标注为剪影/背影/虚化，绝不标注为某已绑定 <Subject N> 的清晰形象。\n");
        sb.Append("6.3. 【宣布/告知类文戏两镜切分纪律（最高优先级硬约束）】(a) 触发条件：文戏/情感镜头中，有明确身份的主体角色（如讲台上的老师、堂上宣读的长官）在正式场合当众向在场群体宣布、告知、宣读重大消息，且分镜时间线在消息之后留有听众的群体反应节拍（如众人屏息、静默、低头、攥紧手中物件、侧首对视等）；此类镜头禁止从片头到片尾用单一固定机位把宣布者与台下听众同时框进同一构图、不分主次。(b) 必须切为两镜：宣布镜（[Shot N]）机位对准宣布者，以近景或中近景为主构图，宣布者（含其讲台/案前等身份空间）作为画面主体，全部台词按时间轴在宣布镜内说完（一句说完可自然停顿再续下一句，不必每句另开镜头）；反应镜（[Shot N+1]）自宣布完毕、反应节拍开始的时刻（At MM:SS.mmm）切换机位，改为对准台下/在场听众群体，交代群体听闻后的反应——若该镜头已绑定听众对应的群像/人物参考图，听众即可作为清晰主体入画，点出两三名代表性个体的细微反应（凝望、垂眼、攥纸、抿唇、侧首望窗外等），景别用中景或中近景；若未绑定任何听众参考图，反应镜必须依规则 6.2(b)(c) 只以背影、剪影、侧影或远景虚化呈现听众，禁止清晰正脸。(c) 切分点不得截断台词或打断语句：台词收尾与反应节拍起始重合处即为默认切分点（如 1-3s 说完、3-5s 反应），[Shot 1] 与 [Shot 2] 的时间戳须连续覆盖总时长；两镜间如需明示剪辑可用 <scenetrans> 衔接。\n");
        sb.Append("7. 正文一律用中文撰写（分镜脚本与台词本就是中文，直接照搬叙述，禁止转成英文再写回）；行文平实直接、画面感明确，禁止辞藻堆砌与空泛形容词；标点使用中文标点，句末用 。；禁止 ~~ ～ …… 与 ！？ ？！ 连用、禁止 emoji。以下结构化标签与固定术语必须保留原文，禁止翻译成中文：<Subject N>、<Picture N>、[Shot N]、At MM:SS.mmm、<d>…</d>、[Chinese]、[unclear]、off-screen、scenetrans、cutoff、保留度枚举（fully_preserved / partially_preserved / attribute_transfer / weak_reference）、负向约束（no subtitles, no watermarks, no character-name overlays）。<d> 内对白必须保留原词与原始语言（中文台词用 [Chinese] 标签），听不清的部分写 [unclear]，不猜测、不转述。\n");
        sb.Append("8. 输出直接是可提交的 H3 提示词全文（【参考素材说明】+ 六节正文），禁止输出任何解释、引言或 Markdown 代码块。\n");
        sb.Append("9. detailed_description 正文（不含开头原样引用的风格句）350–600 中文字（生成类任务）；对白密集的镜头优先完整容纳口播时间线，不机械凑字数。\n");
        sb.Append("10. 无台词角色（分镜『对话/台词』中不发言的角色，含他人画外音时画面中的角色）必须用正面表述明确其全程沉默：如 全程沉默不语 / 自始至终未发一言 / 紧闭嘴唇，无任何口型动作；禁止仅用否定式（不说话/不开口）带过，禁止『嘴唇微张/欲言又止/张口欲言』等易被模型误读为开口的动作描述；沉默角色不分配说话人 ID，(Sx) 只分配给实际开口的角色（含画外音说话人，如 老板 (S1) 画外音说话（off-screen），<d>...</d>）；对白归属必须与分镜『对话/台词』的说话人完全一致，未分配给该角色的台词一句也不能出现。\n");
        sb.Append("11. 色调锚定与防偏色纪律（硬约束）：(a) detailed_description 开头的风格句是【项目风格】原文引用，照抄其中的色温词不算违规；除该句之外，逐镜头描述里禁止反复铺陈暖/冷色形容词（英文 warm / amber / golden / yellow / cool / blue，中文 暖/暖金/金黄/偏黄/冷/蓝 等同义表达），禁止用同义词在多个镜头重复强调同一色温；(b) 本流程不设画面风格参考图：逐镜头描述的光线与色温依据优先取主要环境/场景参考图的实测观感，与风格文本措辞不一致时一律服从参考图实测；(c) 参考图明显偏黄/偏冷时，正文逐镜头的肤色、环境光、背景光一律沿用该基准色温，禁止额外堆叠同类色温词或反向夸大（如参考图偏黄时不得再用 暖/暖金/golden/warm 反复强调各处光线）。\n");
        sb.Append("12. 字幕纪律（最高优先级硬约束，分镜线与 SD 线均适用）：无论源文本（Stage 5 分镜脚本或已有 SD 提示词）是否出现字幕类内容，本提示词正文一律禁止生成/保留任何字幕——包括「字幕淡入/浮现」「白色/白字字幕」「片头/时间地点字幕」及分镜『对话/台词』字段中形如「字幕：民国二十六年初夏…」的画面文字。具体判定：①「对话/台词」为「字幕：…」或仅含画面文字时，一律视为无台词镜头，不写 <d>、不分配 (Sx)，该行文字不得以任何形式出现在正文或作为说话内容；②正文（summary/detailed_description 等）禁止出现“字幕淡入/浮现/白字字幕”等作为画面动作或氛围的描述——源文本这类措辞属于后期合层提示，不是视频画面可生成的元素，一律不照搬；③源文本时代/地点信息若仅由字幕承担，改用画面元素（场景、光线、道具、服装）在正文中交代，不得写成字幕；④负向约束 no subtitles 仍保留在首句风格句。\n");
        return sb.ToString();
    }

    private static (string SkillText, List<SkillLibraryItem> LockedSkills) BuildUnitSkillText(string unitText, List<SkillLibraryItem> skills, List<string>? characterNames = null, IReadOnlyList<CharacterAsset>? characters = null)
    {
        var locked = new List<SkillLibraryItem>();
        if (!string.IsNullOrWhiteSpace(unitText))
        {
            // 收集 unitText 中所有「技能：」行（单元级与镜头级均可），累加去重为单元锁定技能。
            var skillLineMatches = System.Text.RegularExpressions.Regex.Matches(unitText, @"(?:\*\*)?技能(?:\*\*)?\s*[:：]\s*([^\r\n]+)");
            foreach (System.Text.RegularExpressions.Match m in skillLineMatches)
            {
                var skillLine = m.Groups[1].Value.Trim();
                if (string.IsNullOrWhiteSpace(skillLine) || skillLine.Equals("无", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (var name in skillLine.Split(new[] { ",", "，", "、", ";", "；", " ", "|" }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => x.Length > 0))
                {
                    var hit = skills.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                        ?? skills.FirstOrDefault(s => s.Name.Contains(name, StringComparison.OrdinalIgnoreCase) || name.Contains(s.Name, StringComparison.OrdinalIgnoreCase));
                    if (hit != null)
                    {
                        if (!locked.Contains(hit)) locked.Add(hit);
                    }
                    else if (!locked.Any(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)))
                    {
                        locked.Add(new SkillLibraryItem
                        {
                            Name = name,
                            Element = "技能",
                            Tier = 4,
                            PromptVideo = "分镜锁定技能：形态、色调与节奏以分镜描述为准",
                            PromptImage = "分镜锁定技能：形态、色调与节奏以分镜描述为准",
                            Tags = name
                        });
                    }
                }
            }
            if (locked.Count == 0)
                locked = CombatPlanSelector.MatchSkillsToText(skills, unitText, characterNames, characters);
        }
        var text = string.Join("\n", locked.Select(s => "- " + s.Name + "（" + s.Element + "系·T" + s.Tier + "）: " + s.PromptVideoForLLM));
        return (text, locked);
    }

    private static string EnsureSkillRefImages(string promptText, List<SkillLibraryItem> skills, List<SkillLibraryItem> lockedSkills, List<string>? effectNames = null, string? unitContext = null)
    {
        skills ??= new List<SkillLibraryItem>();
        lockedSkills ??= new List<SkillLibraryItem>();
        effectNames ??= new List<string>();
        if (string.IsNullOrWhiteSpace(promptText) || (skills.Count == 0 && lockedSkills.Count == 0 && effectNames.Count == 0)) return promptText;

        // 与解析器保持一致，先把中文集号（如“第一集”）规范成阿拉伯数字，避免多镜头切分失效。
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);

        var shotRegex = new System.Text.RegularExpressions.Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(EnsureShotSkillRefs(part, skills, lockedSkills, effectNames, unitContext));
        }
        return builder.ToString();
    }

    private static readonly System.Collections.Generic.HashSet<string> GenericEffectNames =
        new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
        {
            "拳风", "火墙", "冲击", "罡气", "气劲", "剑气", "护体"
        };

    private static string EnsureShotSkillRefs(string shotText, List<SkillLibraryItem> skills, List<SkillLibraryItem> lockedSkills, List<string>? effectNames, string? unitContext = null)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (SeedanceAtImageLine.IsAtImageLine(lines[i]))
            {
                lines[i] = EnsureAtImageSkillRefs(lines[i], lines, shotText, skills, lockedSkills, effectNames, unitContext);
                break;
            }
            if (!lines[i].StartsWith("参考图", StringComparison.Ordinal) && !lines[i].StartsWith("参考图：", StringComparison.Ordinal)) continue;
            var refLine = lines[i];
            var body = string.Join("\n", lines.Where((l, idx) =>
                idx != i
                && !l.TrimStart().StartsWith("禁止", StringComparison.Ordinal)
                && !l.TrimStart().StartsWith("禁止：", StringComparison.Ordinal)
                && !l.TrimStart().StartsWith("参考图", StringComparison.Ordinal)));

            var mentioned = new List<string>();
            foreach (var skill in lockedSkills.Concat(skills))
            {
                if (skill == null) continue;
                // 无参考图（ImageUrl 为空）的技能不参与 @图N/参考图绑定，靠 PromptVideo 文本驱动；
                // 有图的技能仍必须保留 @图N 绑定，保证特效形态、色调、氛围一致。
                if (string.IsNullOrWhiteSpace(skill.ImageUrl)) continue;
                var name = skill.Name ?? "";
                if (name.Length >= 2 && body.Contains(name, StringComparison.OrdinalIgnoreCase) && !mentioned.Contains(name)) mentioned.Add(name);
            }
            foreach (var name in effectNames ?? new List<string>())
            {
                if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;
                if (body.Contains(name, StringComparison.OrdinalIgnoreCase) && !mentioned.Contains(name)) mentioned.Add(name);
            }
            // 战斗镜头出现「角色名战斗态」时，强制带上该角色专属状态技参考图（如赤金气血），
            // 避免 LLM 只给战斗态卡却漏掉状态技，造成 1.1-1 有、1.2-1 没有的不一致。
            var forcedStateSkills = new List<string>();
            if (IsCombatShot(shotText, unitContext))
            {
                var battleNames = new List<string>();
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(shotText, @"\[([^\]\r\n]+战斗态)\]"))
                {
                    var v = m.Groups[1].Value.Trim();
                    if (v.Length > 0 && !battleNames.Contains(v, StringComparer.OrdinalIgnoreCase)) battleNames.Add(v);
                }
                foreach (var seg in SeedanceAtImageLine.Parse(string.Join("\n", lines.Where(l => SeedanceAtImageLine.IsAtImageLine(l)))))
                {
                    if (seg.Category == "战斗态" && seg.Name.Length > 0 && !battleNames.Contains(seg.Name, StringComparer.OrdinalIgnoreCase))
                        battleNames.Add(seg.Name);
                }
                foreach (var battleName in battleNames)
                {
                    var baseName = battleName.EndsWith("战斗态", StringComparison.Ordinal)
                        ? battleName.Substring(0, battleName.Length - "战斗态".Length)
                        : battleName;
                    if (baseName.Length == 0) continue;
                    foreach (var skill in lockedSkills.Concat(skills))
                    {
                        if (skill == null || !IsStateSkill(skill)) continue;
                        // 状态技同样要求有参考图才强制绑定；无图状态技靠 PromptVideo 文本驱动。
                        if (string.IsNullOrWhiteSpace(skill.ImageUrl)) continue;
                        var owner = skill.OwnerCharacter ?? "";
                        if (owner.Length == 0) continue;
                        if (!owner.Contains(baseName, StringComparison.OrdinalIgnoreCase)
                            && !baseName.Contains(owner, StringComparison.OrdinalIgnoreCase)) continue;
                        var name = skill.Name ?? "";
                        if (name.Length >= 2 && !mentioned.Contains(name))
                        {
                            mentioned.Add(name);
                            forcedStateSkills.Add(name);
                        }
                    }
                }
            }
            if (mentioned.Count == 0) break;

            foreach (var name in mentioned)
            {
                if (GenericEffectNames.Contains(name)) continue;
                var marker = "[" + name + "]特效参考";
                if (refLine.Contains(marker, StringComparison.OrdinalIgnoreCase)) continue;
                var sceneIdx = refLine.IndexOf("倒数第二张", StringComparison.Ordinal);
                if (sceneIdx < 0) sceneIdx = refLine.IndexOf("最后一张", StringComparison.Ordinal);
                var beforeCount = sceneIdx > 0 ? System.Text.RegularExpressions.Regex.Matches(refLine.Substring(0, sceneIdx), @"@图(?:X|\d+)").Count : System.Text.RegularExpressions.Regex.Matches(refLine, @"@图(?:X|\d+)").Count;
                var insert = "第" + ChineseNumber(beforeCount + 1) + "张(@图X)为[" + name + "]特效参考，保持形态、色调、氛围一致；";
                if (sceneIdx > 0) refLine = refLine.Insert(sceneIdx, insert);
                else refLine += insert;
            }

            var renum = 1;
            refLine = System.Text.RegularExpressions.Regex.Replace(refLine, @"@图(?:X|\d+)", m => "@图" + renum++);
            lines[i] = refLine;
            if (forcedStateSkills.Count > 0)
            {
                for (var k = 0; k < lines.Count; k++)
                {
                    if (!lines[k].TrimStart().StartsWith("禁止", StringComparison.Ordinal)
                        && !lines[k].TrimStart().StartsWith("禁止：", StringComparison.Ordinal)) continue;
                    foreach (var name in forcedStateSkills)
                        lines[k] = RemoveForbiddenSkillClause(lines[k], name);
                }
            }
            break;
        }
        return string.Join("\n", lines);
    }

    private static string EnsureAtImageSkillRefs(
        string atImageLine,
        List<string> lines,
        string shotText,
        List<SkillLibraryItem> skills,
        List<SkillLibraryItem> lockedSkills,
        List<string>? effectNames,
        string? unitContext)
    {
        var segments = SeedanceAtImageLine.Parse(atImageLine);
        if (segments.Count == 0) return atImageLine;

        var body = string.Join("\n", lines.Where((l, idx) =>
            !SeedanceAtImageLine.IsAtImageLine(l)
            && !l.TrimStart().StartsWith("禁止", StringComparison.Ordinal)
            && !l.TrimStart().StartsWith("禁止：", StringComparison.Ordinal)));

        // 以 Stage 5 镜头描述为技能使用依据，正文常把“七曜诛圣阵”写成“七色法光/法光”，
        // 只用生成正文判断会误删真在使用的技能卡；镜头描述没点名、正文也没出现的才清理。
        var shotDesc = string.Empty;
        var unitTitle = string.Empty;
        if (!string.IsNullOrWhiteSpace(unitContext))
        {
            // 单元标题是该单元核心剧情摘要（如“...冰刃成球形刃阵「霜刃千葬」围杀...”），
            // 帧描述常把技能名写成描述性语言，标题里的技能名可作为镜头技能判定依据。
            var unitTitleMatch = System.Text.RegularExpressions.Regex.Match(unitContext, @"【单元[\d.]+[a-zA-Z]?】([^\r\n]*)");
            if (unitTitleMatch.Success) unitTitle = unitTitleMatch.Groups[1].Value.Trim();
            var shotNo = System.Text.RegularExpressions.Regex.Match(shotText, @"镜头\s*([\d.]+[a-zA-Z]?-\d+)").Groups[1].Value.Trim();
            if (shotNo.Length > 0)
            {
                var blocks = System.Text.RegularExpressions.Regex.Split(unitContext, @"(?=- \*\*镜头编号\*\*: )");
                foreach (var block in blocks)
                {
                    if (!block.Contains(shotNo, StringComparison.OrdinalIgnoreCase)) continue;
                    var descMatch = System.Text.RegularExpressions.Regex.Match(block, @"- \*\*镜头描述\*\*: ([^\r\n]+)");
                    if (descMatch.Success) shotDesc = descMatch.Groups[1].Value.Trim();
                    break;
                }
            }
        }
        var matchSource = shotDesc.Length > 0 ? body + "\n" + shotDesc : body;
        if (unitTitle.Length > 0) matchSource = matchSource + "\n" + unitTitle;

        var lockedNameSet = lockedSkills
            .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
            .Select(s => s.Name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mentioned = new List<string>();
        foreach (var skill in lockedSkills.Concat(skills))
        {
            if (skill == null) continue;
            // 无参考图（ImageUrl 为空）的技能不参与 @图N/参考图绑定，靠 PromptVideo 文本驱动；
            // 有图的技能仍必须保留 @图N 绑定，保证特效形态、色调、氛围一致。
            if (string.IsNullOrWhiteSpace(skill.ImageUrl)) continue;
            var name = skill.Name ?? "";
            // 仅对当前单元锁定技能启用两字以上子串模糊匹配（帧描述常把技能名写成描述性语言）；
            // 非锁定技能只做整名精确匹配，避免“寒魄封天”因正文出现“寒魄仙姥”被子串误判为已使用。
            if (name.Length >= 2 && BodyMentions(name, lockedNameSet.Contains(name)) && !mentioned.Contains(name)) mentioned.Add(name);
        }
        foreach (var name in effectNames ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length < 2) continue;
            if (BodyMentions(name, false) && !mentioned.Contains(name)) mentioned.Add(name);
        }

        // 正文常写“圣光”“赤金”等技能名片段而非全名，按任意两字以上子串判定，避免误删真用到的技能卡；
        // 子串匹配仅对单元锁定技能生效，非锁定技能必须整名出现在正文/镜头描述/单元标题中才算被使用。
        bool BodyMentions(string candidate, bool fuzzy)
        {
            if (string.IsNullOrEmpty(candidate) || candidate.Length < 2) return false;
            if (matchSource.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return true;
            if (!fuzzy) return false;
            for (var len = candidate.Length - 1; len >= 2; len--)
                for (var start = 0; start + len <= candidate.Length; start++)
                    if (matchSource.IndexOf(candidate.Substring(start, len), StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        var forcedStateSkills = new List<string>();
        if (IsCombatShot(shotText, unitContext))
        {
            var battleNames = segments.Where(s => s.Category == "战斗态").Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var battleName in battleNames)
            {
                var baseName = battleName.EndsWith("战斗态", StringComparison.Ordinal)
                    ? battleName.Substring(0, battleName.Length - "战斗态".Length)
                    : battleName;
                if (baseName.Length == 0) continue;
                foreach (var skill in lockedSkills.Concat(skills))
                {
                    if (skill == null || !IsStateSkill(skill)) continue;
                    // 状态技同样要求有参考图才强制绑定；无图状态技靠 PromptVideo 文本驱动。
                    if (string.IsNullOrWhiteSpace(skill.ImageUrl)) continue;
                    var owner = skill.OwnerCharacter ?? "";
                    if (owner.Length == 0) continue;
                    if (!owner.Contains(baseName, StringComparison.OrdinalIgnoreCase)
                        && !baseName.Contains(owner, StringComparison.OrdinalIgnoreCase)) continue;
                    var name = skill.Name ?? "";
                    if (name.Length >= 2 && !mentioned.Contains(name))
                    {
                        mentioned.Add(name);
                        forcedStateSkills.Add(name);
                    }
                }
            }
        }

        var existing = segments.Where(s => s.Category == "特效")
            .Select(s => s.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var toAdd = mentioned.Where(name => !GenericEffectNames.Contains(name) && !existing.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (toAdd.Count == 0 && forcedStateSkills.Count == 0) return atImageLine;
        var unusedSkillEffects = segments.Any(s => s.Category == "特效"
            && !GenericEffectNames.Contains(s.Name)
            && !mentioned.Contains(s.Name, StringComparer.OrdinalIgnoreCase));
        if (toAdd.Count == 0 && forcedStateSkills.Count == 0 && !unusedSkillEffects) return atImageLine;

        // 镜头没用到的单元级技能（如只锁了单元、镜头正文未出现）从 @图N 行清理掉，
        // 避免 LLM 把整单元技能全塞进单镜头参考图；通用特效名与场景/风格卡保留。
        var allowedEffects = mentioned.Where(n => !GenericEffectNames.Contains(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updated = segments.Where(s =>
            s.Category != "特效"
            || GenericEffectNames.Contains(s.Name)
            || allowedEffects.Contains(s.Name)).ToList();
        var sceneIdx = updated.FindIndex(s => s.Category == "场景");
        var styleIdx = updated.FindIndex(s => s.Category == "光影质感");
        var insertAt = sceneIdx >= 0 ? sceneIdx : (styleIdx >= 0 ? styleIdx : updated.Count);
        foreach (var name in toAdd)
        {
            updated.Insert(insertAt++, new SeedanceAtImageLine.Segment(0, name, "特效"));
        }

        if (forcedStateSkills.Count > 0)
        {
            for (var k = 0; k < lines.Count; k++)
            {
                if (!lines[k].TrimStart().StartsWith("禁止", StringComparison.Ordinal)
                    && !lines[k].TrimStart().StartsWith("禁止：", StringComparison.Ordinal)) continue;
                foreach (var name in forcedStateSkills)
                    lines[k] = RemoveForbiddenSkillClause(lines[k], name);
            }
        }

        return SeedanceAtImageLine.Rebuild(updated);
    }
    private static bool IsCombatShot(string shotText, string? unitContext)
    {
        var typeMatch = System.Text.RegularExpressions.Regex.Match(shotText, @"类型\s*[:：]\s*([^\r\n]+)");
        if (typeMatch.Success)
        {
            var type = typeMatch.Groups[1].Value;
            if (type.Contains("打斗/动作", StringComparison.OrdinalIgnoreCase)
                || type.Contains("追逐/逃亡", StringComparison.OrdinalIgnoreCase)
                || type.Contains("高潮/对决", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        if (!string.IsNullOrWhiteSpace(unitContext))
        {
            if (System.Text.RegularExpressions.Regex.IsMatch(unitContext, @"(?:\*\*)?控制模式(?:\*\*)?\s*[:：]\s*[^\r\n]*打斗"))
                return true;
            if (System.Text.RegularExpressions.Regex.IsMatch(unitContext, @"(?:\*\*)?打斗模板(?:\*\*)?\s*[:：]"))
                return true;
        }
        return shotText.Contains("技能特效参考", StringComparison.Ordinal);
    }

    /// <summary>状态技：低层级（T1-T3）的状态爆发/气血类技能，如赤金气血。</summary>
    private static bool IsStateSkill(SkillLibraryItem skill)
    {
        if (skill == null || skill.Tier > 3) return false;
        var promptVideo = skill.PromptVideo ?? "";
        var tags = skill.Tags ?? "";
        return promptVideo.Contains("状态", StringComparison.OrdinalIgnoreCase)
            || tags.Contains("状态", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>状态技被强制补进参考图后，移除“（技能名不得出现）”这类自相矛盾的禁令。</summary>
    private static string RemoveForbiddenSkillClause(string line, string skillName)
    {
        var escaped = System.Text.RegularExpressions.Regex.Escape(skillName);
        line = System.Text.RegularExpressions.Regex.Replace(line, @"[（(]" + escaped + @"[^）)]*不得出现[^）)]*[）)]", "");
        line = System.Text.RegularExpressions.Regex.Replace(line, escaped + @"[^，。；;]*不得出现", "");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"不得出现" + escaped, "");
        return line.Trim();
    }

    /// <summary>
    /// 每个时间段必须有「对话」行；无台词镜头补齐「对话:无」。
    /// </summary>
    private static string EnsureDialogueLines(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        var lines = promptText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var headerRegex = new System.Text.RegularExpressions.Regex(@"^\d+(?:\.\d+)?-\d+(?:\.\d+)?秒\[\d+\]");
        for (var i = 0; i < lines.Count; i++)
        {
            if (!headerRegex.IsMatch(lines[i].TrimStart())) continue;
            var end = i + 1;
            while (end < lines.Count && !headerRegex.IsMatch(lines[end].TrimStart())) end++;
            var hasDialogue = false;
            for (var k = i + 1; k < end; k++)
            {
                var t = lines[k].TrimStart();
                if (t.StartsWith("对话:", StringComparison.Ordinal) || t.StartsWith("对话：", StringComparison.Ordinal))
                {
                    hasDialogue = true;
                    break;
                }
            }
            if (hasDialogue)
            {
                i = end - 1;
                continue;
            }
            var insertAt = -1;
            for (var k = i + 1; k < end; k++)
            {
                var t = lines[k].TrimStart();
                if (t.StartsWith("动作:", StringComparison.Ordinal) || t.StartsWith("动作：", StringComparison.Ordinal))
                {
                    insertAt = k + 1;
                    break;
                }
            }
            if (insertAt < 0) insertAt = i + 1;
            lines.Insert(insertAt, "对话:无");
            i = end;
        }
        return string.Join("\n", lines);
    }

    /// <summary>
    /// 时间段标记与「时长」行不一致时，按各段时长重排连续时间轴（如 0-4秒 → 0-5秒）。
    /// </summary>
    /// <summary>
    /// 移除每个镜头里残留的「负向提示词：」行（Seedance 不支持 negative prompt，
    /// 负面词有诱导畸变风险，禁止项已并入「约束：」行），并清除独立「音效：」字段及游离的时间音效行。
    /// </summary>
    private static string EnsureNegativePromptLines(string promptText)
    {
        return EnsureShotAuxLines(promptText);
    }

    private static string EnsureShotAuxLines(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var shotRegex = new System.Text.RegularExpressions.Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(EnsureShotAuxLine(part));
        }
        return builder.ToString();
    }

    private static string EnsureShotAuxLine(string shotText)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(shotText, @"【镜头[\d.]+[a-zA-Z]?\-\d+】")) return shotText;
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        lines = RemoveStandaloneSoundLines(lines);
        return string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("负向提示词", StringComparison.Ordinal)));
    }

    private static List<string> RemoveStandaloneSoundLines(List<string> lines)
    {
        var cleaned = new List<string>(lines.Count);
        var skipContinuation = false;
        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^音效\s*[:：]"))
            {
                skipContinuation = true;
                continue;
            }
            if (skipContinuation
                && System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^\d+(?:\.\d+)?\s*[-–]\s*\d+(?:\.\d+)?\s*(?:s|秒)"))
            {
                skipContinuation = false;
                continue;
            }
            skipContinuation = false;
            cleaned.Add(line);
        }
        return cleaned;
    }

    private static readonly string[] ActionCameraKeywords =
    {
        "机位", "镜头", "视角", "透视", "特写", "近景", "中景", "全景", "远景", "仰拍", "俯拍", "平拍", "侧拍", "背拍",
        "过肩", "虫视", "低角度", "高角度", "广角", "推镜", "推近", "推至", "拉远", "拉高", "拉回", "横移",
        "纵摇", "横摇", "上摇", "下摇", "环绕", "跟拍", "甩镜", "快切", "硬切", "跳切", "顿帧", "定格",
        "慢动作", "升格", "急推", "急拉", "锁焦", "定焦", "主观镜头", "POV", "震屏", "画面四角微震", "镜头升空", "镜头急拉"
    };

    private static readonly string[] ImpactCameraKeywords =
    {
        "出拳", "重拳", "快拳", "连拳", "一拳", "拳劲", "拳锋", "拳压", "挥拳", "拳影", "拳头", "肘击", "掌风", "爪风",
        "轰", "砸", "撞", "命中", "对撞", "炸", "爆", "震飞", "倒飞", "冲击波", "气浪", "迸发", "喷射", "劈", "斩", "刺", "剑锋", "刀光", "横扫", "直劈", "贯穿"
    };

    private static readonly string[] DefenseCameraKeywords =
    {
        "格挡", "闪避", "躲", "侧身", "后仰", "卸力", "硬抗", "防御", "挡", "让过", "横移", "封挡"
    };

    private static readonly string[] PressureCameraKeywords =
    {
        "法光", "圣光", "光柱", "光罩", "封锁", "合围", "压制", "压迫", "收拢", "天网", "封印", "大阵", "阵纹",
        "压向", "封死", "屏障", "笼罩", "法阵", "结印", "围拢", "压迫感"
    };

    private static readonly string[] PowerCameraKeywords =
    {
        "蓄力", "凝聚", "涌动", "汇聚", "爆发", "低喝", "怒吼", "握拳", "催动", "升腾", "喷涌", "释放", "气环", "狂涌", "充盈"
    };

    private static readonly string[] StaticCameraKeywords =
    {
        "定格", "收束", "消散", "沉寂", "站定", "挺立", "傲立", "落定", "凝望", "注视", "神情", "目光", "表情", "眼神",
        "静立", "不动", "垂落", "跪下", "行礼", "缠绕", "摩挲", "端坐", "站立", "缓缓", "沉稳", "坚定", "平静", "决绝", "沉静"
    };

    /// <summary>
    /// 兜底保险：打斗/高潮镜头动作行的时间小节缺失机位时，按镜头运镜行或内容语义补一句，避免动作只有事件没有镜头。
    /// </summary>
    private static string EnsureActionCameraEmbedded(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var shotRegex = new System.Text.RegularExpressions.Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(EmbedActionCameraInShot(part));
        }
        return builder.ToString();
    }

    private static string EmbedActionCameraInShot(string shotText)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        if (IsDramaShot(lines)) return shotText;
        var cameraPhrases = ExtractCameraPhrases(lines);
        var segmentRegex = new System.Text.RegularExpressions.Regex(@"^(\d+(?:\.\d+)?-\d+(?:\.\d+)?秒)\s*(.*)$");
        var changed = false;
        for (var i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (!trimmed.StartsWith("动作:", StringComparison.Ordinal) && !trimmed.StartsWith("动作：", StringComparison.Ordinal)) continue;
            var colonIdx = lines[i].IndexOfAny(new[] { ':', '：' });
            if (colonIdx < 0) continue;
            var prefix = lines[i].Substring(0, colonIdx + 1);
            var body = lines[i].Substring(colonIdx + 1);
            var rebuilt = new System.Text.StringBuilder(prefix);
            var pieces = body.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (var p = 0; p < pieces.Length; p++)
            {
                var piece = pieces[p].Trim();
                if (string.IsNullOrEmpty(piece)) continue;
                if (p > 0) rebuilt.Append('；');
                var m = segmentRegex.Match(piece);
                if (!m.Success)
                {
                    rebuilt.Append(piece);
                    continue;
                }
                var time = m.Groups[1].Value;
                var content = m.Groups[2].Value.Trim();
                if (HasActionCamera(content))
                {
                    rebuilt.Append(time).Append(' ').Append(content);
                }
                else
                {
                    var camera = PickActionCamera(content, cameraPhrases);
                    rebuilt.Append(time).Append(' ').Append(camera).Append('，').Append(content);
                    changed = true;
                }
            }
            lines[i] = rebuilt.ToString();
        }
        return changed ? string.Join("\n", lines) : shotText;
    }

    private static bool IsDramaShot(List<string> lines)
    {
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("类型:", StringComparison.Ordinal) && t.Contains("文戏", StringComparison.Ordinal)) return true;
            if (t.StartsWith("约束:", StringComparison.Ordinal) &&
                (t.Contains("文戏/情感", StringComparison.Ordinal) || t.Contains("文戏保持", StringComparison.Ordinal) ||
                 t.Contains("——文戏", StringComparison.Ordinal) || t.Contains("文戏稳定", StringComparison.Ordinal))) return true;
        }
        return false;
    }

    private static bool HasActionCamera(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;
        foreach (var keyword in ActionCameraKeywords)
            if (content.Contains(keyword, StringComparison.Ordinal)) return true;
        return false;
    }

    private static List<string> ExtractCameraPhrases(List<string> lines)
    {
        string? yunJing = null;
        foreach (var line in lines)
        {
            var t = line.TrimStart();
            if (t.StartsWith("运镜:", StringComparison.Ordinal) || t.StartsWith("运镜：", StringComparison.Ordinal))
            {
                yunJing = line;
                break;
            }
        }
        if (yunJing == null) return new List<string>();
        var colonIdx = yunJing.IndexOfAny(new[] { ':', '：' });
        var body = colonIdx >= 0 ? yunJing.Substring(colonIdx + 1) : yunJing;
        var chunks = body.Replace("→", "，").Replace("->", "，")
            .Split(new[] { '，', '；', ';', '。', '/', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        var result = new List<string>();
        foreach (var chunk in chunks)
        {
            var t = chunk.Trim();
            if (t.Length < 2 || t.Length > 18) continue;
            if (!LooksLikeCameraPhrase(t)) continue;
            result.Add(t);
        }
        return result;
    }

    private static bool LooksLikeCameraPhrase(string text)
    {
        foreach (var keyword in ActionCameraKeywords)
            if (text.Contains(keyword, StringComparison.Ordinal)) return true;
        return System.Text.RegularExpressions.Regex.IsMatch(text, @"(推|拉|摇|移|升|降|跟|环绕|甩|切|震|晃|绕)$");
    }

    private static string PickActionCamera(string content, List<string> shotCameraPhrases)
    {
        var fallback = ChooseFallbackCamera(content);
        if (shotCameraPhrases.Count == 0) return fallback;
        var preferred = PreferredCameraWords(fallback);
        foreach (var phrase in shotCameraPhrases)
            if (preferred.Any(k => phrase.Contains(k, StringComparison.Ordinal))) return phrase;
        return fallback;
    }

    private static string ChooseFallbackCamera(string content)
    {
        if (ContainsAny(content, DefenseCameraKeywords)) return "贴身平行横移";
        if (ContainsAny(content, ImpactCameraKeywords)) return "低角度极速推镜";
        if (ContainsAny(content, PressureCameraKeywords)) return "全景仰拍";
        if (ContainsAny(content, PowerCameraKeywords)) return "快速推近";
        if (ContainsAny(content, StaticCameraKeywords)) return "固定机位缓推";
        return "固定机位锁焦";
    }

    private static string[] PreferredCameraWords(string fallback)
    {
        if (fallback == "贴身平行横移") return new[] { "横移", "跟拍", "跟随", "环绕", "摇", "移" };
        if (fallback == "低角度极速推镜") return new[] { "低角度", "仰拍", "特写", "近景", "极近", "推近", "推至", "硬切", "顿帧", "快切", "震" };
        if (fallback == "全景仰拍") return new[] { "全景", "仰拍", "俯拍", "远景", "拉高", "低角度", "俯瞰" };
        if (fallback == "快速推近") return new[] { "推近", "推至", "近景", "特写", "快切", "急推" };
        if (fallback == "固定机位缓推") return new[] { "固定", "定格", "静止", "缓推", "悬停", "拉远", "上摇" };
        return new[] { "固定", "锁焦", "近景", "特写" };
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        foreach (var keyword in keywords)
            if (text.Contains(keyword, StringComparison.Ordinal)) return true;
        return false;
    }
    private static string NormalizeTimeSegmentHeaders(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var shotRegex = new System.Text.RegularExpressions.Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(NormalizeShotSegmentHeaders(part));
        }
        return builder.ToString();
    }

    private static string NormalizeShotSegmentHeaders(string shotText)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        var headerPattern = new System.Text.RegularExpressions.Regex(@"^(\d+(?:\.\d+)?)-(\d+(?:\.\d+)?)秒\[(\d+)\]");
        var headerIdx = new List<int>();
        for (var i = 0; i < lines.Count; i++)
            if (headerPattern.IsMatch(lines[i].TrimStart())) headerIdx.Add(i);
        if (headerIdx.Count == 0) return shotText;

        var durations = new List<decimal>();
        for (var s = 0; s < headerIdx.Count; s++)
        {
            var start = headerIdx[s] + 1;
            var end = s + 1 < headerIdx.Count ? headerIdx[s + 1] : lines.Count;
            var segDur = 0m;
            for (var k = start; k < end; k++)
            {
                var m = System.Text.RegularExpressions.Regex.Match(lines[k].TrimStart(), @"^时长[:：]\s*([\d.]+)\s*秒");
                if (m.Success && decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var d))
                    segDur += d;
            }
            durations.Add(segDur);
        }
        if (durations.Any(d => d <= 0)) return shotText;

        var total = durations.Sum();
        var cursor = 0m;
        var consistent = true;
        for (var s = 0; s < headerIdx.Count; s++)
        {
            var m = headerPattern.Match(lines[headerIdx[s]].TrimStart());
            if (!m.Success || !decimal.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var st) || !decimal.TryParse(m.Groups[2].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var en))
            {
                consistent = false;
                break;
            }
            if (st != cursor || en != cursor + durations[s])
            {
                consistent = false;
                break;
            }
            cursor += durations[s];
        }
        if (consistent && cursor == total) return shotText;

        cursor = 0m;
        for (var s = 0; s < headerIdx.Count; s++)
        {
            var start = cursor;
            cursor += durations[s];
            lines[headerIdx[s]] = FormatSegmentHeader(start, cursor, s + 1);
        }
        return string.Join("\n", lines);
    }

    private static string FormatSegmentHeader(decimal start, decimal end, int index)
    {
        return FmtSegmentTime(start) + "-" + FmtSegmentTime(end) + "秒[" + index + "]";
    }

    private static string FmtSegmentTime(decimal value)
    {
        var rounded = decimal.Round(value, 2);
        return rounded == decimal.Truncate(rounded)
            ? decimal.Truncate(rounded).ToString(System.Globalization.CultureInfo.InvariantCulture)
            : rounded.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 紧凑模板兜底：时间块 [0-3s] 若出现空洞、重叠或未从 0 开始，
    /// 按总时长重新铺成连续整数段，避免视频中间出现空白。
    /// </summary>
    private static string NormalizeCompactTimeBlocks(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);
        var shotRegex = new System.Text.RegularExpressions.Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(NormalizeCompactShotBlocks(part));
        }
        return builder.ToString();
    }

    private static string NormalizeCompactShotBlocks(string shotText)
    {
        var blockRegex = new System.Text.RegularExpressions.Regex(@"\[(?<start>\d+(?:\.\d+)?)\s*[-–]\s*(?<end>\d+(?:\.\d+)?)\s*s\]");
        var matches = blockRegex.Matches(shotText);
        if (matches.Count == 0) return shotText;

        var starts = new List<decimal>();
        var ends = new List<decimal>();
        foreach (System.Text.RegularExpressions.Match m in matches)
        {
            if (!decimal.TryParse(m.Groups["start"].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var st)
                || !decimal.TryParse(m.Groups["end"].Value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var en))
                return shotText;
            starts.Add(st);
            ends.Add(en);
        }

        var total = ends.Max();
        if (total != 5m && total != 11m && total != 15m) return shotText;
        if (starts[0] == 0m)
        {
            var contiguous = true;
            for (var i = 1; i < matches.Count; i++)
            {
                if (starts[i] != ends[i - 1]) { contiguous = false; break; }
            }
            if (contiguous) return shotText;
        }

        var totalSec = (int)total;
        var smallLen = totalSec / matches.Count;
        var bigLen = (int)Math.Ceiling((double)totalSec / matches.Count);
        var bigCount = totalSec - smallLen * matches.Count;
        var lengths = new List<int>();
        for (var i = 0; i < matches.Count; i++)
            lengths.Add(i < bigCount ? bigLen : smallLen);

        var cursor = 0m;
        var offset = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var replacement = "[" + FmtSegmentTime(cursor) + "-" + FmtSegmentTime(cursor + lengths[i]) + "s]";
            shotText = shotText.Substring(0, m.Index + offset) + replacement + shotText.Substring(m.Index + m.Length + offset);
            offset += replacement.Length - m.Length;
            cursor += lengths[i];
        }
        return shotText;
    }

    /// <summary>
    /// 兜底保险：每条提示词的参考图行最多 9 张，角色/群像优先，特效和道具靠后裁剪。
    /// </summary>
    private static string EnsureMaxNineRefImages(string promptText)
    {
        if (string.IsNullOrWhiteSpace(promptText)) return promptText;

        // 与解析器保持一致，先把中文集号规范成阿拉伯数字，确保每个镜头都能被独立裁剪。
        promptText = SeedancePromptParser.NormalizeEpisodeMarkers(promptText);

        var shotRegex = new System.Text.RegularExpressions.Regex(@"(?=【第\d+集】\s*【单元[\d.]+[a-zA-Z]?】\s*【镜头[\d.]+[a-zA-Z]?\-\d+】)");
        var parts = shotRegex.Split(promptText);
        var builder = new System.Text.StringBuilder();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            builder.Append(TrimShotRefImages(part));
        }
        return builder.ToString();
    }

    private static string TrimShotRefImages(string shotText)
    {
        var lines = shotText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        for (var i = 0; i < lines.Count; i++)
        {
            if (SeedanceAtImageLine.IsAtImageLine(lines[i]))
            {
                lines[i] = TrimAtImageLine(lines[i]);
                break;
            }
            if (!lines[i].StartsWith("参考图", StringComparison.Ordinal) && !lines[i].StartsWith("参考图：", StringComparison.Ordinal)) continue;

            var colonIdx = lines[i].IndexOfAny(new[] { ':', '：' });
            var prefix = colonIdx >= 0 ? lines[i].Substring(0, colonIdx + 1) : "参考图:";
            var body = colonIdx >= 0 ? lines[i].Substring(colonIdx + 1) : lines[i];

            var segments = body.Split(new[] { '；', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToList();
            if (segments.Count == 0) continue;

            var characters = new List<string>();
            var props = new List<string>();
            var effects = new List<string>();
            string? scene = null;
            string? style = null;

            foreach (var seg in segments)
            {
                if (seg.Contains("画面风格参考", StringComparison.Ordinal)) style = seg;
                else if (seg.Contains("场景参考", StringComparison.Ordinal)) scene = seg;
                else if (seg.Contains("形象参考", StringComparison.Ordinal) || seg.Contains("群像参考", StringComparison.Ordinal)) characters.Add(seg);
                else if (seg.Contains("道具参考", StringComparison.Ordinal)) props.Add(seg);
                else effects.Add(seg);
            }

            var contentBudget = style == null ? 9 : 8;
            var charactersKept = characters.Take(contentBudget).ToList();
            var keepScene = scene != null && charactersKept.Count < contentBudget;
            var room = contentBudget - charactersKept.Count - (keepScene ? 1 : 0);
            var propsKept = props.Take(room).ToList();
            room -= propsKept.Count;
            var effectsKept = effects.Take(room).ToList();

            var kept = new List<string>();
            kept.AddRange(charactersKept);
            kept.AddRange(propsKept);
            kept.AddRange(effectsKept);
            if (keepScene) kept.Add(scene!);

            if (style != null) kept.Add(style);

            var prefixRegex = new System.Text.RegularExpressions.Regex(@"^\s*(?:第[一二三四五六七八九十X]+张|倒数第[一二三]+张|最后一张)\(@图(?:\d+|X)\)");
            var rebuilt = new System.Text.StringBuilder(prefix);
            for (var j = 0; j < kept.Count; j++)
            {
                var cleaned = prefixRegex.Replace(kept[j], "").Trim();
                string ordinal;
                if (style != null && j == kept.Count - 1) ordinal = "最后一张(@图" + kept.Count + ")";
                else if (keepScene && j == kept.Count - 2) ordinal = "倒数第二张(@图" + (kept.Count - 1) + ")";
                else ordinal = "第" + ChineseNumber(j + 1) + "张(@图" + (j + 1) + ")";
                if (j > 0) rebuilt.Append('；');
                rebuilt.Append(ordinal).Append(cleaned);
            }
            lines[i] = rebuilt.ToString();
            break;
        }
        return string.Join("\n", lines);
    }

    private static string TrimAtImageLine(string line)
    {
        var segments = SeedanceAtImageLine.Parse(line);
        if (segments.Count == 0) return line;

        var characters = new List<SeedanceAtImageLine.Segment>();
        var props = new List<SeedanceAtImageLine.Segment>();
        var effects = new List<SeedanceAtImageLine.Segment>();
        SeedanceAtImageLine.Segment? scene = null;
        SeedanceAtImageLine.Segment? style = null;
        foreach (var seg in segments)
        {
            switch (seg.Category)
            {
                case "人物":
                case "战斗态":
                case "群像":
                    characters.Add(seg);
                    break;
                case "道具":
                    props.Add(seg);
                    break;
                case "特效":
                    effects.Add(seg);
                    break;
                case "场景":
                    scene = seg;
                    break;
                case "光影质感":
                    style = seg;
                    break;
            }
        }

        var contentBudget = style == null ? 9 : 8;
        var charactersKept = characters.Take(contentBudget).ToList();
        var keepScene = scene != null && charactersKept.Count < contentBudget;
        var room = contentBudget - charactersKept.Count - (keepScene ? 1 : 0);
        var propsKept = props.Take(room).ToList();
        room -= propsKept.Count;
        var effectsKept = effects.Take(room).ToList();

        var kept = new List<SeedanceAtImageLine.Segment>();
        kept.AddRange(charactersKept);
        kept.AddRange(propsKept);
        kept.AddRange(effectsKept);
        if (keepScene && scene != null) kept.Add(scene);
        if (style != null) kept.Add(style);

        if (kept.Count == 0) return line;
        return SeedanceAtImageLine.Rebuild(kept);
    }
    private static string ChineseNumber(int n)
    {
        var digits = new[] { "零", "一", "二", "三", "四", "五", "六", "七", "八", "九", "十" };
        if (n is >= 1 and <= 10) return digits[n];
        if (n is >= 11 and <= 19) return "十" + digits[n - 10];
        if (n is >= 20 and <= 99) return digits[n / 10] + "十" + (n % 10 == 0 ? "" : digits[n % 10]);
        return n.ToString();
    }
}
