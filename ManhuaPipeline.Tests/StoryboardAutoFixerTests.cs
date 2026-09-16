using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class StoryboardAutoFixerTests
{
    private static SkillLibraryItem Skill(string name) => new()
    {
        SkillId = 0,
        UserId = 1,
        ProjectId = 30,
        Name = name,
        Element = "体修",
        Tier = 3,
        OwnerCharacter = "岳沉天",
        PromptVideo = name + "特效"
    };

    [Fact]
    public void InvalidDurations_AreNormalizedToAllowedValues()
    {
        var text = """
        #### 【单元1.6】觉醒
        - **单元类型**: 高潮/对决
        - **镜头编号**: 1.6-1
        - **镜头描述**: 赤金气血爆发
        - **镜头时长**: 4秒
        - **镜头编号**: 1.6-2
        - **镜头描述**: 气血冲顶
        - **镜头时长**: 3.5秒
        """;

        var fixer = new StoryboardAutoFixer();
        var result = fixer.Fix(text, "", new List<SkillLibraryItem>(), out var fixes);

        Assert.Contains("- **镜头时长**: 5秒", result);
        Assert.DoesNotContain("4秒", result);
        Assert.DoesNotContain("3.5秒", result);
        Assert.Equal(2, fixes.Count);
    }

    [Fact]
    public void UnrelatedSkills_AreRemovedFromSkillLine()
    {
        var stage5 = """
        #### 【单元1.9】踏天步赶路
        - **控制模式**: 打斗模板
        - **技能**: 圣光诛邪, 天门镇魔, 九曜神罚, 金翎破晓, 踏天步
        - **单元类型**: 打斗/动作
        - **镜头编号**: 1.9-1
        - **镜头描述**: 岳沉天施展「踏天步」，足底白色圆环炸开。
        - **镜头时长**: 5秒
        """;
        var stage4 = """
        【第1集】
        【单元1.9】
        类型：打斗/动作
        核心动作/情绪：岳沉天发动「踏天步」踏空而来
        关键元素：少年岳沉天、「踏天步」
        """;

        var skills = new List<SkillLibraryItem>
        {
            Skill("圣光诛邪"),
            Skill("天门镇魔"),
            Skill("九曜神罚"),
            Skill("金翎破晓"),
            Skill("踏天步")
        };

        var fixer = new StoryboardAutoFixer();
        var result = fixer.Fix(stage5, stage4, skills, out var fixes);

        Assert.Contains("- **技能**: 踏天步", result);
        Assert.DoesNotContain("圣光诛邪", result);
        Assert.DoesNotContain("天门镇魔", result);
        Assert.DoesNotContain("九曜神罚", result);
        Assert.DoesNotContain("金翎破晓", result);
        Assert.Contains("移除误锁技能", string.Join("\n", fixes));
    }

    [Fact]
    public void MentionedSkills_AreAddedToSkillLine()
    {
        var stage5 = """
        #### 【单元1.3】力破万法
        - **控制模式**: 打斗模板
        - **技能**: 力破万法
        - **单元类型**: 高潮/对决
        - **镜头编号**: 1.3-2
        - **镜头描述**: 岳沉天发动「力破万法」，全身「赤金气血」压缩入右拳。
        - **镜头时长**: 5秒
        """;
        var stage4 = """
        【第1集】
        【单元1.3】
        类型：高潮/对决
        核心动作/情绪：岳沉天发动「力破万法」与「赤金气血」完成一击
        关键元素：少年岳沉天、「力破万法」、「赤金气血」
        """;

        var fixer = new StoryboardAutoFixer();
        var result = fixer.Fix(stage5, stage4, new List<SkillLibraryItem>(), out var fixes);

        Assert.Contains("- **技能**: 力破万法, 赤金气血", result);
        Assert.Contains("补充技能锁定", fixes[0]);
    }

    [Fact]
    public void BodyCultivatorGenericTerms_AreReplaced()
    {
        var stage5 = """
        #### 【单元1.1】七宗镇杀
        - **单元类型**: 高潮/对决
        - **镜头编号**: 1.1-3
        - **镜头描述**: 灵力炸裂，灵光瞬移；拳风呼啸，金身崩碎。
        - **镜头时长**: 11秒
        """;

        var fixer = new StoryboardAutoFixer();
        var result = fixer.Fix(stage5, "", new List<SkillLibraryItem>(), out _);

        Assert.Contains("气血炸裂，借力闪身", result);
        Assert.Contains("拳势破空", result);
        Assert.Contains("肉身崩碎", result);
        Assert.DoesNotContain("灵力炸裂", result);
        Assert.DoesNotContain("灵光瞬移", result);
    }

    [Fact]
    public void LockedSkillNames_AreNotCleanedAsGenericWords()
    {
        var stage5 = """
        #### 【单元2.1】山门拳拳到肉
        - **控制模式**: 打斗模板
        - **技能**: 拳风, 武极金身
        - **单元类型**: 打斗/动作
        - **镜头编号**: 2.1-1
        - **镜头描述**: 岳沉天以拳风呼啸开路，武极金身护体，金身纹路亮起。
        - **镜头时长**: 11秒
        """;

        var fixer = new StoryboardAutoFixer();
        var result = fixer.Fix(stage5, "", new List<SkillLibraryItem>(), out _);

        Assert.Contains("拳风呼啸", result);
        Assert.Contains("武极金身护体", result);
        Assert.Contains("武极金身纹路亮起", result);
    }

    [Fact]
    public void SingleCharacterSoundEffects_AreNotAddedAsSkills()
    {
        var stage5 = """
        #### 【单元1.9】踏天步赶路
        - **单元类型**: 打斗/动作
        - **镜头编号**: 1.9-1
        - **镜头描述**: 岳沉天一步踏出，脚底爆裂声「嘭」，白色圆环炸开。
        - **镜头时长**: 5秒
        """;

        var fixer = new StoryboardAutoFixer();
        var result = fixer.Fix(stage5, "", new List<SkillLibraryItem>(), out _);

        Assert.DoesNotContain("**技能**:", result);
        Assert.DoesNotContain("脚底爆裂声,", result);
    }
}
