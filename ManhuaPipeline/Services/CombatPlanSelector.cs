using System.Text;
using System.Text.RegularExpressions;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Combat;

namespace ManhuaPipeline.Services;

/// <summary>
/// 打斗模板 / 运镜原子 / 技能库的选择打分器。
/// 打分全部基于关键词匹配 + n-gram 共同子串，权重数值已由测试锁定，改动需谨慎。
/// </summary>
public static class CombatPlanSelector
{
    // ==================== n-gram 打分词表 ====================

    /// <summary>n-gram 共同子串打分时忽略的高频噪声词。</summary>
    private static readonly HashSet<string> GenericGrams = new(StringComparer.OrdinalIgnoreCase)
    {
        "画面", "镜头", "场景", "人物", "背景", "视觉", "效果", "缓慢", "快速", "瞬间",
        "氛围", "气势", "构图", "光线", "空间", "动作", "情绪", "视角", "正面", "侧面",
        "远景", "全景", "近景", "特写", "动态", "静态", "细节", "强烈", "自然", "逐渐",
        "最终", "整个", "中心", "前方", "后方", "上方", "下方", "方向", "状态", "结束",
        "开始", "核心", "关键", "元素", "单元", "类型", "时长", "地点", "对话", "台词",
        "起始", "一个", "之间", "同时", "形成", "出现", "切换", "展现", "营造", "增强",
        "完成", "推进", "移动", "跟随", "方式", "整体", "明显", "层次", "丰富",
        "清晰", "流畅", "饱满", "完整", "紧密", "稳定"
    };

    /// <summary>场景亲和分组：镜头文本与单元上下文命中同一组词即视为场景契合。</summary>
    private static readonly string[][] SceneGroups =
    {
        new[] { "雪域", "风雪", "雪地", "雪山", "冰雪", "霜", "寒", "雪夜", "冰" },
        new[] { "海", "崖", "碧海", "海风", "海浪", "海水", "海面", "巨浪" },
        new[] { "沙漠", "戈壁", "沙海", "黄沙" },
        new[] { "边境", "关隘", "边关", "骑兵", "烽火", "城墙" },
        new[] { "机甲", "赛博", "霓虹", "机房", "车流", "都市", "高楼", "仓库", "城市", "电车" },
        new[] { "森林", "树林", "林木", "枝叶", "密林", "林间" },
        new[] { "火", "焰", "焚", "燃", "炎", "赤焰", "火场", "烈火", "火莲", "烈阳" },
        new[] { "雷", "电", "霆" },
        new[] { "剑", "刀", "兵刃", "剑光", "剑阵", "刃" },
        new[] { "雨", "湖", "江", "河", "水", "海" },
        new[] { "殿", "宫", "王座", "祭坛", "神庙", "大厅", "室内" },
        new[] { "山门", "宗门", "仙门", "石阶", "山道", "门派" },
        new[] { "高空", "空中", "天空", "云端", "上空", "飞行", "坠落" },
        new[] { "祭", "阵", "法阵", "阵眼", "符文", "灵光" },
        new[] { "峡谷", "裂谷", "谷底", "山壁", "峭壁", "石柱", "岩石" },
        new[] { "书", "卷", "纸", "案", "密信", "文书", "卷宗", "古籍" },
        new[] { "追", "逃", "奔", "跑", "脚步", "追击" },
        new[] { "屋顶", "天台", "夜雨", "瓦" },
        new[] { "火山", "祖坛", "岩浆", "熔岩" },
        new[] { "月光", "黑夜", "暗", "影", "煞", "雾" },
        new[] { "黎明", "破晓", "晨光", "日出", "朝阳", "清晨", "拂晓" },
        new[] { "逆光", "剪影", "夕照", "余晖", "暮色", "黄昏" }
    };

    // ==================== 模板选择词表 ====================

    /// <summary>单人冲突词：纯单人战斗命中任一即排除该模板。</summary>
    private static readonly string[] SoloConflictKeywords =
    {
        "双方", "对射", "互射", "对轰", "对撞", "互怼", "反打", "缠斗", "擒拿", "制服",
        "围攻", "围杀", "群战", "护主", "对砍", "正反打", "灵宠", "追逃", "追杀", "追兵",
        "对手", "敌人", "敌方", "防御", "格挡", "绝境反杀", "秒杀", "打脸", "贴身", "肘膝",
        "摔投", "突刺", "贯穿", "压制对手"
    };

    /// <summary>位移/轻功系战斗形态。</summary>
    private static readonly string[] MovementCombatForms = { "位移", "轻功", "赶路", "疾行", "纵跃", "腾跃", "移动" };

    /// <summary>多敌上下文词：命中即不是纯单人战斗。</summary>
    private static readonly string[] MultiCombatContext = { "敌人", "敌方", "对手", "追兵", "混战", "多人", "围杀", "围攻", "群战" };

    /// <summary>徒手/近战形态词。</summary>
    private static readonly string[] MeleeCombatForms = { "徒手", "拳脚", "肘", "膝", "摔", "擒", "格斗", "掌", "体术" };

    /// <summary>冷兵器关键词（判定参与者是否实际持械）。</summary>
    private static readonly string[] WeaponKeywords = { "刀", "剑", "枪", "兵刃", "兵器", "刃", "斧", "戟", "锤" };

    /// <summary>模板名称/标签中的冷兵器标记。</summary>
    private static readonly string[] ColdWeaponTemplateMarkers = { "刀", "剑", "枪", "兵刃", "对砍", "斩", "刺", "冷兵器" };

    /// <summary>近身格斗形态词。</summary>
    private static readonly string[] CloseCombatForms = { "近身", "肘", "膝", "摔", "擒", "格斗", "拳脚" };

    /// <summary>擒拿/控制形态词。</summary>
    private static readonly string[] GrapplingForms = { "擒", "擒拿", "锁", "控", "卸" };

    /// <summary>远程/对轰形态词。</summary>
    private static readonly string[] RangedForms = { "远程", "对轰" };

    /// <summary>模板中的远程标记（跳过冲突判断用，含"火符"）。</summary>
    private static readonly string[] RangedTemplateMarkers = { "远程", "对轰", "法术互射", "火符" };

    /// <summary>模板中的远程标记（加分用，不含"火符"，与原始打分行为一致）。</summary>
    private static readonly string[] RangedBonusTemplateMarkers = { "远程", "对轰", "法术互射" };

    /// <summary>模板中的近身标记（跳过冲突判断用，不含"格斗"）。</summary>
    private static readonly string[] CloseCombatTemplateMarkers = { "徒手", "近身", "肘", "膝", "摔投", "贴身" };

    /// <summary>模板中的近身标记（加分用，含"格斗"，与原始打分行为一致）。</summary>
    private static readonly string[] CloseCombatBonusTemplateMarkers = { "徒手", "近身", "肘", "膝", "摔投", "格斗", "贴身" };

    /// <summary>模板中的擒拿标记。</summary>
    private static readonly string[] GrapplingTemplateMarkers = { "擒拿", "控拿", "锁拿", "卸兵器", "制服" };

    /// <summary>群战模板标记。</summary>
    private static readonly string[] GroupTemplateMarkers = { "群战", "破阵", "围杀", "护主", "一夫当关", "突围", "围攻", "压制", "冲锋" };

    /// <summary>位移系应排除的站桩类模板标记。</summary>
    private static readonly string[] MovementBlockedTemplateMarkers = { "对砍", "群战", "围攻", "大招", "技能", "灵宠", "收招" };

    /// <summary>单人爆发向关键词：命中后对单人战斗额外加分。</summary>
    private static readonly string[] SoloBurstKeywords = { "蓄势", "单发重击", "大招", "法相", "虚影", "全力一击", "收招", "定格", "轻功", "位移", "纵跃", "疾行", "爆发", "气血", "天地变色" };

    /// <summary>闪避/坠物系形态词。</summary>
    private static readonly string[] DodgeRelatedForms = { "闪避", "躲避", "陨石", "坠物" };

    /// <summary>闪避/坠物系模板标记。</summary>
    private static readonly string[] DodgeRelatedTemplateMarkers = { "闪避", "躲避", "陨石", "坠物", "环境" };

    /// <summary>位移爆发向模板标记。</summary>
    private static readonly string[] MovementBurstTemplateMarkers = { "轻功", "位移", "纵跃", "疾行" };

    /// <summary>高潮/终极形态词：命中即放开 T5 技能上限。</summary>
    private static readonly string[] ClimaxCombatForms = { "大招", "对决", "终极", "对撞", "压轴", "天地" };

    /// <summary>灵宠类模板标记。</summary>
    private static readonly string[] PetTemplateMarkers = { "灵宠", "召唤兽", "人宠", "兽宠", "战宠" };

    /// <summary>灵宠上下文正则模式。</summary>
    private const string PetContextPattern = "灵宠|召唤兽|灵兽|妖兽|战宠|神宠|宠物|兽宠";

    /// <summary>追逐类模板标记。</summary>
    private static readonly string[] ChaseTemplateMarkers = { "追逐", "逃杀", "奔逃", "追逃" };

    /// <summary>追逐上下文正则模式。</summary>
    private const string ChaseContextPattern = "追逐|追逃|奔逃|逃杀|逃跑|追击";

    /// <summary>群战模板标记（模板识别用）。</summary>
    private static readonly string[] GroupTemplateMarkers2 = { "群战", "围杀", "围攻", "军阵", "多人" };

    /// <summary>群战上下文正则模式（HasGroupContext 用，需按词拆分匹配）。</summary>
    private const string GroupContextPattern =
        "群战|围攻|围杀|混战|多人|军队|军阵|包围|围困|被围|合围|围剿|围堵|四面|四面合围|封天锁地|人群|群体战|" +
        "弟子们|众弟子|数名弟子|数十弟子|多名弟子|几名弟子|四名弟子|五名弟子|一群弟子|扇形|火墙压制|列阵|阵列";

    /// <summary>防御型上下文词：命中即走内置防御模板（模板库为空时也能兜底）。</summary>
    private static readonly string[] DefensiveContextMarkers =
    {
        "被围", "围困", "围杀", "围攻", "被轰", "被击", "崩碎", "碎裂", "战败", "战死",
        "陨落", "牺牲", "硬抗", "受击", "被困", "困于", "镇杀", "封死", "死域", "轰向",
        "同时落下", "撑住", "无法"
    };

    /// <summary>防御模板"被围"分支标记。</summary>
    private static readonly string[] DefensiveGroupMarkers =
    {
        "被围", "围困", "围杀", "围攻", "围剿", "围堵", "包围", "群敌", "四面", "合围",
        "镇杀", "封死", "困于", "被困"
    };

    /// <summary>防御模板"战败"分支标记。</summary>
    private static readonly string[] DefeatedMarkers =
    {
        "战败", "战死", "陨落", "牺牲", "崩碎", "碎裂", "死域", "败亡", "重伤", "坠落"
    };

    /// <summary>技能文本匹配时视为通用技能、不做归属匹配的名称。</summary>
    private static readonly string[] GenericSkillNames = { "拳风", "火墙", "冲击", "罡气", "气劲", "剑气", "护体" };

    // ==================== 运镜原子打分词表 ====================

    /// <summary>战斗上下文词（ScoreCameraAtom）。</summary>
    private static readonly string[] CombatContextMarkers =
    {
        "打斗", "战斗", "对决", "追杀", "追击", "奔逃", "逃", "拳", "掌", "剑", "刀", "技能", "大招"
    };

    /// <summary>悬疑上下文词（ScoreCameraAtom）。</summary>
    private static readonly string[] SuspenseContextMarkers =
    {
        "悬疑", "惊悚", "神秘", "线索", "调查", "夜", "暗", "阴影"
    };

    /// <summary>战斗类标签（ScoreCameraAtom）。</summary>
    private static readonly string[] CombatAtomTags = { "打斗", "追逐", "高潮" };

    /// <summary>文戏类标签（ScoreCameraAtom）。</summary>
    private static readonly string[] DramaAtomTags = { "文戏", "日常" };

    // ==================== 元素关键词表（技能打分用） ====================

    // CombatElementHintBonus：元素与文本的直接线索
    private static readonly string[] BodyHintKeywords = { "拳", "气血", "金身", "罡", "震", "武", "近身", "掌", "腿", "摔", "抱", "踏", "身法" };
    private static readonly string[] FireHintKeywords = { "火", "焰", "焚", "燃", "炎", "赤焰", "烈阳" };
    private static readonly string[] IceHintKeywords = { "冰", "霜", "雪", "寒", "凝月" };
    private static readonly string[] ThunderHintKeywords = { "雷", "电", "霆", "紫电", "雷罚" };
    private static readonly string[] WindHintKeywords = { "风", "云", "翼", "腾跃", "疾行" };
    private static readonly string[] SwordHintKeywords = { "剑", "刃", "刀", "斩", "剑阵", "剑光" };
    private static readonly string[] DarkHintKeywords = { "暗", "影", "煞", "夜", "黑", "魔", "幽冥" };
    private static readonly string[] HolyHintKeywords = { "圣", "光", "辉", "神", "天", "金身", "法相" };

    // CombatFormElementBonus：元素与战斗形态的匹配（近身形态会压制元素远程加成）
    private static readonly string[] MeleeElementForms = { "近身", "拳脚", "近战", "摔", "肘", "膝", "擒", "抱", "体术", "格斗" };
    private static readonly string[] BodyElementForms = { "拳", "脚", "近身", "近战", "体", "摔", "肘", "膝", "擒", "抱", "掌", "腿", "格斗" };
    private static readonly string[] FireElementForms = { "火", "焰", "焚", "燃", "炎", "法", "术", "符", "烈阳" };
    private static readonly string[] IceElementForms = { "冰", "霜", "雪", "寒", "法", "术" };
    private static readonly string[] ThunderElementForms = { "雷", "电", "霆", "法", "术" };
    private static readonly string[] WindElementForms = { "风", "云", "翼", "腾跃" };
    private static readonly string[] SwordElementForms = { "剑", "刃", "刀", "斩" };
    private static readonly string[] DarkElementForms = { "暗", "影", "煞", "夜", "黑", "魔" };
    private static readonly string[] HolyElementForms = { "圣", "光", "辉", "神", "天", "法", "术" };

    // ElementContextBonus：元素与整体上下文的匹配
    private static readonly string[] FireContextKeywords = { "火", "焰", "焚", "燃", "炎", "赤焰" };
    private static readonly string[] IceContextKeywords = { "冰", "霜", "雪", "寒" };
    private static readonly string[] ThunderContextKeywords = { "雷", "电", "霆" };
    private static readonly string[] SwordContextKeywords = { "剑", "刃", "斩", "锋", "剑阵" };
    private static readonly string[] WindContextKeywords = { "风", "云", "翼" };
    private static readonly string[] DarkContextKeywords = { "暗", "影", "煞", "夜", "黑" };
    private static readonly string[] HolyContextKeywords = { "圣", "光", "辉", "神", "天" };
    private static readonly string[] BodyContextKeywords = { "拳", "体", "金身", "罡", "震", "武", "气血", "近身", "肘", "膝", "肩", "脚", "掌", "腿", "摔", "抱", "步" };

    // ==================== 公共 API ====================

    /// <summary>
    /// 从模板库中为一次战斗意图挑选最合适的打斗模板。
    /// 导演点名（directorTemplate）优先锁定；防御型上下文走内置模板；否则逐模板打分。
    /// </summary>
    public static FightTemplateItem? SelectFightTemplate(
        List<FightTemplateItem> templates,
        CombatIntent intent,
        string? directorTemplate = null,
        string? unitText = null)
    {
        string context = BuildCombatContext(unitText, intent);

        // 1) 导演显式点名模板：优先按 FightTemplateId，其次按名称包含匹配，命中即锁定。
        if (!string.IsNullOrWhiteSpace(directorTemplate) && templates != null && templates.Count > 0)
        {
            string dt = directorTemplate.Trim();
            FightTemplateItem? byId = templates.FirstOrDefault(t => t.FightTemplateId.ToString() == dt);
            if (byId != null) return byId;
            FightTemplateItem? byName = templates.FirstOrDefault(t => t.Name.Contains(dt, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
        }

        // 2) 防御型上下文：模板库可能为空，直接走内置防御模板兜底。
        if (IsDefensiveContext(context)) return BuildDefensiveTemplate(intent);

        // 3) 模板库为空：无模板可选。
        if (templates == null || templates.Count == 0) return null;

        // 4) 参数归约：时长仅认 5/11/15 三档，其余归一为 11；参与人数折算为 单人/双人/多人。
        int effectiveDuration = intent.Duration is 5 or 11 or 15 ? intent.Duration : 11;
        string participantLabel = intent.Participants.Count switch
        {
            1 => "单人",
            2 => "双人",
            _ => "多人"
        };

        bool wantsMovement = ContainsAny(intent.CombatForm, MovementCombatForms);            // 位移/轻功系
        bool isSoloFight = intent.Participants.Count == 1                                     // 纯单人且无多敌上下文
            && !HasGroupContext(context)
            && !ContainsAny(context, MultiCombatContext);
        bool isGroupFight = intent.Participants.Count > 2 || HasGroupContext(context);        // 群战
        bool meleeOrMovementForm = ContainsAny(intent.CombatForm, MeleeCombatForms) || wantsMovement;
        bool participantsHaveWeapons = intent.Participants.Any(p =>
            !string.IsNullOrWhiteSpace(p.Weapon) && ContainsAny(p.Weapon, WeaponKeywords));

        // 5) 逐模板打分筛选。
        FightTemplateItem? bestTemplate = null;
        int bestScore = int.MinValue;
        foreach (FightTemplateItem template in templates)
        {
            string templateBlob = BuildTemplateBlob(template);

            // 5.1 模板类别与上下文不符 → 跳过
            if ((IsPetTemplate(template) && !ContextHasPet(context))
                || (IsChaseTemplate(template) && !TextMatchesAny(context, ChaseContextPattern))
                || (IsGroupTemplate(template) && !isGroupFight))
                continue;

            // 5.2 徒手/位移系战斗撞上冷兵器模板、但参与者并未持械 → 跳过
            bool templateIsColdWeapon = ContainsAny(template.Name + " " + template.Tags, ColdWeaponTemplateMarkers);
            if (meleeOrMovementForm && templateIsColdWeapon && !participantsHaveWeapons)
                continue;

            bool closeCombatForm = ContainsAny(intent.CombatForm, CloseCombatForms);
            bool grapplingForm = ContainsAny(intent.CombatForm, GrapplingForms);

            // 5.3 形态冲突 → 跳过
            // a. 近身格斗不要远程对轰模板
            if (closeCombatForm && !ContainsAny(intent.CombatForm, RangedForms)
                && ContainsAny(template.Name + " " + template.Tags, RangedTemplateMarkers))
                continue;
            // b. 擒拿系必须命中擒拿模板
            if (grapplingForm
                && ContainsAny(template.Name + " " + template.Tags, CloseCombatTemplateMarkers)
                && !ContainsAny(template.Name + " " + template.Tags, GrapplingTemplateMarkers))
                continue;
            // c. 群战必须用群战模板
            if (isGroupFight && !ContainsAny(template.Name + " " + template.Tags, GroupTemplateMarkers))
                continue;
            // d. 位移系排除站桩类模板
            if (wantsMovement && ContainsAny(template.Name + " " + template.Tags, MovementBlockedTemplateMarkers))
                continue;
            // e. 纯单人战斗排除含多人/对抗冲突词的模板
            if (isSoloFight && ContainsAny(templateBlob, SoloConflictKeywords))
                continue;

            // 5.4 结构与层级分（时长匹配 / 强度匹配）
            int structureScore = 0;
            if (template.Duration == effectiveDuration) structureScore += 40;
            else if (Math.Abs(template.Duration - effectiveDuration) <= 4) structureScore += 10;
            if (template.Tier == intent.Intensity) structureScore += 20;
            else if (Math.Abs(template.Tier - intent.Intensity) <= 1) structureScore += 8;

            // 5.5 语义匹配分（形态 / 环境 / 上下文 / 参与人数 / 节拍 / 场景 / n-gram）
            int semanticScore = 0;
            if (TextMatchesAny(templateBlob, intent.CombatForm)) semanticScore += 60;
            if (TextMatchesAny(templateBlob, intent.EnvironmentType)) semanticScore += 40;
            if (TextMatchesAny(templateBlob, context)) semanticScore += 50;
            if (TextMatchesAny(templateBlob, participantLabel)) semanticScore += 20;
            if (BeatMatches(template.Beat, intent.RequiredActions)) semanticScore += 40;
            if (TextMatchesAny(template.Scene, context)) semanticScore += 30;
            semanticScore += SharedPhraseScore(context, templateBlob) / 2;
            if (isSoloFight && ContainsAny(templateBlob, SoloBurstKeywords)) semanticScore += 80;

            // 5.6 形态专属加分（模板名称+标签层面）
            string templateNameAndTags = template.Name + " " + template.Tags;
            if (isGroupFight && ContainsAny(templateNameAndTags, GroupTemplateMarkers)) structureScore += 80;
            if (grapplingForm && ContainsAny(templateNameAndTags, GrapplingTemplateMarkers)) structureScore += 120;
            if (ContainsAny(intent.CombatForm, RangedForms) && ContainsAny(templateNameAndTags, RangedBonusTemplateMarkers)) structureScore += 60;
            if (ContainsAny(intent.CombatForm, DodgeRelatedForms) && ContainsAny(templateNameAndTags, DodgeRelatedTemplateMarkers)) structureScore += 60;
            if (wantsMovement && ContainsAny(templateNameAndTags, MovementBurstTemplateMarkers)) structureScore += 80;
            if (closeCombatForm && !ContainsAny(intent.CombatForm, RangedForms) && ContainsAny(templateNameAndTags, CloseCombatBonusTemplateMarkers)) structureScore += 80;

            // 5.7 选优：总分高者胜；平分时用 n-gram 共同子串分破平。
            int totalScore = structureScore + semanticScore;
            if (totalScore > bestScore)
            {
                bestScore = totalScore;
                bestTemplate = template;
            }
            else if (totalScore == bestScore && bestTemplate != null)
            {
                int candidatePhraseScore = SharedPhraseScore(context, templateBlob);
                if (candidatePhraseScore > SharedPhraseScore(context, BuildTemplateBlob(bestTemplate)))
                {
                    bestScore = totalScore;
                    bestTemplate = template;
                }
            }
        }
        return bestTemplate;
    }

    /// <summary>
    /// 从运镜原子库中按槽位选出一组镜头原子（景别/运镜/运镜/光影/转场）。
    /// 运镜槽位刻意出现两次：允许同时选中两个运镜原子。
    /// </summary>
    public static List<CameraAtomItem> SelectCameraAtoms(List<CameraAtomItem> atoms, StageUnit unit, CombatIntent intent)
    {
        var result = new List<CameraAtomItem>();
        if (atoms == null || atoms.Count == 0) return result;

        string context = BuildDirectiveContext(unit, intent);
        var source = atoms
            .Select(a => new ScoredCameraAtom(a, ScoreCameraAtom(a, context, unit.Location, unit.KeyElements)))
            .ToList();

        var used = new HashSet<int>();
        foreach (string slot in new[] { "景别", "运镜", "运镜", "光影", "转场" })
        {
            // 优先取本槽位类别中分数最高且未使用的原子；该类别无可用则退而取任意未使用原子。
            ScoredCameraAtom? best = source
                .Where(x => !used.Contains(x.Atom.AtomId))
                .Where(x => slot switch
                {
                    "运镜" => x.Atom.Category == "运镜",
                    "转场" => x.Atom.Category == "转场",
                    _ => x.Atom.Category is "景别" or "光影"
                })
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            best ??= source
                .Where(x => !used.Contains(x.Atom.AtomId))
                .OrderByDescending(x => x.Score)
                .FirstOrDefault();
            if (best == null) break;

            result.Add(best.Atom);
            used.Add(best.Atom.AtomId);
        }
        return result;
    }

    /// <summary>
    /// 为战斗意图挑选技能：分集明确点名的技能直接锁定（不受 T5 上限过滤），
    /// 否则只从本单元出场角色归属的技能中按动作语义打分，最多取 3 个。
    /// </summary>
    public static List<SkillLibraryItem> SelectSkills(
        List<SkillLibraryItem> skills,
        CombatIntent intent,
        string unitText,
        List<string>? characterNames = null,
        IReadOnlyList<CharacterAsset>? characters = null)
    {
        if (skills == null || skills.Count == 0) return new List<SkillLibraryItem>();

        string context = BuildCombatContext(unitText, intent);
        bool isClimax = intent.Intensity >= 4 || ContainsAny(intent.CombatForm, ClimaxCombatForms);
        int maxTier = isClimax ? 5 : Math.Max(1, Math.Min(3, intent.Intensity));

        List<string> requiredSkills = intent.RequiredSkills ?? new List<string>();
        bool anyRequiredSkillMatched = requiredSkills.Count > 0
            && requiredSkills.Any(req => skills.Any(s => MatchesRequiredSkill(s.Name, req)));

        // 意图点名技能命中 → 直接锁定被点名的技能。
        if (requiredSkills.Count > 0 && anyRequiredSkillMatched)
        {
            return skills
                .Where(s => requiredSkills.Any(req => MatchesRequiredSkill(s.Name, req)))
                .Select(s => (Skill: s, Score: ScoreSkill(s, intent, unitText, context)))
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Skill.Tier)
                .Take(3)
                .Select(x => x.Skill)
                .ToList();
        }

        // 未点名 → 归属过滤 + 层级上限 + 语义分 > 0，最多取 3 个。
        return skills
            .Where(s => OwnerMatches(s, characterNames, characters))
            .Where(s => s.Tier <= maxTier)
            .Select(s => (Skill: s, Score: ScoreSkill(s, intent, unitText, context)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Skill.Tier)
            .Take(3)
            .Select(x => x.Skill)
            .ToList();
    }

    /// <summary>
    /// 在分镜单元原始文本中做技能名/标签的纯文本匹配，命中分集细化点名的技能（防丢失兜底）。
    /// </summary>
    public static List<SkillLibraryItem> MatchSkillsToText(
        List<SkillLibraryItem> skills,
        string text,
        List<string>? characterNames = null,
        IReadOnlyList<CharacterAsset>? characters = null)
    {
        if (!IsCombatUnitText(text)) return new List<SkillLibraryItem>();
        if (skills == null || skills.Count == 0 || string.IsNullOrWhiteSpace(text)) return new List<SkillLibraryItem>();

        // 从单元文本里解析出场角色，与传入的角色名单合并。
        List<string> unitCharacters = ExtractUnitCharacterNames(text);
        if (characterNames == null || characterNames.Count == 0)
        {
            characterNames = unitCharacters;
        }
        else if (unitCharacters.Count > 0)
        {
            characterNames = characterNames.Concat(unitCharacters).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return skills
            .Where(s => !GenericSkillNames.Contains(s.Name, StringComparer.OrdinalIgnoreCase))
            .Where(s => OwnerMatches(s, characterNames, characters))
            .Select(s => (Skill: s, Score: ScoreSkillText(s, text)))
            .Where(x => x.Score >= 30)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Skill.Tier)
            .Take(4)
            .Select(x => x.Skill)
            .ToList();
    }

    /// <summary>由战斗意图生成内置防御模板（被围/战败/常规三分支）。</summary>
    public static FightTemplateItem BuildDefensiveTemplate(CombatIntent intent)
    {
        string context = BuildCombatContext(null, intent);
        return BuildDefensiveTemplateCore(context, intent.Duration, intent.Intensity);
    }

    /// <summary>按模板名解析内置防御模板（Stage 9 衔接兜底用），未命中返回 null。</summary>
    public static FightTemplateItem? TryResolveDefensiveTemplateByName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        return BuildDefensiveTemplateCore(name, 11, 3);
    }

    // ==================== 打分实现 ====================

    /// <summary>运镜原子打分：名称直击 + n-gram 匹配 + 场景亲和 + 类别基础分 + 标签加成。</summary>
    private static int ScoreCameraAtom(CameraAtomItem atom, string context, string? location = null, string? keyElements = null)
    {
        string atomText = atom.Name + " " + atom.Description;
        int score = 0;

        // 1) 名称直接出现在上下文中
        if (!string.IsNullOrWhiteSpace(atom.Name) && context.Contains(atom.Name, StringComparison.OrdinalIgnoreCase))
            score += 60;

        // 2) n-gram 共同子串（名称权重最高，位置/元素次之）
        score += SharedPhraseScore(context, atom.Name) * 3;
        score += SharedPhraseScore(location, atom.Name) * 5;
        score += SharedPhraseScore(keyElements, atom.Name) * 4;
        score += SharedPhraseScore(context, atomText) / 2;

        // 3) 场景亲和
        score += SceneAffinity(atomText, context);

        // 4) 类别基础分
        score += atom.Category switch
        {
            "运镜" => 8,
            "景别" => 7,
            "转场" => 6,
            "光影" => 5,
            "节奏" => 6,
            _ => 0
        };

        // 5) 标签加成
        var tags = SplitTags(atom.Tags).ToList();
        bool isCombatContext = ContainsAny(context, CombatContextMarkers);
        bool isSuspenseContext = ContainsAny(context, SuspenseContextMarkers);
        if (isCombatContext && tags.Any(t => t is "打斗" or "追逐" or "高潮")) score += 45;
        if (!isCombatContext && tags.Any(t => t is "文戏" or "日常")) score += 25;
        if (isSuspenseContext && tags.Contains("悬疑")) score += 30;
        if (tags.Count == 0 || tags.Contains("通用")) score += 10;

        return score;
    }

    /// <summary>技能打分：n-gram + 名称直击 + 元素匹配 + 点名技能/要求动作命中。</summary>
    private static int ScoreSkill(SkillLibraryItem skill, CombatIntent intent, string unitText, string context)
    {
        string skillName = skill.Name ?? "";
        var tags = SplitTags(skill.Tags).ToList();
        string tagsText = string.Join(" ", tags);
        string nameAndTags = skillName + " " + tagsText;
        string promptText = skill.PromptVideo + " " + skill.PromptImage;

        int score = SharedPhraseScore(context, nameAndTags);
        if (!string.IsNullOrWhiteSpace(unitText) && unitText.Contains(skillName, StringComparison.OrdinalIgnoreCase))
            score += 80;
        score += CombatFormElementBonus(skill.Element, intent.CombatForm);
        score += ElementContextBonus(skill.Element, context);

        // 分集点名技能命中 → 大幅加分（技能名、公共子串、标签逐级递减）。
        foreach (string requiredSkill in intent.RequiredSkills)
        {
            if (string.IsNullOrWhiteSpace(requiredSkill)) continue;
            if (skillName.Contains(requiredSkill, StringComparison.OrdinalIgnoreCase)
                || requiredSkill.Contains(skillName, StringComparison.OrdinalIgnoreCase))
                score += 120;
            else if (CommonSubstringLength(skillName, requiredSkill) >= 2)
                score += 60;
            else if (!string.IsNullOrWhiteSpace(tagsText) && tagsText.Contains(requiredSkill, StringComparison.OrdinalIgnoreCase))
                score += 40;
        }

        // 要求动作命中 → 加分（技能名/标签、提示词、n-gram 逐级递减）。
        foreach (string requiredAction in intent.RequiredActions)
        {
            if (string.IsNullOrWhiteSpace(requiredAction)) continue;
            if (skillName.Contains(requiredAction, StringComparison.OrdinalIgnoreCase)
                || requiredAction.Contains(skillName, StringComparison.OrdinalIgnoreCase)
                || (!string.IsNullOrWhiteSpace(tagsText) && tagsText.Contains(requiredAction, StringComparison.OrdinalIgnoreCase)))
                score += 40;
            else if (promptText.Contains(requiredAction, StringComparison.OrdinalIgnoreCase))
                score += 20;
            else if (SharedPhraseScore(requiredAction, promptText) >= 4)
                score += 10;
        }
        return score;
    }

    /// <summary>技能文本匹配打分（MatchSkillsToText 用）：名称直击 + 标签命中 + 元素线索。</summary>
    private static int ScoreSkillText(SkillLibraryItem skill, string text)
    {
        string skillName = skill.Name ?? "";
        var tags = SplitTags(skill.Tags).ToList();
        string nameAndTags = skillName + " " + string.Join(" ", tags);

        int score = SharedPhraseScore(text, nameAndTags);
        // 注意：skillName 为空时 text.Contains("") 恒为 true，技能名缺失会被无条件加分（保留原行为）。
        if (text.Contains(skillName, StringComparison.OrdinalIgnoreCase)) score += 120;
        foreach (string tag in tags)
        {
            if (tag.Length >= 2 && text.Contains(tag, StringComparison.OrdinalIgnoreCase)) score += 60;
        }
        score += ElementContextBonus(skill.Element, text);
        return score + CombatElementHintBonus(skill.Element, text);
    }

    /// <summary>元素线索加分：文本命中元素关键词。</summary>
    private static int CombatElementHintBonus(string element, string text) => element switch
    {
        "体" => ContainsAny(text, BodyHintKeywords) ? 25 : 0,
        "火" => ContainsAny(text, FireHintKeywords) ? 25 : 0,
        "冰" => ContainsAny(text, IceHintKeywords) ? 25 : 0,
        "雷" => ContainsAny(text, ThunderHintKeywords) ? 25 : 0,
        "风" => ContainsAny(text, WindHintKeywords) ? 25 : 0,
        "剑阵" => ContainsAny(text, SwordHintKeywords) ? 25 : 0,
        "暗" => ContainsAny(text, DarkHintKeywords) ? 25 : 0,
        "圣" => ContainsAny(text, HolyHintKeywords) ? 25 : 0,
        _ => 0
    };

    /// <summary>元素-形态加分：体系偏爱近身，远程元素在近身形态下不加分。</summary>
    private static int CombatFormElementBonus(string element, string combatForm)
    {
        if (string.IsNullOrWhiteSpace(combatForm)) return 0;
        bool meleeForm = ContainsAny(combatForm, MeleeElementForms);
        return element switch
        {
            "体" => ContainsAny(combatForm, BodyElementForms) ? 40 : 0,
            "火" => (!meleeForm && ContainsAny(combatForm, FireElementForms)) ? 35 : 0,
            "冰" => (!meleeForm && ContainsAny(combatForm, IceElementForms)) ? 35 : 0,
            "雷" => (!meleeForm && ContainsAny(combatForm, ThunderElementForms)) ? 35 : 0,
            "风" => ContainsAny(combatForm, WindElementForms) ? 30 : 0,
            "剑阵" => ContainsAny(combatForm, SwordElementForms) ? 30 : 0,
            "暗" => ContainsAny(combatForm, DarkElementForms) ? 30 : 0,
            "圣" => ContainsAny(combatForm, HolyElementForms) ? 30 : 0,
            _ => 0
        };
    }

    /// <summary>元素-上下文加分：整体上下文命中元素关键词。</summary>
    private static int ElementContextBonus(string element, string context) => element switch
    {
        "火" => ContainsAny(context, FireContextKeywords) ? 20 : 0,
        "冰" => ContainsAny(context, IceContextKeywords) ? 20 : 0,
        "雷" => ContainsAny(context, ThunderContextKeywords) ? 20 : 0,
        "剑阵" => ContainsAny(context, SwordContextKeywords) ? 20 : 0,
        "风" => ContainsAny(context, WindContextKeywords) ? 20 : 0,
        "暗" => ContainsAny(context, DarkContextKeywords) ? 20 : 0,
        "圣" => ContainsAny(context, HolyContextKeywords) ? 20 : 0,
        "体" => ContainsAny(context, BodyContextKeywords) ? 20 : 0,
        _ => 0
    };

    // ==================== 文本匹配工具 ====================

    /// <summary>n-gram 共同子串打分：6→2 长度滑窗，命中 +长度×3，去重并跳过停用词。</summary>
    private static int SharedPhraseScore(string? context, string? text)
    {
        if (string.IsNullOrWhiteSpace(context) || string.IsNullOrWhiteSpace(text)) return 0;

        string text2 = Regex.Replace(context, "[\\s\\r\\n，。！？、；：,.;:！？（）()]+", "");
        string text3 = Regex.Replace(text, "[\\s\\r\\n，。！？、；：,.;:！？（）()]+", "");
        int num = 0;
        var hashSet = new HashSet<string>();
        for (int num2 = 6; num2 >= 2; num2--)
        {
            for (int i = 0; i <= text2.Length - num2; i++)
            {
                string text4 = text2.Substring(i, num2);
                if (hashSet.Add(text4) && !GenericGrams.Contains(text4) && text3.Contains(text4, StringComparison.OrdinalIgnoreCase))
                    num += num2 * 3;
            }
        }
        return num;
    }

    /// <summary>两个字符串的最长公共子串长度（区分大小写）。</summary>
    private static int CommonSubstringLength(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return 0;

        int alen = a.Length;
        int blen = b.Length;
        int best = 0;
        var dp = new int[alen + 1, blen + 1];
        for (int i = 1; i <= alen; i++)
        {
            for (int j = 1; j <= blen; j++)
            {
                if (a[i - 1] == b[j - 1])
                {
                    dp[i, j] = dp[i - 1, j - 1] + 1;
                    if (dp[i, j] > best) best = dp[i, j];
                }
            }
        }
        return best;
    }

    /// <summary>点名技能与技能名是否命中：包含关系，或公共子串接近完整名。</summary>
    private static bool MatchesRequiredSkill(string skillName, string requiredName)
    {
        if (string.IsNullOrWhiteSpace(skillName) || string.IsNullOrWhiteSpace(requiredName)) return false;
        if (skillName.Contains(requiredName, StringComparison.OrdinalIgnoreCase)
            || requiredName.Contains(skillName, StringComparison.OrdinalIgnoreCase))
            return true;
        return requiredName.Length >= 4 && CommonSubstringLength(skillName, requiredName) >= requiredName.Length - 1;
    }

    /// <summary>上下文是否出现灵宠相关词。</summary>
    private static bool ContextHasPet(string context) => TextMatchesAny(context, PetContextPattern);

    /// <summary>上下文是否出现群战相关词（按词拆分匹配，命中任一群战词即视为群战上下文）。</summary>
    private static bool HasGroupContext(string context) => TextMatchesAny(context, GroupContextPattern);

    /// <summary>模板是否为灵宠类模板（名称+场景+标签）。</summary>
    private static bool IsPetTemplate(FightTemplateItem template) => ContainsAny(template.Name + " " + template.Scene + " " + template.Tags, PetTemplateMarkers);

    /// <summary>模板是否为追逐类模板（名称+场景+标签）。</summary>
    private static bool IsChaseTemplate(FightTemplateItem template) => ContainsAny(template.Name + " " + template.Scene + " " + template.Tags, ChaseTemplateMarkers);

    /// <summary>模板是否为群战模板（名称+场景+标签）。</summary>
    private static bool IsGroupTemplate(FightTemplateItem template) => ContainsAny(template.Name + " " + template.Scene + " " + template.Tags, GroupTemplateMarkers2);

    /// <summary>上下文是否触发防御型战斗。</summary>
    private static bool IsDefensiveContext(string context) => ContainsAny(context, DefensiveContextMarkers);

    /// <summary>
    /// 内置防御模板核心：按 被围 / 常规 / 战败 三分支生成，被围场景时长不足 11 秒时强制拉到 11 秒。
    /// 模板文案为运营打磨产物，改动前需确认下游提示词守卫与之匹配。
    /// </summary>
    private static FightTemplateItem BuildDefensiveTemplateCore(string context, int duration, int intensity)
    {
        bool isSurrounded = ContainsAny(context, DefensiveGroupMarkers);
        bool isDefeated = ContainsAny(context, DefeatedMarkers);
        int tier = Math.Clamp(intensity, 1, 5);
        int seconds = duration is 5 or 11 or 15 ? duration : 11;
        if (isSurrounded && seconds < 11) seconds = 11;

        FightTemplateItem item;
        if (isSurrounded)
        {
            item = new FightTemplateItem
            {
                Name = "被围硬抗 · 围杀突围",
                Scene = "被围困、围攻、围杀、被群敌合拢逼近的防御型战斗",
                Beat = "被围（压迫）→ 格挡闪避反击（快）→ 护体硬抗破阵（重）",
                ActionPrompt = "A:被围在正中，前后左右兵刃与攻势同时合拢逼近；A 侧身闪开第一波，架臂格挡第二波，反手连击点倒左右两敌、踢开正面一敌；正面重击砸来，A 以护体/气血硬抗，震开攻势后爆发气浪将一圈敌人掀飞，突围而出",
                CameraPrompt = "围拢段缓慢环绕并缓缓压低，营造压迫感；闪避反击段手持快切、甩镜跟随 A 身形；硬抗瞬间推进至接触点顿帧 0.3-0.5 秒，突围瞬间拉高俯拍慢动作看气浪扩散",
                ConstraintPrompt = "围拢要有压迫感（镜头缓慢、敌人同步逼近）；闪避反击要快、动作干净；硬抗与突围命中瞬间必须顿帧接慢动作；禁止敌人原地待机，必须同时出手；禁止 A 全程站桩或被一击打倒",
                Tags = "群战,围杀,被围,硬抗,突围,防御"
            };
        }
        else if (!isDefeated)
        {
            item = new FightTemplateItem
            {
                Name = "受击硬抗 · 格挡反打",
                Scene = "受击、被轰、硬抗、撑住、绝境防守后反击的防御型战斗",
                Beat = "受击（重）→ 格挡硬抗（顿）→ 反打（快）",
                ActionPrompt = "A:正面重击压来，A 不闪不避，双臂交叉/护体硬接一击，脚下退半步卸力、地面开裂；随即架开或震开攻势，抓住对手旧力未收的瞬间一记重拳/重击反打，将对手逼退半步；双方重新拉开距离对峙",
                CameraPrompt = "受击瞬间推进至接触点特写并轻微晃动；硬抗段顿帧 0.3-0.5 秒看衣袍/护体受击反馈；反打用快速甩镜跟随拳头，命中后慢动作看对手后仰/退步",
                ConstraintPrompt = "硬抗要有重量感（受击顿帧+慢动作+环境反馈）；反打要快、干净利落；禁止 A 被一击打倒；禁止对手打完就原地站桩，必须保持攻防回合",
                Tags = "受击,硬抗,格挡,反击,防御"
            };
        }
        else
        {
            item = new FightTemplateItem
            {
                Name = "受击败退 · 硬抗坠落",
                Scene = "战败、受击败退、护体崩碎、陨落牺牲的收束型战斗",
                Beat = "受击（重）→ 护体崩碎（顿）→ 败退（慢）",
                ActionPrompt = "A:正面攻势轰至，A 强行硬抗一击，护体/罡气当场崩碎、碎片四溅；A 被震退数步、气血翻涌，强行稳住身形仍被余波压得踉跄/坠落，败退收场",
                CameraPrompt = "受击瞬间推进特写+轻微晃动；护体崩碎顿帧 0.5 秒看碎片与气浪飞溅；败退段慢动作跟拍 A 后退/坠落",
                ConstraintPrompt = "受击要真实（顿帧+碎片/尘土细节）；败退要有重量感，禁止 A 毫发无损站定；禁止战斗结果被改写成反杀或胜利",
                Tags = "受击,败退,硬抗,坠落,防御"
            };
        }

        item.FightTemplateId = 0;
        item.UserId = 0;
        item.Tier = tier;
        item.Duration = seconds;
        return item;
    }

    /// <summary>文本是否包含任一关键词（忽略大小写与空白关键词）。</summary>
    private static bool ContainsAny(string text, params string[] keywords)
    {
        return keywords.Any(k => !string.IsNullOrWhiteSpace(k) && text.Contains(k, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>按分隔符切分标签/节拍文本，返回 Trim 后长度 ≥2 的片段。</summary>
    private static IEnumerable<string> SplitTags(string? tags)
    {
        if (string.IsNullOrWhiteSpace(tags)) return Enumerable.Empty<string>();
        return tags.Split(',', '，', '、', ';', '；', ' ', '|')
            .Select(t => t.Trim())
            .Where(t => t.Length >= 2);
    }

    /// <summary>构建打斗上下文字符串：单元原始文本 + 战斗意图全部字段。</summary>
    private static string BuildCombatContext(string? unitText, CombatIntent intent)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(unitText)) sb.Append(unitText).Append(' ');
        AppendIntentValues(sb, intent);
        return sb.ToString();
    }

    /// <summary>构建导演上下文字符串：单元字段 + 战斗意图全部字段。</summary>
    private static string BuildDirectiveContext(StageUnit unit, CombatIntent intent)
    {
        var sb = new StringBuilder();
        AppendValue(sb, unit.Location);
        AppendValue(sb, unit.CoreAction);
        AppendValue(sb, unit.StartState);
        AppendValue(sb, unit.EndState);
        AppendValue(sb, unit.KeyElements);
        AppendValue(sb, unit.Dialogue);
        AppendIntentValues(sb, intent);
        return sb.ToString();
    }

    /// <summary>追加战斗意图的公共字段（CombatForm → Participants），两个上下文构建方法共用。</summary>
    private static void AppendIntentValues(StringBuilder sb, CombatIntent intent)
    {
        AppendValue(sb, intent.CombatForm);
        AppendValue(sb, intent.EnvironmentType);
        AppendValue(sb, intent.Objective);
        AppendValue(sb, intent.Result);
        AppendValue(sb, intent.StartState);
        AppendValue(sb, intent.EndState);
        foreach (string skill in intent.RequiredSkills) AppendValue(sb, skill);
        foreach (string action in intent.RequiredActions) AppendValue(sb, action);
        foreach (CombatParticipant participant in intent.Participants)
        {
            AppendValue(sb, participant.Name);
            AppendValue(sb, participant.Weapon);
        }
    }

    /// <summary>非空字段追加到 StringBuilder，字段间以空格分隔。</summary>
    private static void AppendValue(StringBuilder sb, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) sb.Append(value).Append(' ');
    }

    /// <summary>模板全部字段拼接为一个待匹配文本。</summary>
    private static string BuildTemplateBlob(FightTemplateItem template) => string.Join(" ",
        template.Name, template.Scene, template.Tags, template.Beat,
        template.ActionPrompt, template.CameraPrompt, template.ConstraintPrompt);

    /// <summary>needle 整体命中，或按分隔符拆词后互相包含（长度 ≥2 的词）。</summary>
    private static bool TextMatchesAny(string? haystack, string? needle)
    {
        if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle)) return false;
        if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;

        var sourceWords = Regex.Split(needle, "[|｜,，、;；/／\\s]+").Where(s => s.Length >= 2).ToList();
        if (sourceWords.Count == 0) return false;
        var hayWords = Regex.Split(haystack, "[|｜,，、;；/／\\s]+").Where(s => s.Length >= 2).ToList();
        if (hayWords.Count == 0) return false;

        return hayWords.Any(hw => sourceWords.Any(sw =>
            hw.Contains(sw, StringComparison.OrdinalIgnoreCase) || sw.Contains(hw, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>模板节拍是否覆盖要求动作：节拍文本拆词后与任一要求动作互相包含。</summary>
    private static bool BeatMatches(string? beat, List<string>? requiredActions)
    {
        if (string.IsNullOrWhiteSpace(beat) || requiredActions == null || requiredActions.Count == 0) return false;

        var beatWords = Regex.Split(beat, "[→\\->,，、;；/／\\s]+").Where(s => s.Length >= 2).ToList();
        if (beatWords.Count == 0) return false;
        return requiredActions.Any(action => beatWords.Any(word =>
            action.Contains(word, StringComparison.OrdinalIgnoreCase)
            || word.Contains(action, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>场景亲和加分：镜头文本与上下文命中同一场景组即 +25。</summary>
    private static int SceneAffinity(string text, string context)
    {
        int score = 0;
        foreach (string[] group in SceneGroups)
        {
            if (ContainsAny(text, group) && ContainsAny(context, group))
                score += 25;
        }
        return score;
    }

    /// <summary>
    /// 技能归属过滤：技能未绑定角色则放行；否则角色名单（含别名）内任一角色命中即放行。
    /// </summary>
    private static bool OwnerMatches(SkillLibraryItem skill, IEnumerable<string>? characterNames, IReadOnlyList<CharacterAsset>? characters = null)
    {
        if (string.IsNullOrWhiteSpace(skill.OwnerCharacter)) return true;
        if (characterNames == null) return true;

        var aliases = CharacterAliasCatalog.GetOwnerAliases(skill.OwnerCharacter, characters);
        foreach (string name in characterNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            foreach (string alias in aliases)
            {
                if (name.Contains(alias, StringComparison.OrdinalIgnoreCase)
                    || alias.Contains(name, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 从单元文本提取出场角色名：
    /// 1) “@角色引用”行内的方括号名单；2) “- A=名字”式参与者行。
    /// </summary>
    private static List<string> ExtractUnitCharacterNames(string text)
    {
        var result = new List<string>();
        foreach (Match m in Regex.Matches(text, "@角色引用\\s*[:：]\\s*((?:\\[[^\\]\\r\\n]+\\])+)"))
        {
            foreach (Match name in Regex.Matches(m.Groups[1].Value, "\\[([^\\]\\r\\n]+)\\]"))
            {
                string trimmed = name.Groups[1].Value.Trim();
                if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    result.Add(trimmed);
            }
        }
        foreach (Match m in Regex.Matches(text, "(?m)^\\s*[-*]\\s*([A-C])\\s*=\\s*([^（(\\r\\n]+)"))
        {
            string trimmed = m.Groups[2].Value.Trim();
            if (trimmed.Length > 0 && !result.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                result.Add(trimmed);
        }
        return result;
    }

    /// <summary>是否为战斗型单元：单元类型/控制模式标注了打斗/高潮/对决/追逐/追杀，或控制模式含打斗模板。</summary>
    private static bool IsCombatUnitText(string text)
    {
        string type = Regex.Match(text, "(?:单元类型|控制模式|类型)\\s*[:：]\\s*([^\\r\\n]+)").Groups[1].Value;
        if (type.Contains("打斗") || type.Contains("高潮") || type.Contains("对决")
            || type.Contains("追逐") || type.Contains("逃亡") || type.Contains("追杀"))
            return true;
        return text.Contains("控制模式") && text.Contains("打斗模板");
    }

    /// <summary>带分数的运镜原子（SelectCameraAtoms 内部排序用）。</summary>
    private sealed record ScoredCameraAtom(CameraAtomItem Atom, int Score);
}
