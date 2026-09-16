using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Director;
using System.Text.Json;
using Xunit;

namespace ManhuaPipeline.Tests;

public class DirectorValidatorTests
{
    private static StageUnit Unit(int episode = 3, string unitNumber = "3.2")
        => new()
        {
            EpisodeNumber = episode,
            UnitNumber = unitNumber,
            Type = "打斗/动作",
            Duration = 11,
            RawText = "单元原文"
        };

    [Fact]
    public void ValidJson_IsMappedAndNormalized()
    {
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "hero",
              "secondarySubject": "villain",
              "conflictType": "1v1",
              "corePayoff": "hard hit",
              "emotionCurve": ["calm", "tense", "burst"],
              "rhythmStrategy": "slow to fast",
              "actionStrategy": "trade blows",
              "performanceStrategy": "rage",
              "cameraStrategy": "close push in",
              "vfxStrategy": "low then spike then stop",
              "intensityLevel": 12,
              "combatGrammarIds": [1, 2, 1]
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(7, Unit(), raw);

        Assert.NotNull(plan);
        Assert.Equal(7, plan.ProjectId);
        Assert.Equal("3.2", plan.UnitNumber);
        Assert.Equal("1V1", plan.ConflictType);
        Assert.Equal(10, plan.IntensityLevel);
        Assert.Equal("1,2", plan.CombatGrammarIds);
        Assert.Contains("calm", plan.EmotionCurve);
        Assert.Contains("burst", plan.EmotionCurve);
    }

    [Fact]
    public void InvalidJson_ReturnsNull()
    {
        Assert.Null(DirectorValidator.ValidateAndMap(1, Unit(), "{not json"));
    }

    [Fact]
    public void MarkdownWrappedJson_IsExtracted()
    {
        var raw = "```json\n{\"dramaticPurpose\":\"p\",\"primarySubject\":\"s\",\"conflictType\":\"NonCombat\",\"corePayoff\":\"c\",\"emotionCurve\":[\"e1\"],\"rhythmStrategy\":\"r\",\"actionStrategy\":\"a\",\"performanceStrategy\":\"p2\",\"cameraStrategy\":\"cam\",\"vfxStrategy\":\"v\",\"intensityLevel\":2}\n```";

        var plan = DirectorValidator.ValidateAndMap(1, Unit(), raw);

        Assert.NotNull(plan);
        Assert.Equal("s", plan.PrimarySubject);
    }

    [Fact]
    public void MissingRequiredField_ReturnsNull()
    {
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "hero",
              "conflictType": "1V1",
              "emotionCurve": ["calm"],
              "rhythmStrategy": "slow",
              "cameraStrategy": "push",
              "vfxStrategy": "low"
            }
            """;

        Assert.Null(DirectorValidator.ValidateAndMap(1, Unit(), raw));
    }

    [Fact]
    public void MissingStrategies_AreFilledWithDefaults()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.5",
            Type = "文戏/情感",
            Duration = 5,
            RawText = "少年岳沉天握紧旧拳带"
        };
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "hero",
              "conflictType": "文戏",
              "corePayoff": "reveal",
              "emotionCurve": ["calm", "tension"],
              "rhythmStrategy": "slow",
              "cameraStrategy": "fixed",
              "vfxStrategy": "none"
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(1, unit, raw);

        Assert.NotNull(plan);
        Assert.Equal("NonCombat", plan.ConflictType);
        Assert.False(string.IsNullOrWhiteSpace(plan.ActionStrategy));
        Assert.False(string.IsNullOrWhiteSpace(plan.PerformanceStrategy));
    }

    [Fact]
    public void ActionPlan_UnknownGrammar_IsNotPersisted()
    {
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "hero",
              "conflictType": "1VN",
              "corePayoff": "hard hit",
              "emotionCurve": ["calm", "burst"],
              "rhythmStrategy": "slow to fast",
              "cameraStrategy": "push in",
              "vfxStrategy": "late peak",
              "actionPlan": {
                "conflictType": "1VN",
                "primaryFighterId": "hero",
                "enemyIds": ["e1", "e2"],
                "combatStyle": "碾压",
                "roundCount": 3,
                "dominanceCurve": "压→破→胜",
                "combatGrammarIds": ["SUPER_COOL_DRAGON_ATTACK"],
                "endingState": "击退"
              },
              "combatRoundCount": 3,
              "vfxPeakPhase": "Late"
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(1, Unit(), raw);

        Assert.NotNull(plan);
        var actionPlan = JsonSerializer.Deserialize<DirectorActionPlan>(plan.ActionPlan);
        Assert.NotNull(actionPlan);
        Assert.Empty(actionPlan.CombatGrammarIds);
        Assert.Equal(3, plan.CombatRoundCount);
        Assert.Equal("Late", plan.VfxPeakPhase);
    }

    [Fact]
    public void ActionPlan_MissingRoundCount_InfersFromGrammarCount()
    {
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "hero",
              "conflictType": "1VN",
              "corePayoff": "hard hit",
              "emotionCurve": ["calm", "burst"],
              "rhythmStrategy": "slow to fast",
              "cameraStrategy": "push in",
              "vfxStrategy": "late peak",
              "actionPlan": {
                "conflictType": "1VN",
                "primaryFighterId": "hero",
                "enemyIds": ["e1", "e2"],
                "combatStyle": "碾压",
                "dominanceCurve": "压→破→胜",
                "combatGrammarIds": ["T1_DODGE_COUNTER", "T2_CLOSE_COUNTER"],
                "endingState": "击退"
              }
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(1, Unit(), raw);

        Assert.NotNull(plan);
        Assert.Equal(2, plan.CombatRoundCount);
        Assert.Equal("T1_DODGE_COUNTER,T2_CLOSE_COUNTER", plan.CombatGrammarIds);
    }

    [Fact]
    public void CombatUnitWithNonCombatLlm_IsForcedToCombatAndGetsFallbackPlan()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.2",
            Type = "高潮/对决",
            Duration = 11,
            KeyElements = "前世岳沉天战斗态、七宗宗主、献祭阵纹",
            RawText = "岳沉天看穿阵纹抽取三城生机，抬拳震台"
        };
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "前世岳沉天战斗态",
              "secondarySubject": "七宗宗主",
              "conflictType": "NonCombat",
              "corePayoff": "hard hit",
              "emotionCurve": ["calm", "burst"],
              "rhythmStrategy": "slow to fast",
              "cameraStrategy": "push in",
              "vfxStrategy": "late peak",
              "actionPlan": null,
              "combatRoundCount": 0,
              "vfxPeakPhase": "Late"
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(30, unit, raw, new List<string> { "前世岳沉天战斗态", "前世岳沉天", "七宗宗主", "残烛", "碎瓦" });

        Assert.NotNull(plan);
        Assert.Equal("1VN", plan.ConflictType);
        Assert.NotEmpty(plan.ActionPlan);
        var actionPlan = JsonSerializer.Deserialize<DirectorActionPlan>(plan.ActionPlan);
        Assert.NotNull(actionPlan);
        Assert.Equal("前世岳沉天战斗态", actionPlan.PrimaryFighterId);
        Assert.Contains("七宗宗主", actionPlan.EnemyIds);
        Assert.DoesNotContain("前世岳沉天", actionPlan.EnemyIds);
        Assert.DoesNotContain("残烛", actionPlan.EnemyIds);
        Assert.Equal(3, actionPlan.RoundCount);
        Assert.Equal(3, plan.CombatRoundCount);
    }

    [Fact]
    public void ActionPlan_UnknownEnemyNames_AreFilteredOut()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.6",
            Type = "高潮/对决",
            Duration = 11,
            CoreAction = "两世记忆完整归位，三缕赤金气血依次爆发",
            RawText = "单人气血爆发：残烛熄灭、碎瓦悬起、夜云中分"
        };
        var raw = """
            {
              "dramaticPurpose": "payoff",
              "primarySubject": "少年岳沉天战斗态",
              "conflictType": "1VN",
              "corePayoff": "hard hit",
              "emotionCurve": ["calm", "burst"],
              "rhythmStrategy": "slow to fast",
              "cameraStrategy": "push in",
              "vfxStrategy": "late peak",
              "actionPlan": {
                "primaryFighterId": "少年岳沉天战斗态",
                "enemyIds": ["残烛", "碎瓦", "夜云"],
                "combatGrammarIds": ["T3_PIN_DOWN"],
                "roundCount": 3
              }
            }
            """;

        var plan = DirectorValidator.ValidateAndMap(30, unit, raw, new List<string> { "少年岳沉天战斗态", "岳沉天" });

        Assert.NotNull(plan);
        var actionPlan = JsonSerializer.Deserialize<DirectorActionPlan>(plan.ActionPlan);
        Assert.NotNull(actionPlan);
        Assert.Empty(actionPlan.EnemyIds);
    }
}
