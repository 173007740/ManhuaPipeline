namespace ManhuaPipeline.Models;

/// <summary>
/// V1.5 结构化动作计划：导演只决定“怎么打”的骨架（冲突关系、回合数、
/// 压制曲线、Grammar 选段），具体 CombatBeat 由 CombatGrammarEngine 展开。
/// </summary>
public class DirectorActionPlan
{
    // 1V1 / 1VN / NVN
    public string ConflictType { get; set; } = "1V1";

    public string PrimaryFighterId { get; set; } = "";

    public List<string> EnemyIds { get; set; } = [];

    // 碾压 / 苦战 / 反杀 / 追逃 / 压迫后碾压
    public string CombatStyle { get; set; } = "";

    // 期望回合数，V1.5 轻量校验只保证不丢 Beat，V2 再核对回合
    public int RoundCount { get; set; } = 1;

    // 敌强→均势→主角碾压
    public string DominanceCurve { get; set; } = "";

    // 只能从 CombatGrammarCatalog 里选，禁止自创 ID
    public List<string> CombatGrammarIds { get; set; } = [];

    // 击退 / 击倒 / 击杀 / 中断
    public string EndingState { get; set; } = "";
}
