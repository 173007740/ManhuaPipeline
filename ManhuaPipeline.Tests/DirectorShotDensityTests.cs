using ManhuaPipeline.Models;
using ManhuaPipeline.Models.Combat;
using ManhuaPipeline.Services;
using ManhuaPipeline.Services.Combat;
using ManhuaPipeline.Services.Director;
using System.Text.Json;
using Xunit;

namespace ManhuaPipeline.Tests;

public class DirectorShotDensityTests
{
    private static StageUnit CombatUnit() =>
        new()
        {
            EpisodeNumber = 1,
            UnitNumber = "1.1",
            Type = "打斗/动作",
            Duration = 11,
            RawText = "岳沉天与太虚圣主交手"
        };

    private static DirectorPlan Plan()
    {
        var plan = new DirectorPlan
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            UnitNumber = "1.1",
            PrimarySubject = "岳沉天",
            SecondarySubject = "太虚圣主",
            IntensityLevel = 8,
            CombatRoundCount = 3,
            VfxPeakPhase = "Late",
            ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
            {
                PrimaryFighterId = "岳沉天",
                EnemyIds = ["太虚圣主"],
                RoundCount = 3,
                CombatGrammarIds = ["T3_SURROUND_ATTACK", "T1_DODGE_COUNTER", "T4_AOE_BREAK"],
                EndingState = "太虚圣主被震退"
            })
        };
        return plan;
    }

    private static List<CombatBeat> TwoBeats() =>
    [
        new()
        {
            Index = 1,
            GrammarId = "T1_DODGE_COUNTER",
            AttackerId = "岳沉天",
            TargetIds = ["太虚圣主"],
            ActionDescription = "爆发灵力，蹬地突进，第一剑横斩",
            DefenseResponse = "侧身闪避，后仰",
            Result = "剑锋擦过衣袍",
            SpatialRelation = "近身交错",
            Intensity = "高"
        },
        new()
        {
            Index = 2,
            GrammarId = "T2_CLOSE_COUNTER",
            AttackerId = "岳沉天",
            TargetIds = ["太虚圣主"],
            ActionDescription = "侧步切入，左手扣腕，右拳蓄力",
            DefenseResponse = "格挡",
            Result = "重拳命中，太虚圣主被震退",
            SpatialRelation = "贴身",
            Intensity = "极高"
        }
    ];

    [Fact]
    public void ActionChainBuilder_SplitsBeatIntoMicroActionsAndParallelEvents()
    {
        var chains = ActionChainBuilder.Build(TwoBeats(), new DirectorActionPlan
        {
            PrimaryFighterId = "岳沉天",
            EnemyIds = ["太虚圣主"]
        });

        Assert.Equal(2, chains.Count);
        Assert.Equal(["Beat01"], chains[0].CombatBeatIds);
        Assert.Equal(["Beat02"], chains[1].CombatBeatIds);
        Assert.True(chains[0].Actions.Count >= 8, "一个 Beat 应展开为多个顺序动作 + 并行事件");
        Assert.Contains(chains[0].Actions, a => a.IsParallel);
        Assert.Contains(chains[0].Actions, a => a.Kind == "Camera");
        Assert.Contains(chains[0].Actions, a => a.Kind == "Environment");
    }

    [Fact]
    public void ShotPacker_PacksMultipleBeatsIntoSingleHighDensityShot()
    {
        var chains = ActionChainBuilder.Build(TwoBeats(), new DirectorActionPlan
        {
            PrimaryFighterId = "岳沉天",
            EnemyIds = ["太虚圣主"]
        });
        var packed = ShotPacker.Pack(chains, CombatUnit(), Plan());

        var shot = Assert.Single(packed);
        Assert.Equal(11, shot.DurationSeconds);
        Assert.Equal(["Beat01", "Beat02"], shot.CombatBeatIds);
        Assert.Equal(["AC01", "AC02"], shot.ActionChainIds);
        Assert.Equal(5, shot.Timeline.Count);
        Assert.Contains("太虚圣主被震退", shot.EndState);

        var text = ShotPacker.BuildPlanText(packed);
        Assert.Contains("建议 11秒", text);
        Assert.Contains("覆盖节拍: Beat01,Beat02", text);
        Assert.Contains("LLM 可按内容密度调整为 5/11/15", text);
    }

    [Fact]
    public void ShotPacker_BuildTimeline_UsesElevenSecondSegments()
    {
        var actions = new List<MicroAction>
        {
            new() { Actor = "岳沉天", Action = "突进", IsParallel = false },
            new() { Actor = "太虚圣主", Action = "格挡", IsParallel = false },
            new() { Actor = "镜头", Action = "贴身跟拍", IsParallel = true }
        };

        var segments = ShotPacker.BuildTimeline(actions, 11);

        Assert.Equal(5, segments.Count);
        Assert.Equal(0, segments[0].StartSecond);
        Assert.Equal(2, segments[0].EndSecond);
        Assert.Equal(2, segments[1].StartSecond);
        Assert.Equal(4, segments[1].EndSecond);
        Assert.Equal(4, segments[2].StartSecond);
        Assert.Equal(6, segments[2].EndSecond);
        Assert.Equal(6, segments[3].StartSecond);
        Assert.Equal(8, segments[3].EndSecond);
        Assert.Equal(8, segments[4].StartSecond);
        Assert.Equal(11, segments[4].EndSecond);
    }

    [Fact]
    public void ShotPacker_BuildTimeline_UsesFiveOneSecondSegmentsForFiveSeconds()
    {
        var actions = new List<MicroAction>
        {
            new() { Actor = "岳沉天", Action = "突进", IsParallel = false },
            new() { Actor = "太虚圣主", Action = "格挡", IsParallel = false },
            new() { Actor = "镜头", Action = "贴身跟拍", IsParallel = true }
        };

        var segments = ShotPacker.BuildTimeline(actions, 5);

        Assert.Equal(5, segments.Count);
        Assert.Equal(0, segments[0].StartSecond);
        Assert.Equal(1, segments[0].EndSecond);
        Assert.Equal(1, segments[1].StartSecond);
        Assert.Equal(2, segments[1].EndSecond);
        Assert.Equal(2, segments[2].StartSecond);
        Assert.Equal(3, segments[2].EndSecond);
        Assert.Equal(3, segments[3].StartSecond);
        Assert.Equal(4, segments[3].EndSecond);
        Assert.Equal(4, segments[4].StartSecond);
        Assert.Equal(5, segments[4].EndSecond);
    }

    [Theory]
    [InlineData("- **节拍**: Beat1", "1")]
    [InlineData("- **节拍**: Beat1,Beat2", "1,2")]
    [InlineData("节拍: Beat3、Beat1", "1,3")]
    [InlineData("节拍: 2,4", "2,4")]
    [InlineData("Beat2 和 Beat5", "2,5")]
    [InlineData("- **节拍**: beat1,BEAT2", "1,2")]
    [InlineData("节拍: 无", null)]
    [InlineData("承接节拍: 无", null)]
    public void CombatBeatIds_ParsesSingleAndMultiBeat(string line, string? expected)
    {
        Assert.Equal(expected, StoryboardFrameParser.TryGetCombatBeatIds(line));
    }

    [Fact]
    public void ShotHeader_CompactBracketFormat_IsRecognized()
    {
        Assert.Equal("1.2-2", StoryboardFrameParser.TryGetShotNumber("### 【镜头1.2-2】（对峙·合围压力段 0-4s）"));
        Assert.Equal("1.2-3", StoryboardFrameParser.TryGetShotNumber("### 【镜头1.2-3】（言语交锋·怒斥段 4-8s）"));
    }

    [Fact]
    public void MultiBeatStoryboard_PassesWithoutSplittingEveryBeat()
    {
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1,Beat2
            描述: 太虚圣主突进合围，岳沉天侧闪格挡反击
            起始画面: 双方对峙
            结束画面: 岳沉天切入
            镜头编号: 1.1-2
            节拍: Beat2,Beat3
            描述: 岳沉天反打命中，太虚圣主受击后仰
            起始画面: 岳沉天切入
            结束画面: 太虚圣主后仰
            镜头编号: 1.1-3
            节拍: Beat3
            描述: 岳沉天气血爆发，太虚圣主被震退
            起始画面: 气血凝聚
            结束画面: 胜负已定
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard);

        Assert.True(result.Passed, result.Verdict);
        Assert.False(result.HasHardFailure);
        Assert.Equal(100, result.Score);
        Assert.DoesNotContain(result.Violations, v => v.Code == "COMBAT_BEAT_MISSING");
    }

    [Fact]
    public void ShotFragmentation_IsFlaggedForHighIntensityCombat()
    {
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天挥出一拳
            起始画面: 岳沉天抬手
            结束画面: 岳沉天收拳
            镜头编号: 1.1-2
            节拍: Beat2
            描述: 太虚圣主原地站定
            起始画面: 双方对峙
            结束画面: 太虚圣主未动
            镜头编号: 1.1-3
            节拍: Beat3
            描述: 岳沉天收回拳势，太虚圣主被震退
            起始画面: 岳沉天收拳
            结束画面: 太虚圣主被震退
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard);

        Assert.False(result.Passed);
        var violation = Assert.Single(result.Violations, v => v.Code == "SHOT_FRAGMENTATION");
        Assert.Contains("ActionChain", violation.RepairInstruction);
    }

    [Fact]
    public void CombatTimeBudgetBuilder_ContainsDurationAndDensityRules()
    {
        var text = CombatTimeBudgetBuilder.Build(CombatUnit(), null, beatCount: 3);

        Assert.Contains("11 秒", text);
        Assert.Contains("快闪", text);
        Assert.Contains("时间轴", text);
        Assert.Contains("5/11/15", text);
    }

    [Fact]
    public void CombatTimeline_CoversShotDuration_Passes()
    {
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1,Beat2,Beat3
            镜头时间轴: 0-2s: 突进合围；2-4s: 侧闪格挡；4-6s: 变招压制；6-8s: 反打连击；8-11s: 重拳命中
            镜头时长: 11秒
            描述: 岳沉天连续三回合攻防，太虚圣主被震退
            起始画面: 双方对峙
            结束画面: 太虚圣主被震退
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard);

        Assert.DoesNotContain(result.Violations, v => v.Code == "TIME_BUDGET_MISMATCH");
    }

    [Fact]
    public void CombatTimeline_ShorterThanShotDuration_Flagged()
    {
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1,Beat2,Beat3
            镜头时间轴: 0-2s: 突进合围；2-4s: 侧闪格挡；4-5s: 重拳命中
            镜头时长: 11秒
            描述: 岳沉天连续三回合攻防，太虚圣主被震退
            起始画面: 双方对峙
            结束画面: 太虚圣主被震退
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard);

        var violation = Assert.Single(result.Violations, v => v.Code == "TIME_BUDGET_MISMATCH");
        Assert.Contains("11", violation.Expected);
    }

    [Fact]
    public void CombatUnit_FewerThanThreeShots_HardFails()
    {
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1,Beat2,Beat3
            描述: 岳沉天连续三回合攻防，太虚圣主被震退
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard);

        Assert.True(result.HasHardFailure);
        var violation = Assert.Single(result.Violations, v => v.Code == "SHOT_COUNT_LOW");
        Assert.Contains("3", violation.Expected);
    }

    [Fact]
    public void NonCombatUnit_FewerThanTwoShots_HardFails()
    {
        var unit = new StageUnit
        {
            EpisodeNumber = 1,
            UnitNumber = "1.12",
            Type = "文戏/情感",
            Duration = 5,
            RawText = "岳沉天凝视阵纹" 
        };
        var plan = new DirectorPlan
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            UnitNumber = "1.12",
            PrimarySubject = "岳沉天",
            IntensityLevel = 2,
            ConflictType = "NonCombat"
        };
        var storyboard = """
            镜头编号: 1.12-1
            描述: 岳沉天垂目凝视阵纹
            """;

        var result = DirectorRuleValidator.Validate(plan, unit, storyboard);

        Assert.True(result.HasHardFailure);
        var violation = Assert.Single(result.Violations, v => v.Code == "SHOT_COUNT_LOW");
        Assert.Contains("2", violation.Expected);
    }
}
