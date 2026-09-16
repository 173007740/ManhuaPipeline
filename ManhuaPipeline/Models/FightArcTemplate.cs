namespace ManhuaPipeline.Models;

/// <summary>
/// 战斗段落骨架模板：一整场打斗的阶段结构、时长预算与规则。
/// 模板以数据维护，导演层读取后按单元输出 FightSequencePlan。
/// </summary>
public class FightArcTemplate
{
    public int FightArcTemplateId { get; set; }
    public string ArcTypeId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string Version { get; set; } = "";
    public List<FightArcPhase> Phases { get; set; } = new();
    public FightDurationBudget DurationBudget { get; set; } = new();
    public List<string> Rules { get; set; } = new();
    public string Status { get; set; } = "Active";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

/// <summary>段落总时长预算：秒数区间 + 5/11/15 镜头档位。</summary>
public class FightDurationBudget
{
    public int MinSeconds { get; set; }
    public int MaxSeconds { get; set; }
    public int DefaultSeconds { get; set; }
    public List<int> ShotTiers { get; set; } = new() { 5, 11, 15 };
}

/// <summary>战斗段落中的单个阶段，导演层锁定后交给分镜按阶段生成镜头。</summary>
public class FightArcPhase
{
    public int PhaseNo { get; set; }
    public string Name { get; set; } = "";
    public string Purpose { get; set; } = "";
    public int DurationPercent { get; set; }
    public int MinSeconds { get; set; }
    public int MaxSeconds { get; set; }
    public int ShotCount { get; set; }
    public string ShotStyle { get; set; } = "";
    public string Camera { get; set; } = "";
    public int VfxLevel { get; set; }
    public List<string> Skills { get; set; } = new();
    public string Dialogue { get; set; } = "";
    public string EndState { get; set; } = "";
    public string NextCondition { get; set; } = "";
}

/// <summary>导演层针对单个单元输出的战斗段落总谱，序列化后存入 DirectorPlan。</summary>
public class FightSequencePlan
{
    public string ArcType { get; set; } = "";
    public string ArcName { get; set; } = "";
    public int TotalDurationSeconds { get; set; }
    public string Winner { get; set; } = "";
    public List<FightArcPhase> Phases { get; set; } = new();
}
