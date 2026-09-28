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
            "Surround", "主角在包围圈中持续转身换面，正面来招用前臂硬架、侧翼来招用跨步侧身让过、身后偷袭用回身短击逼退，脚步不停在圈内游走避免被钉死",
            "完整态：对方多人同步压近，层层封堵退路；崩防：正面被架开后侧翼立刻补位，主角活动空间被压至圈心；过渡态：合拢节奏出现半拍空档，未能立刻收口",
            "合围形成，主角压力上升但未被击穿", "被围困在中央", "中", false),

        new("T1_DODGE_COUNTER", "闪避反击",
            "DodgeCounter", "主角重心骤沉，上半身大幅下潜侧倒让开攻击线，脚下横向跨步切出攻击范围；随即借回弹之势拧身，肘或拳自下而上砸向对方肋下与下颚；接触点对方身体被顶得离地失衡；收招后主角不停步，绕至对方侧面",
            "完整态：对方攻势沿原轨迹扑空；崩防：收势不及被抓住空档，门户大开；过渡态：踉跄转身，重心失守",
            "对方攻势被化解，主角抢占侧面位", "近身交错", "低", false),

        new("T2_CLOSE_COUNTER", "近身反打",
            "CloseCounter", "主角贴身切入对方内线，肩撞压住对方重心，肘击自内向外砸向胸腹；借对方后仰之势回身，短促勾拳追击下颚；接触点对方胸腹受压凹陷、上身折起；收招时主角贴身不退，占据中线",
            "完整态：对方双臂下压招架；崩防：被贴身顶住，无法发力也无法退步；过渡态：上身被迫后折，脚步凌乱",
            "最近的目标被击退，其余人被迫补位", "贴身缠斗", "中", false),

        new("T4_AOE_BREAK", "范围破局",
            "AoeBreak", "主角沉腰蓄力至极，力量自脚下爆发，双拳或双臂向外猛然撑开；一圈可见的冲击自接触点向四面八方炸开，周围目标同时被掀离地面向外翻滚飞退；地面随冲击龟裂，碎石与尘土呈环状掀起；收招后主角维持发力前倾姿态，脚下裂纹继续蔓延",
            "完整态崩：周围目标同时被冲击波正面命中；崩防：多人被掀飞、相互撞在一起；过渡态：落地后翻滚数圈，挣扎难起",
            "敌方群体被震退失衡，短时间内无法重新合围", "以主角为中心扩散", "高", true),

        new("T1_QUICK_STRIKE", "快攻试探",
            "QuickStrike", "主角后脚蹬地拧腰转胯，肩背发力推拳压向对方面门与心口，拳锋破空带出可见轨迹；接触瞬间对方头部与上半身后仰震颤，双方脚下错步换位；收招时拳锋回拉蓄势，身形不停直接衔接下一击",
            "完整态：对方架双臂格挡，防线完整；崩防：双臂被连续冲击震开，中线大开；过渡态：碎步踉跄后撤，重心失守",
            "对方被压退数步，防御节奏被打乱", "中距离对攻", "低", false),

        new("T2_GRAPPLE_COUNTER", "擒拿反制",
            "GrappleCounter", "主角错步让开来袭手腕，双手顺势扣住对方腕部与小臂，借其前冲之力向下拧转带动重心前倾；肩顶住对方腋下将整条手臂锁死；对方被反关节压得屈膝矮身；收招时主角保持锁扣不放，借势将其甩向地面",
            "完整态：对方试图抽手回拉；崩防：腕部被锁，重心被带走；过渡态：屈膝矮身，被迫跪地或翻滚卸力",
            "对方攻势中断，被压制在地面或半跪", "贴身纠缠", "中", false),

        new("T3_PIN_DOWN", "定点压制",
            "PinDown", "主角锁定单一目标持续进逼，第一击压其防守、第二击追其退路，肩撞加肘击连压不让其转身，用身体重量压住对方重心使其无法起势",
            "完整态：对方勉强抬臂招架；崩防：被连续冲击压得抬不起手；过渡态：被逼至墙角或地面，只能蜷身护住要害",
            "目标失去反击能力，其余人被迫救援", "近身压制", "中", false),

        new("T5_FINAL_BREAK", "终结破局",
            "FinalBreak", "主角将所有力量压向最后一击，蹬地腾身或蓄势至极后全力贯入，攻击轨迹带出贯穿性拖尾；命中瞬间接触点炸开环形冲击，对方被整个掀飞、撞穿地形后嵌进岩壁或砸入地面；收招后主角维持出招惯性前倾，余波持续扩散",
            "完整态碎：对方最后的防御被正面贯穿；崩防：身体被击飞离地，护身姿态崩散；过渡态：落地后嵌入地形不再动弹",
            "战斗结束，胜负已定", "全空间覆盖", "极高", true),

        new("T2_RETREAT_HOLD", "且战且退",
            "RetreatHold", "主角前脚蹬地后撤拉开距离，撤步间前手不停格挡与拨开来招，后手短击逼停追击者；每退数步用一次回身重击打断追击节奏，始终保持面向敌人不背身",
            "完整态：对方紧追不舍，连续进招；崩防：追击被回身重击逼停，前冲惯性被打断；过渡态：被迫减速重整，无法贴身",
            "双方僵持，主角保持距离观察阵型", "拉开距离", "低", false),

        new("T3_AMBUSH_BREAK", "突围反杀",
            "AmbushBreak", "主角锁定包围圈最薄弱的一环，沉身蓄力后爆发突进，肩撞硬开缺口；随即反手重击砸向补位者，借其飞退之势撕开通路；收招不停，连续两击扩大缺口后冲出包围",
            "完整态：薄弱处两人举臂试图封堵；崩防：被撞得横向失衡，合拢中断；过渡态：补位者被反击命中后仰飞退",
            "包围被击穿，主角突围成功", "包围圈边缘", "高", true)
    ];

    public static CombatGrammarPattern? Get(string id) =>
        Patterns.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsKnown(string id) => Get(id) != null;

    public static string BuildLibraryText()
    {
        return string.Join("\n", Patterns.Select(p => $"- {p.Id} {p.Name}：{p.ActionDescription}"));
    }
}
