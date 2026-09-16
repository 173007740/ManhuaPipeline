using ManhuaPipeline.Models;
using ManhuaPipeline.Services.Director;
using System.Text.Json;
using Xunit;

namespace ManhuaPipeline.Tests;

public class DirectorV2Tests
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

    private static DirectorPlan Plan(
        string primary = "岳沉天",
        string? secondary = "太虚圣主",
        int intensity = 8,
        int roundCount = 3,
        string vfxPeak = "Late")
    {
        var plan = new DirectorPlan
        {
            ProjectId = 30,
            EpisodeNumber = 1,
            UnitNumber = "1.1",
            PrimarySubject = primary,
            SecondarySubject = secondary ?? "",
            IntensityLevel = intensity,
            CombatRoundCount = roundCount,
            VfxPeakPhase = vfxPeak
        };
        if (roundCount > 0)
        {
            plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
            {
                PrimaryFighterId = primary,
                EnemyIds = secondary == null ? [] : [secondary],
                RoundCount = roundCount,
                CombatGrammarIds = ["T3_SURROUND_ATTACK", "T1_DODGE_COUNTER", "T4_AOE_BREAK"],
                EndingState = "太虚圣主被震退"
            });
        }
        return plan;
    }

    private static string CompliantStoryboard() =>
        """
        镜头编号: 1.1-1
        节拍: Beat1
        描述: 太虚圣主抬手合围，岳沉天格挡反击，太虚圣主被震退
        起始画面: 七宗宗主分列阵心
        结束画面: 岳沉天保持核心站位
        镜头编号: 1.1-2
        节拍: Beat2
        描述: 岳沉天闪避反打命中，攻防一回合
        起始画面: 岳沉天侧身
        结束画面: 太虚圣主后仰
        镜头编号: 1.1-3
        节拍: Beat3
        描述: 岳沉天气血爆发，本单元VFX峰值100%，太虚圣主被震退
        起始画面: 气血凝聚
        结束画面: 胜负已定
        """;

    [Fact]
    public void CompliantStoryboard_PassesWithFullScore()
    {
        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), CompliantStoryboard());

        Assert.True(result.Passed, result.Verdict);
        Assert.False(result.HasHardFailure);
        Assert.Equal(100, result.Score);
        Assert.Equal("PASS", result.Verdict);
    }

    [Fact]
    public void MissingCombatBeat_HardFailsAndProvidesRepairInstruction()
    {
        var plan = Plan();
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "岳沉天",
            EnemyIds = ["太虚圣主"],
            RoundCount = 2,
            CombatGrammarIds = ["T1_DODGE_COUNTER", "T4_AOE_BREAK"],
            EndingState = "太虚圣主被震退"
        });

        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天侧身闪避并反击
            起始画面: 岳沉天被围
            结束画面: 反击命中
            """;

        var result = DirectorRuleValidator.Validate(plan, CombatUnit(), storyboard);

        Assert.False(result.Passed);
        Assert.True(result.HasHardFailure);
        var violation = Assert.Single(result.Violations, v => v.Code == "COMBAT_BEAT_MISSING");
        Assert.Equal("Beat02", violation.CombatBeatId);
        Assert.Contains("补充", violation.RepairInstruction);
    }

    [Fact]
    public void WrongPrimarySubject_HardFails()
    {
        var storyboard = """
            镜头编号: 1.1-1
            描述: 太虚圣主抬手压阵
            起始画面: 法光落下
            结束画面: 太虚圣主宣判
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard);

        Assert.False(result.Passed);
        Assert.True(result.HasHardFailure);
        Assert.Contains(result.Violations, v => v.Code == "PRIMARY_SUBJECT_MISSING");
    }

    [Fact]
    public void RoundCountMismatch_IsError()
    {
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天突进对撞，太虚圣主格挡
            起始画面: 岳沉天握拳
            结束画面: 双方分开
            镜头编号: 1.1-2
            节拍: Beat2
            描述: 岳沉天站定观察
            起始画面: 双方分开
            结束画面: 岳沉天屏息
            镜头编号: 1.1-3
            节拍: Beat3
            描述: 岳沉天反打命中
            起始画面: 岳沉天蓄力
            结束画面: 太虚圣主后仰
            """;

        var result = DirectorRuleValidator.Validate(Plan(intensity: 5), CombatUnit(), storyboard);

        Assert.False(result.Passed);
        Assert.Contains(result.Violations, v => v.Code == "ROUND_COUNT_MISMATCH");
    }

    [Fact]
    public void SkillOwnerMismatch_HardFails()
    {
        var skills = new List<SkillLibraryItem>
        {
            new()
            {
                Name = "七曜诛圣阵",
                OwnerCharacter = "太虚圣主",
                PromptVideo = "七色法光合拢"
            }
        };
        var storyboard = """
            镜头编号: 1.1-1
            描述: 岳沉天催动七曜诛圣阵，法光炸开
            起始画面: 阵纹亮起
            结束画面: 法光满屏
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard, skills);

        Assert.False(result.Passed);
        Assert.True(result.HasHardFailure);
        Assert.Contains(result.Violations, v => v.Code == "SKILL_OWNER_MISMATCH");
    }

    [Fact]
    public void VfxPeakMismatch_NoLongerFlagsError()
    {
        var storyboard = """
            镜头编号: 1.1-1
            描述: 岳沉天出手，本单元VFX峰值100%，法光炸开
            起始画面: 岳沉天握拳
            结束画面: 法光满屏
            镜头编号: 1.1-2
            描述: 岳沉天追击
            起始画面: 法光渐弱
            结束画面: 岳沉天逼近
            镜头编号: 1.1-3
            描述: 太虚圣主被震退
            起始画面: 双方对撞
            结束画面: 胜负已定
            """;

        var result = DirectorRuleValidator.Validate(Plan(intensity: 5), CombatUnit(), storyboard);

        Assert.DoesNotContain(result.Violations, v => v.Code is "VFX_PEAK_TOO_EARLY" or "VFX_PEAK_MISMATCH");
    }

    [Fact]
    public void EndingStateMismatch_HardFails()
    {
        var plan = Plan();
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "岳沉天",
            EnemyIds = ["太虚圣主"],
            RoundCount = 1,
            CombatGrammarIds = ["T4_AOE_BREAK"],
            EndingState = "六名弟子被震退"
        });

        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天气血爆发，太虚圣主被震退
            起始画面: 岳沉天蓄力
            结束画面: 太虚圣主后仰
            """;

        var result = DirectorRuleValidator.Validate(plan, CombatUnit(), storyboard);

        Assert.False(result.Passed);
        Assert.True(result.HasHardFailure);
        Assert.Contains(result.Violations, v => v.Code == "ENDING_STATE_MISMATCH");
    }

    [Fact]
    public void RepairPlanner_BuildsTargetedFeedback()
    {
        var plan = Plan();
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "岳沉天",
            EnemyIds = ["太虚圣主"],
            RoundCount = 2,
            CombatGrammarIds = ["T1_DODGE_COUNTER", "T4_AOE_BREAK"],
            EndingState = "太虚圣主被震退"
        });
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天侧身闪避并反击
            起始画面: 岳沉天被围
            结束画面: 反击命中
            """;

        var validation = DirectorRuleValidator.Validate(plan, CombatUnit(), storyboard);
        var feedback = DirectorRepairPlanner.BuildRepairFeedback(validation, storyboard);

        Assert.Contains(feedback, line => line.Contains("保留以下镜头"));
        Assert.Contains(feedback, line => line.Contains("漏拍 Beat02"));
        Assert.Contains(feedback, line => line.Contains("修复:"));
    }

    [Fact]
    public void Merge_AppliesSemanticWarningAndKeepsPassWithWarning()
    {
        var rule = DirectorRuleValidator.Validate(Plan(), CombatUnit(), CompliantStoryboard());
        var semantic = DirectorValidationResult.Pass();
        semantic.Violations.Add(new DirectorViolation
        {
            Code = "CORE_PAYOFF",
            Severity = "Warning",
            Message = "核心爽点的冲击感还不够",
            Expected = "爽点成立",
            Actual = "爽点力度偏弱",
            RepairInstruction = "把最后一镜的命中反馈写得更重"
        });

        var merged = DirectorRuleValidator.Merge(rule, semantic);

        Assert.True(merged.Passed);
        Assert.False(merged.HasHardFailure);
        Assert.Equal(95, merged.Score);
        Assert.Equal("PASS", merged.Verdict);
    }

    [Fact]
    public void SemanticError_TurnsResultIntoFail()
    {
        var rule = DirectorRuleValidator.Validate(Plan(), CombatUnit(), CompliantStoryboard());
        var semantic = DirectorValidationResult.Pass();
        semantic.Violations.Add(new DirectorViolation
        {
            Code = "DRAMATIC_PURPOSE",
            Severity = "Error",
            Message = "戏剧目的没有落到镜头上",
            Expected = "压迫后反击",
            Actual = "全程平拍",
            RepairInstruction = "重拍核心镜头"
        });

        var merged = DirectorRuleValidator.Merge(rule, semantic);

        Assert.False(merged.Passed);
        Assert.Equal(90, merged.Score);
        Assert.Equal("FAIL", merged.Verdict);
    }

    [Fact]
    public void Merge_WarningScoreBelow90_YieldsPassWithWarning()
    {
        var rule = DirectorRuleValidator.Validate(Plan(), CombatUnit(), CompliantStoryboard());
        var semantic = DirectorValidationResult.Pass();
        semantic.Violations.Add(new DirectorViolation
        {
            Code = "CORE_PAYOFF", Severity = "Warning", Message = "爽点弱", RepairInstruction = "加重命中"
        });
        semantic.Violations.Add(new DirectorViolation
        {
            Code = "DRAMATIC_PURPOSE", Severity = "Warning", Message = "压迫感不足", RepairInstruction = "前两镜加压迫"
        });
        semantic.Violations.Add(new DirectorViolation
        {
            Code = "CAMERA_STRATEGY", Severity = "Warning", Message = "镜头不够贴主体", RepairInstruction = "多用近景"
        });

        var merged = DirectorRuleValidator.Merge(rule, semantic);

        Assert.True(merged.Passed);
        Assert.Equal(83, merged.Score);
        Assert.Equal("PASS WITH WARNING", merged.Verdict);
    }

    [Fact]
    public void UnitPreamble_IsNotTreatedAsShot_ContinuityPasses()
    {
        var storyboard = """
            ### 【第1集】

            #### 【单元1.1】七曜诛圣阵拔地而起
            - **控制模式**: 打斗模板
            - **技能**: 力破万法

            - **镜头编号**: 1.1-1
            - **节拍**: Beat1
            - **描述**: 岳沉天格挡反击，太虚圣主被震退
            - **起始画面**: 岳沉天握拳
            - **结束画面**: 太虚圣主后仰
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard);

        Assert.DoesNotContain(result.Violations, v => v.Code == "CONTINUITY_INCOMPLETE");
    }

    [Fact]
    public void SkillOwner_ParenthesizedAlias_DoesNotFail()
    {
        var skills = new List<SkillLibraryItem>
        {
            new() { Name = "力破万法", OwnerCharacter = "岳沉罡", PromptVideo = "拳锋贯穿阵光" }
        };
        var storyboard = """
            镜头编号: 1.1-1
            描述: 前世岳沉天战斗态（岳沉罡本体）发动力破万法，拳锋贯穿阵光
            起始画面: 岳沉罡本体蓄力
            结束画面: 阵光被贯穿
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard, skills);

        Assert.DoesNotContain(result.Violations, v => v.Code == "SKILL_OWNER_MISMATCH");
    }

    [Fact]
    public void SkillOwner_GroupName_AcceptsAny宗主()
    {
        var skills = new List<SkillLibraryItem>
        {
            new() { Name = "七曜诛圣阵", OwnerCharacter = "七宗宗主", PromptVideo = "七色法光合拢" }
        };
        var characters = new List<CharacterAsset>
        {
            new()
            {
                Name = "七宗宗主",
                Description = "角色描述：七位宗门之主，太虚圣主亦在其中。"
            },
            new()
            {
                Name = "赤焰宗主",
                Description = "角色描述：七宗之一赤焰宗的宗主。"
            }
        };
        var storyboard = """
            镜头编号: 1.1-1
            描述: 赤焰宗主催动七曜诛圣阵，法光合拢
            起始画面: 阵柱亮起
            结束画面: 法光满屏
            """;

        var result = DirectorRuleValidator.Validate(Plan(), CombatUnit(), storyboard, skills, characters: characters);

        Assert.DoesNotContain(result.Violations, v => v.Code == "SKILL_OWNER_MISMATCH");
    }

    [Fact]
    public void Dialogue_留音Variant_DoesNotFail()
    {
        var unit = CombatUnit();
        unit.Dialogue = "顾残山：十八年前七个人一起出的手。";
        var storyboard = """
            镜头编号: 1.1-1
            描述: 顾残山留音开口
            对话/台词: 顾残山留音：十八年前七个人一起出的手。
            起始画面: 遗骨前
            结束画面: 岳沉天聆听
            """;

        var result = DirectorRuleValidator.Validate(Plan(), unit, storyboard);

        Assert.DoesNotContain(result.Violations, v => v.Code == "DIALOGUE_MISSING");
    }

    [Fact]
    public void Dialogue_MultipleLines_PresentLineByLine_DoesNotFail()
    {
        var unit = CombatUnit();
        unit.Dialogue = "岳沉天：十八年前七个人一起出的手。\n岳沉天：从今晚起，太虚圣主便是天下法统。";
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天开口
            对话/台词: 岳沉天：十八年前七个人一起出的手。
            起始画面: 岳沉天立于阵心
            结束画面: 岳沉天抬头
            镜头编号: 1.1-2
            节拍: Beat2
            描述: 岳沉天宣判
            对话/台词: 岳沉天：从今晚起，太虚圣主便是天下法统。
            起始画面: 岳沉天抬头
            结束画面: 太虚圣主沉默
            """;

        var result = DirectorRuleValidator.Validate(Plan(), unit, storyboard);

        Assert.DoesNotContain(result.Violations, v => v.Code == "DIALOGUE_MISSING");
    }

    [Fact]
    public void Dialogue_MultipleLines_MissingOneLine_StillFails()
    {
        var unit = CombatUnit();
        unit.Dialogue = "岳沉天：十八年前七个人一起出的手。\n岳沉天：从今晚起，太虚圣主便是天下法统。";
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 岳沉天开口
            对话/台词: 岳沉天：十八年前七个人一起出的手。
            起始画面: 岳沉天立于阵心
            结束画面: 岳沉天抬头
            """;

        var result = DirectorRuleValidator.Validate(Plan(), unit, storyboard);

        Assert.Contains(result.Violations, v => v.Code == "DIALOGUE_MISSING");
    }

    [Fact]
    public void EndingState_PhrasingVariant_DoesNotFail()
    {
        var plan = Plan();
        plan.ActionPlan = JsonSerializer.Serialize(new DirectorActionPlan
        {
            PrimaryFighterId = "岳沉天",
            EnemyIds = ["七宗宗主"],
            RoundCount = 1,
            CombatGrammarIds = ["T4_AOE_BREAK"],
            EndingState = "岳沉天被[七曜诛圣阵]完全罩住"
        });
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 七色法光压下，[七曜诛圣阵]将岳沉天整个罩住
            起始画面: 岳沉天立于阵心
            结束画面: 七道法光完全封住天地，[七曜诛圣阵]将岳沉天整个罩住
            """;

        var result = DirectorRuleValidator.Validate(plan, CombatUnit(), storyboard);

        Assert.DoesNotContain(result.Violations, v => v.Code == "ENDING_STATE_MISMATCH");
    }

    [Fact]
    public void Dialogue_MultipleSentences_SplitAcrossShots_DoesNotFail()
    {
        var unit = CombatUnit();
        unit.Dialogue = "顾残山留音：七个人的名字、护宗阵和镇宗器，都在契里。你若还想讨这笔债，第一枚印，是赤焰宗。";
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 血契展开
            对话/台词: 顾残山留音：七个人的名字、护宗阵和镇宗器，都在契里。
            起始画面: 血契平放
            结束画面: 契文亮起
            镜头编号: 1.1-2
            节拍: Beat2
            描述: 火莲宗印亮起
            对话/台词: 顾残山留音：你若还想讨这笔债，第一枚印，是赤焰宗。
            起始画面: 宗印微光
            结束画面: 赤焰宗标识灼亮
            """;

        var result = DirectorRuleValidator.Validate(Plan(), unit, storyboard);

        Assert.DoesNotContain(result.Violations, v => v.Code == "DIALOGUE_MISSING");
    }

    [Fact]
    public void Dialogue_MultipleSentences_MissingSecond_StillFails()
    {
        var unit = CombatUnit();
        unit.Dialogue = "顾残山留音：七个人的名字、护宗阵和镇宗器，都在契里。你若还想讨这笔债，第一枚印，是赤焰宗。";
        var storyboard = """
            镜头编号: 1.1-1
            节拍: Beat1
            描述: 血契展开
            对话/台词: 顾残山留音：七个人的名字、护宗阵和镇宗器，都在契里。
            起始画面: 血契平放
            结束画面: 契文亮起
            """;

        var result = DirectorRuleValidator.Validate(Plan(), unit, storyboard);

        Assert.Contains(result.Violations, v => v.Code == "DIALOGUE_MISSING");
    }
}
