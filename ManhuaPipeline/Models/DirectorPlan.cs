namespace ManhuaPipeline.Models;

/// <summary>
/// 分集细化（Unit）与分镜脚本之间的导演决策层数据。
/// LLM 负责导演判断字段，角色/技能/资产等事实由系统在提示词中给定。
/// </summary>
public class DirectorPlan
{
    public int DirectorPlanId { get; set; }
    public int ProjectId { get; set; }
    public int EpisodeNumber { get; set; }
    public string UnitNumber { get; set; } = "";
    public string UnitType { get; set; } = "";

    // 戏剧目的
    public string DramaticPurpose { get; set; } = "";

    // 当前单元核心主体
    public string PrimarySubject { get; set; } = "";

    // 当前单元次要主体
    public string SecondarySubject { get; set; } = "";

    // 1V1 / 1VN / NVN / NonCombat
    public string ConflictType { get; set; } = "NonCombat";

    // 核心爽点
    public string CorePayoff { get; set; } = "";

    // 情绪变化，箭头或顿号分隔
    public string EmotionCurve { get; set; } = "";

    // 节奏设计
    public string RhythmStrategy { get; set; } = "";

    // 动作导演意图（非打斗单元可为调度/站位层面）
    public string ActionStrategy { get; set; } = "";

    // V1.5 结构化动作计划（JSON 序列化的 DirectorActionPlan），旧 ActionStrategy 保留兼容
    public string ActionPlan { get; set; } = "";

    // 战斗段落骨架类型：full_duel / encounter / assassination / siege_breakout / chase / drama_confrontation
    public string FightArcType { get; set; } = "";

    // 战斗段落总谱（JSON 序列化的 FightSequencePlan），分镜按阶段生成镜头
    public string FightSequenceJson { get; set; } = "";

    // 表演导演意图
    public string PerformanceStrategy { get; set; } = "";

    // 摄影导演意图
    public string CameraStrategy { get; set; } = "";

    // 特效导演意图：给多少、什么时候给、什么时候收
    public string VfxStrategy { get; set; } = "";

    // 当前单元高潮等级 1-10
    public int IntensityLevel { get; set; } = 3;

    // 后期 Combat Grammar 使用，逗号分隔 ID
    public string CombatGrammarIds { get; set; } = "";

    // 本单元要求的攻防回合数，打斗/高潮/对决单元必填
    public int CombatRoundCount { get; set; }

    // VFX 峰值所在段：Early / Mid / Late / None
    public string VfxPeakPhase { get; set; } = "";

    // Director V2 校验结果
    public bool NeedsReview { get; set; }
    public int ValidationScore { get; set; }
    public string ViolationsJson { get; set; } = "";
    public int RepairCount { get; set; }
    public DateTime? LastValidationAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}
