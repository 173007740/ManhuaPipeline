using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Director;
using System.Text.Json;
using Xunit;

namespace ManhuaPipeline.Tests;

public class DirectorComplianceGuardTests
{
    private static StageUnit Unit(string type = "打斗/动作") =>
        new()
        {
            EpisodeNumber = 1,
            UnitNumber = "1.1",
            Type = type,
            Duration = 11,
            RawText = "岳沉天与太虚圣主交手"
        };

    private static DirectorPlan Plan(string primary, string? secondary = null, int intensity = 8) =>
        new()
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            UnitNumber = "1.1",
            PrimarySubject = primary,
            SecondarySubject = secondary ?? "",
            IntensityLevel = intensity
        };

    [Fact]
    public void CompliantCombatUnit_Passes()
    {
        var result = """
            镜头编号: 1.4-1
            描述: 少年岳沉天突进与太虚圣主对撞，命中后双方各自震退
            镜头编号: 1.4-2
            描述: 岳沉天反打一回合，压制对方
            镜头编号: 1.4-3
            描述: 岳沉天受击后硬抗反击
            """;

        var check = DirectorComplianceGuard.Check(Plan("岳沉天", "太虚圣主"), Unit(), result);

        Assert.True(check.Passed);
    }

    [Fact]
    public void MissingPrimarySubject_Fails()
    {
        var result = """
            镜头编号: 1.4-1
            描述: 太虚圣主抬手压阵
            镜头编号: 1.4-2
            描述: 太虚圣主宣判
            """;

        var check = DirectorComplianceGuard.Check(Plan("岳沉天", "太虚圣主"), Unit(), result);

        Assert.False(check.Passed);
        Assert.Contains("核心主体", check.Reason);
    }

    [Fact]
    public void TooFewShotsForHighIntensityCombat_Fails()
    {
        var result = """
            镜头编号: 1.4-1
            描述: 岳沉天挥拳命中太虚圣主
            """;

        var check = DirectorComplianceGuard.Check(Plan("岳沉天", "太虚圣主", intensity: 9), Unit(), result);

        Assert.False(check.Passed);
        Assert.Contains("镜头数不足", check.Reason);
    }

    [Fact]
    public void HighIntensityCombatWithoutRhythm_Fails()
    {
        var result = """
            镜头编号: 1.4-1
            描述: 岳沉天握拳看向太虚圣主
            镜头编号: 1.4-2
            描述: 太虚圣主居高临下
            镜头编号: 1.4-3
            描述: 岳沉天站定
            """;

        var check = DirectorComplianceGuard.Check(Plan("岳沉天", "太虚圣主", intensity: 9), Unit(), result);

        Assert.False(check.Passed);
        Assert.Contains("攻防回合", check.Reason);
    }

    [Fact]
    public void PrefixedSubjectName_MatchesBareName()
    {
        var result = """
            镜头编号: 1.4-1
            描述: 少年岳沉天抬拳，倒影中岳沉天握剑
            镜头编号: 1.4-2
            描述: 岳沉天出手
            """;

        var check = DirectorComplianceGuard.Check(Plan("前世岳沉天"), Unit("文戏/情感"), result);

        Assert.True(check.Passed);
    }

    [Fact]
    public void NullPlan_AlwaysPasses()
    {
        Assert.True(DirectorComplianceGuard.Check(null, Unit(), "随便一段文字").Passed);
    }

    [Fact]
    public void MissingDirectorBeat_FailsWithBeatReason()
    {
        var plan = Plan("岳沉天", "太虚圣主", intensity: 5);
        plan.CombatRoundCount = 2;
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "岳沉天",
            EnemyIds = ["太虚圣主"],
            CombatGrammarIds = ["T1_DODGE_COUNTER", "T4_AOE_BREAK"]
        });

        var result = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天侧身闪避并反击
            镜头编号: 1.1-2
            节拍: Beat1
            描述: 岳沉天借位压制
            """;

        var check = DirectorComplianceGuard.Check(plan, Unit(), result);

        Assert.False(check.Passed);
        Assert.Contains("漏拍 Beat02", check.Reason);
    }

    [Fact]
    public void SoloOrMovementCombat_IsExemptFromRoundCountCheck()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.9",
            Type = "打斗/动作",
            Duration = 11,
            CoreAction = "发动踏天步踏空而来，越过荒野落在枯井边",
            RawText = "踏天步位移展示"
        };
        var plan = Plan("少年岳沉天", intensity: 9);
        plan.ConflictType = "1V1";
        plan.CombatRoundCount = 3;
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "少年岳沉天",
            RoundCount = 3,
            CombatGrammarIds = []
        });

        var result = """
            镜头编号: 1.9-1
            描述: 少年岳沉天一步踏出，足底白色气环炸开
            镜头编号: 1.9-2
            描述: 岳沉天飞掠荒野
            镜头编号: 1.9-3
            描述: 岳沉天落在枯井边
            """;

        var check = DirectorComplianceGuard.Check(plan, unit, result);

        Assert.True(check.Passed, check.Reason ?? "");
    }

    [Fact]
    public void NonCombatSecondarySubjectProp_IsNotStrictlyChecked()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.5",
            Type = "文戏/情感",
            Duration = 5,
            RawText = "少年岳沉天握紧宋小川旧拳带"
        };
        var plan = Plan("少年岳沉天", "宋小川旧拳带", intensity: 2);
        plan.ConflictType = "NonCombat";

        var result = """
            镜头编号: 1.5-1
            描述: 少年岳沉天靠神像坐下，双手一圈圈摩挲旧拳带
            镜头编号: 1.5-2
            描述: 旧拳带在烛火下微微反光，少年岳沉天抬头
            """;

        var check = DirectorComplianceGuard.Check(plan, unit, result);

        Assert.True(check.Passed, check.Reason ?? "");
    }

    [Fact]
    public void QiBurstSoloUnit_IsExemptFromRoundCountCheck()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.6",
            Type = "高潮/对决",
            Duration = 11,
            CoreAction = "两世记忆完整归位，三缕赤金气血依次爆发",
            RawText = "三缕赤金气血依次爆发：残烛熄灭、碎瓦悬起、夜云中分"
        };
        var plan = Plan("少年岳沉天战斗态", intensity: 9);
        plan.ConflictType = "1VN";
        plan.CombatRoundCount = 3;
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "少年岳沉天战斗态",
            RoundCount = 3,
            CombatGrammarIds = ["T3_PIN_DOWN", "T4_AOE_BREAK", "T5_FINAL_BREAK"]
        });

        var result = """
            镜头编号: 1.6-1
            节拍: Beat1
            描述: 少年岳沉天第一缕赤金气血定点压制，残烛熄灭
            镜头编号: 1.6-2
            节拍: Beat2
            描述: 少年岳沉天第二缕赤金气血范围扩散，碎瓦悬起
            镜头编号: 1.6-3
            节拍: Beat3
            描述: 少年岳沉天第三缕赤金气血冲天，夜云中分定格
            """;

        var check = DirectorComplianceGuard.Check(plan, unit, result);

        Assert.True(check.Passed);
    }

    [Fact]
    public void SingleStrikeTemplate_IsExemptFromRoundCountCheck()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.2",
            Type = "高潮/对决",
            Duration = 11,
            CoreAction = "愤怒宣言，抬拳震台，天地剧震",
            RawText = "岳沉天看穿阵纹，抬拳震台"
        };
        var plan = Plan("前世岳沉天战斗态", intensity: 8);
        plan.ConflictType = "1VN";
        plan.CombatRoundCount = 3;
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "前世岳沉天战斗态",
            RoundCount = 3,
            CombatGrammarIds = ["T3_SURROUND_ATTACK", "T1_DODGE_COUNTER", "T4_AOE_BREAK"]
        });

        var result = """
            - **打斗模板**: 4 / 大招蓄势 · 单发重击
            镜头编号: 1.2-1
            节拍: Beat1
            描述: 前世岳沉天战斗态看穿阵纹，怒斥
            镜头编号: 1.2-2
            节拍: Beat2
            描述: 前世岳沉天战斗态抬拳
            镜头编号: 1.2-3
            节拍: Beat3
             描述: 前世岳沉天战斗态一拳砸落，天地剧震
            """;

        var check = DirectorComplianceGuard.Check(plan, unit, result);

        Assert.True(check.Passed, check.Reason ?? "");
    }

    [Fact]
    public void ExplicitVfxPeakMarkers_OverrideDensityDetection()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.4",
            Type = "高潮/对决",
            Duration = 11,
            CoreAction = "阵心贯穿，金身崩碎，武印化光",
            RawText = "岳沉天以武极金身硬抗七宗合击"
        };
        var plan = Plan("前世岳沉天战斗态", intensity: 9);
        plan.ConflictType = "1VN";
        plan.VfxPeakPhase = "Late";
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "前世岳沉天战斗态",
            RoundCount = 3,
            CombatGrammarIds = ["T3_SURROUND_ATTACK", "T4_AOE_BREAK", "T5_FINAL_BREAK"]
        });

        var result = """
            镜头编号: 1.4-1
            节拍: Beat1
            描述: 前世岳沉天战斗态硬抗七色阵光，赤金气血炸开（VFX 40%）
            镜头编号: 1.4-2
            节拍: Beat2
            描述: 前世岳沉天战斗态硬格合围，法光对撞反击
            镜头编号: 1.4-3
            节拍: Beat3
             描述: 前世岳沉天战斗态武极金身崩碎命中，本单元VFX峰值100%
            """;

        var check = DirectorComplianceGuard.Check(plan, unit, result);

        Assert.True(check.Passed, check.Reason ?? "");
    }

    [Fact]
    public void CombinedSecondarySubject_MatchesEachPartIndependently()
    {
        var plan = Plan("前世岳沉天战斗态", "七宗宗主与[七曜诛圣阵]", intensity: 9);
        plan.ConflictType = "1VN";
        plan.CombatRoundCount = 3;
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "前世岳沉天战斗态",
            RoundCount = 3,
            CombatGrammarIds = ["T3_SURROUND_ATTACK", "T4_AOE_BREAK", "T5_FINAL_BREAK"]
        });

        var result = """
            镜头编号: 1.3-1
            节拍: Beat1
            描述: 七曜诛圣阵七道法光落下，七宗宗主同时催动阵纹，正面压进侧翼夹击后方封路，前世岳沉天战斗态保持核心站位硬抗
            镜头编号: 1.3-2
            节拍: Beat2
            描述: 前世岳沉天战斗态爆发气血范围冲击，震开全部敌人，七宗宗主被震退失衡
            镜头编号: 1.3-3
            节拍: Beat3
            描述: 前世岳沉天战斗态施展终结技，一拳轰向献祭阵眼，命中后战斗结束胜负已定
            """;

        var check = DirectorComplianceGuard.Check(plan, Unit(), result);

        Assert.True(check.Passed, check.Reason ?? "");
    }

    [Fact]
    public void ExplicitVfxPeakWordingVariants_OverrideDensityDetection()
    {
        var plan = Plan("前世岳沉天战斗态", "七宗宗主", intensity: 9);
        plan.ConflictType = "1VN";
        plan.VfxPeakPhase = "Late";
        plan.CombatRoundCount = 3;
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "前世岳沉天战斗态",
            RoundCount = 3,
            CombatGrammarIds = ["T3_SURROUND_ATTACK", "T4_AOE_BREAK", "T5_FINAL_BREAK"]
        });

        var result = """
            镜头编号: 1.4-1
            节拍: Beat1
            描述: 七宗宗主正面压进侧翼夹击后方封路，前世岳沉天战斗态保持核心站位格挡
            镜头编号: 1.4-2
            节拍: Beat2
            描述: 前世岳沉天战斗态受击后反打，爆发瞬间急推顿帧，气血范围冲击震开全部敌人
            镜头编号: 1.4-3
            节拍: Beat3
            描述: 武极金身崩碎，金色碎片四溅，全场特效峰值，终结技对撞后战斗结束胜负已定
            """;

        var check = DirectorComplianceGuard.Check(plan, Unit(), result);

        Assert.True(check.Passed, check.Reason ?? "");
    }
}
