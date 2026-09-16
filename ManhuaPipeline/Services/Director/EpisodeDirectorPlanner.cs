using System.Collections.Concurrent;
using System.Text;
using ManhuaPipeline.Models;
using Microsoft.Extensions.Configuration;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// Director V3 总导演服务：为每集生成 EpisodeDirectorPlan，
/// 维护 EpisodeDirectorState，并在单元分镜通过后回写已用套路。
/// </summary>
public class EpisodeDirectorPlanner
{
    private readonly LLMService _llm;
    private readonly DbService _db;
    private readonly double _temperature;
    private readonly ConcurrentDictionary<(int ProjectId, int EpisodeNumber), object> _stateLocks = new();

    public EpisodeDirectorPlanner(LLMService llm, DbService db, IConfiguration config)
    {
        _llm = llm;
        _db = db;
        _temperature = Math.Clamp(config.GetValue("Director:Episode:Temperature", 0.2), 0, 2);
    }

    public async Task<int> EnsureEpisodeDirectorPlansAsync(
        int projectId,
        List<StageUnit> units,
        string apiUrl,
        string apiKey,
        string model,
        Action<StageProgressUpdate>? onProgress = null,
        bool force = false,
        string? thinkingMode = null)
    {
        if (units == null || units.Count == 0) return 0;

        var groups = units
            .GroupBy(GetEpisodeNumber)
            .OrderBy(g => g.Key)
            .ToList();

        if (groups.Count == 0) return 0;

        var project = _db.GetProjectById(projectId);
        var stage3 = _db.GetStageData(projectId, 3);
        var blueprint = stage3?.LlmResponse ?? stage3?.Content;
        var episodes = _db.GetEpisodes(projectId);

        onProgress?.Invoke(new StageProgressUpdate
        {
            Done = 0,
            Total = groups.Count,
            CurrentPhase = "整集导演",
            Message = $"共 {groups.Count} 集需要生成整集导演计划"
        });

        var generated = 0;
        foreach (var group in groups)
        {
            var episodeNumber = group.Key;
            if (!force)
            {
                var existing = _db.GetEpisodeDirectorPlan(projectId, episodeNumber);
                if (IsValid(existing))
                {
                    onProgress?.Invoke(new StageProgressUpdate
                    {
                        Done = ++generated,
                        Total = groups.Count,
                        CurrentPhase = "整集导演",
                        Message = $"第{episodeNumber}集复用已有整集导演计划"
                    });
                    continue;
                }
            }

            var episode = episodes.FirstOrDefault(e => e.EpisodeNumber == episodeNumber);
            var systemPrompt = BuildSystemPrompt();
            var userMessage = BuildUserMessage(project, blueprint, episode, group.ToList());
            var raw = await SafeCallAsync(apiUrl, apiKey, model, systemPrompt, userMessage, thinkingMode);

            var plan = EpisodeDirectorPlanParser.Parse(raw, units, projectId, episodeNumber);
            if (plan == null)
            {
                plan = EpisodeDirectorPlanParser.BuildFallback(group.ToList(), projectId, episodeNumber);
                LogEpisodeCall(projectId, model, episodeNumber, userMessage, raw, "整集导演生成失败，已使用系统回退计划");
            }
            else
            {
                LogEpisodeCall(projectId, model, episodeNumber, userMessage, raw, "成功");
            }

            _db.SaveEpisodeDirectorPlan(plan);
            generated++;
            onProgress?.Invoke(new StageProgressUpdate
            {
                Done = generated,
                Total = groups.Count,
                CurrentPhase = "整集导演",
                Message = $"已完成第{episodeNumber}集整集导演计划 {generated}/{groups.Count}"
            });
        }

        return generated;
    }

    public Task<EpisodeDirectorPlan?> GetPlanAsync(int projectId, int episodeNumber)
        => Task.FromResult(_db.GetEpisodeDirectorPlan(projectId, episodeNumber));

    public Task<EpisodeDirectorState?> GetStateAsync(int projectId, int episodeNumber)
        => Task.FromResult(_db.GetEpisodeDirectorState(projectId, episodeNumber));

    public Task<EpisodeUnitStateSnapshot?> GetPreviousSnapshotAsync(
        int projectId,
        StageUnit unit,
        List<StageUnit>? units)
    {
        if (units == null || units.Count == 0 || unit == null) return Task.FromResult<EpisodeUnitStateSnapshot?>(null);
        var episodeNumber = GetEpisodeNumber(unit);
        for (var i = 0; i < units.Count; i++)
        {
            if (GetEpisodeNumber(units[i]) != episodeNumber ||
                !string.Equals(units[i].UnitNumber, unit.UnitNumber, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i == 0) return Task.FromResult<EpisodeUnitStateSnapshot?>(null);
            var prev = units[i - 1];
            return Task.FromResult(_db.GetEpisodeUnitStateSnapshot(projectId, episodeNumber, prev.UnitNumber));
        }
        return Task.FromResult<EpisodeUnitStateSnapshot?>(null);
    }

    public Task MarkUnitCompletedAsync(
        int projectId,
        StageUnit unit,
        DirectorPlan? plan,
        string storyboard)
    {
        if (unit == null || string.IsNullOrWhiteSpace(storyboard)) return Task.CompletedTask;
        var episodeNumber = GetEpisodeNumber(unit);
        var episodePlan = _db.GetEpisodeDirectorPlan(projectId, episodeNumber);
        if (episodePlan == null) return Task.CompletedTask;

        lock (_stateLocks.GetOrAdd((projectId, episodeNumber), _ => new object()))
        {
            var state = _db.GetEpisodeDirectorState(projectId, episodeNumber)
                ?? new EpisodeDirectorState
                {
                    ProjectId = projectId,
                    EpisodeNumber = episodeNumber
                };
            EpisodeDirectorStateUpdater.Apply(state, episodePlan, plan, storyboard);
            _db.SaveEpisodeDirectorState(state);

            var snapshot = EpisodeStateSnapshotBuilder.Build(projectId, unit, episodePlan, plan, storyboard);
            _db.SaveEpisodeUnitStateSnapshot(snapshot);
        }

        return Task.CompletedTask;
    }

    private async Task<string?> SafeCallAsync(string apiUrl, string apiKey, string model, string systemPrompt, string userMessage, string? thinkingMode = null)
    {
        try
        {
            return await _llm.CallAsync(apiUrl, apiKey, model, systemPrompt, userMessage, jsonMode: true, temperature: _temperature, thinkingMode: thinkingMode);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsValid(EpisodeDirectorPlan? plan)
    {
        return plan != null &&
            (!string.IsNullOrWhiteSpace(plan.EpisodeGoal) || (plan.IntensityCurve?.Count ?? 0) > 0);
    }

    private static int GetEpisodeNumber(StageUnit unit)
    {
        if (unit.EpisodeNumber > 0) return unit.EpisodeNumber;
        if (unit.UnitNumber.Contains('.'))
        {
            var first = unit.UnitNumber.Split('.')[0];
            if (int.TryParse(first, out var ep)) return ep;
        }
        return 0;
    }

    private static string BuildSystemPrompt()
    {
        return """
你是一个漫剧整集总导演（Director V4 Episode Master Director）。你的职责是在「分集细化」完成之后、Unit Director 之前，为整集做全局导演计划、Unit 交接协议与高潮预算，约束后面的 Unit Director 与分镜。

【目标】
1. 让整集形成强度起伏，而不是每个单元都像终极大战。
2. 给每个单元分配强度上限、爽点任务和特效预算。
3. 防止后置高潮被提前释放，防止近期套路高频重复。

【硬性规则】
1. intensityCurve 必须覆盖输入的全部单元，单元编号必须与输入完全一致；普通铺垫单元压低，阶段高潮单元拉高，本集最终高潮才允许 9-10。
2. payoffSchedule 按故事推进顺序列出：小爽点 → 阶段压制 → 反杀 → 阶段高潮 → 最终爆点，禁止乱序。
3. reservedVisuals 用于保留本集最贵的视觉：整集禁止的写 reservedUnit 为空；只允许高潮单元用的写对应单元编号。
4. forbiddenEarlyPayoffs 写不能提前释放的核心爆点画面关键词。
5. repetitionPolicy 给慢动作、高潮型特效和镜头/动作/特效套路设置整集次数上限，避免连续单元使用同一组合。
6. 保留现有 Unit Director 的自由度：除保留视觉与强度上限外，不限制特效创意。
7. unitEmotionCurve 必须覆盖全部单元：给每个单元分配 emotion（情绪任务）、level（1-10）和 directingNote（导演注意）。directingNote 只准写情绪基调、表演节奏、运镜意图、站位调度等导演可执行指令；字幕/片头/章节标题等画面文字（如“字幕淡入”“白字字幕”“字幕：民国二十六年…”）属于后期合层内容，不属于视频画面可生成的元素——禁止把字幕相关内容写进 emotion、directingNote 或任何字段，也禁止把“显示字幕”作为单元的情绪任务或视觉要求；含字幕的时代/地点信息改由单元画面内容（场景、光线、道具）承载。
8. unitTransitions 必须覆盖相邻单元：fromUnit → toUnit 按单元顺序首尾相接，transitionType 只能从 ActionMatchCut / CameraMatchCut / EmotionCarry / DialogueCarry / ImpactCut / BreathingReset / SceneChange 中选择；carryEmotion、narrativeCarry、cameraDirection 写清上一单元如何进入下一单元。
9. unitEndStates 必须覆盖全部单元：给出本单元结束瞬间的角色位置/朝向/姿态/外观/伤势/武器/技能状态、环境状态、镜头方向、残留特效、动作状态、情绪与剧情延续；下一 Unit 必须以它为起点，禁止下一 Unit 重新建立上一 Unit 已经建立的战斗或情绪状态。
10. climaxBudget 定义小/中/大高潮次数上限：小高潮（level 1-4）≤ smallClimaxLimit，中高潮（5-7）≤ midClimaxLimit，大高潮（8-10）≤ largeClimaxLimit；reservedVisuals 只允许指定高潮 Unit 使用。

【输出要求】
只输出 JSON，不要 Markdown 代码块，不要解释。JSON 字段：
{
  "episodeGoal": "本集目标",
  "emotionCurve": "压抑 → 挑衅 → 冲突 → 碾压 → 更强敌人出现",
  "intensityCurve": [{"unitNumber":"1.1","intensityLimit":2}, {"unitNumber":"1.2","intensityLimit":3}],
  "unitEmotionCurve": [{"unitNumber":"1.1","emotion":"探索","level":2,"directingNote":"..."}],
  "unitTransitions": [{"fromUnit":"1.1","toUnit":"1.2","transitionType":"EmotionCarry","carryEmotion":"...","narrativeCarry":"...","cameraDirection":"...","startState":"...","endState":"..."}],
  "unitEndStates": [{"unitNumber":"1.1","characters":{"岳沉天":{"position":"阵心","facing":"前方","pose":"收拳","appearance":"玄黑武袍","injury":"右肩伤","weapon":"无","skillState":"赤金气血30%"}},"environmentState":"...","cameraDirection":"...","activeVfxState":"...","lastActionState":"...","emotionalCarry":"...","narrativeCarry":"..."}],
  "climaxBudget": {"smallClimaxLimit":2,"midClimaxLimit":1,"largeClimaxLimit":1,"reservedVisuals":[{"visual":"法相天地","reservedUnit":"1.5","note":"仅最终高潮"}],"notes":"..."},
  "payoffSchedule": [{"unitNumber":"1.5","type":"小爽点","name":"六人围攻反被碾压","description":"..."}],
  "reservedVisuals": [{"visual":"法相天地","reservedUnit":"","note":"禁止使用"},{"visual":"全屏赤金爆发","reservedUnit":"1.7","note":"仅最终高潮"}],
  "forbiddenEarlyPayoffs": ["万剑齐发", "天地异象"],
  "repetitionPolicy": {
    "slowMotionLimit": 2,
    "majorExplosionLimit": 1,
    "cameraPatternLimits": [{"pattern":"低机位","maxCount":2},{"pattern":"推镜","maxCount":2}],
    "combatPatternLimits": [{"pattern":"重拳","maxCount":2},{"pattern":"震飞","maxCount":1}],
    "vfxPatternLimits": [{"pattern":"气血爆发","maxCount":1},{"pattern":"法相","maxCount":1}]
  }
}
""";
    }

    private static string BuildUserMessage(
        Project? project,
        string? blueprint,
        Episode? episode,
        List<StageUnit> units)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【项目信息】");
        if (project != null)
        {
            sb.AppendLine("项目名: " + project.Title);
            if (!string.IsNullOrWhiteSpace(project.Description))
                sb.AppendLine("简介: " + project.Description);
        }
        sb.AppendLine();

        sb.AppendLine("【全局蓝图】");
        sb.AppendLine(Truncate(blueprint, 8000) ?? "无");
        sb.AppendLine();

        sb.AppendLine("【本集信息】");
        if (episode != null)
        {
            sb.AppendLine("第" + episode.EpisodeNumber + "集: " + episode.Title);
            if (!string.IsNullOrWhiteSpace(episode.Summary))
                sb.AppendLine("本集概要: " + episode.Summary);
        }
        sb.AppendLine();

        sb.AppendLine("【本集单元列表（按顺序）】");
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i];
            sb.AppendLine((i + 1) + ". " + unit.UnitNumber + "（" + unit.Type + "·" + (unit.Duration > 0 ? unit.Duration + "秒" : "未定时长") + "）: " +
                (string.IsNullOrWhiteSpace(unit.CoreAction) ? Truncate(unit.RawText, 120) : unit.CoreAction));
            if (!string.IsNullOrWhiteSpace(unit.Location))
                sb.AppendLine("   地点: " + unit.Location);
            if (!string.IsNullOrWhiteSpace(unit.StartState))
                sb.AppendLine("   起始状态: " + unit.StartState);
            if (!string.IsNullOrWhiteSpace(unit.EndState))
                sb.AppendLine("   结束状态: " + unit.EndState);
            if (!string.IsNullOrWhiteSpace(unit.Dialogue))
                sb.AppendLine("   对话/台词: " + Truncate(unit.Dialogue, 300));
        }
        sb.AppendLine();

        sb.AppendLine("【任务】");
        sb.AppendLine("根据以上上下文，为第" + (episode?.EpisodeNumber.ToString() ?? "?") + "集生成 Director V4 Episode Master Plan（含 Unit 情绪曲线、Unit 交接协议、Unit 结束状态与高潮预算）。");
        return sb.ToString();
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "……（已截断）";
    }

    private static void LogEpisodeCall(
        int projectId,
        string model,
        int episodeNumber,
        string request,
        string? raw,
        string status)
    {
        var sb = new StringBuilder();
        sb.AppendLine("================================================================");
        sb.AppendLine("[EpisodeDirector] " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.AppendLine("项目ID: " + projectId);
        sb.AppendLine("模型: " + model);
        sb.AppendLine("集: " + episodeNumber);
        sb.AppendLine("状态: " + status);
        sb.AppendLine("---- 请求 ----");
        sb.AppendLine(request);
        sb.AppendLine("---- LLM返回 ----");
        sb.AppendLine(string.IsNullOrEmpty(raw) ? "(空)" : raw);
        sb.AppendLine("================================================================");
        sb.AppendLine();
        StoryboardPlanningLogger.Append(sb.ToString());
    }
}
