using ManhuaPipeline.Models.Combat;

namespace ManhuaPipeline.Services.Combat;

public static class CombatActionCatalog
{
    public static IReadOnlyList<CombatAction> Actions { get; } = BuildActions();

    public static IReadOnlyList<CombatStyle> Styles { get; } = BuildStyles();

    public static IReadOnlyList<CombatPreset> Presets { get; } = BuildPresets();

    public static CombatStyle? GetStyle(string id) =>
        Styles.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    public static CombatPreset? GetPreset(string id) =>
        Presets.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    private static CombatAction Act(
        string id,
        string name,
        string category,
        string subCategory,
        string[] distances,
        string[] requireEnemy,
        string[] requireSelf,
        string enemyResult,
        string selfResult,
        string resultDistance,
        string[] tiers,
        int weight,
        string[] next,
        string description = "",
        string weapon = "",
        string element = "",
        int destruction = 0,
        bool isReaction = false) =>
        new()
        {
            Id = id,
            Name = name,
            Category = category,
            SubCategory = subCategory,
            AllowedDistances = distances.ToList(),
            RequireEnemyStates = requireEnemy.ToList(),
            RequireSelfStates = requireSelf.ToList(),
            EnemyResultState = enemyResult,
            SelfResultState = selfResult,
            ResultDistance = resultDistance,
            Tiers = tiers.ToList(),
            Weight = weight,
            NextActions = next.ToList(),
            Description = description,
            WeaponType = weapon,
            Element = element,
            DestructionLevel = destruction,
            IsReaction = isReaction
        };

    private static List<CombatAction> BuildActions() =>
    [
        // ===== 移动 =====
        Act("MOVE_DASH_IN_001", "前冲突进", "Move", "Dash", ["Mid"], ["Neutral", "Guarding", "OffBalance", "Knockdown"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T1", "T2", "T3", "T4", "T5"], 55,
            ["ATK_JAB_001", "ATK_CROSS_001", "GRB_WRIST_001", "ATK_SHOULDER_001"], "压低重心一步抢入中距离，为下一击创造贴身空间。"),
        Act("MOVE_BACK_001", "后撤拉开", "Move", "Retreat", ["Close", "Clinch"], ["Neutral", "OffBalance"], ["Neutral", "Attack"], "Neutral", "Neutral", "Mid", ["T1", "T2", "T3", "T4"], 45,
            [], "脚步后移拉开距离，重新进入中距离对峙。"),
        Act("MOVE_SIDE_STEP_001", "侧闪", "Dodge", "Step", ["Close", "Mid"], ["Attack", "Charging", "Neutral"], ["Neutral", "Attack"], "Neutral", "Attack", "", ["T1", "T2", "T3", "T4", "T5"], 60,
            ["ATK_JAB_001", "DEF_PARRY_001", "GRB_WRIST_001", "MOVE_ARC_BEHIND_001"], "向侧面横移半步，让攻击从身侧擦过。"),
        Act("MOVE_ARC_BEHIND_001", "绕背", "Move", "Arc", ["Close"], ["Neutral", "Attack", "Guarding"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T1", "T2", "T3", "T4"], 50,
            ["GRB_WRIST_001", "ATK_ELBOW_001", "ATK_CROSS_001"], "围绕对手弧线移动，抢占背身或侧身角度。"),
        Act("MOVE_SHADOW_STEP_001", "残影突进", "Move", "Dash", ["Mid"], ["Neutral"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T2", "T3", "T4", "T5"], 70,
            ["ATK_CROSS_001", "ATK_ELBOW_001", "GRB_WRIST_001"], "身形拉出残影，短距离爆发切入贴身距离。"),
        Act("MOVE_LONG_CHARGE_001", "长途突进", "Move", "Charge", ["Long"], ["Neutral", "Stagger", "OffBalance"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T1", "T2", "T3", "T4", "T5"], 70,
            ["ATK_CROSS_001", "GRB_WRIST_001", "ATK_ELBOW_001"], "拉开架势长距离爆发冲锋，瞬间从远距压到贴身。"),
        Act("MOVE_JUMP_BACK_001", "腾空后跃", "Dodge", "Jump", ["Close"], ["Attack", "Charging"], ["Neutral", "Attack"], "Attack", "Neutral", "Mid", ["T1", "T2", "T3", "T4"], 50,
            [], "借势向斜后方腾跃，避开贴身攻势并拉开距离。"),
        Act("MOVE_BODY_PRESS_001", "贴身压进", "Move", "Press", ["Mid", "Close"], ["Neutral", "Guarding", "OffBalance"], ["Neutral"], "Neutral", "Attack", "Clinch", ["T1", "T2", "T3"], 60,
            ["GRB_WRIST_001", "GRB_WAIST_001", "ATK_ELBOW_001"], "以肩背持续向前压迫，把对手逼入贴身纠缠距离。"),
        Act("MOVE_SLIDE_001", "滑铲闪避", "Dodge", "Slide", ["Close", "Mid"], ["Attack", "Charging"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T1", "T2", "T3"], 55,
            ["ATK_SWEEP_001", "ATK_KNEE_001", "GRB_WRIST_001"], "低身滑步穿过攻击下盘，快速转入近身攻击。"),
        Act("MOVE_WALL_KICK_001", "踏墙借力", "Move", "WallKick", ["Close", "Mid"], ["Neutral", "Attack", "Guarding"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T2", "T3", "T4", "T5"], 60,
            ["SWORD_LUNGE_001", "ATK_SPIN_KICK_001", "ATK_ELBOW_001"], "借墙面或立柱发力蹬踏，从侧上方改变进攻角度。"),
        Act("MOVE_AIR_TURN_001", "空中变向", "Dodge", "AirTurn", ["Close", "Mid"], ["Attack", "Charging", "Neutral"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T3", "T4", "T5"], 62,
            ["SWORD_SLASH_001", "ATK_SPIN_KICK_001", "FIN_SWORD_SLASH_001"], "腾空后凌空转向，避开原攻击线并从新角度落地反击。"),
        Act("MOVE_PHASE_001", "穿身而过", "Move", "Phase", ["Close"], ["Neutral", "Attack", "Guarding"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T3", "T4", "T5"], 68,
            ["MOVE_ARC_BEHIND_001", "ATK_ELBOW_001", "FIN_SWORD_SLASH_001"], "贴身瞬间从对方身侧穿到身后，抢占背身攻击位。"),

        // ===== 攻击 =====
        Act("ATK_JAB_001", "直拳", "Attack", "Punch", ["Close", "Mid"], ["Neutral", "Guarding", "Stagger", "OffBalance"], ["Neutral", "Attack"], "Guarding", "Attack", "Close", ["T1", "T2", "T3"], 60,
            ["ATK_CROSS_001", "ATK_ELBOW_001", "GRB_WRIST_001", "ATK_KNEE_001"], "前手直线出拳，试探并压缩对手防御。"),
        Act("ATK_CROSS_001", "重拳", "Attack", "Punch", ["Close"], ["Neutral", "Guarding", "Stagger", "OffBalance", "Exposed"], ["Neutral", "Attack"], "Stagger", "Attack", "Close", ["T1", "T2", "T3", "T4"], 70,
            ["ATK_SHOULDER_001", "ATK_SWEEP_001", "FIN_HEAVY_PUNCH_001"], "后手重拳轰向胸口，命中后令对手短暂硬直。"),
        Act("ATK_HOOK_001", "摆拳", "Attack", "Punch", ["Close"], ["Neutral", "Stagger", "OffBalance"], ["Neutral", "Attack"], "OffBalance", "Attack", "Close", ["T1", "T2", "T3"], 55,
            ["ATK_CROSS_001", "ATK_KNEE_001", "GRB_WRIST_001"], "弧线摆拳击打头侧或肋部，破坏对手重心。"),
        Act("ATK_ELBOW_001", "贴身横肘", "Attack", "Elbow", ["Close", "Clinch"], ["Neutral", "Guarding", "Stagger", "Grabbed", "OffBalance", "Exposed"], ["Neutral", "Attack"], "Stagger", "Attack", "Clinch", ["T1", "T2", "T3"], 65,
            ["ATK_KNEE_001", "THR_OVER_SHOULDER_001", "ATK_CROSS_001"], "贴身距离横肘撞击胸口或面门，造成短促硬直。"),
        Act("ATK_KNEE_001", "膝撞", "Attack", "Knee", ["Close", "Clinch"], ["Grabbed", "Stagger", "Guarding", "OffBalance"], ["Attack"], "OffBalance", "Attack", "Close", ["T1", "T2", "T3"], 60,
            ["ATK_ELBOW_001", "THR_OVER_SHOULDER_001", "ATK_SHOULDER_001"], "提膝撞向腹部或面门，让对手失去平衡。"),
        Act("ATK_SHOULDER_001", "肩撞", "Attack", "Shoulder", ["Close", "Clinch"], ["Stagger", "Guarding", "Neutral", "OffBalance", "Exposed"], ["Attack"], "OffBalance", "Attack", "Mid", ["T1", "T2", "T3"], 65,
            ["MOVE_DASH_IN_001", "ATK_CROSS_001", "FIN_HEAVY_PUNCH_001"], "沉肩撞入对手胸膛，将其撞退并破坏重心。"),
        Act("ATK_FRONT_KICK_001", "正踹", "Attack", "Kick", ["Close", "Mid"], ["Neutral", "Stagger", "OffBalance"], ["Neutral", "Attack"], "Neutral", "Attack", "Mid", ["T1", "T2", "T3", "T4"], 55,
            ["MOVE_DASH_IN_001", "ATK_CROSS_001"], "正蹬踹向腹部或胸口，把距离重新打开。"),
        Act("ATK_SWEEP_001", "扫堂腿", "Attack", "Kick", ["Close"], ["Neutral", "Attack", "Stagger", "OffBalance", "Knockdown"], ["Neutral"], "OffBalance", "Attack", "Close", ["T1", "T2"], 50,
            ["FIN_HEAVY_PUNCH_001", "THR_BODY_SLAM_001"], "低身扫踢下盘，破坏对手重心。"),
        Act("ATK_DOWNED_PRESS_001", "扑身追击", "Attack", "GroundStrike", ["Close", "Mid"], ["Knockdown", "Airborne"], ["Attack", "Neutral"], "Neutral", "Attack", "Close", ["T2", "T3", "T4", "T5"], 70,
            ["FIN_HEAVY_PUNCH_001", "FIN_GRAB_SLAM_001"], "顺势扑向倒地敌人连续补击，把对手按死在地面。", destruction: 1),
        Act("ATK_UPPERCUT_001", "上勾拳", "Attack", "Punch", ["Close"], ["Neutral", "Guarding"], ["Attack"], "Airborne", "Attack", "Close", ["T2", "T3", "T4"], 60,
            ["FIN_HEAVY_PUNCH_001", "FIN_FRONT_KICK_001"], "自下而上勾拳，将对手下巴或胸口打离地面。"),
        Act("ATK_SPIN_BACK_001", "转身横扫", "Attack", "Spin", ["Close"], ["Neutral", "Attack"], ["Neutral"], "Stagger", "Attack", "Close", ["T1", "T2", "T3"], 55,
            ["ATK_CROSS_001", "FIN_HEAVY_PUNCH_001"], "旋转半周扫出重击，借助旋转增加打击质量。"),

        Act("ATK_PALM_STRIKE_001", "连环掌击", "Attack", "PalmStrike", ["Close", "Mid"], ["Neutral", "Guarding", "Stagger", "OffBalance"], ["Neutral", "Attack"], "Guarding", "Attack", "Close", ["T1", "T2", "T3"], 58,
            ["ATK_CROSS_001", "ATK_ELBOW_001", "GRB_WRIST_001"], "双掌连环拍击，不断压缩对手防御。", element: "体"),
        Act("ATK_SPIN_KICK_001", "回旋踢", "Attack", "SpinKick", ["Close", "Mid"], ["Neutral", "Attack", "Stagger", "Guarding"], ["Neutral", "Attack"], "OffBalance", "Attack", "Mid", ["T2", "T3", "T4"], 62,
            ["ATK_CROSS_001", "FIN_FRONT_KICK_001", "MOVE_DASH_IN_001"], "旋转一周甩出鞭腿，命中后破坏对手重心。"),
        Act("ATK_BODY_CHARGE_001", "气血冲撞", "Attack", "BodyCharge", ["Mid"], ["Neutral", "Guarding", "Charging"], ["Neutral", "Attack"], "OffBalance", "Attack", "Close", ["T3", "T4", "T5"], 70,
            ["ATK_CROSS_001", "FIN_HEAVY_PUNCH_001", "GRB_WRIST_001"], "气血护住肩背猛然冲撞，从中间距直接压入贴身。", element: "体", destruction: 2),
        Act("ATK_OPENING_STRIKE_001", "破绽追击", "Attack", "OpeningStrike", ["Close", "Mid"], ["Exposed"], ["Neutral", "Attack"], "Stagger", "Attack", "Close", ["T1", "T2", "T3", "T4", "T5"], 72,
            ["ATK_CROSS_001", "FIN_HEAVY_PUNCH_001", "SWORD_SLASH_001", "FIN_SWORD_SLASH_001"], "抓住对方露出的破绽顺势追击，命中后直接形成硬直。", destruction: 1),
        Act("ATK_AIR_COMBO_001", "凌空连打", "Attack", "AirCombo", ["Close", "Mid"], ["Airborne"], ["Neutral", "Attack"], "Airborne", "Attack", "Close", ["T3", "T4", "T5"], 72,
            ["FIN_HEAVY_PUNCH_001", "FIN_IRON_FIST_001", "THR_SLAM_GROUND_001"], "追上被击飞的对手凌空连打，将其继续压制在空中。"),
        Act("ATK_ENV_THROW_001", "掷物轰击", "Attack", "EnvThrow", ["Mid", "Long"], ["Neutral", "Stagger", "OffBalance", "Knockdown"], ["Neutral", "Attack"], "Stagger", "Attack", "Mid", ["T2", "T3", "T4", "T5"], 55,
            ["MOVE_DASH_IN_001", "ATK_CROSS_001", "GRB_WRIST_001"], "顺手抓起碎石、断木或杂物掷向对手，在中距离持续施压。", destruction: 1),
        Act("ATK_ENV_SLAM_001", "抡物砸击", "Attack", "EnvSlam", ["Close", "Mid"], ["Neutral", "Guarding", "Stagger", "OffBalance"], ["Neutral", "Attack"], "OffBalance", "Attack", "Close", ["T3", "T4", "T5"], 65,
            ["ATK_CROSS_001", "FIN_HEAVY_PUNCH_001", "FIN_IRON_FIST_001"], "抓起石墩、木梁等重物抡圆砸下，压开防线并破坏重心。", destruction: 2),
        // ===== 技能 =====
        Act("SKILL_QI_BURST_001", "气血爆发", "Skill", "QiBurst", ["Close", "Mid"], ["Neutral", "Guarding", "Stagger"], ["Neutral", "Attack"], "Stagger", "Attack", "Mid", ["T3", "T4", "T5"], 60,
            ["ATK_CROSS_001", "FIN_HEAVY_PUNCH_001"], "赤金气血从体内炸开，短暂提升力量并震开近身敌人。", element: "体", destruction: 1),
        Act("SKILL_SWORD_BURST_001", "剑意爆发", "Skill", "SwordBurst", ["Mid", "Long"], ["Neutral", "Guarding", "Stagger"], ["Neutral", "Attack"], "Stagger", "Attack", "Mid", ["T4", "T5"], 78,
            ["ATK_CROSS_001", "FIN_HEAVY_PUNCH_001"], "剑意凝成一道凌厉气劲轰向敌人，命中后短暂硬直。", weapon: "剑", element: "金", destruction: 1),
        Act("SWORD_LUNGE_001", "剑步突刺", "Attack", "SwordLunge", ["Mid"], ["Neutral", "Stagger", "Guarding", "OffBalance", "Exposed"], ["Neutral", "Attack"], "Stagger", "Attack", "Close", ["T4", "T5"], 82,
            ["ATK_CROSS_001", "ATK_ELBOW_001", "FIN_SWORD_SLASH_001"], "脚下剑步一纵，长剑笔直刺向敌人胸口，把距离压到贴身。", weapon: "剑", element: "金", destruction: 1),
        Act("SWORD_SLASH_001", "剑光横斩", "Attack", "SwordSlash", ["Close"], ["Neutral", "Stagger", "Guarding", "OffBalance", "Exposed"], ["Neutral", "Attack"], "Stagger", "Attack", "Mid", ["T4", "T5"], 80,
            ["SWORD_LUNGE_001", "FIN_SWORD_SLASH_001", "SKILL_QI_BURST_001"], "贴身横斩一剑，剑气贴着剑锋压住对手反击空间。", weapon: "剑", element: "金", destruction: 1),
        Act("SWORD_ARC_001", "剑弧横扫", "Attack", "SwordArc", ["Mid"], ["Neutral", "Stagger", "Guarding", "OffBalance", "Exposed"], ["Neutral", "Attack"], "Stagger", "Attack", "Close", ["T4", "T5"], 85,
            ["SWORD_SLASH_001", "FIN_SWORD_SLASH_001"], "剑锋划出弧光横扫中距，命中后顺势压入贴身。", weapon: "剑", element: "金", destruction: 1),
        Act("SWORD_UPPER_001", "剑尖上挑", "Attack", "SwordUppercut", ["Close", "Mid"], ["Neutral", "Stagger", "Guarding", "OffBalance"], ["Neutral", "Attack"], "OffBalance", "Attack", "Mid", ["T3", "T4", "T5"], 75,
            ["SWORD_SLASH_001", "SWORD_LUNGE_001", "FIN_SWORD_SLASH_001"], "剑尖自下而上挑开对手防线，露出中路破绽。", weapon: "剑", element: "金", destruction: 1),
        Act("SWORD_DOWNSTRIKE_001", "剑锋下劈", "Attack", "SwordDownstrike", ["Close", "Mid"], ["Neutral", "Stagger", "Guarding", "OffBalance"], ["Neutral", "Attack"], "Knockdown", "Attack", "Close", ["T3", "T4", "T5"], 78,
            ["FIN_SWORD_FALL_001", "ATK_DOWNED_PRESS_001"], "双手举剑重重下劈，将正面攻势连同防御一同砸开。", weapon: "剑", element: "金", destruction: 2),
        Act("SWORD_SPIN_001", "回旋剑斩", "Attack", "SwordSpin", ["Close", "Mid"], ["Neutral", "Attack", "Stagger", "Guarding"], ["Neutral", "Attack"], "OffBalance", "Attack", "Close", ["T3", "T4", "T5"], 80,
            ["SWORD_SLASH_001", "SWORD_LUNGE_001", "FIN_SWORD_SLASH_001"], "旋身带剑斩出一圈剑光，同时挡开身侧来势并重创敌人。", weapon: "剑", element: "金", destruction: 2),
        Act("SWORD_AIR_SLASH_001", "凌空追斩", "Attack", "SwordAirSlash", ["Close", "Mid"], ["Airborne"], ["Neutral", "Attack"], "Airborne", "Attack", "Mid", ["T4", "T5"], 84,
            ["FIN_SWORD_FALL_001", "SWORD_DOWNSTRIKE_001", "FIN_SWORD_SLASH_001"], "追上被挑空的敌人凌空补斩，以剑气连环压制空中目标。", weapon: "剑", element: "金", destruction: 2),
        Act("DEF_SWORD_GUARD_001", "剑身格挡", "Defense", "SwordGuard", ["Close", "Mid"], ["Attack", "Charging"], ["Neutral", "Attack"], "Neutral", "Guarding", "Close", ["T3", "T4", "T5"], 62,
            ["CTR_SWORD_PARRY_001", "SWORD_SLASH_001", "SWORD_UPPER_001"], "横剑封挡对方攻势，稳住身位后寻找反打契机。", weapon: "剑", element: "金"),
        Act("CTR_SWORD_PARRY_001", "剑锋格反", "Counter", "SwordParry", ["Close", "Mid"], ["Attack"], ["Neutral", "Guarding"], "Exposed", "Attack", "Close", ["T3", "T4", "T5"], 72,
            ["ATK_OPENING_STRIKE_001", "SWORD_LUNGE_001", "FIN_SWORD_SLASH_001"], "剑身封挡对方攻势后顺势滑刃，令其露出中路空档。", weapon: "剑", element: "金"),
        Act("FIN_BODY_CRUSH_001", "气血震爆", "Finisher", "BodyCrush", ["Close", "Clinch"], ["Grabbed", "Airborne", "Disabled"], ["Attack"], "Knockdown", "Attack", "Close", ["T4", "T5"], 88,
            [], "爆发气血将对手凌空震落，重重砸地结束战斗。", element: "体", destruction: 3),
        Act("SKILL_QI_PALM_001", "拳风轰击", "Skill", "QiPalm", ["Mid", "Long"], ["Neutral", "Guarding"], ["Neutral", "Charging"], "Stagger", "Attack", "Mid", ["T3", "T4", "T5"], 65,
            ["MOVE_DASH_IN_001", "SKILL_QI_BURST_001"], "隔空轰出拳风，气浪把对手推入硬直。", element: "体", destruction: 1),
        Act("SKILL_MAGE_SHIELD_001", "灵盾震开", "Skill", "MageShield", ["Close", "Clinch"], ["Attack", "Neutral", "Guarding", "Stagger", "OffBalance", "Exposed"], ["Neutral", "Attack"], "OffBalance", "Attack", "Mid", ["T3", "T4", "T5"], 70,
            ["SKILL_QI_PALM_001", "SKILL_QI_BURST_001", "MOVE_BACK_001"], "身前浮现灵力护盾，硬接攻势后瞬间震开对手。", element: "灵", destruction: 0),
        Act("SKILL_MAGE_BOLT_001", "灵光远击", "Skill", "MageBolt", ["Mid", "Long"], ["Neutral", "Stagger", "Guarding"], ["Neutral", "Attack"], "Neutral", "Attack", "Long", ["T3", "T4", "T5"], 75,
            ["SKILL_MAGE_BOLT_001", "SKILL_QI_PALM_001", "MOVE_BACK_001"], "灵力凝聚成光束远距离轰出，命中后把对手留在远处。", element: "灵", destruction: 1),
        Act("FLY_SWORD_001", "飞剑追击", "Skill", "FlySword", ["Mid", "Long"], ["Neutral", "Stagger", "OffBalance"], ["Neutral", "Attack"], "Stagger", "Attack", "Long", ["T4", "T5"], 82,
            ["SKILL_SWORD_ARRAY_001", "SKILL_SWORD_RAIN_001", "FIN_SWORD_SLASH_001"], "飞剑离手化光追击，压制远程距离并持续牵制对手。", weapon: "剑", element: "金", destruction: 2),
        Act("SKILL_SWORD_ARRAY_001", "剑阵绞杀", "Skill", "SwordArray", ["Mid", "Long"], ["Neutral", "Stagger", "Guarding"], ["Neutral", "Attack"], "Stagger", "Attack", "Mid", ["T4", "T5"], 84,
            ["FIN_SWORD_SLASH_001", "SKILL_SWORD_RAIN_001"], "多柄剑影结成剑阵合拢绞杀，将敌人锁死在阵心。", weapon: "剑", element: "金", destruction: 2),
        Act("SKILL_SWORD_RAIN_001", "万剑齐发", "Skill", "SwordRain", ["Mid", "Long"], ["Neutral", "Stagger", "OffBalance", "Grabbed"], ["Neutral", "Attack"], "Stagger", "Attack", "Long", ["T5"], 92,
            ["FIN_SWORD_FALL_001"], "漫天剑影如雨倾落，将敌人连同防线一同压制。", weapon: "剑", element: "金", destruction: 4),
        Act("SKILL_MAGE_DART_001", "灵光飞针", "Skill", "MageDart", ["Mid", "Long"], ["Neutral", "Stagger", "Guarding"], ["Neutral", "Attack"], "Stagger", "Attack", "Long", ["T1", "T2", "T3"], 55,
            ["SKILL_MAGE_BOLT_001", "SKILL_FIRE_ORB_001", "SKILL_WIND_BLADE_001"], "指尖凝出数道灵光飞针远射，命中后令敌人短暂失衡。", element: "灵", destruction: 0),
        Act("SKILL_FIRE_ORB_001", "火球轰击", "Skill", "FireOrb", ["Mid", "Long"], ["Neutral", "Stagger", "Guarding"], ["Neutral", "Attack"], "Stagger", "Attack", "Long", ["T3", "T4", "T5"], 76,
            ["SKILL_THUNDER_SPEAR_001", "SKILL_MAGE_BOLT_001", "FIN_MAGE_CRUSH_001"], "灵力凝成火球轰向敌人，拉开距离并制造硬直。", element: "火", destruction: 2),
        Act("SKILL_WIND_BLADE_001", "风刃连斩", "Skill", "WindBlade", ["Mid", "Long"], ["Neutral", "Stagger", "Guarding"], ["Neutral", "Attack"], "Stagger", "Attack", "Long", ["T3", "T4", "T5"], 74,
            ["SKILL_FIRE_ORB_001", "SKILL_MAGE_BOLT_001", "FIN_MAGE_CRUSH_001"], "连发数道风刃切割空间，命中后令对手短暂硬直。", element: "风", destruction: 1),
        Act("SKILL_THUNDER_SPEAR_001", "雷枪贯穿", "Skill", "ThunderSpear", ["Mid", "Long"], ["Neutral", "Stagger", "OffBalance"], ["Neutral", "Attack"], "Stagger", "Attack", "Long", ["T4", "T5"], 84,
            ["FIN_MAGE_CRUSH_001"], "雷光凝成战枪贯穿长空，命中后令敌人短暂失衡。", element: "雷", destruction: 3),
        Act("SKILL_FROST_PRISON_001", "冰封禁制", "Skill", "FrostPrison", ["Mid", "Long"], ["Neutral", "Stagger", "Guarding"], ["Neutral", "Attack"], "Stagger", "Attack", "Mid", ["T4", "T5"], 78,
            ["SKILL_FIRE_ORB_001", "SKILL_THUNDER_SPEAR_001", "FIN_MAGE_CRUSH_001"], "寒气凝成冰晶锁链缠住敌人，短暂限制其行动。", element: "冰", destruction: 0),
        Act("SKILL_MAGE_FORMATION_001", "法阵镇压", "Skill", "Formation", ["Mid", "Long"], ["Neutral", "Stagger", "Guarding", "Charging"], ["Neutral", "Attack"], "Stagger", "Attack", "Long", ["T4", "T5"], 86,
            ["SKILL_FIRE_ORB_001", "SKILL_THUNDER_SPEAR_001", "FIN_MAGE_CRUSH_001"], "展开法阵压住战场，不断削弱敌人并制造硬直。", element: "阵", destruction: 3),
        Act("SKILL_LAW_BODY_001", "法相镇压", "Skill", "LawBody", ["Mid", "Long"], ["Neutral", "Stagger", "Charging"], ["Neutral", "Attack"], "Stagger", "Attack", "Mid", ["T5"], 90,
            ["FIN_MAGE_CRUSH_001", "SKILL_MAGE_FORMATION_001", "SKILL_THUNDER_SPEAR_001"], "法相虚影在身后展开，以巨掌或领域镇压敌人。", element: "法", destruction: 4),
        Act("SKILL_MAGE_BLINK_001", "灵光瞬移", "Move", "Blink", ["Close", "Mid"], ["Attack", "Charging", "Neutral", "Stagger", "OffBalance", "Exposed"], ["Neutral", "Attack"], "Neutral", "Attack", "Long", ["T3", "T4", "T5"], 68,
            ["SKILL_FIRE_ORB_001", "SKILL_MAGE_BOLT_001", "SKILL_MAGE_FORMATION_001"], "灵光一闪瞬移到远处，脱离近身威胁后重新组织法术。", element: "灵"),
        Act("SKILL_BLOOD_RAGE_001", "气血狂涌", "Skill", "BloodRage", ["Close", "Mid"], ["Neutral", "Stagger", "Guarding"], ["Neutral", "Attack"], "Stagger", "Attack", "Close", ["T4", "T5"], 80,
            ["ATK_CROSS_001", "FIN_HEAVY_PUNCH_001"], "气血瞬间沸腾形成气浪，将敌人震入硬直。", element: "体", destruction: 2),
        Act("DEF_IRON_BODY_001", "金身硬抗", "Defense", "IronBody", ["Close", "Mid"], ["Attack", "Charging"], ["Neutral", "Attack"], "Neutral", "Guarding", "Close", ["T3", "T4", "T5"], 68,
            ["ATK_CROSS_001", "GRB_WRIST_001", "ATK_ELBOW_001"], "气血覆体硬抗对方攻势，纹丝不动后直接反打。", element: "体", destruction: 1),
        Act("FIN_SWORD_FALL_001", "一剑开天", "Finisher", "SwordFall", ["Close", "Mid", "Long"], ["Stagger", "OffBalance", "Grabbed", "Knockdown"], ["Attack"], "Airborne", "Attack", "Long", ["T5"], 95,
            [], "凝聚全身剑意劈出惊天一剑，剑气贯穿天地，终结战局。", weapon: "剑", element: "金", destruction: 5),
        Act("FIN_MAGE_CRUSH_001", "万法归元", "Finisher", "MageCrush", ["Mid", "Long"], ["Stagger", "OffBalance", "Grabbed", "Airborne"], ["Attack"], "Airborne", "Attack", "Long", ["T5"], 94,
            [], "五行灵力汇聚归元，化作毁灭光柱终结战局。", element: "灵", destruction: 5),
        Act("FIN_IRON_FIST_001", "一拳碎山", "Finisher", "IronFist", ["Close", "Mid"], ["Stagger", "OffBalance", "Knockdown"], ["Attack"], "Airborne", "Attack", "Long", ["T4", "T5"], 90,
            [], "全力一拳轰出，气劲崩碎山石，将敌人轰飞终结战局。", element: "体", destruction: 4),
        Act("REA_SHIELD_BREAK_001", "护体碎裂", "Reaction", "ShieldBreak", ["Close", "Mid"], ["Attack"], ["Neutral"], "Attack", "Stagger", "", ["T3", "T4", "T5"], 35,
            [], "护体灵光被一击打碎，碎片与气浪四散，身体短暂失衡。", destruction: 2, isReaction: true),
        Act("REA_ARMOR_BREAK_001", "破甲反震", "Reaction", "ArmorBreak", ["Close", "Mid"], ["Attack"], ["Neutral"], "Attack", "OffBalance", "", ["T3", "T4", "T5"], 35,
            [], "护甲或护体法术被正面击破，金属碎片/灵光炸开，人被震退。", destruction: 2, isReaction: true),
        Act("REA_WALL_CRASH_001", "撞穿墙体", "Reaction", "WallCrash", ["Close", "Mid"], ["Attack"], ["Neutral"], "Attack", "Knockdown", "", ["T4", "T5"], 35,
            [], "被重击轰飞撞穿墙体或山壁，碎石飞溅后倒地。", destruction: 4, isReaction: true),

        // ===== 防御 =====
        Act("DEF_HARD_BLOCK_001", "硬抗格挡", "Defense", "Block", ["Close", "Mid"], ["Attack", "Charging"], ["Neutral", "Attack"], "Neutral", "Attack", "Close", ["T1", "T2", "T3", "T4", "T5"], 50,
            ["CTR_COUNTER_PUNCH_001", "GRB_WRIST_001", "DEF_PARRY_001"], "不闪不避，以肉身或双臂硬接攻击。"),
        Act("DEF_PARRY_001", "拍开攻势", "Counter", "Parry", ["Close"], ["Attack"], ["Neutral", "Guarding"], "Exposed", "Attack", "Close", ["T1", "T2", "T3", "T4"], 60,
            ["ATK_OPENING_STRIKE_001", "ATK_ELBOW_001", "GRB_WRIST_001", "ATK_SHOULDER_001", "ATK_CROSS_001"], "用掌或前臂拍开攻击线路，让对手露出破绽。"),
        Act("DEF_BLOCK_BREAK_001", "护体反震", "Defense", "Block", ["Close", "Mid"], ["Attack", "Charging"], ["Neutral", "Charging"], "Stagger", "Attack", "Close", ["T2", "T3", "T4", "T5"], 70,
            ["ATK_CROSS_001", "ATK_ELBOW_001"], "以护体气血硬接攻击并瞬间反震，震麻对手攻势。", element: "体", destruction: 1),
        Act("DEF_ARM_BLOCK_001", "架臂格挡", "Defense", "Block", ["Close"], ["Attack"], ["Neutral"], "Attack", "Guarding", "Close", ["T1", "T2", "T3", "T4"], 45,
            ["CTR_COUNTER_PUNCH_001", "DEF_PARRY_001"], "双臂交叉架住攻势，保持贴身距离等待反击机会。"),

        // ===== 反击 / 擒拿 =====
        Act("CTR_COUNTER_PUNCH_001", "接拳反击", "Counter", "CounterPunch", ["Close"], ["Attack"], ["Guarding", "Neutral"], "Stagger", "Attack", "Close", ["T1", "T2", "T3", "T4"], 65,
            ["ATK_CROSS_001", "ATK_ELBOW_001"], "格挡后抓住对手旧力未收的瞬间，直接反击。"),
        Act("GRB_WRIST_001", "抓腕", "Grab", "Wrist", ["Close", "Clinch"], ["Attack", "Guarding", "Neutral", "OffBalance", "Exposed"], ["Neutral", "Attack"], "Grabbed", "Attack", "Clinch", ["T1", "T2", "T3", "T4", "T5"], 60,
            ["ATK_ELBOW_001", "ATK_KNEE_001", "THR_OVER_SHOULDER_001", "THR_FLING_001"], "扣住对手手腕，控制攻击线路并转入擒拿。"),
        Act("GRB_SHOULDER_001", "抓肩", "Grab", "Shoulder", ["Close"], ["Neutral", "Attack", "Stagger"], ["Neutral"], "Grabbed", "Attack", "Clinch", ["T1", "T2", "T3", "T4"], 55,
            ["ATK_KNEE_001", "THR_BODY_SLAM_001"], "抓住肩颈位置，限制对手转身与脱身。"),
        Act("GRB_WAIST_001", "抱腰", "Grab", "Waist", ["Clinch"], ["Neutral", "OffBalance"], ["Attack"], "Grabbed", "Attack", "Clinch", ["T2", "T3", "T4"], 55,
            ["THR_OVER_SHOULDER_001", "THR_BODY_SLAM_001", "ATK_KNEE_001"], "贴身抱腰，为摔投创造稳定支点。"),
        Act("GRB_ARM_LOCK_001", "锁臂", "Grab", "ArmLock", ["Close", "Clinch"], ["Grabbed", "Attack"], ["Attack"], "OffBalance", "Attack", "Clinch", ["T2", "T3", "T4"], 65,
            ["ATK_ELBOW_001", "THR_OVER_SHOULDER_001"], "反关节锁死对手手臂，令其短时间内无法出招。"),

        // ===== 摔投 =====
        Act("THR_OVER_SHOULDER_001", "过肩摔", "Throw", "OverShoulder", ["Clinch"], ["Grabbed", "OffBalance"], ["Attack", "Neutral"], "Knockdown", "Attack", "Mid", ["T1", "T2", "T3", "T4"], 65,
            ["MOVE_DASH_IN_001", "FIN_HEAVY_PUNCH_001"], "借对手重心前移，转身将其从肩上摔出。", destruction: 1),
        Act("THR_BODY_SLAM_001", "抱摔", "Throw", "BodySlam", ["Clinch"], ["Grabbed", "OffBalance"], ["Attack"], "Knockdown", "Attack", "Close", ["T1", "T2", "T3"], 60,
            ["FIN_HEAVY_PUNCH_001", "ATK_SWEEP_001"], "抱起对手整体砸向地面，直接结束当前回合。", destruction: 2),
        Act("THR_FLING_001", "甩飞", "Throw", "Fling", ["Close", "Clinch"], ["Grabbed", "OffBalance"], ["Attack"], "Airborne", "Attack", "Mid", ["T1", "T2", "T3", "T4"], 60,
            ["FIN_HEAVY_PUNCH_001", "FIN_FRONT_KICK_001"], "抓住对手身体或肢体，旋转发力将其甩飞。", destruction: 1),
        Act("THR_SLAM_GROUND_001", "砸地", "Throw", "Slam", ["Close", "Clinch"], ["Grabbed", "Airborne"], ["Attack"], "Knockdown", "Attack", "Close", ["T2", "T3", "T4", "T5"], 70,
            ["FIN_HEAVY_PUNCH_001"], "抓住对手后直接砸向地面，造成明显破坏反馈。", destruction: 2),

        // ===== 终结 =====
        Act("FIN_SWORD_SLASH_001", "一剑封喉", "Finisher", "SwordSlash", ["Close", "Mid"], ["Stagger", "OffBalance", "Grabbed", "Exposed"], ["Attack"], "Airborne", "Attack", "Mid", ["T4", "T5"], 90,
            [], "长剑横斩，剑气将敌人连同防线一起击飞。", weapon: "剑", element: "金", destruction: 2),
        Act("FIN_HEAVY_PUNCH_001", "一拳轰飞", "Finisher", "HeavyPunch", ["Close"], ["Stagger", "OffBalance", "Knockdown"], ["Attack"], "Airborne", "Attack", "Mid", ["T1", "T2", "T3", "T4", "T5"], 80,
            [], "终结重拳正面命中，将对手轰飞出去。", destruction: 2),
        Act("FIN_FRONT_KICK_001", "一脚踹飞", "Finisher", "FrontKick", ["Close", "Mid"], ["Stagger", "OffBalance"], ["Attack"], "Airborne", "Attack", "Long", ["T1", "T2", "T3", "T4", "T5"], 75,
            [], "终结正踹命中躯干，把对手踢飞到远处。", destruction: 1),
        Act("FIN_GRAB_SLAM_001", "抓头砸地", "Finisher", "GrabSlam", ["Close"], ["Grabbed", "Stagger", "OffBalance"], ["Attack"], "Knockdown", "Attack", "Close", ["T2", "T3", "T4", "T5"], 80,
            [], "抓住对手头部或衣领，狠狠砸向地面结束战斗。", destruction: 2),
        Act("FIN_PALM_STRIKE_001", "一掌拍飞", "Finisher", "PalmStrike", ["Close", "Mid"], ["Neutral", "Stagger", "OffBalance"], ["Attack"], "Airborne", "Attack", "Mid", ["T1", "T2", "T3", "T4"], 70,
            [], "一掌正面拍出，把对手连同攻势一起拍飞。", destruction: 1),
        Act("FIN_BODY_BREAK_001", "拳破护体", "Finisher", "BreakGuard", ["Close"], ["Guarding", "Charging"], ["Attack", "Charging"], "Knockdown", "Attack", "Close", ["T3", "T4", "T5"], 85,
            [], "一拳正面轰碎对手护体/防御姿态，形成决定性一击。", element: "体", destruction: 3),

        // ===== 受击反应（V1 不参与主动选择，供后续镜头/描写使用）=====
        Act("REA_FLINCH_001", "轻微后仰", "Reaction", "Flinch", ["Close", "Mid"], ["Attack"], ["Neutral"], "Attack", "Stagger", "", ["T1", "T2", "T3"], 30,
            [], "受击后头部与上半身轻微后仰。", isReaction: true),
        Act("REA_STAGGER_001", "踉跄", "Reaction", "Stagger", ["Close", "Mid"], ["Attack"], ["Neutral"], "Attack", "Stagger", "", ["T1", "T2", "T3"], 30,
            [], "受击后脚步混乱，短时间失去稳定姿态。", isReaction: true),
        Act("REA_BACKSTEP_001", "后退一步", "Reaction", "Backstep", ["Close"], ["Attack"], ["Neutral"], "Attack", "Neutral", "Mid", ["T1", "T2", "T3"], 35,
            [], "受击后借势后退一步，重新拉开距离。", isReaction: true),
        Act("REA_KNOCKBACK_001", "被击退", "Reaction", "Knockback", ["Close", "Mid"], ["Attack"], ["Neutral", "Stagger"], "Attack", "OffBalance", "Mid", ["T1", "T2", "T3", "T4"], 35,
            [], "被重击推退数步，身体短暂失衡。", destruction: 1, isReaction: true),
        Act("REA_KNOCKDOWN_001", "倒地", "Reaction", "Knockdown", ["Close", "Mid"], ["Attack"], ["Neutral", "Stagger"], "Attack", "Knockdown", "Close", ["T2", "T3", "T4"], 35,
            [], "被终结技命中后倒地，暂时失去作战能力。", destruction: 1, isReaction: true),
        Act("REA_AIRBORNE_001", "凌空击飞", "Reaction", "Airborne", ["Close", "Mid"], ["Attack"], ["Neutral"], "Attack", "Airborne", "Mid", ["T2", "T3", "T4", "T5"], 35,
            [], "被重击轰离地面，身体短暂腾空。", destruction: 1, isReaction: true)
    ];

    private static List<CombatStyle> BuildStyles() =>
    [
        new CombatStyle
        {
            Id = "BODY_CULTIVATOR",
            Name = "体修",
            ForbiddenSkills = ["SKILL_MAGE_SHIELD_001", "SKILL_SWORD_BURST_001", "SKILL_MAGE_BOLT_001", "SKILL_MAGE_DART_001", "SWORD_LUNGE_001", "SWORD_SLASH_001", "SWORD_ARC_001", "SWORD_UPPER_001", "SWORD_DOWNSTRIKE_001", "SWORD_SPIN_001", "CTR_SWORD_PARRY_001", "FLY_SWORD_001", "SKILL_SWORD_ARRAY_001", "SKILL_SWORD_RAIN_001", "FIN_SWORD_SLASH_001", "FIN_SWORD_FALL_001", "SKILL_FIRE_ORB_001", "SKILL_WIND_BLADE_001", "SKILL_THUNDER_SPEAR_001", "SKILL_FROST_PRISON_001", "SKILL_MAGE_FORMATION_001", "SKILL_LAW_BODY_001", "SKILL_MAGE_BLINK_001", "FIN_MAGE_CRUSH_001"],
            CategoryWeights = new Dictionary<string, int>
            {
                ["Attack"] = 30,
                ["Counter"] = 20,
                ["Grab"] = 25,
                ["Dodge"] = -15,
                ["Defense"] = 5,
                ["Move"] = 10,
                ["Skill"] = -30,
                ["Throw"] = 20,
                ["Finisher"] = 15
            },
            ActionWeights = new Dictionary<string, int>
            {
                ["ATK_CROSS_001"] = 30,
                ["ATK_SHOULDER_001"] = 35,
                ["GRB_WRIST_001"] = 25,
                ["DEF_HARD_BLOCK_001"] = 40,
                ["FIN_HEAVY_PUNCH_001"] = 30,
                ["THR_OVER_SHOULDER_001"] = 25
                ,
                ["ATK_PALM_STRIKE_001"] = 25,
                ["ATK_SPIN_KICK_001"] = 20,
                ["ATK_BODY_CHARGE_001"] = 30,
                ["ATK_OPENING_STRIKE_001"] = 25,
                ["DEF_IRON_BODY_001"] = 40,
                ["SKILL_BLOOD_RAGE_001"] = 30,
                ["FIN_IRON_FIST_001"] = 35,
                ["ATK_AIR_COMBO_001"] = 35,
                ["ATK_ENV_THROW_001"] = 20,
                ["ATK_ENV_SLAM_001"] = 30
            }
        },
        new CombatStyle
        {
            Id = "SWORD_CULTIVATOR",
            Name = "剑修",
            ForbiddenSkills = ["SKILL_MAGE_SHIELD_001", "SKILL_QI_BURST_001", "SKILL_MAGE_BOLT_001", "SKILL_MAGE_DART_001", "SKILL_QI_PALM_001", "FIN_BODY_CRUSH_001", "FIN_HEAVY_PUNCH_001", "FIN_FRONT_KICK_001", "FIN_GRAB_SLAM_001", "FIN_PALM_STRIKE_001", "FIN_BODY_BREAK_001", "DEF_IRON_BODY_001", "SKILL_BLOOD_RAGE_001", "FIN_IRON_FIST_001", "ATK_BODY_CHARGE_001", "ATK_PALM_STRIKE_001", "ATK_SPIN_KICK_001", "ATK_UPPERCUT_001", "ATK_AIR_COMBO_001", "ATK_ENV_THROW_001", "ATK_ENV_SLAM_001", "GRB_WRIST_001", "GRB_SHOULDER_001", "GRB_WAIST_001", "GRB_ARM_LOCK_001", "THR_OVER_SHOULDER_001", "THR_BODY_SLAM_001", "THR_FLING_001", "THR_SLAM_GROUND_001", "SKILL_FIRE_ORB_001", "SKILL_WIND_BLADE_001", "SKILL_THUNDER_SPEAR_001", "SKILL_FROST_PRISON_001", "SKILL_MAGE_FORMATION_001", "SKILL_LAW_BODY_001", "SKILL_MAGE_BLINK_001", "FIN_MAGE_CRUSH_001"],
            CategoryWeights = new Dictionary<string, int>
            {
                ["Attack"] = 35,
                ["Counter"] = 25,
                ["Dodge"] = 20,
                ["Move"] = 20,
                ["Grab"] = -30,
                ["Defense"] = 5,
                ["Skill"] = 10,
                ["Throw"] = -20,
                ["Finisher"] = 15
            },
            ActionWeights = new Dictionary<string, int>
            {
                ["DEF_PARRY_001"] = 25,
                ["CTR_COUNTER_PUNCH_001"] = 25,
                ["MOVE_SIDE_STEP_001"] = 25,
                ["SWORD_LUNGE_001"] = 30,
                ["SWORD_SLASH_001"] = 30,
                ["SKILL_SWORD_BURST_001"] = 8,
                ["FIN_SWORD_SLASH_001"] = 45,
                ["SKILL_QI_BURST_001"] = -40,
                ["SWORD_ARC_001"] = 40,
                ["SWORD_UPPER_001"] = 35,
                ["SWORD_DOWNSTRIKE_001"] = 35,
                ["SWORD_SPIN_001"] = 40,
                ["CTR_SWORD_PARRY_001"] = 35,
                ["ATK_OPENING_STRIKE_001"] = 25,
                ["FLY_SWORD_001"] = 25,
                ["SKILL_SWORD_ARRAY_001"] = 30,
                ["SKILL_SWORD_RAIN_001"] = 35,
                ["FIN_SWORD_FALL_001"] = 50,
                ["SWORD_AIR_SLASH_001"] = 45,
                ["DEF_SWORD_GUARD_001"] = 25
            }
        },
        new CombatStyle
        {
            Id = "MAGE",
            Name = "法修",
            ForbiddenSkills = ["SKILL_SWORD_BURST_001", "SKILL_QI_BURST_001", "MOVE_LONG_CHARGE_001", "SKILL_QI_PALM_001", "SWORD_LUNGE_001", "SWORD_SLASH_001", "SWORD_ARC_001", "SWORD_UPPER_001", "SWORD_DOWNSTRIKE_001", "SWORD_SPIN_001", "SWORD_AIR_SLASH_001", "CTR_SWORD_PARRY_001", "DEF_SWORD_GUARD_001", "FLY_SWORD_001", "SKILL_SWORD_ARRAY_001", "SKILL_SWORD_RAIN_001", "FIN_SWORD_SLASH_001", "FIN_SWORD_FALL_001", "DEF_IRON_BODY_001", "SKILL_BLOOD_RAGE_001", "FIN_IRON_FIST_001", "FIN_BODY_CRUSH_001", "FIN_HEAVY_PUNCH_001", "FIN_FRONT_KICK_001", "FIN_GRAB_SLAM_001", "FIN_PALM_STRIKE_001", "FIN_BODY_BREAK_001", "ATK_JAB_001", "ATK_CROSS_001", "ATK_HOOK_001", "ATK_ELBOW_001", "ATK_KNEE_001", "ATK_SHOULDER_001", "ATK_FRONT_KICK_001", "ATK_SWEEP_001", "ATK_DOWNED_PRESS_001", "ATK_SPIN_BACK_001", "ATK_OPENING_STRIKE_001", "ATK_BODY_CHARGE_001", "ATK_PALM_STRIKE_001", "ATK_SPIN_KICK_001", "ATK_UPPERCUT_001", "ATK_AIR_COMBO_001", "ATK_ENV_THROW_001", "ATK_ENV_SLAM_001", "GRB_WRIST_001", "GRB_SHOULDER_001", "GRB_WAIST_001", "GRB_ARM_LOCK_001", "THR_OVER_SHOULDER_001", "THR_BODY_SLAM_001", "THR_FLING_001", "THR_SLAM_GROUND_001", "MOVE_BODY_PRESS_001", "MOVE_PHASE_001", "MOVE_WALL_KICK_001"],
            CategoryWeights = new Dictionary<string, int>
            {
                ["Attack"] = 15,
                ["Counter"] = 10,
                ["Dodge"] = 30,
                ["Move"] = 25,
                ["Grab"] = -50,
                ["Defense"] = 5,
                ["Skill"] = 50,
                ["Throw"] = -50,
                ["Finisher"] = 20
            },
            ActionWeights = new Dictionary<string, int>
            {
                ["SKILL_MAGE_BOLT_001"] = 55,
                ["SKILL_MAGE_DART_001"] = 40,
                ["SKILL_MAGE_SHIELD_001"] = 20,
                ["MOVE_BACK_001"] = 30,
                ["MOVE_JUMP_BACK_001"] = 30,
                ["SKILL_MAGE_BLINK_001"] = 35,
                ["SKILL_FIRE_ORB_001"] = 50,
                ["SKILL_WIND_BLADE_001"] = 40,
                ["SKILL_THUNDER_SPEAR_001"] = 45,
                ["SKILL_FROST_PRISON_001"] = 45,
                ["SKILL_MAGE_FORMATION_001"] = 50,
                ["SKILL_LAW_BODY_001"] = 60,
                ["FIN_MAGE_CRUSH_001"] = 50
            }
        }
    ];

    private static List<CombatPreset> BuildPresets() =>
    [
        new CombatPreset
        {
            Id = "T1_QUICK_COMBO",
            Tier = "T1",
            MinBeat = 3,
            MaxBeat = 5,
            CategoryWeights = new Dictionary<string, int>
            {
                ["Attack"] = 40,
                ["Move"] = 30,
                ["Counter"] = 15,
                ["Dodge"] = 10,
                ["Finisher"] = 20
            },
            PreferredIntents = ["Pressure", "Finish"],
            ForbiddenCategories = ["Throw"]
        },
        new CombatPreset
        {
            Id = "T2_BODY_PRESSURE",
            Tier = "T2",
            MinBeat = 6,
            MaxBeat = 10,
            CategoryWeights = new Dictionary<string, int>
            {
                ["Attack"] = 35,
                ["Move"] = 20,
                ["Grab"] = 20,
                ["Counter"] = 15,
                ["Defense"] = 10,
                ["Throw"] = 15,
                ["Finisher"] = 10
            },
            PreferredIntents = ["Pressure", "Counter", "Finish"],
            ForbiddenCategories = []
        },
        new CombatPreset
        {
            Id = "T3_GROUP_MELEE",
            Tier = "T3",
            MinBeat = 10,
            MaxBeat = 15,
            CategoryWeights = new Dictionary<string, int>
            {
                ["Attack"] = 30,
                ["Move"] = 20,
                ["Counter"] = 15,
                ["Grab"] = 15,
                ["Throw"] = 15,
                ["Skill"] = 10,
                ["Finisher"] = 15
            },
            PreferredIntents = ["Pressure", "BreakGuard", "Finish"],
            ForbiddenCategories = []
        },
        new CombatPreset
        {
            Id = "T4_SKILL_EXPLOSION",
            Tier = "T4",
            MinBeat = 8,
            MaxBeat = 12,
            CategoryWeights = new Dictionary<string, int>
            {
                ["Skill"] = 35,
                ["Attack"] = 25,
                ["Move"] = 10,
                ["Counter"] = 10,
                ["Finisher"] = 25
            },
            PreferredIntents = ["BreakGuard", "Pressure", "Finish"],
            ForbiddenCategories = []
        },
        new CombatPreset
        {
            Id = "T5_FINALE_CLASH",
            Tier = "T5",
            MinBeat = 12,
            MaxBeat = 18,
            CategoryWeights = new Dictionary<string, int>
            {
                ["Skill"] = 30,
                ["Attack"] = 20,
                ["Move"] = 10,
                ["Counter"] = 10,
                ["Throw"] = 10,
                ["Finisher"] = 30
            },
            PreferredIntents = ["Finish", "BreakGuard", "Counter"],
            ForbiddenCategories = []
        }
    ];
}
