using System.Text;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Combat;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// 组装 DirectorPlan 的 LLM 提示词：事实（资产/技能/模板/前后单元）由系统提供，
/// LLM 只负责在事实约束下做导演判断。
/// </summary>
public static class DirectorPromptBuilder
{
    public static string BuildSystemPrompt()
    {
        return """
你是一个 AI 导演。你的职责不是写分镜，而是为「分集细化」中的一个单元做出导演决策，输出 DirectorPlan。

【角色定位】
导演决策层位于「分集细化」和「分镜脚本」之间。你回答的是：这一场戏为什么这么拍、重点拍谁、情绪怎么走、动作怎么组织、镜头怎么服务爽点、特效怎么设计（不限制）。

【硬性规则】
1. 只基于给定上下文做决策，禁止虚构上下文之外的角色、技能、资产、地点、事件。
2. 人物名、技能名、资产名、道具名必须与上下文完全一致，禁止改名、缩写或自创。
3. 打斗/高潮/对决/追逐单元必须明确：核心主体、攻防关系、战斗阶段推进、本单元爽点、动作策略、VFX 参考方向（不限制后续增删）。
4. 非打斗单元 conflictType 必须为 NonCombat，actionStrategy 写人物调度、站位或表演动作层面的处理，不要写招式；actionPlan 输出 null。
5. 打斗/高潮/对决/追逐单元必须从【战斗段落骨架库】选择 fightArcType 并输出 fightSequence；阶段百分比总和必须等于 100，阶段顺序必须按骨架推进，禁止跳段或只写一段；非打斗单元 fightArcType 与 fightSequence 必须为 null。
6. VFX 策略只给出参考方向，允许分镜自由新增、调整特效量/时机/峰值，不把 VFX 限制当硬约束；唯一硬约束是技能名与技能归属不能改。
7. emotionCurve、rhythmStrategy 用先后顺序表达（如 冷静 → 压迫 → 期待），禁止只写单个情绪。
8. intensityLevel 是 1-10 的本单元高潮等级，必须服从本集蓝图；普通铺垫单元压低，本集高潮单元才拉高，禁止每单元都到 8 以上。
9. combatGrammarIds 必须从【战斗语法库】选择至少 3 个动作 ID，高手过招/快节奏对决允许 12 个以上，按攻防回合先后顺序排列；终结技只能放最后；打斗/高潮/对决单元必填，其余单元留空数组。
10. actionPlan 必须结构化输出：conflictType、primaryFighterId、enemyIds、combatStyle、roundCount、dominanceCurve、combatGrammarIds、endingState；combatGrammarIds 只能从【战斗语法库】选择现有 ID，禁止自创 ID。
11. combatRoundCount 与 actionPlan.roundCount 必须一致；vfxPeakPhase 只能填 Early / Mid / Late / None。
12. 必须服从【整集导演计划 V3】：本单元强度不得超过当前单元强度上限，禁止提前使用保留视觉、后置爽点和本集禁止画面。
13. 若【整集导演计划】标记某套路已用满预算，本单元必须换拍法；V3 之外的特效创意仍不限制。

【输出要求】
只输出 JSON，不要 Markdown 代码块，不要解释，不要输出任何额外文字。JSON 字段：
{
  "dramaticPurpose": "本单元戏剧目的",
  "primarySubject": "核心主体",
  "secondarySubject": "次要主体",
  "conflictType": "1V1 | 1VN | NVN | NonCombat",
  "corePayoff": "本单元核心爽点/观众获得什么",
  "emotionCurve": ["情绪1", "情绪2", "情绪3"],
  "rhythmStrategy": "慢 → 快 → 快 → 停",
  "actionStrategy": "动作策略",
  "performanceStrategy": "表演策略",
  "cameraStrategy": "摄影策略",
  "vfxStrategy": "VFX参考方向（不限制后续增删）",
  "intensityLevel": 3,
  "combatGrammarIds": [],
  "actionPlan": {
    "conflictType": "1VN",
    "primaryFighterId": "角色ID或角色名",
    "enemyIds": ["敌方1", "敌方2"],
    "combatStyle": "压迫后碾压",
    "roundCount": 3,
    "dominanceCurve": "敌方人数压迫→主角掌控→主角碾压",
    "combatGrammarIds": ["T3_SURROUND_ATTACK", "T1_DODGE_COUNTER", "T2_CLOSE_COUNTER", "T4_AOE_BREAK"],
    "endingState": "敌方群体击退"
  },
  "fightArcType": "full_duel",
  "fightSequence": {
    "arcType": "full_duel",
    "arcName": "完整对决",
    "totalDurationSeconds": 11,
    "winner": "岳沉天",
    "phases": [
      { "phaseNo": 1, "name": "对峙进场", "purpose": "建立双方身份与距离", "durationPercent": 15, "minSeconds": 1, "maxSeconds": 2, "shotCount": 1, "shotStyle": "特写/双人同框", "camera": "缓推", "vfxLevel": 10, "skills": [], "dialogue": "", "endState": "双方进入战斗距离", "nextCondition": "对话或直接开打" }
    ]
  },
  "combatRoundCount": 3,
  "vfxPeakPhase": "Late"
}
""";
    }

    public static string BuildUserMessage(
        Project? project,
        string? stylePrompt,
        string? storyAnalysis,
        string? blueprint,
        List<Episode> episodes,
        StageUnit current,
        StageUnit? prev,
        StageUnit? next,
        List<CharacterAsset> characters,
        List<PropAsset> props,
        List<EnvironmentAsset> environments,
        List<EffectAsset> effects,
        List<SkillLibraryItem> skills,
        List<FightTemplateItem> templates,
        List<CameraAtomItem> atoms,
        List<FightArcTemplate>? fightArcs = null,
        EpisodeDirectorPlan? episodePlan = null,
        EpisodeDirectorState? episodeState = null,
        EpisodeUnitStateSnapshot? previousSnapshot = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("【项目信息】");
        if (project != null)
        {
            sb.AppendLine("项目名: " + project.Title);
            if (!string.IsNullOrWhiteSpace(project.Tags))
                sb.AppendLine("标签: " + project.Tags);
            if (!string.IsNullOrWhiteSpace(project.Description))
                sb.AppendLine("简介: " + project.Description);
        }
        if (!string.IsNullOrWhiteSpace(stylePrompt))
            sb.AppendLine("视觉方向: " + stylePrompt);
        sb.AppendLine();

        sb.AppendLine("【故事分析】");
        sb.AppendLine(Truncate(storyAnalysis, 6000) ?? "无");
        sb.AppendLine();

        sb.AppendLine("【全局蓝图】");
        sb.AppendLine(Truncate(blueprint, 8000) ?? "无");
        sb.AppendLine();

        var currentEpisode = episodes.FirstOrDefault(e => e.EpisodeNumber == current.EpisodeNumber);
        if (currentEpisode != null)
        {
            sb.AppendLine("【本集蓝图】");
            sb.AppendLine("第" + currentEpisode.EpisodeNumber + "集: " + currentEpisode.Title);
            if (!string.IsNullOrWhiteSpace(currentEpisode.Summary))
                sb.AppendLine("本集概要: " + currentEpisode.Summary);
            sb.AppendLine();
        }

        if (episodePlan != null)
        {
            sb.AppendLine(EpisodeDirectorPlanParser.BuildUnitConstraintSection(episodePlan, episodeState, current));
            sb.AppendLine(EpisodeDirectorPlanParser.BuildUnitConstraintSection(episodePlan, episodeState, current, previousSnapshot));
            sb.AppendLine();
        }

        sb.AppendLine("【当前单元】");
        sb.AppendLine(current.RawText);
        sb.AppendLine();
        sb.AppendLine("【前一单元】");
        sb.AppendLine(prev == null ? "（本单元是全剧/本批第一个单元）" : prev.RawText);
        sb.AppendLine();
        var snapshotSection = EpisodeContinuityValidator.BuildSnapshotSection(previousSnapshot);
        if (!string.IsNullOrWhiteSpace(snapshotSection))
        {
            sb.AppendLine("【上一单元落镜快照】");
            sb.AppendLine(snapshotSection);
            sb.AppendLine();
        }
        sb.AppendLine("【后一单元】");
        sb.AppendLine(next == null ? "（本单元是全剧/本批最后一个单元）" : next.RawText);
        sb.AppendLine();

        sb.AppendLine("【角色资产】");
        sb.AppendLine(JoinAssets(characters.Select(c => "- " + c.Name + ": " + JoinParts(c.Description, c.Attributes)), 40, "无"));
        sb.AppendLine();
        sb.AppendLine("【道具资产】");
        sb.AppendLine(JoinAssets(props.Select(p => "- " + p.Name + ": " + p.Description), 30, "无"));
        sb.AppendLine();
        sb.AppendLine("【环境资产】");
        sb.AppendLine(JoinAssets(environments.Select(e => "- " + e.Name + ": " + e.Description), 30, "无"));
        sb.AppendLine();
        sb.AppendLine("【特效资产】");
        sb.AppendLine(JoinAssets(effects.Select(e => "- " + e.Name + ": " + e.Description), 30, "无"));
        sb.AppendLine();

        sb.AppendLine("【技能库】");
        if (skills == null || skills.Count == 0)
        {
            sb.AppendLine("无");
        }
        else
        {
            foreach (var s in skills.Take(40))
            {
                sb.AppendLine("- " + s.Name + "（" + s.Element + "系·T" + s.Tier + "）: " + s.PromptVideoForLLM + (string.IsNullOrWhiteSpace(s.OwnerCharacter) ? "" : "；归属:" + s.OwnerCharacter));
            }
            if (skills.Count > 40)
                sb.AppendLine("（技能库共 " + skills.Count + " 条，仅列出前 40 条；未列出条目仍以数据库为准，禁止自创）");
        }
        sb.AppendLine();

        sb.AppendLine("【打斗模板库】");
        if (templates == null || templates.Count == 0)
        {
            sb.AppendLine("无");
        }
        else
        {
            foreach (var t in templates.Take(20))
            {
                sb.AppendLine("- 模板" + t.Name + "（T" + t.Tier + "·" + t.Duration + "s）: 适用:" + t.Scene + "；节拍:" + t.Beat);
            }
            if (templates.Count > 20)
                sb.AppendLine("（模板库共 " + templates.Count + " 条，仅列出前 20 条）");
        }
        sb.AppendLine();

        sb.AppendLine("【运镜原子库】");
        sb.AppendLine(JoinAssets(atoms.Select(a => "- " + a.Name + "（" + a.Category + "）: " + a.Description), 40, "无"));
        sb.AppendLine();

        sb.AppendLine("【战斗语法库（仅限以下 ID，禁止自创）】");
        sb.AppendLine(CombatGrammarCatalog.BuildLibraryText());
        sb.AppendLine();

        sb.AppendLine("【任务】");
        sb.AppendLine("【战斗段落骨架库】");
        sb.AppendLine(FightArcCatalog.BuildLibraryText(fightArcs));
        sb.AppendLine();
        sb.AppendLine("根据以上上下文为当前单元生成 DirectorPlan。输出必须满足系统提示中的 JSON 格式与规则。");
        sb.AppendLine("打斗/高潮/对决/追逐单元必须从【战斗段落骨架库】选择 fightArcType 并按阶段生成 fightSequence；非打斗单元两者必须为 null。");
        return sb.ToString();
    }

    public static string BuildDecisionSection(DirectorPlan plan)
    {
        if (plan == null) return "";
        var sb = new StringBuilder();
        sb.AppendLine("【导演决策】（动作/站位/表演/摄影为意图约束；特效不限制，仅技能不可改）");
        sb.AppendLine("- 戏剧目的: " + plan.DramaticPurpose);
        sb.AppendLine("- 核心主体: " + plan.PrimarySubject);
        sb.AppendLine("- 次要主体: " + (string.IsNullOrWhiteSpace(plan.SecondarySubject) ? "无" : plan.SecondarySubject));
        sb.AppendLine("- 冲突关系: " + plan.ConflictType);
        sb.AppendLine("- 核心爽点: " + plan.CorePayoff);
        sb.AppendLine("- 情绪曲线: " + plan.EmotionCurve);
        sb.AppendLine("- 节奏策略: " + plan.RhythmStrategy);
        sb.AppendLine("- 动作策略: " + plan.ActionStrategy);
        sb.AppendLine("- 表演策略: " + plan.PerformanceStrategy);
        sb.AppendLine("- 摄影策略: " + plan.CameraStrategy);
        sb.AppendLine("- VFX策略（仅参考，不限制）: " + plan.VfxStrategy);
        sb.AppendLine("- 本单元高潮等级: " + plan.IntensityLevel + "/10");
        if (!string.IsNullOrWhiteSpace(plan.CombatGrammarIds))
            sb.AppendLine("- CombatGrammarIds: " + plan.CombatGrammarIds);
        if (plan.CombatRoundCount > 0)
            sb.AppendLine("- 期望回合数: " + plan.CombatRoundCount);
        if (!string.IsNullOrWhiteSpace(plan.VfxPeakPhase))
            sb.AppendLine("- VFX峰值段（仅参考，不限制）: " + plan.VfxPeakPhase);
        if (!string.IsNullOrWhiteSpace(plan.FightArcType))
            sb.AppendLine("- 战斗段落骨架: " + plan.FightArcType);
        var fightSequenceText = FightArcCatalog.BuildSequenceText(plan.FightSequenceJson);
        if (!string.IsNullOrWhiteSpace(fightSequenceText))
            sb.AppendLine("- 战斗段落:\n" + fightSequenceText);
        if (!string.IsNullOrWhiteSpace(plan.ActionPlan))
            sb.AppendLine("- ActionPlan: " + plan.ActionPlan);
        return sb.ToString();
    }

    private static string? Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "……（已截断）";
    }

    private static string JoinAssets(IEnumerable<string> lines, int take, string emptyText)
    {
        var list = (lines ?? Enumerable.Empty<string>()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (list.Count == 0) return emptyText;
        var sb = new StringBuilder();
        foreach (var line in list.Take(take))
            sb.AppendLine(line);
        if (list.Count > take)
            sb.AppendLine("（共 " + list.Count + " 条，仅列出前 " + take + " 条）");
        return sb.ToString().TrimEnd();
    }

    private static string JoinParts(params string?[] parts)
    {
        return string.Join("；", parts.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));
    }
}
