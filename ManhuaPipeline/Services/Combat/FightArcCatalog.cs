using System.Text;
using System.Text.Json;
using ManhuaPipeline.Models;

namespace ManhuaPipeline.Services.Combat;

/// <summary>
/// 战斗段落骨架目录：提供骨架库文本、类型归一、JSON 解析与缺失时的回退。
/// 骨架数据来自 FightArcTemplates 表，代码只负责渲染与校验。
/// </summary>
public static class FightArcCatalog
{
    public const string FullDuel = "full_duel";
    public const string Encounter = "encounter";
    public const string Assassination = "assassination";
    public const string SiegeBreakout = "siege_breakout";
    public const string Chase = "chase";
    public const string DramaConfrontation = "drama_confrontation";

    public static string BuildLibraryText(IEnumerable<FightArcTemplate>? templates)
    {
        var active = (templates ?? Enumerable.Empty<FightArcTemplate>())
            .Where(t => !string.Equals(t.Status, "Inactive", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.FightArcTemplateId)
            .ToList();
        if (active.Count == 0) return "无";

        var sb = new StringBuilder();
        foreach (var t in active)
        {
            var budget = t.DurationBudget ?? new FightDurationBudget();
            sb.AppendLine("- " + t.Name + "（" + t.ArcTypeId + "）: " + t.Description +
                "；整场建议 " + budget.MinSeconds + "-" + budget.MaxSeconds + "s");
            var phaseText = string.Join(" → ",
                t.Phases.OrderBy(p => p.PhaseNo).Select(p => p.Name + " " + p.DurationPercent + "%"));
            if (phaseText.Length > 0)
                sb.AppendLine("  阶段: " + phaseText);
            if (t.Rules.Count > 0)
                sb.AppendLine("  规则: " + string.Join("；", t.Rules));
        }
        return sb.ToString().TrimEnd();
    }

    public static string NormalizeArcTypeId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var text = raw.Trim();
        if (ContainsAny(text, "full_duel", "完整对决", "终极对决", "巅峰对决", "正面对决")) return FullDuel;
        if (ContainsAny(text, "encounter", "遭遇战", "遭遇", "中途遇敌", "小冲突")) return Encounter;
        if (ContainsAny(text, "assassination", "偷袭", "暗杀", "速杀", "秒杀", "一击制胜", "quick_kill", "sneak")) return Assassination;
        if (ContainsAny(text, "siege_breakout", "围杀", "突围", "合围", "围攻", "以一敌多", "以少敌多", "破阵")) return SiegeBreakout;
        if (ContainsAny(text, "chase", "追逐", "追逃", "追击", "逃亡", "追赶")) return Chase;
        if (ContainsAny(text, "drama_confrontation", "文戏对峙", "对峙", "confrontation", "装逼", "不下死手")) return DramaConfrontation;
        return "";
    }

    public static string PickArcType(StageUnit unit)
    {
        var text = string.Join(" ", unit.Type, unit.CoreAction, unit.RawText);
        if (ContainsAny(text, "追逐", "追逃", "追击", "逃亡", "追赶")) return Chase;
        if (ContainsAny(text, "偷袭", "暗杀", "速杀", "秒杀", "一击")) return Assassination;
        if (ContainsAny(text, "围杀", "突围", "合围", "围攻", "以一敌多", "以少敌多", "破阵", "阵法")) return SiegeBreakout;
        if (ContainsAny(text, "文戏", "对峙", "谈判", "嘴炮", "不动手")) return DramaConfrontation;
        return FullDuel;
    }

    /// <summary>纯位移/赶路单元（无对手、无攻防）不适合套战斗段落骨架。</summary>
    public static bool IsMovementOnlyUnit(StageUnit unit)
    {
        // 单元类型常写成“打斗/动作”，不能把类型词当成真实攻防内容。
        var text = string.Join(" ", unit.CoreAction, unit.StartState, unit.EndState, unit.RawText);
        var movement = ContainsAny(text, "踏天步", "轻功", "一步踏出", "飞掠", "越过", "位移", "赶路", "前往", "出发", "落地");
        if (!movement) return false;
        return !ContainsAny(text, "敌", "战", "攻", "防", "杀", "围", "对撞", "交手", "轰", "斗", "追", "逃");
    }

    /// <summary>
    /// 根据单元内容纠正 LLM 选择的骨架：纯位移不套骨架；
    /// 围杀/合围优先围杀突围；5 秒短单元不允许完整对决。
    /// </summary>
    public static string ResolveCombatArcType(StageUnit unit, string? rawArcType)
    {
        var normalized = NormalizeArcTypeId(rawArcType);
        if (IsMovementOnlyUnit(unit)) return "";
        var text = string.Join(" ", unit.Type, unit.CoreAction, unit.RawText);
        if (ContainsAny(text, "围杀", "围困", "围攻", "合围", "围于", "以一敌多", "以少敌多", "破阵", "阵法"))
            return SiegeBreakout;
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = unit.IsCombat ? PickArcType(unit) : "";
        if (string.Equals(normalized, FullDuel, StringComparison.OrdinalIgnoreCase) && unit.Duration == 5)
            return Encounter;
        return normalized;
    }

    public static FightSequencePlan? ParseSequence(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "null" || json == "[]") return null;
        try
        {
            var plan = JsonSerializer.Deserialize<FightSequencePlan>(json, JsonOptions);
            if (plan == null) return null;
            plan.ArcType = NormalizeArcTypeId(plan.ArcType);
            if (string.IsNullOrWhiteSpace(plan.ArcType)) return null;
            return plan;
        }
        catch
        {
            return null;
        }
    }

    public static string BuildSequenceText(string? json)
        => BuildSequenceText(ParseSequence(json));

    public static string BuildSequenceText(FightSequencePlan? sequence)
    {
        if (sequence == null || sequence.Phases.Count == 0) return "";
        var sb = new StringBuilder();
        sb.AppendLine(sequence.ArcName + "（" + sequence.ArcType + "）· 总时长 " +
            (sequence.TotalDurationSeconds > 0 ? sequence.TotalDurationSeconds + "s" : "按单元时长") +
            (string.IsNullOrWhiteSpace(sequence.Winner) ? "" : " · 胜负落点:" + sequence.Winner));
        foreach (var p in sequence.Phases.OrderBy(p => p.PhaseNo))
        {
            sb.AppendLine($"  {p.PhaseNo}. {p.Name}（{p.DurationPercent}%）: " +
                (string.IsNullOrWhiteSpace(p.Purpose) ? "" : p.Purpose + "；") +
                "景别:" + (string.IsNullOrWhiteSpace(p.ShotStyle) ? "按镜头" : p.ShotStyle) +
                "；运镜:" + (string.IsNullOrWhiteSpace(p.Camera) ? "按镜头" : p.Camera) +
                "；VFX:" + p.VfxLevel + "%" +
                (string.IsNullOrWhiteSpace(p.Dialogue) ? "" : "；台词:" + p.Dialogue) +
                (string.IsNullOrWhiteSpace(p.EndState) ? "" : "；结束状态:" + p.EndState));
        }
        sb.AppendLine("  分镜要求：镜头按阶段顺序推进，禁止跳段；后一镜接前一镜结束状态；VFX 峰值只能出现在大招/对轰阶段。");
        return sb.ToString().TrimEnd();
    }

    public static bool HasValidPercentSum(FightSequencePlan? sequence)
    {
        if (sequence == null || sequence.Phases.Count == 0) return false;
        return sequence.Phases.Sum(p => p.DurationPercent) == 100;
    }

    /// <summary>百分比总和偏差在 5 个点内时，把差值并入最后一个阶段，避免整体回退。</summary>
    public static FightSequencePlan? NormalizePercentSum(FightSequencePlan? sequence)
    {
        if (sequence == null || sequence.Phases.Count == 0) return sequence;
        var sum = sequence.Phases.Sum(p => p.DurationPercent);
        if (sum == 100) return sequence;
        var diff = 100 - sum;
        if (sum <= 0 || Math.Abs(diff) > 5) return sequence;
        sequence.Phases[^1].DurationPercent += diff;
        return sequence;
    }

    /// <summary>把骨架阶段秒数按单元总时长重新分配，避免 5 秒单元还写 6-27 秒阶段。</summary>
    public static FightSequencePlan? FitToDuration(FightSequencePlan? sequence, int duration)
    {
        if (sequence == null || sequence.Phases.Count == 0) return sequence;
        var total = duration is 5 or 11 or 15 ? duration : 11;
        sequence.TotalDurationSeconds = total;
        var sum = sequence.Phases.Sum(p => p.DurationPercent);
        if (sum <= 0) return sequence;
        foreach (var phase in sequence.Phases)
        {
            var ratio = phase.DurationPercent / 100.0;
            var min = Math.Max(1, (int)Math.Floor(ratio * total));
            var max = Math.Max(min, (int)Math.Ceiling(ratio * total));
            phase.MinSeconds = min;
            phase.MaxSeconds = max;
        }
        return sequence;
    }

    public static FightSequencePlan? BuildFallbackSequence(StageUnit unit, string? arcType, IEnumerable<FightArcTemplate>? templates)
    {
        var normalized = NormalizeArcTypeId(arcType);
        if (string.IsNullOrWhiteSpace(normalized))
            normalized = PickArcType(unit);

        var template = (templates ?? Enumerable.Empty<FightArcTemplate>())
            .FirstOrDefault(t => string.Equals(t.ArcTypeId, normalized, StringComparison.OrdinalIgnoreCase));

        var duration = unit.Duration is 5 or 11 or 15 ? unit.Duration : 11;
        if (template != null && template.Phases.Count > 0)
        {
            return new FightSequencePlan
            {
                ArcType = normalized,
                ArcName = template.Name,
                TotalDurationSeconds = duration,
                Winner = unit.EndState,
                Phases = template.Phases.OrderBy(p => p.PhaseNo).Select(ClonePhase).ToList()
            };
        }

        return new FightSequencePlan
        {
            ArcType = normalized,
            ArcName = NormalizeArcName(normalized),
            TotalDurationSeconds = duration,
            Winner = unit.EndState,
            Phases =
            [
                new FightArcPhase
                {
                    PhaseNo = 1, Name = "对峙进场", Purpose = "建立双方身份与距离",
                    DurationPercent = 15, MinSeconds = 1, MaxSeconds = 2, ShotCount = 1,
                    ShotStyle = "特写/双人同框", Camera = "缓推", VfxLevel = 10,
                    EndState = "双方进入战斗距离", NextCondition = "对话或直接开打"
                },
                new FightArcPhase
                {
                    PhaseNo = 2, Name = "攻防交锋", Purpose = "多回合连续攻防",
                    DurationPercent = 40, MinSeconds = 2, MaxSeconds = 5, ShotCount = 2,
                    ShotStyle = "快切中近景", Camera = "手持跟拍/快切", VfxLevel = 35,
                    EndState = "一方露出破绽或压制对手", NextCondition = "进入升级或收招"
                },
                new FightArcPhase
                {
                    PhaseNo = 3, Name = "升级/大招", Purpose = "蓄力并释放技能",
                    DurationPercent = 25, MinSeconds = 1, MaxSeconds = 3, ShotCount = 1,
                    ShotStyle = "拉开全景", Camera = "拉高/定格", VfxLevel = 80,
                    EndState = "技能命中，冲击波扩散", NextCondition = "分出胜负"
                },
                new FightArcPhase
                {
                    PhaseNo = 4, Name = "胜负收束", Purpose = "命中定格并留情绪余韵",
                    DurationPercent = 20, MinSeconds = 1, MaxSeconds = 3, ShotCount = 1,
                    ShotStyle = "特写/远景", Camera = "定格/缓拉", VfxLevel = 50,
                    EndState = unit.EndState, NextCondition = "切到下一单元"
                }
            ]
        };
    }

    public static string NormalizeArcName(string arcTypeId) => arcTypeId switch
    {
        FullDuel => "完整对决",
        Encounter => "遭遇战",
        Assassination => "偷袭/速杀",
        SiegeBreakout => "围杀/突围",
        Chase => "追逐战",
        DramaConfrontation => "文戏对峙",
        _ => arcTypeId
    };

    private static FightArcPhase ClonePhase(FightArcPhase p) => new()
    {
        PhaseNo = p.PhaseNo,
        Name = p.Name,
        Purpose = p.Purpose,
        DurationPercent = p.DurationPercent,
        MinSeconds = p.MinSeconds,
        MaxSeconds = p.MaxSeconds,
        ShotCount = p.ShotCount,
        ShotStyle = p.ShotStyle,
        Camera = p.Camera,
        VfxLevel = p.VfxLevel,
        Skills = p.Skills?.ToList() ?? new List<string>(),
        Dialogue = p.Dialogue,
        EndState = p.EndState,
        NextCondition = p.NextCondition
    };

    private static bool ContainsAny(string text, params string[] candidates)
        => candidates.Any(c => text.Contains(c, StringComparison.OrdinalIgnoreCase));

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
}
