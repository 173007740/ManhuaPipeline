namespace ManhuaPipeline.Models;

/// <summary>
/// L2 连续性层表类型（对标 short-drama-agent 第 6-11 节）。
/// 六类连续性表统一存放在 <see cref="ProjectContinuityTable"/>，用 TableType 区分。
/// 这些表把「空间方位 / 角色状态 / 道具状态 / 线索顺序 / 动作因果 / 转场动机」
/// 落成可校验、可注入下游的数据字段，而不是留在提示词字符串里靠 LLM 记忆。
/// </summary>
public static class ContinuityTableTypes
{
    /// <summary>场景空间表：左/右/中后/前景、入口出口、危险与逃生方向、动作轴线。</summary>
    public const string SceneSpace = "SceneSpace";
    /// <summary>角色连续性表：外貌锚定 + 服装 + 随身物 + 伤势 + 禁止变化。</summary>
    public const string CharacterContinuity = "CharacterContinuity";
    /// <summary>道具状态 timeline：何时出现、何时消失、禁止状态倒退。</summary>
    public const string PropState = "PropState";
    /// <summary>线索揭示表：揭示顺序 + 观众必须注意到什么 + 禁止提前揭示。</summary>
    public const string ClueReveal = "ClueReveal";
    /// <summary>动作因果表：单元级因果 + 每单元观众获得的新信息。</summary>
    public const string ActionCausality = "ActionCausality";
    /// <summary>转场动机表：每个衔接点的动机类型与说明。</summary>
    public const string TransitionMotive = "TransitionMotive";

    /// <summary>生成顺序（与 short-drama-agent 第 6-11 节一致）。</summary>
    public static readonly string[] All =
    {
        SceneSpace, CharacterContinuity, PropState, ClueReveal, ActionCausality, TransitionMotive
    };

    public static string DisplayName(string type) => type switch
    {
        SceneSpace => "场景空间表",
        CharacterContinuity => "角色连续性表",
        PropState => "道具状态表",
        ClueReveal => "线索揭示表",
        ActionCausality => "动作因果表",
        TransitionMotive => "转场动机表",
        _ => type
    };
}

/// <summary>
/// 项目连续性表落库行（L2 连续性层）。
/// ContentJson 保存结构化字段，ContentText 保存注入下游提示词的渲染文本，
/// 两者一次生成、同时写入，避免下游每次重新渲染。
/// </summary>
public class ProjectContinuityTable
{
    public int ContinuityId { get; set; }
    public int ProjectId { get; set; }
    /// <summary>0 = 全片/整季级；&gt;0 = 该集级。</summary>
    public int EpisodeNumber { get; set; }
    /// <summary>见 <see cref="ContinuityTableTypes"/>。</summary>
    public string TableType { get; set; } = "";
    /// <summary>结构化内容 JSON，字段名与各类型强类型模型一致。</summary>
    public string ContentJson { get; set; } = "{}";
    /// <summary>渲染为注入提示词的纯文本。</summary>
    public string? ContentText { get; set; }
    /// <summary>llm = 模型抽取；manual = 人工编辑。</summary>
    public string Source { get; set; } = "llm";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;

    public string TypeName => ContinuityTableTypes.DisplayName(TableType);
}

/// <summary>场景空间表条目（<see cref="ContinuityTableTypes.SceneSpace"/>）。</summary>
public class SceneSpaceEntry
{
    /// <summary>环境资产规范名。</summary>
    public string Scene { get; set; } = "";
    public string Left { get; set; } = "";
    public string Right { get; set; } = "";
    public string MidBack { get; set; } = "";
    public string Foreground { get; set; } = "";
    public string Entrance { get; set; } = "";
    public string Exit { get; set; } = "";
    public string DangerDirection { get; set; } = "";
    public string EscapeDirection { get; set; } = "";
    /// <summary>动作轴线：主要对峙双方的站位关系（如「A 在左、B 在右」）。</summary>
    public string ActionAxis { get; set; } = "";
    /// <summary>固定道具及其位置。</summary>
    public List<string> FixedProps { get; set; } = new();
    /// <summary>禁止改变的空间关系。</summary>
    public List<string> ForbiddenChanges { get; set; } = new();
}

/// <summary>角色连续性表条目（<see cref="ContinuityTableTypes.CharacterContinuity"/>）。</summary>
public class CharacterContinuityEntry
{
    /// <summary>角色资产规范名。</summary>
    public string Character { get; set; } = "";
    /// <summary>外貌锚定：发型、服装、气质等跨镜必须一致的部分。</summary>
    public string AppearanceAnchor { get; set; } = "";
    public string Costume { get; set; } = "";
    /// <summary>随身武器 / 专属道具。</summary>
    public string WeaponOrProp { get; set; } = "";
    /// <summary>伤势状态（随剧情推进变化，必须写清在第几单元变化）。</summary>
    public string InjuryState { get; set; } = "";
    public List<string> ForbiddenChanges { get; set; } = new();
}

/// <summary>道具状态表条目（<see cref="ContinuityTableTypes.PropState"/>）。</summary>
public class PropStateTimelineEntry
{
    /// <summary>道具资产规范名。</summary>
    public string Prop { get; set; } = "";
    public List<PropStatePoint> States { get; set; } = new();
    /// <summary>必须出现的单元。</summary>
    public List<string> MustAppear { get; set; } = new();
    /// <summary>禁止出现的状态（如「落地后不得再出现在手中」）。</summary>
    public List<string> Forbidden { get; set; } = new();
}

/// <summary>道具状态时间点。</summary>
public class PropStatePoint
{
    /// <summary>发生在哪个单元（如 unit_3）。</summary>
    public string At { get; set; } = "";
    /// <summary>该时点的状态（如「持在右手」「落地」）。</summary>
    public string State { get; set; } = "";
}

/// <summary>线索揭示表条目（<see cref="ContinuityTableTypes.ClueReveal"/>）。</summary>
public class ClueRevealEntry
{
    /// <summary>线索名（关联道具 / 特效 / 环境资产规范名）。</summary>
    public string Clue { get; set; } = "";
    /// <summary>第几个被揭示。</summary>
    public int RevealOrder { get; set; }
    /// <summary>在哪个单元揭示。</summary>
    public string RevealAt { get; set; } = "";
    /// <summary>观众必须注意到的具体内容。</summary>
    public string AudienceMustNotice { get; set; } = "";
    /// <summary>禁止在此单元之前出现。</summary>
    public string MustNotRevealBefore { get; set; } = "";
}

/// <summary>动作因果表条目（<see cref="ContinuityTableTypes.ActionCausality"/>）。</summary>
public class ActionCausalityEntry
{
    public string UnitNumber { get; set; } = "";
    /// <summary>观众在这一单元获得的新信息（无新信息写「无」）。</summary>
    public string NewInformation { get; set; } = "";
    /// <summary>因为上一单元发生了什么，本单元才成立。</summary>
    public string BecauseOf { get; set; } = "";
    /// <summary>本单元的结果导向下一单元什么。</summary>
    public string LeadsTo { get; set; } = "";
}

/// <summary>转场动机表条目（<see cref="ContinuityTableTypes.TransitionMotive"/>）。</summary>
public class TransitionMotiveEntry
{
    public string FromUnit { get; set; } = "";
    public string ToUnit { get; set; } = "";
    /// <summary>声音桥 / 视线 / 动作 / 信息问题 / 空间。</summary>
    public string MotiveType { get; set; } = "";
    public string Detail { get; set; } = "";
}

/// <summary>
/// 一次连续性抽取调用的完整结果（六类表）。模型一次输出整包，
/// 由 <c>ContinuityExtractionService</c> 按类型拆分成 <see cref="ProjectContinuityTable"/> 落库。
/// </summary>
public class ContinuityExtractionResult
{
    public List<SceneSpaceEntry> SceneSpace { get; set; } = new();
    public List<CharacterContinuityEntry> CharacterContinuity { get; set; } = new();
    public List<PropStateTimelineEntry> PropState { get; set; } = new();
    public List<ClueRevealEntry> ClueReveal { get; set; } = new();
    public List<ActionCausalityEntry> ActionCausality { get; set; } = new();
    public List<TransitionMotiveEntry> TransitionMotive { get; set; } = new();

    /// <summary>六类表是否全部为空（全部为空视为抽取失败，不落库以免污染下游）。</summary>
    public bool IsEmpty
        => SceneSpace.Count == 0 && CharacterContinuity.Count == 0 && PropState.Count == 0
           && ClueReveal.Count == 0 && ActionCausality.Count == 0 && TransitionMotive.Count == 0;
}
