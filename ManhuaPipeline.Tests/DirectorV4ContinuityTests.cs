using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Director;
using Xunit;

namespace ManhuaPipeline.Tests;

public class DirectorV4ContinuityTests
{
    private static StageUnit Unit(string unitNumber) =>
        new() { EpisodeNumber = 1, UnitNumber = unitNumber };

    [Fact]
    public void SnapshotBuilder_UsesLastShotAndEpisodePlanState()
    {
        var episodePlan = new EpisodeDirectorPlan
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            UnitEndStates =
            [
                new UnitEndState
                {
                    UnitNumber = "1.1",
                    LastActionState = "林烬踏入演武场",
                    EnvironmentState = "演武场边缘",
                    CameraDirection = "前推",
                    ActiveVfxState = "赤金气血微涌",
                    Characters = new Dictionary<string, CharacterState>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["林烬"] = new CharacterState
                        {
                            Position = "演武场边缘",
                            Injury = "右肩伤",
                            SkillState = "赤金气血30%"
                        }
                    }
                }
            ],
            UnitTransitions =
            [
                new UnitTransition
                {
                    FromUnit = "1.1",
                    ToUnit = "1.2",
                    CarryEmotion = "警惕持续",
                    NarrativeCarry = "准备接招"
                }
            ]
        };
        var plan = new DirectorPlan
        {
            ProjectId = 30,
            UnitNumber = "1.1",
            ActionPlan = """{"endingState":"林烬收拳立于演武场中央"}"""
        };
        var storyboard = """
            镜头1
            起始画面: 林烬立于演武场边缘
            结束画面: 林烬收拳，右肩血迹，赤金气血翻涌
            场景: 演武场中央
            运镜: 固定机位慢推
            特效: 赤金气血翻涌
            """;

        var snapshot = EpisodeStateSnapshotBuilder.Build(30, Unit("1.1"), episodePlan, plan, storyboard);

        Assert.Equal("storyboard", snapshot.Source);
        Assert.Contains("收拳", snapshot.State.LastActionState);
        Assert.Equal("演武场中央", snapshot.State.EnvironmentState);
        Assert.Equal("固定机位慢推", snapshot.State.CameraDirection);
        Assert.Equal("赤金气血翻涌", snapshot.State.ActiveVfxState);
        Assert.Equal("警惕持续", snapshot.State.EmotionalCarry);
        Assert.Equal("右肩伤", snapshot.State.Characters["林烬"].Injury);
        Assert.Equal("赤金气血30%", snapshot.State.Characters["林烬"].SkillState);
    }

    [Fact]
    public void ContinuityValidator_DetectsUnitReset()
    {
        var previous = new EpisodeUnitStateSnapshot
        {
            State = new UnitEndState
            {
                LastActionState = "林烬一拳震飞六名弟子",
                ActiveVfxState = "赤金气血爆发"
            }
        };
        var plan = new DirectorPlan { UnitNumber = "1.2", IntensityLevel = 6 };

        var violations = EpisodeContinuityValidator.Validate(
            previous, null, Unit("1.2"), plan,
            "镜头1：两人重新摆开架势，再次对峙");

        Assert.Contains(violations, v => v.Code == "UNIT_RESET_DETECTED");
        Assert.Contains(violations, v => v.Severity == "Error");
    }

    [Fact]
    public void ContinuityValidator_PassesWhenCombatContinues()
    {
        var previous = new EpisodeUnitStateSnapshot
        {
            State = new UnitEndState
            {
                LastActionState = "林烬一拳震飞六名弟子",
                ActiveVfxState = "赤金气血爆发"
            }
        };
        var plan = new DirectorPlan { UnitNumber = "1.2", IntensityLevel = 6 };

        var violations = EpisodeContinuityValidator.Validate(
            previous, null, Unit("1.2"), plan,
            "镜头1：赤金气血持续翻涌，林烬追斩倒飞弟子");

        Assert.DoesNotContain(violations, v => v.Code == "UNIT_RESET_DETECTED");
    }

    [Fact]
    public void ContinuityValidator_DetectsEmotionJump()
    {
        var previous = new EpisodeUnitStateSnapshot
        {
            State = new UnitEndState
            {
                EmotionalCarry = "愤怒持续"
            }
        };
        var plan = new DirectorPlan
        {
            UnitNumber = "1.3",
            EmotionCurve = "平静 → 警惕",
            IntensityLevel = 4
        };

        var violations = EpisodeContinuityValidator.Validate(
            previous, null, Unit("1.3"), plan,
            "镜头1：岳沉天负手而立，神态平静");

        Assert.Contains(violations, v => v.Code == "EMOTION_JUMP");
    }

    [Fact]
    public void ContinuityValidator_DetectsInjuryRecoveryAndTeleport()
    {
        var previous = new EpisodeUnitStateSnapshot
        {
            State = new UnitEndState
            {
                Characters = new Dictionary<string, CharacterState>(StringComparer.OrdinalIgnoreCase)
                {
                    ["岳沉天"] = new CharacterState
                    {
                        Position = "阵心",
                        Injury = "右肩伤"
                    }
                }
            }
        };
        var plan = new DirectorPlan { UnitNumber = "1.2", IntensityLevel = 3 };

        var violations = EpisodeContinuityValidator.Validate(
            previous, null, Unit("1.2"), plan,
            "镜头1：岳沉天立于山门前，毫发无伤，负手而立");

        Assert.Contains(violations, v => v.Code == "INJURY_RECOVERED");
        Assert.Contains(violations, v => v.Code == "CHARACTER_TELEPORT");
    }

    [Fact]
    public void RuleValidator_IncludesContinuityViolations()
    {
        var previous = new EpisodeUnitStateSnapshot
        {
            State = new UnitEndState
            {
                LastActionState = "林烬一拳震飞六名弟子",
                ActiveVfxState = "赤金气血爆发"
            }
        };
        var episodePlan = new EpisodeDirectorPlan
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            RepetitionPolicy = new RepetitionPolicy()
        };
        var plan = new DirectorPlan
        {
            UnitNumber = "1.2",
            PrimarySubject = "林烬",
            IntensityLevel = 6
        };

        var result = DirectorRuleValidator.Validate(
            plan,
            Unit("1.2"),
            "镜头1：两人重新摆开架势，林烬再次对峙",
            null,
            episodePlan,
            null,
            previous);

        Assert.Contains(result.Violations, v => v.Code == "UNIT_RESET_DETECTED");
    }
}
