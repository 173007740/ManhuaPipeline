namespace ManhuaPipeline.Models;

/// <summary>
/// Director V3：整集导演计划。在全局蓝图之后、Unit DirectorPlan 之前生成，
/// 为整集分配强度曲线、爽点预算、保留视觉与重复套路预算。
/// </summary>
public class EpisodeDirectorPlan
{
    public int EpisodeDirectorPlanId { get; set; }
    public int ProjectId { get; set; }
    public int EpisodeNumber { get; set; }

    public string EpisodeGoal { get; set; } = "";
    public string EmotionCurve { get; set; } = "";

    // UnitDirector 必须服从的强度上限
    public List<UnitIntensityPlan> IntensityCurve { get; set; } = new();

    // 本集爽点释放顺序，后置爽点禁止提前释放
    public List<PayoffPoint> PayoffSchedule { get; set; } = new();

    // 全局视觉预算：禁止使用，或仅指定单元可用
    public List<ReservedVisual> ReservedVisuals { get; set; } = new();

    // 明确禁止提前出现的核心爆点画面
    public List<string> ForbiddenEarlyPayoffs { get; set; } = new();

    // 本集镜头/动作/特效套路重复上限
    public RepetitionPolicy RepetitionPolicy { get; set; } = new();

    // Director V4：本集总导演的 Unit 情绪曲线
    public List<UnitEmotionPlan> UnitEmotionCurve { get; set; } = new();

    // Director V4：Unit 与 Unit 之间的交接协议
    public List<UnitTransition> UnitTransitions { get; set; } = new();

    // Director V4：每个 Unit 结束时应形成的标准化状态快照
    public List<UnitEndState> UnitEndStates { get; set; } = new();

    // Director V4：本集小/中/大高潮预算
    public ClimaxBudget ClimaxBudget { get; set; } = new();

    public string RawJson { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
}

public class UnitIntensityPlan
{
    public string UnitNumber { get; set; } = "";
    public int IntensityLimit { get; set; } = 3;
}

public class PayoffPoint
{
    public string UnitNumber { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
}

public class ReservedVisual
{
    public string Visual { get; set; } = "";
    public string ReservedUnit { get; set; } = "";

    // 允许"禁止使用"或"仅 Unit07 可用"这类说明，用于调试与日志
    public string Note { get; set; } = "";
}

public class RepetitionPolicy
{
    public int SlowMotionLimit { get; set; } = 2;
    public int MajorExplosionLimit { get; set; } = 1;

    public List<PatternLimit> CameraPatternLimits { get; set; } = new();
    public List<PatternLimit> CombatPatternLimits { get; set; } = new();
    public List<PatternLimit> VfxPatternLimits { get; set; } = new();
}

public class PatternLimit
{
    public string Pattern { get; set; } = "";
    public int MaxCount { get; set; } = 1;
}

public class UnitEmotionPlan
{
    public string UnitNumber { get; set; } = "";
    public string Emotion { get; set; } = "";
    public int Level { get; set; } = 3;
    public string DirectingNote { get; set; } = "";
}

public class UnitTransition
{
    public string FromUnit { get; set; } = "";
    public string ToUnit { get; set; } = "";
    public string TransitionType { get; set; } = "EmotionCarry";
    public string CarryEmotion { get; set; } = "";
    public string NarrativeCarry { get; set; } = "";
    public string CameraDirection { get; set; } = "";
    public string StartState { get; set; } = "";
    public string EndState { get; set; } = "";
}

public class UnitEndState
{
    public string UnitNumber { get; set; } = "";
    public Dictionary<string, CharacterState> Characters { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string EnvironmentState { get; set; } = "";
    public string CameraDirection { get; set; } = "";
    public string ActiveVfxState { get; set; } = "";
    public string LastActionState { get; set; } = "";
    public string EmotionalCarry { get; set; } = "";
    public string NarrativeCarry { get; set; } = "";
}

public class CharacterState
{
    public string Position { get; set; } = "";
    public string Facing { get; set; } = "";
    public string Pose { get; set; } = "";
    public string Appearance { get; set; } = "";
    public string Injury { get; set; } = "";
    public string Weapon { get; set; } = "";
    public string SkillState { get; set; } = "";
}

public class ClimaxBudget
{
    public int SmallClimaxLimit { get; set; } = 2;
    public int MidClimaxLimit { get; set; } = 1;
    public int LargeClimaxLimit { get; set; } = 1;
    public List<ReservedVisual> ReservedVisuals { get; set; } = new();
    public string Notes { get; set; } = "";
}
