using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// Director V3 RuleValidator：整集层程序规则。
/// 检查强度上限、保留视觉提前使用、爽点顺序、镜头/动作/特效套路超预算。
/// </summary>
public static class EpisodeDirectorRuleValidator
{
    public static List<DirectorViolation> Check(
        DirectorPlan? plan,
        StageUnit unit,
        string result,
        EpisodeDirectorPlan? episodePlan,
        EpisodeDirectorState? state)
    {
        var violations = new List<DirectorViolation>();
        if (episodePlan == null || string.IsNullOrWhiteSpace(result)) return violations;

        CheckIntensity(violations, plan, unit, episodePlan);
        CheckReservedVisuals(violations, unit, result, episodePlan);
        CheckPayoffOrder(violations, unit, result, episodePlan);
        CheckPatternOveruse(violations, result, episodePlan, state);
        CheckClimaxBudget(violations, plan, episodePlan, state);
        return violations;
    }

    private static void CheckIntensity(
        List<DirectorViolation> violations,
        DirectorPlan? plan,
        StageUnit unit,
        EpisodeDirectorPlan episodePlan)
    {
        if (plan == null) return;
        var limit = EpisodeDirectorPlanParser.GetIntensityLimit(episodePlan, unit);
        if (limit <= 0 || plan.IntensityLevel <= limit) return;

        violations.Add(new DirectorViolation
        {
            Code = "UNIT_INTENSITY_EXCEEDED",
            Severity = "Error",
            Message = $"本单元强度 {plan.IntensityLevel}/10 超过整集上限 {limit}/10",
            Expected = "≤ " + limit,
            Actual = plan.IntensityLevel.ToString(),
            RepairInstruction = $"把本单元导演强度降到 {limit}/10 以内，动作密度、运镜规模与特效量同步降级，禁止抢本集高潮。"
        });
    }

    private static void CheckReservedVisuals(
        List<DirectorViolation> violations,
        StageUnit unit,
        string result,
        EpisodeDirectorPlan episodePlan)
    {
        foreach (var visual in episodePlan.ReservedVisuals ?? [])
        {
            if (string.IsNullOrWhiteSpace(visual.Visual)) continue;
            if (!ContainsText(result, visual.Visual)) continue;

            var allowed = !string.IsNullOrWhiteSpace(visual.ReservedUnit) &&
                IsUnitAllowed(unit.UnitNumber, visual.ReservedUnit);
            if (allowed) continue;

            violations.Add(new DirectorViolation
            {
                Code = "RESERVED_PAYOFF_USED_EARLY",
                Severity = "Error",
                Message = $"提前使用了保留视觉「{visual.Visual}」",
                Expected = string.IsNullOrWhiteSpace(visual.ReservedUnit)
                    ? "本集禁止使用"
                    : "仅 " + visual.ReservedUnit + " 可用",
                Actual = unit.UnitNumber + " 已出现",
                RepairInstruction = $"从本单元移除「{visual.Visual}」，换成当前强度允许的普通特效/运镜，保留给指定高潮单元。"
            });
        }
    }

    private static void CheckPayoffOrder(
        List<DirectorViolation> violations,
        StageUnit unit,
        string result,
        EpisodeDirectorPlan episodePlan)
    {
        foreach (var forbidden in episodePlan.ForbiddenEarlyPayoffs ?? [])
        {
            if (string.IsNullOrWhiteSpace(forbidden) || !ContainsText(result, forbidden)) continue;
            violations.Add(new DirectorViolation
            {
                Code = "PAYOFF_ORDER_VIOLATION",
                Severity = "Error",
                Message = $"提前释放了本集爆点「{forbidden}」",
                Expected = "后置爽点",
                Actual = unit.UnitNumber + " 已出现",
                RepairInstruction = $"删除或改写「{forbidden}」相关画面，本单元只释放当前阶段爽点。"
            });
        }

        foreach (var payoff in episodePlan.PayoffSchedule ?? [])
        {
            if (string.IsNullOrWhiteSpace(payoff.UnitNumber) || string.IsNullOrWhiteSpace(payoff.Name)) continue;
            if (!IsUnitBefore(unit.UnitNumber, payoff.UnitNumber)) continue;
            if (!ContainsText(result, payoff.Name)) continue;

            violations.Add(new DirectorViolation
            {
                Code = "PAYOFF_ORDER_VIOLATION",
                Severity = "Error",
                Message = $"提前释放了「{payoff.UnitNumber}」的爽点「{payoff.Name}」",
                Expected = payoff.UnitNumber + " 才能出现",
                Actual = unit.UnitNumber + " 已出现",
                RepairInstruction = $"把「{payoff.Name}」从本单元移除，只保留属于本单元的小爽点。"
            });
        }
    }

    private static void CheckPatternOveruse(
        List<DirectorViolation> violations,
        string result,
        EpisodeDirectorPlan episodePlan,
        EpisodeDirectorState? state)
    {
        var policy = episodePlan.RepetitionPolicy;
        if (policy == null || state == null) return;

        CheckCategory(
            violations, result, state.CameraPatternCounts, policy.CameraPatternLimits,
            "CAMERA_PATTERN_OVERUSED", "镜头套路");

        CheckCategory(
            violations, result, state.CombatPatternCounts, policy.CombatPatternLimits,
            "COMBAT_PATTERN_OVERUSED", "动作套路");

        CheckCategory(
            violations, result, state.VfxPatternCounts, policy.VfxPatternLimits,
            "VFX_PATTERN_OVERUSED", "特效套路");

        if (policy.SlowMotionLimit >= 0 &&
            state.SlowMotionCount >= policy.SlowMotionLimit &&
            ContainsSlowMotion(result))
        {
            violations.Add(new DirectorViolation
            {
                Code = "CAMERA_PATTERN_OVERUSED",
                Severity = "Warning",
                Message = $"慢动作已超出整集预算（{state.SlowMotionCount}/{policy.SlowMotionLimit}）",
                Expected = "≤ " + policy.SlowMotionLimit,
                Actual = state.SlowMotionCount.ToString(),
                RepairInstruction = "本单元不再使用慢动作/慢放，改用加速、顿帧或正常速度表现命中。"
            });
        }

        if (policy.MajorExplosionLimit >= 0 &&
            state.MajorExplosionCount >= policy.MajorExplosionLimit &&
            ContainsMajorExplosion(result))
        {
            violations.Add(new DirectorViolation
            {
                Code = "VFX_PATTERN_OVERUSED",
                Severity = "Warning",
                Message = $"高潮型特效已超出整集预算（{state.MajorExplosionCount}/{policy.MajorExplosionLimit}）",
                Expected = "≤ " + policy.MajorExplosionLimit,
                Actual = state.MajorExplosionCount.ToString(),
                RepairInstruction = "本单元不再安排爆炸/炸裂级特效，改用集中、克制的特效表现。"
            });
        }
    }

    private static void CheckClimaxBudget(
        List<DirectorViolation> violations,
        DirectorPlan? plan,
        EpisodeDirectorPlan episodePlan,
        EpisodeDirectorState? state)
    {
        if (plan == null || episodePlan.ClimaxBudget == null) return;
        var category = EpisodeDirectorPlanParser.ClimaxCategory(plan.IntensityLevel);
        var limit = EpisodeDirectorPlanParser.ClimaxBudgetLimit(episodePlan, plan.IntensityLevel);
        if (limit < 0) return;
        var used = category switch
        {
            "大" => state?.LargeClimaxCount ?? 0,
            "中" => state?.MidClimaxCount ?? 0,
            _ => state?.SmallClimaxCount ?? 0
        };
        if (used >= limit)
        {
            violations.Add(new DirectorViolation
            {
                Code = "CLIMAX_BUDGET_EXCEEDED",
                Severity = "Warning",
                Message = $"{category}高潮已超出本集预算（{used}/{limit}）", 
                Expected = "≤ " + limit,
                Actual = used.ToString(),
                RepairInstruction = $"本单元强度降级为其他高潮档位，避免再占{category}高潮名额；已用名额留给计划中的高潮单元。"
            });
        }
    }

    private static void CheckCategory(
        List<DirectorViolation> violations,
        string result,
        Dictionary<string, int>? counts,
        List<PatternLimit>? limits,
        string code,
        string label)
    {
        foreach (var limit in limits ?? [])
        {
            if (string.IsNullOrWhiteSpace(limit.Pattern) || limit.MaxCount < 0) continue;
            var used = counts != null && counts.TryGetValue(limit.Pattern, out var n) ? n : 0;
            var willUse = ContainsText(result, limit.Pattern);
            if (used + (willUse ? 1 : 0) <= limit.MaxCount) continue;
            var alreadyOver = used >= limit.MaxCount;

            violations.Add(new DirectorViolation
            {
                Code = code,
                Severity = alreadyOver ? "Warning" : "Error",
                Message = $"{label}「{limit.Pattern}」已超出整集预算（{used}/{limit.MaxCount}）",
                Expected = "≤ " + limit.MaxCount,
                Actual = used.ToString(),
                RepairInstruction = $"本单元避免再次使用「{limit.Pattern}」，优先换成其他{label}。"
            });
        }
    }

    private static bool IsUnitBefore(string current, string target)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(target)) return false;
        var a = ParseUnitOrder(current);
        var b = ParseUnitOrder(target);
        if (a == b) return false;
        if (a.Episode != b.Episode) return a.Episode < b.Episode;
        return a.Sub < b.Sub;
    }

    private static (int Episode, decimal Sub) ParseUnitOrder(string token)
    {
        var normalized = EpisodeDirectorPlanParser.NormalizeUnitToken(token);
        var parts = normalized.Split('.');
        var episode = 0;
        if (parts.Length > 0 && int.TryParse(parts[0], out var ep)) episode = ep;
        var sub = 0m;
        if (parts.Length > 1)
        {
            var digits = Regex.Match(parts[1], @"^\d+");
            if (digits.Success && decimal.TryParse(digits.Value, out var parsed)) sub = parsed;
        }
        return (episode, sub);
    }

    private static bool IsUnitAllowed(string current, string reserved)
    {
        if (string.IsNullOrWhiteSpace(current)) return false;
        var a = EpisodeDirectorPlanParser.NormalizeUnitToken(current);
        var b = EpisodeDirectorPlanParser.NormalizeUnitToken(reserved);
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
        if (a.Contains('.') && b.Length > 0)
        {
            var suffixA = a.Substring(a.LastIndexOf('.') + 1).TrimStart('0');
            var suffixB = b.TrimStart('0');
            if (string.Equals(suffixA, suffixB, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    internal static bool ContainsText(string text, string expected)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(expected)) return false;
        var t = NormalizePattern(text);
        var e = NormalizePattern(expected);
        return e.Length > 0 && t.Contains(e, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePattern(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        return Regex.Replace(text, @"[\s，。！？：；、,.;:!?()（）\[\]【】+\-_/\\|]", "");
    }

    internal static bool ContainsSlowMotion(string text)
    {
        return ContainsText(text, "慢动作") ||
               ContainsText(text, "慢放") ||
               ContainsText(text, "慢镜头");
    }

    internal static bool ContainsMajorExplosion(string text)
    {
        return ContainsText(text, "爆炸") ||
               ContainsText(text, "爆裂") ||
               ContainsText(text, "炸开") ||
               ContainsText(text, "炸裂");
    }
}

/// <summary>
/// 每个单元分镜通过校验后，把本单元实际用过的套路/慢动作/高潮特效写回整集状态。
/// </summary>
public static class EpisodeDirectorStateUpdater
{
    public static void Apply(
        EpisodeDirectorState state,
        EpisodeDirectorPlan episodePlan,
        DirectorPlan? plan,
        string storyboard)
    {
        if (state == null || episodePlan?.RepetitionPolicy == null) return;
        var haystack = storyboard + "\n" + plan?.CameraStrategy + "\n" + plan?.ActionStrategy + "\n" + plan?.VfxStrategy;

        IncrementPatterns(state.CameraPatternCounts, episodePlan.RepetitionPolicy.CameraPatternLimits, haystack);
        IncrementPatterns(state.CombatPatternCounts, episodePlan.RepetitionPolicy.CombatPatternLimits, haystack);
        IncrementPatterns(state.VfxPatternCounts, episodePlan.RepetitionPolicy.VfxPatternLimits, haystack);

        if (EpisodeDirectorRuleValidator.ContainsSlowMotion(haystack))
            state.SlowMotionCount++;

        if (EpisodeDirectorRuleValidator.ContainsMajorExplosion(haystack))
            state.MajorExplosionCount++;

        state.CurrentPeakIntensity = Math.Max(state.CurrentPeakIntensity, plan?.IntensityLevel ?? 0);

        var climaxCategory = EpisodeDirectorPlanParser.ClimaxCategory(plan?.IntensityLevel ?? 0);
        if (climaxCategory == "大") state.LargeClimaxCount++;
        else if (climaxCategory == "中") state.MidClimaxCount++;
        else state.SmallClimaxCount++;
        state.UpdatedAt = DateTime.Now;
    }

    private static void IncrementPatterns(
        Dictionary<string, int> counts,
        List<PatternLimit>? limits,
        string haystack)
    {
        foreach (var limit in limits ?? [])
        {
            if (string.IsNullOrWhiteSpace(limit.Pattern) || !EpisodeDirectorRuleValidator.ContainsText(haystack, limit.Pattern)) continue;
            counts.TryGetValue(limit.Pattern, out var current);
            counts[limit.Pattern] = current + 1;
        }
    }
}
