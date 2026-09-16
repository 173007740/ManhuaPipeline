using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Director;
using Xunit;

namespace ManhuaPipeline.Tests;

public class DirectorV3Tests
{
    private static List<StageUnit> Units()
    {
        return
        [
            new() { EpisodeNumber = 1, UnitNumber = "1.1", Type = "文戏/情感", Duration = 5 },
            new() { EpisodeNumber = 1, UnitNumber = "1.2", Type = "打斗/动作", Duration = 11 },
            new() { EpisodeNumber = 1, UnitNumber = "1.7", Type = "高潮/对决", Duration = 15 },
            new() { EpisodeNumber = 1, UnitNumber = "1.10", Type = "打斗/动作", Duration = 11 }
        ];
    }

    private static EpisodeDirectorPlan EpisodePlan(int limit = 4)
    {
        return new EpisodeDirectorPlan
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            EpisodeGoal = "测试集",
            IntensityCurve =
            [
                new UnitIntensityPlan { UnitNumber = "1.1", IntensityLimit = limit },
                new UnitIntensityPlan { UnitNumber = "1.2", IntensityLimit = limit },
                new UnitIntensityPlan { UnitNumber = "1.7", IntensityLimit = 10 },
                new UnitIntensityPlan { UnitNumber = "1.10", IntensityLimit = limit }
            ],
            RepetitionPolicy = new RepetitionPolicy
            {
                SlowMotionLimit = 2,
                MajorExplosionLimit = 1,
                CameraPatternLimits = [new PatternLimit { Pattern = "低机位", MaxCount = 2 }],
                CombatPatternLimits = [new PatternLimit { Pattern = "重拳", MaxCount = 2 }],
                VfxPatternLimits = [new PatternLimit { Pattern = "气血爆发", MaxCount = 1 }]
            }
        };
    }

    private static StageUnit Unit(string unitNumber) =>
        new() { EpisodeNumber = 1, UnitNumber = unitNumber };

    [Fact]
    public void Parse_FillsMissingIntensityCurveEntries()
    {
        var json = """
            {
              "episodeGoal": "复仇铺垫",
              "emotionCurve": "压抑 → 挑衅",
              "intensityCurve": [{"unitNumber": "1.1", "intensityLimit": 2}],
              "payoffSchedule": [],
              "reservedVisuals": [],
              "forbiddenEarlyPayoffs": [],
              "repetitionPolicy": {"slowMotionLimit": 2, "majorExplosionLimit": 1}
            }
            """;

        var plan = EpisodeDirectorPlanParser.Parse(json, Units(), 30, 1);

        Assert.NotNull(plan);
        Assert.Equal("复仇铺垫", plan!.EpisodeGoal);
        Assert.Equal(2, EpisodeDirectorPlanParser.GetIntensityLimit(plan, Unit("1.1")));
        Assert.True(EpisodeDirectorPlanParser.GetIntensityLimit(plan, Unit("1.2")) > 0);
        Assert.True(EpisodeDirectorPlanParser.GetIntensityLimit(plan, Unit("1.10")) > 0);
    }

    [Fact]
    public void EnforceUnitLimits_ClampsOverLimitPlan()
    {
        var unit = Unit("1.2");
        var plan = new DirectorPlan { ProjectId = 30, UnitNumber = "1.2", IntensityLevel = 9 };

        EpisodeDirectorPlanParser.EnforceUnitLimits(plan, EpisodePlan(4), unit);

        Assert.Equal(4, plan.IntensityLevel);
        Assert.True(plan.NeedsReview);
    }

    [Fact]
    public void RuleValidator_IntensityExceeded_Flags()
    {
        var plan = new DirectorPlan { UnitNumber = "1.2", IntensityLevel = 8 };
        var violations = EpisodeDirectorRuleValidator.Check(
            plan, Unit("1.2"), "岳沉天抬手压阵", EpisodePlan(4), null);

        Assert.Contains(violations, v => v.Code == "UNIT_INTENSITY_EXCEEDED");
    }

    [Fact]
    public void RuleValidator_ReservedVisualUsedEarly_Flags()
    {
        var episodePlan = EpisodePlan();
        episodePlan.ReservedVisuals =
        [
            new ReservedVisual { Visual = "法相天地", ReservedUnit = "1.7", Note = "仅高潮" }
        ];

        var violations = EpisodeDirectorRuleValidator.Check(
            null, Unit("1.2"), "法相天地拔地而起", episodePlan, null);

        Assert.Contains(violations, v => v.Code == "RESERVED_PAYOFF_USED_EARLY");
    }

    [Fact]
    public void RuleValidator_ReservedVisualAllowedOnReservedUnit_Passes()
    {
        var episodePlan = EpisodePlan();
        episodePlan.ReservedVisuals =
        [
            new ReservedVisual { Visual = "法相天地", ReservedUnit = "1.7", Note = "仅高潮" }
        ];

        var violations = EpisodeDirectorRuleValidator.Check(
            null, Unit("1.7"), "法相天地拔地而起", episodePlan, null);

        Assert.DoesNotContain(violations, v => v.Code == "RESERVED_PAYOFF_USED_EARLY");
    }

    [Fact]
    public void RuleValidator_PayoffOrderViolation_Flags()
    {
        var episodePlan = EpisodePlan();
        episodePlan.PayoffSchedule =
        [
            new PayoffPoint { UnitNumber = "1.7", Type = "阶段高潮", Name = "万剑齐发" }
        ];

        var violations = EpisodeDirectorRuleValidator.Check(
            null, Unit("1.2"), "万剑齐发，天地变色", episodePlan, null);

        Assert.Contains(violations, v => v.Code == "PAYOFF_ORDER_VIOLATION");
    }

    [Fact]
    public void RuleValidator_PayoffOrder_UsesNumericUnitOrder()
    {
        var episodePlan = EpisodePlan();
        episodePlan.PayoffSchedule =
        [
            new PayoffPoint { UnitNumber = "1.2", Type = "小爽点", Name = "重拳" }
        ];

        var violations = EpisodeDirectorRuleValidator.Check(
            null, Unit("1.10"), "重拳命中", episodePlan, null);

        Assert.DoesNotContain(violations, v => v.Code == "PAYOFF_ORDER_VIOLATION");
    }

    [Fact]
    public void RuleValidator_PatternOverused_Flags()
    {
        var episodePlan = EpisodePlan();
        var state = new EpisodeDirectorState
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            CameraPatternCounts = new Dictionary<string, int> { ["低机位"] = 2 }
        };

        var violations = EpisodeDirectorRuleValidator.Check(
            null, Unit("1.2"), "低机位跟拍重拳", episodePlan, state);

        Assert.Contains(violations, v => v.Code == "CAMERA_PATTERN_OVERUSED");
    }

    [Fact]
    public void StateUpdater_CountsPatternsSlowMotionAndExplosion()
    {
        var state = new EpisodeDirectorState { ProjectId = 30, EpisodeNumber = 1 };
        var plan = new DirectorPlan { UnitNumber = "1.2", IntensityLevel = 6 };

        EpisodeDirectorStateUpdater.Apply(
            state,
            EpisodePlan(),
            plan,
            "低机位跟拍重拳，气血爆发，命中瞬间慢动作，随后炸开");

        Assert.Equal(1, state.CameraPatternCounts["低机位"]);
        Assert.Equal(1, state.CombatPatternCounts["重拳"]);
        Assert.Equal(1, state.VfxPatternCounts["气血爆发"]);
        Assert.Equal(1, state.SlowMotionCount);
        Assert.Equal(1, state.MajorExplosionCount);
        Assert.Equal(6, state.CurrentPeakIntensity);
    }

    [Fact]
    public void FallbackPlan_ProvidesAscendingIntensityCurve()
    {
        var plan = EpisodeDirectorPlanParser.BuildFallback(Units(), 30, 1);

        Assert.Equal(4, plan.IntensityCurve.Count);
        Assert.Equal(2, EpisodeDirectorPlanParser.GetIntensityLimit(plan, Unit("1.1")));
        Assert.Equal(10, EpisodeDirectorPlanParser.GetIntensityLimit(plan, Unit("1.10")));
    }
}
