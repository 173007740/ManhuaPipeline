using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Director;
using Xunit;

namespace ManhuaPipeline.Tests;

public class DirectorV4Tests
{
    private static List<StageUnit> Units()
    {
        return
        [
            new()
            {
                EpisodeNumber = 1,
                UnitNumber = "1.1",
                Type = "文戏/情感",
                Duration = 5,
                CoreAction = "林烬踏入赤焰宗山门",
                StartState = "林烬站在山门外",
                EndState = "林烬站在演武场边缘"
            },
            new()
            {
                EpisodeNumber = 1,
                UnitNumber = "1.2",
                Type = "打斗/动作",
                Duration = 11,
                CoreAction = "林烬反手震飞围攻弟子",
                StartState = "林烬立于演武场中央",
                EndState = "六名弟子倒飞落地，林烬收拳"
            },
            new()
            {
                EpisodeNumber = 1,
                UnitNumber = "1.3",
                Type = "高潮/对决",
                Duration = 15,
                CoreAction = "林烬与长老对掌",
                StartState = "长老踏前一步",
                EndState = "长老后退三步，气血翻涌"
            }
        ];
    }

    private static StageUnit Unit(string unitNumber) =>
        new() { EpisodeNumber = 1, UnitNumber = unitNumber };

    private static EpisodeDirectorPlan EpisodePlan()
    {
        return new EpisodeDirectorPlan
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            EpisodeGoal = "测试集",
            IntensityCurve =
            [
                new UnitIntensityPlan { UnitNumber = "1.1", IntensityLimit = 3 },
                new UnitIntensityPlan { UnitNumber = "1.2", IntensityLimit = 6 },
                new UnitIntensityPlan { UnitNumber = "1.3", IntensityLimit = 9 }
            ],
            RepetitionPolicy = new RepetitionPolicy
            {
                SlowMotionLimit = 2,
                MajorExplosionLimit = 1
            },
            ClimaxBudget = new ClimaxBudget
            {
                SmallClimaxLimit = 2,
                MidClimaxLimit = 1,
                LargeClimaxLimit = 1
            }
        };
    }

    [Fact]
    public void Parse_ReadsV4UnitFlowAndClimaxBudget()
    {
        var json = """
            {
              "episodeGoal": "立威",
              "emotionCurve": "探索 → 压迫 → 爆发",
              "intensityCurve": [
                {"unitNumber": "1.1", "intensityLimit": 3},
                {"unitNumber": "1.2", "intensityLimit": 6},
                {"unitNumber": "1.3", "intensityLimit": 9}
              ],
              "unitEmotionCurve": [
                {"unitNumber": "1.1", "emotion": "探索", "level": 2, "directingNote": "收着拍"},
                {"unitNumber": "1.2", "emotion": "压迫", "level": 6},
                {"unitNumber": "1.3", "emotion": "爆发", "level": 9}
              ],
              "unitTransitions": [
                {
                  "fromUnit": "1.1",
                  "toUnit": "1.2",
                  "transitionType": "EmotionCarry",
                  "carryEmotion": "警惕持续",
                  "narrativeCarry": "山门威压未散",
                  "cameraDirection": "保持前推"
                }
              ],
              "unitEndStates": [
                {
                  "unitNumber": "1.1",
                  "lastActionState": "林烬踏入演武场",
                  "environmentState": "演武场边缘",
                  "emotionalCarry": "警惕",
                  "narrativeCarry": "准备接受挑衅",
                  "characters": {
                    "林烬": {"position": "演武场边缘", "facing": "前方", "appearance": "普通黑衣"}
                  }
                }
              ],
              "climaxBudget": {
                "smallClimaxLimit": 2,
                "midClimaxLimit": 1,
                "largeClimaxLimit": 1,
                "notes": "大高潮只留给 1.3"
              },
              "repetitionPolicy": {"slowMotionLimit": 2, "majorExplosionLimit": 1}
            }
            """;

        var plan = EpisodeDirectorPlanParser.Parse(json, Units(), 30, 1);

        Assert.NotNull(plan);
        Assert.Equal("立威", plan!.EpisodeGoal);

        var emotion = Assert.Single(plan.UnitEmotionCurve, x => x.UnitNumber == "1.1");
        Assert.Equal("探索", emotion.Emotion);
        Assert.Equal(2, emotion.Level);

        var incoming = Assert.Single(plan.UnitTransitions, x => x.ToUnit == "1.2");
        Assert.Equal("EmotionCarry", incoming.TransitionType);
        Assert.Equal("警惕持续", incoming.CarryEmotion);

        var endState = Assert.Single(plan.UnitEndStates, x => x.UnitNumber == "1.1");
        Assert.Equal("演武场边缘", endState.EnvironmentState);
        Assert.True(endState.Characters.ContainsKey("林烬"));

        Assert.Equal(2, plan.ClimaxBudget.SmallClimaxLimit);
        Assert.Equal(1, plan.ClimaxBudget.MidClimaxLimit);
        Assert.Equal(1, plan.ClimaxBudget.LargeClimaxLimit);
    }

    [Fact]
    public void Parse_FillsMissingUnitFlowForAllEpisodeUnits()
    {
        var json = """
            {
              "episodeGoal": "立威",
              "emotionCurve": "探索 → 爆发",
              "intensityCurve": [
                {"unitNumber": "1.1", "intensityLimit": 3},
                {"unitNumber": "1.2", "intensityLimit": 6},
                {"unitNumber": "1.3", "intensityLimit": 9}
              ],
              "repetitionPolicy": {"slowMotionLimit": 2, "majorExplosionLimit": 1}
            }
            """;

        var plan = EpisodeDirectorPlanParser.Parse(json, Units(), 30, 1);

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.UnitEmotionCurve.Count);
        Assert.Equal(2, plan.UnitTransitions.Count);
        Assert.Equal(3, plan.UnitEndStates.Count);

        Assert.Contains(plan.UnitEmotionCurve, x => x.UnitNumber == "1.3");
        Assert.Contains(plan.UnitTransitions, x => x.ToUnit == "1.3");
        Assert.Contains(plan.UnitEndStates, x => x.UnitNumber == "1.2");
        Assert.Equal(2, plan.ClimaxBudget.SmallClimaxLimit);
    }

    [Fact]
    public void BuildUnitConstraintSection_IncludesV4Flow()
    {
        var plan = EpisodeDirectorPlanParser.Parse(
            """
            {
              "episodeGoal": "立威",
              "intensityCurve": [{"unitNumber": "1.2", "intensityLimit": 6}],
              "unitEmotionCurve": [{"unitNumber": "1.2", "emotion": "压迫", "level": 6}],
              "unitTransitions": [
                {"fromUnit": "1.1", "toUnit": "1.2", "transitionType": "ImpactCut", "carryEmotion": "压迫持续"}
              ],
              "unitEndStates": [
                {"unitNumber": "1.2", "lastActionState": "林烬收拳", "environmentState": "演武场中央"}
              ],
              "climaxBudget": {"smallClimaxLimit": 2, "midClimaxLimit": 1, "largeClimaxLimit": 1}
            }
            """,
            Units(),
            30,
            1);

        var section = EpisodeDirectorPlanParser.BuildUnitConstraintSection(plan, null, Unit("1.2"));

        Assert.Contains("V4 Unit 交接", section);
        Assert.Contains("ImpactCut", section);
        Assert.Contains("V4 本单元结束状态", section);
        Assert.Contains("V4 高潮预算", section);
        Assert.Contains("小高潮≤2", section);
    }

    [Fact]
    public void RuleValidator_ClimaxBudgetExceeded_Flags()
    {
        var episodePlan = EpisodePlan();
        var state = new EpisodeDirectorState
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            LargeClimaxCount = 1
        };
        var plan = new DirectorPlan { UnitNumber = "1.3", IntensityLevel = 9 };

        var violations = EpisodeDirectorRuleValidator.Check(
            plan, Unit("1.3"), "法相天地压落", episodePlan, state);

        Assert.Contains(violations, v => v.Code == "CLIMAX_BUDGET_EXCEEDED");
    }

    [Fact]
    public void RuleValidator_ClimaxBudgetWithinLimit_Passes()
    {
        var episodePlan = EpisodePlan();
        var state = new EpisodeDirectorState
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            MidClimaxCount = 0
        };
        var plan = new DirectorPlan { UnitNumber = "1.2", IntensityLevel = 6 };

        var violations = EpisodeDirectorRuleValidator.Check(
            plan, Unit("1.2"), "震飞围攻弟子", episodePlan, state);

        Assert.DoesNotContain(violations, v => v.Code == "CLIMAX_BUDGET_EXCEEDED");
    }

    [Fact]
    public void StateUpdater_CountsClimaxByIntensity()
    {
        var state = new EpisodeDirectorState { ProjectId = 30, EpisodeNumber = 1 };

        EpisodeDirectorStateUpdater.Apply(
            state, EpisodePlan(), new DirectorPlan { UnitNumber = "1.1", IntensityLevel = 3 }, "山门前行");
        EpisodeDirectorStateUpdater.Apply(
            state, EpisodePlan(), new DirectorPlan { UnitNumber = "1.2", IntensityLevel = 6 }, "震飞弟子");
        EpisodeDirectorStateUpdater.Apply(
            state, EpisodePlan(), new DirectorPlan { UnitNumber = "1.3", IntensityLevel = 9 }, "对掌爆开");

        Assert.Equal(1, state.SmallClimaxCount);
        Assert.Equal(1, state.MidClimaxCount);
        Assert.Equal(1, state.LargeClimaxCount);
    }
}
