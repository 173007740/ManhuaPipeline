namespace ManhuaPipeline.Services.Combat;

/// <summary>
/// V1.5 战斗语法库：导演只能从这里选 Grammar ID，禁止自创。
/// CombatGrammarEngine 负责把选中的 Grammar 展开成具体 CombatBeat。
/// </summary>
public sealed record CombatGrammarPattern(
    string Id,
    string Name,
    string ActionType,
    string ActionDescription,
    string DefenseResponse,
    string Result,
    string SpatialRelation,
    string Intensity,
    bool IsClimax);

public static class CombatGrammarCatalog
{
    public static IReadOnlyList<CombatGrammarPattern> Patterns { get; } =
    [
        new("T3_SURROUND_ATTACK", "合围压迫",
            "Surround", "敌方多人形成包围，正面压进、侧翼夹击、后方封路", "主角暂不反击，保持核心站位", "合围形成，压力上升", "被围困在中央", "中", false),

        new("T1_DODGE_COUNTER", "闪避反击",
            "DodgeCounter", "主角侧身闪过正面攻击，借位避开侧翼夹击", "敌方攻击落空，攻势出现间隙", "敌方攻势被化解", "近身交错", "低", false),

        new("T2_CLOSE_COUNTER", "近身反打",
            "CloseCounter", "主角近身切入，肘击最近敌人，回身格挡第二人并短促反击", "敌方短暂硬直，其余敌人被逼退半步", "击退最近的敌人", "贴身缠斗", "中", false),

        new("T4_AOE_BREAK", "范围破局",
            "AoeBreak", "主角爆发气血/法光，范围冲击震开全部敌人", "敌方群体被震退/失衡，无法立刻再合围", "敌方群体击退", "以主角为中心扩散", "高", true),

        new("T1_QUICK_STRIKE", "快攻试探",
            "QuickStrike", "主角连续快攻试探，压缩敌方防御", "敌方格挡后退，防线出现缺口", "敌方被压退", "中距离对攻", "低", false),

        new("T2_GRAPPLE_COUNTER", "擒拿反制",
            "GrappleCounter", "主角抓住来袭手腕，顺势反制对手重心", "敌方被抓腕失衡，攻势中断", "敌方被压制", "贴身纠缠", "中", false),

        new("T3_PIN_DOWN", "定点压制",
            "PinDown", "主角持续压制单个目标，打乱敌方配合", "目标被压制，其余敌人被迫救援", "目标失去反击能力", "近身压制", "中", false),

        new("T5_FINAL_BREAK", "终结破局",
            "FinalBreak", "主角施展终结技，彻底击溃剩余敌人", "敌方彻底失去再战能力", "战斗结束，胜负已定", "全空间覆盖", "极高", true),

        new("T2_RETREAT_HOLD", "且战且退",
            "RetreatHold", "主角边退边防，保持距离并观察敌方阵型", "敌方追击被格挡/闪避化解", "僵持对峙", "拉开距离", "低", false),

        new("T3_AMBUSH_BREAK", "突围反杀",
            "AmbushBreak", "主角识破包围薄弱点，一击撕开包围并反杀", "包围被击穿，敌方露出破绽", "突围成功", "包围圈边缘", "高", true)
    ];

    public static CombatGrammarPattern? Get(string id) =>
        Patterns.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string id) => Get(id) != null;

    public static string BuildLibraryText()
    {
        return string.Join("\n", Patterns.Select(p => $"- {p.Id} {p.Name}：{p.ActionDescription}"));
    }
}
