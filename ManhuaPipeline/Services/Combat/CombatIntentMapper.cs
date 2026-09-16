using System.Text;
using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;

namespace ManhuaPipeline.Services.Combat;

public static class CombatIntentMapper
{
    public static CombatRequest ToCombatRequest(CombatIntent intent, StageUnit? unit = null)
    {
        var duration = intent.Duration is 5 or 11 or 15 ? intent.Duration : 11;
        var beatCount = duration switch
        {
            5 => 12,
            15 => 40,
            _ => 24
        };

        var endMode = DetectEndMode(intent, unit);
        if (endMode == "Standoff") beatCount = Math.Min(beatCount, 4);

        return new CombatRequest
        {
            Tier = NormalizeTier(intent.Intensity),
            CombatType = IsGroupCombat(intent) ? "GroupMelee" : "Melee",
            Intent = InferIntent(intent),
            Style = InferStyle(intent),
            Result = string.IsNullOrWhiteSpace(intent.Result) ? "DominantVictory" : intent.Result.Trim(),
            BeatCount = beatCount,
            InitialDistance = InferDistance(intent, unit),
            Environment = unit?.Location ?? intent.EnvironmentType ?? "",
            EndMode = endMode
        };
    }

    /// <summary>根据单元原文和意图结果判断结尾模式：Standoff=对峙蓄势，Defensive=受击/被围/战败/硬抗，Offensive=正常压制。</summary>
    public static string DetectEndMode(CombatIntent intent, StageUnit? unit = null)
    {
        var parts = new List<string>
        {
            intent.CombatForm,
            intent.Objective,
            intent.Result,
            intent.StartState,
            intent.EndState,
            intent.EnvironmentType
        };
        parts.AddRange(intent.RequiredSkills);
        parts.AddRange(intent.RequiredActions);
        parts.AddRange(intent.Participants.Select(p => p.Name + " " + p.Weapon));
        if (unit != null)
        {
            parts.Add(unit.CoreAction);
            parts.Add(unit.StartState);
            parts.Add(unit.EndState);
            parts.Add(unit.RawText);
        }

        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        if (ContainsAny(text,
                "对峙", "蓄势", "宣判", "宣示", "开口", "怒斥", "识破", "抬拳",
                "警告", "质问", "宣言", "握拳", "站定", "现身", "叫阵", "威慑", "威压", "审判", "宣告", "注视", "紧盯", "凝视"))
            return "Standoff";

        if (ContainsAny(text,
                "被围", "围困", "围杀", "围攻", "受击", "被轰", "被击", "崩碎", "碎裂", "战败", "败亡",
                "战死", "牺牲", "陨落", "失声", "硬抗", "撑住", "无法", "重伤", "坠落", "吐血", "被困",
                "困于", "镇杀", "封死", "锁成", "死域", "轰向", "轰落", "同时落下"))
            return "Defensive";

        return "Offensive";
    }

    public static string BuildLockedActionChain(CombatSequence sequence)
    {
        if (sequence?.Beats == null || sequence.Beats.Count == 0)
            return "";

        var sb = new StringBuilder();
        foreach (var beat in sequence.Beats)
        {
            sb.AppendLine(
                $"{beat.Index:00} {beat.Action.Name}（{beat.BeforeDistance}→{beat.AfterDistance}，敌方 {beat.EnemyBefore}→{beat.EnemyAfter}）");
        }
        return sb.ToString().TrimEnd();
    }

    public static bool IsMovementCombat(CombatIntent intent, StageUnit? unit = null)
    {
        var parts = new List<string>
        {
            intent.CombatForm,
            intent.Objective,
            intent.Result,
            intent.StartState,
            intent.EndState,
            intent.EnvironmentType,
            unit?.CoreAction ?? "",
            unit?.RawText ?? ""
        };
        parts.AddRange(intent.RequiredSkills);
        parts.AddRange(intent.RequiredActions);

        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return ContainsAny(text,
            "轻功", "位移", "赶路", "奔行", "飞掠", "纵跃", "腾挪", "疾行", "飞遁", "穿梭",
            "追击", "追敌", "逃跑", "逃离", "脱离", "撤退", "撤离", "脱身", "拉开距离",
            "一步踏出", "越过", "跃过", "奔向", "赶往", "掠向", "踏天步");
    }

    public static bool IsSoloCombat(CombatIntent intent, StageUnit? unit = null)
    {
        if (intent == null || intent.Participants.Count != 1) return false;

        var parts = new List<string>
        {
            intent.CombatForm,
            intent.Objective,
            intent.Result,
            intent.StartState,
            intent.EndState,
            intent.EnvironmentType,
            unit?.CoreAction ?? "",
            unit?.RawText ?? ""
        };
        parts.AddRange(intent.RequiredSkills);
        parts.AddRange(intent.RequiredActions);

        var text = string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
        return !ContainsAny(text, "多人", "围攻", "围杀", "群战", "混战", "敌人", "敌方", "对手", "追兵", "一群", "数人", "众多", "围困", "被围", "合围", "围剿", "围堵", "包围", "四面");
    }

    /// <summary>只有明确的一招秒杀/碾压才允许 5 秒战斗镜头。</summary>
    public static bool IsQuickKill(CombatIntent intent)
    {
        var text = BuildContext(intent);
        return ContainsAny(text, "秒杀", "碾压", "一招", "秒掉", "瞬杀", "一击", "斩杀", "秒败", "秒了", "秒级", "一拳解决", "一击解决", "秒决", "干脆利落");
    }

    private static string NormalizeTier(int intensity)
    {
        var tier = Math.Clamp(intensity, 1, 5);
        return "T" + tier;
    }

    private static bool IsGroupCombat(CombatIntent intent)
    {
        if (intent.Participants.Count > 2) return true;
        return ContainsAny(BuildContext(intent), "群战", "围攻", "围杀", "混战", "多人", "数人", "一群");
    }

    private static string InferIntent(CombatIntent intent)
    {
        var text = BuildContext(intent);
        if (ContainsAny(text, "终结", "击杀", "击溃", "击败", "解决", "斩杀", "收招")) return "Finish";
        if (ContainsAny(text, "破防", "护体", "防御", "攻破", "硬碰", "破阵")) return "BreakGuard";
        if (ContainsAny(text, "反击", "防守", "格挡", "反制", "招架")) return "Counter";
        if (ContainsAny(text, "逃跑", "脱离", "撤退", "撤离", "拉开距离", "脱身")) return "Escape";
        if (ContainsAny(text, "压制", "碾压", "强势", "围攻", "镇压")) return "Pressure";
        return "Pressure";
    }

    private static string InferStyle(CombatIntent intent)
    {
        var participantText = string.Join(" ", intent.Participants
            .Select(p => p.Name + " " + p.Weapon));
        if (ContainsAny(participantText, "剑", "刀", "枪", "戟", "兵刃", "刃"))
            return "SWORD_CULTIVATOR";

        var formText = (intent.CombatForm ?? "") + " " + string.Join(" ", intent.RequiredActions);
        if (ContainsAny(participantText, "拳", "掌", "腿", "脚", "肉身", "体修", "气血") ||
            ContainsAny(formText, "拳", "掌", "腿", "脚", "肉身", "体修", "气血", "肉搏", "近战"))
            return "BODY_CULTIVATOR";

        var text = BuildContext(intent);
        if (ContainsAny(text, "法", "术", "符", "咒", "雷", "火", "冰", "风", "灵",
                "远程", "对轰", "法相", "法术", "领域"))
            return "MAGE";

        return "BODY_CULTIVATOR";
    }

    private static string InferDistance(CombatIntent intent, StageUnit? unit)
    {
        var startState = intent.StartState + " " + (unit?.StartState ?? "");
        var text = intent.CombatForm + " " + startState;
        if (ContainsAny(text, "远程", "对轰", "法术", "远距离", "相距甚远", "拉开距离"))
            return "Long";
        if (ContainsAny(text, "贴身", "缠斗", "近身", "擒拿", "摔投", "纠缠", "搂抱"))
            return "Clinch";
        if (ContainsAny(text, "突进", "逼近", "冲锋", "近战", "短兵相接", "交手"))
            return "Close";
        if (ContainsAny(text, "对峙", "中距离", "试探", "绕圈"))
            return "Mid";
        return "Mid";
    }

    private static string BuildContext(CombatIntent intent)
    {
        var parts = new List<string>
        {
            intent.CombatForm,
            intent.Objective,
            intent.Result,
            intent.StartState,
            intent.EndState,
            intent.EnvironmentType
        };
        parts.AddRange(intent.RequiredSkills);
        parts.AddRange(intent.RequiredActions);
        parts.AddRange(intent.Participants.Select(p => p.Name + " " + p.Weapon));
        return string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase));
}
