using System.Text.Json;
using System.Text.Json.Serialization;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// EpisodeDirectorPlan 的解析、归一化与回退：LLM 失败时不阻塞 Stage 5，
/// 退化为按单元顺序递增的强度曲线。
/// </summary>
public static class EpisodeDirectorPlanParser
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    public static EpisodeDirectorPlan? Parse(
        string? raw,
        List<StageUnit> units,
        int projectId,
        int episodeNumber)
    {
        var json = ExtractJsonObject(raw);
        if (json == null) return null;

        EpisodeDirectorPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<EpisodeDirectorPlan>(json, JsonOptions);
        }
        catch
        {
            return null;
        }

        if (plan == null) return null;
        plan.ProjectId = projectId;
        plan.EpisodeNumber = episodeNumber;
        plan.RawJson = raw ?? "";
        plan.EpisodeGoal = plan.EpisodeGoal ?? "";
        plan.EmotionCurve = plan.EmotionCurve ?? "";
        plan.IntensityCurve ??= new List<UnitIntensityPlan>();
        plan.PayoffSchedule ??= new List<PayoffPoint>();
        plan.ReservedVisuals ??= new List<ReservedVisual>();
        plan.ForbiddenEarlyPayoffs ??= new List<string>();
        plan.RepetitionPolicy ??= new RepetitionPolicy();
        plan.RepetitionPolicy.CameraPatternLimits ??= new List<PatternLimit>();
        plan.RepetitionPolicy.CombatPatternLimits ??= new List<PatternLimit>();
        plan.RepetitionPolicy.VfxPatternLimits ??= new List<PatternLimit>();

        FillMissingIntensity(plan, units);
        plan.UnitEmotionCurve ??= new List<UnitEmotionPlan>();
        plan.UnitTransitions ??= new List<UnitTransition>();
        plan.UnitEndStates ??= new List<UnitEndState>();
        plan.ClimaxBudget ??= new ClimaxBudget();
        plan.ClimaxBudget.ReservedVisuals ??= new List<ReservedVisual>();

        FillMissingUnitFlow(plan, units);
        return plan;
    }

    public static EpisodeDirectorPlan BuildFallback(List<StageUnit> units, int projectId, int episodeNumber)
    {
        var episodeUnits = units
            .Where(u => NormalizeEpisode(u.EpisodeNumber) == episodeNumber)
            .ToList();

        var intensity = new List<UnitIntensityPlan>();
        for (var i = 0; i < episodeUnits.Count; i++)
        {
            var limit = episodeUnits.Count <= 1
                ? 5
                : Math.Clamp((int)Math.Round(2 + 8.0 * i / (episodeUnits.Count - 1)), 1, 10);
            intensity.Add(new UnitIntensityPlan
            {
                UnitNumber = episodeUnits[i].UnitNumber,
                IntensityLimit = limit
            });
        }

        var plan = new EpisodeDirectorPlan
        {
            ProjectId = projectId,
            EpisodeNumber = episodeNumber,
            EpisodeGoal = episodeUnits.Count > 0
                ? episodeUnits[0].CoreAction
                : "第" + episodeNumber + "集",
            EmotionCurve = "推进 → 蓄势 → 高潮 → 收束",
            IntensityCurve = intensity,
            PayoffSchedule = new List<PayoffPoint>(),
            ReservedVisuals = new List<ReservedVisual>(),
            ForbiddenEarlyPayoffs = new List<string>(),
            RepetitionPolicy = new RepetitionPolicy
            {
                SlowMotionLimit = 2,
                MajorExplosionLimit = 1,
                CameraPatternLimits =
                [
                    new PatternLimit { Pattern = "低机位", MaxCount = 2 },
                    new PatternLimit { Pattern = "推镜", MaxCount = 2 },
                    new PatternLimit { Pattern = "大远景", MaxCount = 2 }
                ],
                CombatPatternLimits =
                [
                    new PatternLimit { Pattern = "重拳", MaxCount = 2 },
                    new PatternLimit { Pattern = "震飞", MaxCount = 1 },
                    new PatternLimit { Pattern = "对撞", MaxCount = 2 }
                ],
                VfxPatternLimits =
                [
                    new PatternLimit { Pattern = "气血爆发", MaxCount = 1 },
                    new PatternLimit { Pattern = "法相", MaxCount = 1 },
                    new PatternLimit { Pattern = "地裂", MaxCount = 1 }
                ]
            },
            UnitEmotionCurve = new List<UnitEmotionPlan>(),
            UnitTransitions = new List<UnitTransition>(),
            UnitEndStates = new List<UnitEndState>(),
            ClimaxBudget = new ClimaxBudget
            {
                SmallClimaxLimit = 2,
                MidClimaxLimit = 1,
                LargeClimaxLimit = 1,
                ReservedVisuals = new List<ReservedVisual>(),
                Notes = "系统回退：按强度曲线自动控制小/中/大高潮次数"
            },
            RawJson = ""
        };
        FillMissingUnitFlow(plan, units);
        return plan;
    }

    public static int GetIntensityLimit(EpisodeDirectorPlan? plan, StageUnit unit)
    {
        if (plan == null || string.IsNullOrWhiteSpace(unit.UnitNumber)) return 0;
        var unitNumber = unit.UnitNumber.Trim();
        var item = plan.IntensityCurve?.FirstOrDefault(x =>
            string.Equals(NormalizeUnitToken(x.UnitNumber), NormalizeUnitToken(unitNumber), StringComparison.OrdinalIgnoreCase));
        return item?.IntensityLimit ?? 0;
    }

    public static DirectorPlan EnforceUnitLimits(
        DirectorPlan plan,
        EpisodeDirectorPlan? episodePlan,
        StageUnit unit)
    {
        if (episodePlan == null) return plan;
        var limit = GetIntensityLimit(episodePlan, unit);
        if (limit > 0 && plan.IntensityLevel > limit)
        {
            plan.IntensityLevel = limit;
            plan.NeedsReview = true;
        }
        return plan;
    }

    public static string BuildUnitConstraintSection(
        EpisodeDirectorPlan? plan,
        EpisodeDirectorState? state,
        StageUnit unit,
        EpisodeUnitStateSnapshot? previousSnapshot = null)
    {
        if (plan == null) return "";

        var limit = GetIntensityLimit(plan, unit);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("【整集导演计划 V3 + V4（本单元必须服从）】");
        sb.AppendLine("本集目标: " + (string.IsNullOrWhiteSpace(plan.EpisodeGoal) ? "（未生成）" : plan.EpisodeGoal));
        sb.AppendLine("本集情绪曲线: " + (string.IsNullOrWhiteSpace(plan.EmotionCurve) ? "（未生成）" : plan.EmotionCurve));

        var intensityText = string.Join("；", plan.IntensityCurve
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitNumber))
            .Select(x => x.UnitNumber + "=" + x.IntensityLimit));
        sb.AppendLine("本集强度曲线: " + (string.IsNullOrWhiteSpace(intensityText) ? "（未生成）" : intensityText));
        sb.AppendLine("当前单元: " + unit.UnitNumber);
        sb.AppendLine("当前单元强度上限: " + (limit > 0 ? limit + "/10" : "10/10"));

        var emotion = GetUnitEmotion(plan, unit);
        if (emotion != null)
        {
            sb.AppendLine("本单元情绪任务: " + (string.IsNullOrWhiteSpace(emotion.Emotion) ? "承接" : emotion.Emotion) +
                (emotion.Level > 0 ? "（" + emotion.Level + "/10）" : "") +
                (string.IsNullOrWhiteSpace(emotion.DirectingNote) ? "" : "；" + emotion.DirectingNote));
        }

        var incoming = GetIncomingTransition(plan, unit);
        if (incoming != null)
        {
            var fromEnd = previousSnapshot != null &&
                string.Equals(NormalizeUnitToken(previousSnapshot.UnitNumber), NormalizeUnitToken(incoming.FromUnit), StringComparison.OrdinalIgnoreCase)
                    ? previousSnapshot.State
                    : string.IsNullOrWhiteSpace(incoming.FromUnit) ? null : plan.UnitEndStates?.FirstOrDefault(s =>
                        string.Equals(NormalizeUnitToken(s.UnitNumber), NormalizeUnitToken(incoming.FromUnit), StringComparison.OrdinalIgnoreCase));
            sb.AppendLine("V4 Unit 交接（" + incoming.FromUnit + " → " + incoming.ToUnit + "）:");
            sb.AppendLine("- 交接类型: " + (string.IsNullOrWhiteSpace(incoming.TransitionType) ? "EmotionCarry" : incoming.TransitionType));
            sb.AppendLine("- 情绪延续: " + (string.IsNullOrWhiteSpace(incoming.CarryEmotion) ? "（未指定）" : incoming.CarryEmotion));
            sb.AppendLine("- 剧情延续: " + (string.IsNullOrWhiteSpace(incoming.NarrativeCarry) ? "（未指定）" : incoming.NarrativeCarry));
            sb.AppendLine("- 镜头方向: " + (string.IsNullOrWhiteSpace(incoming.CameraDirection) ? "（沿用上一镜头方向）" : incoming.CameraDirection));
            sb.AppendLine("- 上一 Unit 结束状态: " + (fromEnd == null ? (string.IsNullOrWhiteSpace(incoming.StartState) ? "（未指定）" : incoming.StartState) : FormatUnitEndState(fromEnd)));
        }

        var endState = GetUnitEndState(plan, unit);
        if (endState != null)
        {
            sb.AppendLine("V4 本单元结束状态（交给下一 Unit）: " + FormatUnitEndState(endState));
        }

        var budget = plan.ClimaxBudget;
        if (budget != null)
        {
            sb.AppendLine("V4 高潮预算: 小高潮≤" + Math.Max(0, budget.SmallClimaxLimit) +
                "、中高潮≤" + Math.Max(0, budget.MidClimaxLimit) +
                "、大高潮≤" + Math.Max(0, budget.LargeClimaxLimit) +
                (string.IsNullOrWhiteSpace(budget.Notes) ? "" : "；" + budget.Notes));
            if (budget.ReservedVisuals != null && budget.ReservedVisuals.Count > 0)
            {
                sb.AppendLine("V4 保留视觉:");
                foreach (var visual in budget.ReservedVisuals)
                {
                    if (string.IsNullOrWhiteSpace(visual.Visual)) continue;
                    sb.AppendLine("- " + visual.Visual +
                        (string.IsNullOrWhiteSpace(visual.ReservedUnit) ? "（禁止使用）" : "（仅 " + visual.ReservedUnit + " 可用）") +
                        (string.IsNullOrWhiteSpace(visual.Note) ? "" : "；" + visual.Note));
                }
            }
        }

        if (plan.PayoffSchedule.Count > 0)
        {
            sb.AppendLine("爽点排期:");
            foreach (var payoff in plan.PayoffSchedule)
            {
                sb.AppendLine("- " + (string.IsNullOrWhiteSpace(payoff.UnitNumber) ? "?" : payoff.UnitNumber) +
                    "（" + payoff.Type + "）" + payoff.Name +
                    (string.IsNullOrWhiteSpace(payoff.Description) ? "" : "：" + payoff.Description));
            }
        }

        if (plan.ReservedVisuals.Count > 0)
        {
            sb.AppendLine("保留视觉预算（未轮到对应单元禁止出现）:");
            foreach (var visual in plan.ReservedVisuals)
            {
                if (string.IsNullOrWhiteSpace(visual.Visual)) continue;
                sb.AppendLine("- " + visual.Visual +
                    (string.IsNullOrWhiteSpace(visual.ReservedUnit) ? "（禁止使用）" : "（仅 " + visual.ReservedUnit + " 可用）") +
                    (string.IsNullOrWhiteSpace(visual.Note) ? "" : "；" + visual.Note));
            }
        }

        if (plan.ForbiddenEarlyPayoffs.Count > 0)
        {
            sb.AppendLine("禁止提前释放: " + string.Join("、", plan.ForbiddenEarlyPayoffs));
        }

        var policy = plan.RepetitionPolicy;
        if (policy != null)
        {
            sb.AppendLine("重复预算: 慢动作最多 " + Math.Max(0, policy.SlowMotionLimit) +
                " 次，高潮型特效最多 " + Math.Max(0, policy.MajorExplosionLimit) + " 次");
            sb.AppendLine("镜头套路上限: " + FormatPatternLimits(policy.CameraPatternLimits));
            sb.AppendLine("动作套路上限: " + FormatPatternLimits(policy.CombatPatternLimits));
            sb.AppendLine("特效套路上限: " + FormatPatternLimits(policy.VfxPatternLimits));
        }

        if (state != null)
        {
            sb.AppendLine("本集已用: 慢动作 " + state.SlowMotionCount + " 次；高潮型特效 " + state.MajorExplosionCount + " 次；当前最高强度 " + state.CurrentPeakIntensity + "/10");
            sb.AppendLine("本集高潮已用: 小" + state.SmallClimaxCount + "、中" + state.MidClimaxCount + "、大" + state.LargeClimaxCount);
            sb.AppendLine("已用镜头套路: " + FormatCounts(state.CameraPatternCounts));
            sb.AppendLine("已用动作套路: " + FormatCounts(state.CombatPatternCounts));
            sb.AppendLine("已用特效套路: " + FormatCounts(state.VfxPatternCounts));
        }

        sb.AppendLine("铁律: 本单元不允许超过强度上限；禁止提前使用保留视觉与后置爽点；近期已超预算的套路必须换成其他拍法。");
        return sb.ToString();
    }

    private static void FillMissingIntensity(EpisodeDirectorPlan plan, List<StageUnit> units)
    {
        var known = new HashSet<string>(plan.IntensityCurve
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitNumber))
            .Select(x => NormalizeUnitToken(x.UnitNumber)), StringComparer.OrdinalIgnoreCase);

        var episodeUnits = units
            .Where(u => NormalizeEpisode(u.EpisodeNumber) == plan.EpisodeNumber)
            .ToList();

        for (var i = 0; i < episodeUnits.Count; i++)
        {
            var unit = episodeUnits[i];
            if (known.Contains(NormalizeUnitToken(unit.UnitNumber))) continue;
            var limit = episodeUnits.Count <= 1
                ? 5
                : Math.Clamp((int)Math.Round(2 + 8.0 * i / (episodeUnits.Count - 1)), 1, 10);
            plan.IntensityCurve.Add(new UnitIntensityPlan
            {
                UnitNumber = unit.UnitNumber,
                IntensityLimit = limit
            });
        }

        plan.IntensityCurve = plan.IntensityCurve
            .Where(x => !string.IsNullOrWhiteSpace(x.UnitNumber))
            .GroupBy(x => NormalizeUnitToken(x.UnitNumber), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    private static void FillMissingUnitFlow(EpisodeDirectorPlan plan, List<StageUnit> units)
    {
        plan.UnitEmotionCurve ??= new List<UnitEmotionPlan>();
        plan.UnitTransitions ??= new List<UnitTransition>();
        plan.UnitEndStates ??= new List<UnitEndState>();
        plan.ClimaxBudget ??= new ClimaxBudget();
        plan.ClimaxBudget.ReservedVisuals ??= new List<ReservedVisual>();

        var episodeUnits = units
            .Where(u => NormalizeEpisode(u.EpisodeNumber) == plan.EpisodeNumber)
            .ToList();

        var knownEmotions = new HashSet<string>(
            plan.UnitEmotionCurve
                .Where(x => !string.IsNullOrWhiteSpace(x.UnitNumber))
                .Select(x => NormalizeUnitToken(x.UnitNumber)), StringComparer.OrdinalIgnoreCase);
        foreach (var unit in episodeUnits)
        {
            if (knownEmotions.Contains(NormalizeUnitToken(unit.UnitNumber))) continue;
            var limit = GetIntensityLimit(plan, unit);
            plan.UnitEmotionCurve.Add(new UnitEmotionPlan
            {
                UnitNumber = unit.UnitNumber,
                Emotion = limit >= 8 ? "高潮/爆发" : limit >= 5 ? "推进/压迫" : "承接/铺垫",
                Level = limit,
                DirectingNote = "",
            });
        }

        var transitionKeys = new HashSet<string>(
            plan.UnitTransitions
                .Where(t => !string.IsNullOrWhiteSpace(t.FromUnit) && !string.IsNullOrWhiteSpace(t.ToUnit))
                .Select(t => NormalizeUnitToken(t.FromUnit) + "->" + NormalizeUnitToken(t.ToUnit)), StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < episodeUnits.Count; i++)
        {
            var from = episodeUnits[i - 1];
            var to = episodeUnits[i];
            var key = NormalizeUnitToken(from.UnitNumber) + "->" + NormalizeUnitToken(to.UnitNumber);
            if (transitionKeys.Contains(key)) continue;
            var fromEnd = plan.UnitEndStates.FirstOrDefault(s =>
                string.Equals(NormalizeUnitToken(s.UnitNumber), NormalizeUnitToken(from.UnitNumber), StringComparison.OrdinalIgnoreCase));
            plan.UnitTransitions.Add(new UnitTransition
            {
                FromUnit = from.UnitNumber,
                ToUnit = to.UnitNumber,
                TransitionType = "EmotionCarry",
                CarryEmotion = "承接",
                NarrativeCarry = "",
                CameraDirection = "",
                StartState = string.IsNullOrWhiteSpace(to.StartState)
                    ? (string.IsNullOrWhiteSpace(fromEnd?.LastActionState) ? "" : fromEnd.LastActionState)
                    : to.StartState,
                EndState = from.EndState
            });
        }

        var knownEndStates = new HashSet<string>(
            plan.UnitEndStates
                .Where(s => !string.IsNullOrWhiteSpace(s.UnitNumber))
                .Select(s => NormalizeUnitToken(s.UnitNumber)), StringComparer.OrdinalIgnoreCase);
        foreach (var unit in episodeUnits)
        {
            if (knownEndStates.Contains(NormalizeUnitToken(unit.UnitNumber))) continue;
            plan.UnitEndStates.Add(new UnitEndState
            {
                UnitNumber = unit.UnitNumber,
                EnvironmentState = unit.EndState,
                LastActionState = unit.CoreAction,
                EmotionalCarry = "",
                NarrativeCarry = unit.EndState
            });
        }
    }

    internal static UnitEmotionPlan? GetUnitEmotion(EpisodeDirectorPlan? plan, StageUnit unit)
    {
        if (plan == null || string.IsNullOrWhiteSpace(unit.UnitNumber)) return null;
        return plan.UnitEmotionCurve?.FirstOrDefault(x =>
            string.Equals(NormalizeUnitToken(x.UnitNumber), NormalizeUnitToken(unit.UnitNumber), StringComparison.OrdinalIgnoreCase));
    }

    internal static UnitTransition? GetIncomingTransition(EpisodeDirectorPlan? plan, StageUnit unit)
    {
        if (plan == null || string.IsNullOrWhiteSpace(unit.UnitNumber)) return null;
        return plan.UnitTransitions?.FirstOrDefault(t =>
            string.Equals(NormalizeUnitToken(t.ToUnit), NormalizeUnitToken(unit.UnitNumber), StringComparison.OrdinalIgnoreCase));
    }

    internal static UnitEndState? GetUnitEndState(EpisodeDirectorPlan? plan, StageUnit unit)
    {
        if (plan == null || string.IsNullOrWhiteSpace(unit.UnitNumber)) return null;
        return plan.UnitEndStates?.FirstOrDefault(s =>
            string.Equals(NormalizeUnitToken(s.UnitNumber), NormalizeUnitToken(unit.UnitNumber), StringComparison.OrdinalIgnoreCase));
    }

    internal static string ClimaxCategory(int intensity)
    {
        if (intensity >= 8) return "大";
        if (intensity >= 5) return "中";
        return "小";
    }

    internal static int ClimaxBudgetLimit(EpisodeDirectorPlan? plan, int intensity)
    {
        if (plan?.ClimaxBudget == null) return -1;
        return ClimaxCategory(intensity) switch
        {
            "大" => plan.ClimaxBudget.LargeClimaxLimit,
            "中" => plan.ClimaxBudget.MidClimaxLimit,
            _ => plan.ClimaxBudget.SmallClimaxLimit
        };
    }

    internal static string NormalizeUnitToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var t = value.Trim();
        t = System.Text.RegularExpressions.Regex.Replace(t, @"(?i)(unit|episode|单元|第|集)", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"[\s\-_/\\|（）()\[\]【】]", "");
        return t;
    }

    internal static int NormalizeEpisode(int episodeNumber)
    {
        return Math.Max(0, episodeNumber);
    }

    private static string FormatPatternLimits(List<PatternLimit>? limits)
    {
        if (limits == null || limits.Count == 0) return "无";
        return string.Join("、", limits
            .Where(x => !string.IsNullOrWhiteSpace(x.Pattern))
            .Select(x => x.Pattern + "≤" + Math.Max(0, x.MaxCount)));
    }
    internal static string FormatUnitEndState(UnitEndState state)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.LastActionState)) parts.Add("动作:" + state.LastActionState);
        if (state.Characters != null && state.Characters.Count > 0)
            parts.Add("角色:" + string.Join("；", state.Characters.Select(kv => kv.Key + "=" + FormatCharacterState(kv.Value))));
        if (!string.IsNullOrWhiteSpace(state.EnvironmentState)) parts.Add("环境:" + state.EnvironmentState);
        if (!string.IsNullOrWhiteSpace(state.CameraDirection)) parts.Add("镜头:" + state.CameraDirection);
        if (!string.IsNullOrWhiteSpace(state.ActiveVfxState)) parts.Add("特效:" + state.ActiveVfxState);
        if (!string.IsNullOrWhiteSpace(state.EmotionalCarry)) parts.Add("情绪:" + state.EmotionalCarry);
        if (!string.IsNullOrWhiteSpace(state.NarrativeCarry)) parts.Add("剧情:" + state.NarrativeCarry);
        return parts.Count == 0 ? "（未指定）" : string.Join("；", parts);
    }

    private static string FormatCharacterState(CharacterState state)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(state.Position)) parts.Add("位置:" + state.Position);
        if (!string.IsNullOrWhiteSpace(state.Facing)) parts.Add("朝向:" + state.Facing);
        if (!string.IsNullOrWhiteSpace(state.Pose)) parts.Add("姿态:" + state.Pose);
        if (!string.IsNullOrWhiteSpace(state.Appearance)) parts.Add("外观:" + state.Appearance);
        if (!string.IsNullOrWhiteSpace(state.Injury)) parts.Add("伤势:" + state.Injury);
        if (!string.IsNullOrWhiteSpace(state.Weapon)) parts.Add("武器:" + state.Weapon);
        if (!string.IsNullOrWhiteSpace(state.SkillState)) parts.Add("技能:" + state.SkillState);
        return parts.Count == 0 ? "（未指定）" : string.Join("、", parts);
    }

    private static string FormatCounts(Dictionary<string, int>? counts)
    {
        if (counts == null || counts.Count == 0) return "无";
        return string.Join("、", counts
            .Where(x => x.Value > 0)
            .OrderByDescending(x => x.Value)
            .Select(x => x.Key + "×" + x.Value));
    }

    private static string? ExtractJsonObject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        return raw.Substring(start, end - start + 1);
    }
}
