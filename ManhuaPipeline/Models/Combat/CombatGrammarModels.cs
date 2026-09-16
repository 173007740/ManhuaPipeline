namespace ManhuaPipeline.Models.Combat;

public class CombatAction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Attack";
    public string SubCategory { get; set; } = "";
    public List<string> AllowedDistances { get; set; } = [];
    public List<string> RequireEnemyStates { get; set; } = [];
    public List<string> RequireSelfStates { get; set; } = [];
    public string EnemyResultState { get; set; } = "Neutral";
    public string SelfResultState { get; set; } = "Neutral";
    public string ResultDistance { get; set; } = "";
    public List<string> Tiers { get; set; } = [];
    public int Weight { get; set; } = 50;
    public List<string> NextActions { get; set; } = [];
    public string Description { get; set; } = "";
    public string WeaponType { get; set; } = "";
    public string Element { get; set; } = "";
    public int DestructionLevel { get; set; }
    public bool IsReaction { get; set; }
}

public class FighterState
{
    public string State { get; set; } = "Neutral";
    public string Weapon { get; set; } = "";
    public string Style { get; set; } = "";
    public int Energy { get; set; } = 100;
    public int Injury { get; set; }
}

public class CombatState
{
    public string Distance { get; set; } = "Mid";
    public FighterState Self { get; set; } = new();
    public FighterState Enemy { get; set; } = new();
    public string Advantage { get; set; } = "Even";
    public int BeatIndex { get; set; }
}

public class CombatBeat
{
    public int Index { get; set; }
    public CombatAction Action { get; set; } = new();
    public string BeforeDistance { get; set; } = "";
    public string AfterDistance { get; set; } = "";
    public string SelfBefore { get; set; } = "";
    public string SelfAfter { get; set; } = "";
    public string EnemyBefore { get; set; } = "";
    public string EnemyAfter { get; set; } = "";
    public string Description { get; set; } = "";

    // ===== V1.5 导演节拍字段：分镜必须逐拍覆盖 ======
    public string GrammarId { get; set; } = "";
    public string AttackerId { get; set; } = "";
    public List<string> TargetIds { get; set; } = [];
    public string ActionType { get; set; } = "";
    public string ActionDescription { get; set; } = "";
    public string DefenseResponse { get; set; } = "";
    public string Result { get; set; } = "";
    public string SpatialRelation { get; set; } = "";
    public string Intensity { get; set; } = "";
    public bool IsClimaxBeat { get; set; }
}

public class CombatSequence
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Tier { get; set; } = "T2";
    public string Intent { get; set; } = "Pressure";
    public string Style { get; set; } = "BodyCultivator";
    public List<CombatBeat> Beats { get; set; } = [];
    public CombatState? InitialState { get; set; }
    public CombatState? FinalState { get; set; }
}

public class CombatRequest
{
    public string Tier { get; set; } = "T2";
    public string CombatType { get; set; } = "Melee";
    public string Intent { get; set; } = "Pressure";
    public string Style { get; set; } = "BodyCultivator";
    public string Result { get; set; } = "DominantVictory";
    public int BeatCount { get; set; } = 8;
    public string InitialDistance { get; set; } = "Mid";
    public string Environment { get; set; } = "宗门演武场";
    public string? SelfState { get; set; }
    public string? EnemyState { get; set; }
    /// <summary>Offensive / Defensive / Standoff，控制动作链是否允许压制、终结与近身连招。</summary>
    public string EndMode { get; set; } = "Offensive";
    /// <summary>允许进入动作链的技能动作白名单；为空时禁止所有技能类动作。</summary>
    public List<string> AllowedSkillActionIds { get; set; } = [];
}

public class CombatStyle
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, int> CategoryWeights { get; set; } = new();
    public Dictionary<string, int> ActionWeights { get; set; } = new();
    public List<string> ForbiddenSkills { get; set; } = [];
}

public class CombatPreset
{
    public string Id { get; set; } = "";
    public string Tier { get; set; } = "";
    public int MinBeat { get; set; } = 3;
    public int MaxBeat { get; set; } = 5;
    public Dictionary<string, int> CategoryWeights { get; set; } = new();
    public List<string> PreferredIntents { get; set; } = [];
    public List<string> ForbiddenCategories { get; set; } = [];
}
