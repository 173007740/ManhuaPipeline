using System.Text.RegularExpressions;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Director;

/// <summary>
/// Director V4 第二批：Unit 间连续性校验。
/// 前一个 Unit 已落镜的状态是事实，当前 Unit 禁止重新建立、瞬移、伤势自愈、技能状态凭空消失或情绪跳变。
/// </summary>
public static class EpisodeContinuityValidator
{
    private static readonly string[] ActiveCombatMarkers =
    {
        "命中", "震飞", "倒飞", "压制", "对撞", "爆发", "崩碎", "炸开", "贯穿",
        "轰", "踢", "斩", "拳", "掌", "连击", "交锋", "交手", "缠斗", "厮杀",
        "封路", "合围", "围杀", "格挡", "反打", "突进", "收拳"
    };

    private static readonly string[] ResetPrefixes = { "重新", "再次", "退回", "又" };

    private static readonly string[] ResetActions =
    {
        "对峙", "摆出架势", "摆开架势", "拉开距离", "站稳", "起手式", "蓄势待发",
        "冲锋", "回到原点", "回归对峙"
    };

    private static readonly string[] NegativeEmotionMarkers =
    {
        "怒", "杀", "恨", "痛", "惧", "惊", "愤", "燃", "警惕", "压迫", "杀意", "狠"
    };

    private static readonly string[] CalmEmotionMarkers =
    {
        "平静", "轻松", "喜悦", "愉快", "温馨", "淡然", "悠闲", "缓和", "放松"
    };

    private static readonly string[] InjuryRecoveryMarkers =
    {
        "伤势全无", "毫发无伤", "伤口消失", "伤势尽复", "完好如初", "恢复如初"
    };

    private static readonly string[] SkillStateResetMarkers =
    {
        "气血全消", "气机全无", "技能消失", "状态清零", "灵光消散", "气劲全消"
    };

    private static readonly string[] EnvironmentRepairMarkers =
    {
        "恢复原状", "复原", "完好无损", "废墟消失", "裂缝消失"
    };

    private static readonly string[] StrongLocationTokens =
    {
        "山门", "阵心", "演武场", "大殿", "台阶", "屋顶", "山巅", "山崖", "河边",
        "洞府", "枯井", "荒郊", "乱葬岗", "祭坛", "广场", "街道", "房顶", "擂台"
    };

    private static readonly string[] MoveTransitionMarkers =
    {
        "转场", "来到", "进入", "移动到", "切至", "场景切换", "回到", "走向", "抵达"
    };

    public static List<DirectorViolation> Validate(
        EpisodeUnitStateSnapshot? previous,
        EpisodeDirectorPlan? episodePlan,
        StageUnit unit,
        DirectorPlan? plan,
        string result)
    {
        var violations = new List<DirectorViolation>();
        if (previous?.State == null || string.IsNullOrWhiteSpace(result)) return violations;

        CheckUnitReset(violations, previous.State, result);
        CheckEmotionJump(violations, previous.State, episodePlan, unit, plan);
        CheckInjuryRecovery(violations, previous.State, result);
        CheckSkillStateReset(violations, previous.State, result);
        CheckEnvironmentReset(violations, previous.State, result);
        CheckCharacterTeleport(violations, previous.State, result);
        return violations;
    }

    public static string BuildSnapshotSection(EpisodeUnitStateSnapshot? snapshot)
    {
        if (snapshot?.State == null) return "";
        return "V4 上一单元落镜状态（必须继承，禁止重新建立）: " +
            EpisodeDirectorPlanParser.FormatUnitEndState(snapshot.State);
    }

    private static void CheckUnitReset(
        List<DirectorViolation> violations,
        UnitEndState previousState,
        string result)
    {
        if (!HasActiveCombatState(previousState) || !ContainsReset(result)) return;
        violations.Add(MakeViolation(
            "UNIT_RESET_DETECTED",
            "Error",
            "本单元重新建立了上一单元已进入的战斗状态",
            "直接延续上一单元落镜状态（" + PreviousActionSummary(previousState) + "）",
            "重新摆架势/重新对峙/退回起手",
            "删除重开、摆架势、重新对峙等重建立画面，从上一单元结束瞬间的动作、位置、伤势、技能状态继续；若需要短暂喘息，只保留呼吸停顿镜头，禁止退回起手式。"));
    }

    private static void CheckEmotionJump(
        List<DirectorViolation> violations,
        UnitEndState previousState,
        EpisodeDirectorPlan? episodePlan,
        StageUnit unit,
        DirectorPlan? plan)
    {
        var carry = previousState.EmotionalCarry ?? "";
        if (!NegativeEmotionMarkers.Any(m => carry.Contains(m, StringComparison.OrdinalIgnoreCase))) return;

        var incoming = episodePlan?.UnitTransitions?.FirstOrDefault(t =>
            string.Equals(
                EpisodeDirectorPlanParser.NormalizeUnitToken(t.ToUnit),
                EpisodeDirectorPlanParser.NormalizeUnitToken(unit.UnitNumber),
                StringComparison.OrdinalIgnoreCase));
        if (incoming != null &&
            (string.Equals(incoming.TransitionType, "SceneChange", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(incoming.TransitionType, "BreathingReset", StringComparison.OrdinalIgnoreCase) ||
             NegativeEmotionMarkers.Any(m => (incoming.CarryEmotion ?? "").Contains(m, StringComparison.OrdinalIgnoreCase))))
        {
            return;
        }

        var firstEmotion = FirstEmotion(plan?.EmotionCurve);
        if (!CalmEmotionMarkers.Any(m => firstEmotion.Contains(m, StringComparison.OrdinalIgnoreCase))) return;

        violations.Add(MakeViolation(
            "EMOTION_JUMP",
            "Warning",
            "情绪跳变：上一单元延续「" + carry + "」，本单元却从「" + firstEmotion + "」开始",
            "承接上一单元情绪或使用已声明的情绪交接",
            "上一单元" + carry + " → 本单元" + firstEmotion,
            "本单元开头先承接上一单元的情绪，再在镜头内完成合理过渡；不要在同一时间点直接换成完全相反的情绪。"));
    }

    private static void CheckInjuryRecovery(
        List<DirectorViolation> violations,
        UnitEndState previousState,
        string result)
    {
        var hasInjury = (previousState.Characters?.Values ?? Enumerable.Empty<CharacterState>())
            .Any(c => !string.IsNullOrWhiteSpace(c.Injury));
        if (!hasInjury) return;
        var marker = InjuryRecoveryMarkers.FirstOrDefault(m => result.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (marker == null) return;

        violations.Add(MakeViolation(
            "INJURY_RECOVERED",
            "Warning",
            "伤势凭空恢复：画面出现「" + marker + "」",
            "上一单元伤势延续并随时间变化",
            "上一单元有伤，本单元伤势全无",
            "保留伤势的延续表达（伤口、血迹、动作受限），需要恢复时必须有明确的时间/治疗/丹药等承接镜头。"));
    }

    private static void CheckSkillStateReset(
        List<DirectorViolation> violations,
        UnitEndState previousState,
        string result)
    {
        var hasSkillState = (previousState.Characters?.Values ?? Enumerable.Empty<CharacterState>())
            .Any(c => !string.IsNullOrWhiteSpace(c.SkillState));
        if (!hasSkillState) return;
        var marker = SkillStateResetMarkers.FirstOrDefault(m => result.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (marker == null) return;

        violations.Add(MakeViolation(
            "SKILL_STATE_RESET",
            "Warning",
            "技能状态凭空消失：画面出现「" + marker + "」",
            "延续上一单元技能状态，或给明确收招镜头",
            "上一单元技能开启，本单元直接全消",
            "保留或延续技能开启状态；若需要收招，先给收招/吐纳/气血回落镜头，再进入下一状态。"));
    }

    private static void CheckEnvironmentReset(
        List<DirectorViolation> violations,
        UnitEndState previousState,
        string result)
    {
        var environment = previousState.EnvironmentState ?? "";
        var wasBroken = environment.Contains("破碎") ||
            environment.Contains("倒塌") ||
            environment.Contains("裂纹") ||
            environment.Contains("废墟") ||
            environment.Contains("崩裂") ||
            environment.Contains("狼藉");
        if (!wasBroken) return;
        var marker = EnvironmentRepairMarkers.FirstOrDefault(m => result.Contains(m, StringComparison.OrdinalIgnoreCase));
        if (marker == null) return;

        violations.Add(MakeViolation(
            "ENVIRONMENT_RESET",
            "Warning",
            "场景破坏状态凭空恢复：画面出现「" + marker + "」",
            "保留上一单元的场景破坏痕迹",
            "上一单元环境破坏，本单元恢复原状",
            "保留裂缝、碎石、倒塌物等破坏痕迹；需要修复时必须有明确的修复/时间承接镜头。"));
    }

    private static void CheckCharacterTeleport(
        List<DirectorViolation> violations,
        UnitEndState previousState,
        string result)
    {
        var previousLocations = (previousState.Characters?.Values ?? Enumerable.Empty<CharacterState>())
            .Select(c => c.Position ?? "")
            .Concat([previousState.EnvironmentState ?? ""])
            .SelectMany(FindLocations)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var currentLocations = FindLocations(result);
        if (previousLocations.Count != 1 || currentLocations.Count != 1) return;
        if (string.Equals(previousLocations[0], currentLocations[0], StringComparison.OrdinalIgnoreCase)) return;
        if (MoveTransitionMarkers.Any(m => result.Contains(m, StringComparison.OrdinalIgnoreCase))) return;

        violations.Add(MakeViolation(
            "CHARACTER_TELEPORT",
            "Warning",
            "角色位置跳跃：上一单元在「" + previousLocations[0] + "」，本单元直接出现在「" + currentLocations[0] + "」",
            "位置连续移动或明确转场镜头",
            previousLocations[0] + " → " + currentLocations[0],
            "在位置变化前补一个移动/转场镜头（离开、进入、切场景），禁止同一镜头内无承接换位。"));
    }

    private static bool HasActiveCombatState(UnitEndState state)
    {
        if (ActiveCombatMarkers.Any(m => (state.LastActionState ?? "").Contains(m, StringComparison.OrdinalIgnoreCase)))
            return true;
        if (!string.IsNullOrWhiteSpace(state.ActiveVfxState)) return true;
        return (state.Characters?.Values ?? Enumerable.Empty<CharacterState>())
            .Any(c => !string.IsNullOrWhiteSpace(c.SkillState) || !string.IsNullOrWhiteSpace(c.Injury));
    }

    private static bool ContainsReset(string result)
    {
        var normalized = Regex.Replace(result, @"[\s，。！？：；、,.;:!?()（）\[\]【】]", "");
        foreach (var prefix in ResetPrefixes)
        {
            foreach (var action in ResetActions)
            {
                if (normalized.Contains(prefix + action, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static string PreviousActionSummary(UnitEndState state)
    {
        if (!string.IsNullOrWhiteSpace(state.LastActionState)) return state.LastActionState;
        if (!string.IsNullOrWhiteSpace(state.ActiveVfxState)) return state.ActiveVfxState;
        var skill = state.Characters?.Values.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c.SkillState))?.SkillState;
        return string.IsNullOrWhiteSpace(skill) ? "战斗进行中" : skill;
    }

    private static string FirstEmotion(string? emotionCurve)
    {
        if (string.IsNullOrWhiteSpace(emotionCurve)) return "";
        foreach (var separator in new[] { "→", "->", ">", "，", ",", "、", ";" })
        {
            var first = emotionCurve.Split(separator, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            if (!string.IsNullOrWhiteSpace(first)) return first;
        }
        return emotionCurve.Trim();
    }

    private static List<string> FindLocations(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new List<string>();
        return StrongLocationTokens
            .Where(t => text.Contains(t, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static DirectorViolation MakeViolation(
        string code,
        string severity,
        string message,
        string expected,
        string actual,
        string repairInstruction) =>
        new()
        {
            Code = code,
            Severity = severity,
            Message = message,
            Expected = expected,
            Actual = actual,
            RepairInstruction = repairInstruction
        };
}
