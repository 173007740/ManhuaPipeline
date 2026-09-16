using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using ManhuaPipeline.Services.Combat;
using Xunit;

namespace ManhuaPipeline.Tests;

public class SkillOwnerFilterTests
{
    private static SkillLibraryItem Skill(int id, string name, string owner, string? tags = null) => new()
    {
        SkillId = id,
        UserId = 1,
        ProjectId = 30,
        Name = name,
        Element = "体",
        Tier = 3,
        OwnerCharacter = owner,
        PromptVideo = name + "特效",
        Tags = tags ?? name
    };

    [Fact]
    public void SelectSkills_Fallback_OnlyKeepsSkillsOwnedByPresentCharacters()
    {
        var skills = new List<SkillLibraryItem>
        {
            Skill(1, "赤金气血", "岳沉罡"),
            Skill(2, "火墙", "赤焰宗守山弟子"),
            Skill(3, "通用冲击", "")
        };

        var intent = new CombatIntent
        {
            Participants = { new CombatParticipant { Name = "岳沉罡", Role = "A" } },
            CombatForm = "双人徒手近战",
            Intensity = 3,
            Duration = 11
        };

        var unitText = "单元类型：打斗/动作\n核心动作：岳沉罡轰出赤金气血";
        var selected = CombatPlanSelector.SelectSkills(skills, intent, unitText, new List<string> { "岳沉罡" });

        Assert.Contains(selected, s => s.Name == "赤金气血");
        Assert.Contains(selected, s => s.Name == "通用冲击");
        Assert.DoesNotContain(selected, s => s.Name == "火墙");
    }

    [Fact]
    public void SelectSkills_ExplicitRequiredSkill_KeepsExplicitMatch()
    {
        var skills = new List<SkillLibraryItem>
        {
            Skill(1, "赤金气血", "岳沉罡"),
            Skill(2, "火墙", "赤焰宗守山弟子")
        };

        var intent = new CombatIntent
        {
            Participants = { new CombatParticipant { Name = "岳沉罡", Role = "A" } },
            RequiredSkills = { "赤金气血" },
            CombatForm = "双人徒手近战",
            Intensity = 4,
            Duration = 11
        };

        var selected = CombatPlanSelector.SelectSkills(skills, intent, "岳沉罡 使用 赤金气血", new List<string> { "岳沉罡" });

        Assert.Single(selected);
        Assert.Equal("赤金气血", selected[0].Name);
    }

    [Fact]
    public void MatchSkillsToText_OnlyMatchesOwnedSkillsForUnitCharacters()
    {
        var skills = new List<SkillLibraryItem>
        {
            Skill(1, "赤金气血", "岳沉罡"),
            Skill(2, "烈焰阻隔", "赤焰宗守山弟子")
        };

        var unitText = """
        单元类型：打斗/动作
        @角色引用:[岳沉罡][赤焰宗守山弟子]
        镜头描述：岳沉罡以赤金气血轰击，赤焰宗守山弟子升起烈焰阻隔拦截
        """;

        var matched = CombatPlanSelector.MatchSkillsToText(skills, unitText);

        Assert.Contains(matched, s => s.Name == "赤金气血");
    }

    [Fact]
    public void MatchSkillsToText_ExcludesSkillsOfCharactersNotInUnit()
    {
        var skills = new List<SkillLibraryItem>
        {
            Skill(1, "赤金气血", "岳沉罡"),
            Skill(2, "暗金山岳虚影", "太虚圣主")
        };

        var unitText = """
        单元类型：打斗/动作
        @角色引用:[岳沉罡]
        镜头描述：岳沉罡以赤金气血轰击
        """;

        var matched = CombatPlanSelector.MatchSkillsToText(skills, unitText);

        Assert.Contains(matched, s => s.Name == "赤金气血");
        Assert.DoesNotContain(matched, s => s.Name == "暗金山岳虚影");
    }

    [Fact]
    public void SelectSkills_ExplicitT5RequiredSkill_NotDroppedByTierCap()
    {
        var qiyao = Skill(60, "七曜诛圣阵", "七宗宗主", "镇世武圣,七曜,诛圣阵,合击,封天,七宗");
        qiyao.Tier = 5;
        var skills = new List<SkillLibraryItem> { qiyao };
        var characters = new List<CharacterAsset>
        {
            new()
            {
                Name = "七宗宗主",
                Description = "角色描述：七位宗门之主，太虚圣主亦在其中。"
            }
        };

        var intent = new CombatIntent
        {
            Participants = { new CombatParticipant { Name = "七宗宗主", Role = "A" } },
            RequiredSkills = { "七曜诛圣阵" },
            CombatForm = "法阵合击",
            Intensity = 3,
            Duration = 11
        };

        var selected = CombatPlanSelector.SelectSkills(skills, intent, "核心动作/情绪：七曜诛圣阵拔地而起", new List<string> { "七宗宗主" });

        Assert.Single(selected);
        Assert.Equal("七曜诛圣阵", selected[0].Name);
    }

    [Fact]
    public void MatchSkillsToText_RecognizesStage4CombatUnit()
    {
        var qiyao = Skill(60, "七曜诛圣阵", "七宗宗主", "镇世武圣,七曜,诛圣阵,合击,封天,七宗");
        qiyao.Tier = 5;
        var skills = new List<SkillLibraryItem> { qiyao };

        var unitText = """
        类型：打斗/动作
        时长：11秒
        核心动作/情绪：七曜诛圣阵拔地而起，七宗合力封天锁地
        关键元素：前世岳沉罡（战斗态）、七宗宗主；七曜诛圣阵、七根阵柱
        """;

        var matched = CombatPlanSelector.MatchSkillsToText(skills, unitText, new List<string> { "七宗宗主" });

        Assert.Contains(matched, s => s.Name == "七曜诛圣阵");
    }
    [Fact]
    public void OwnerAliases_ContainsGroupMembers()
    {
        var characters = new List<CharacterAsset>
        {
            new()
            {
                Name = "七宗宗主",
                Description = "角色别名：七大宗主\n角色描述：七位宗门之主，太虚圣主亦在其中。"
            },
            new()
            {
                Name = "太虚圣主",
                Description = "角色别名：太虚宗宗主\n角色描述：七宗之一太虚宗的宗主。"
            }
        };

        var aliases = CharacterAliasCatalog.GetOwnerAliases("七宗宗主", characters);
        Assert.Contains("太虚圣主", aliases);
        Assert.Contains("太虚宗宗主", aliases);
        Assert.Contains("七大宗主", aliases);
    }

    [Fact]
    public void SelectSkills_GroupOwner_MatchesAnyGroupMember()
    {
        var qiyao = Skill(60, "七曜诛圣阵", "七宗宗主", "镇世武圣,七曜,诛圣阵,合击,封天,七宗");
        qiyao.Tier = 5;
        var skills = new List<SkillLibraryItem> { qiyao };
        var characters = new List<CharacterAsset>
        {
            new()
            {
                Name = "七宗宗主",
                Description = "角色描述：七位宗门之主，太虚圣主亦在其中。"
            }
        };

        var intent = new CombatIntent
        {
            Participants = { new CombatParticipant { Name = "太虚圣主", Role = "A" } },
            RequiredSkills = { "七曜诛圣阵" },
            CombatForm = "法阵合击",
            Intensity = 3,
            Duration = 11
        };

        var selected = CombatPlanSelector.SelectSkills(skills, intent, "核心动作/情绪：太虚圣主催动七曜诛圣阵", new List<string> { "太虚圣主" }, characters);

        Assert.Contains(selected, s => s.Name == "七曜诛圣阵");
    }

    [Fact]
    public void OwnerAliases_ReadsProjectCharacterAssets()
    {
        var characters = new List<CharacterAsset>
        {
            new()
            {
                Name = "七宗宗主",
                Description = "角色描述：七位宗门之主，太虚圣主亦在其中。"
            },
            new()
            {
                Name = "太虚圣主",
                Description = "角色描述：七宗之一太虚宗的宗主。"
            },
            new()
            {
                Name = "赤焰宗主",
                Description = "角色描述：七宗之一赤焰宗的宗主。"
            }
        };

        var aliases = CharacterAliasCatalog.GetOwnerAliases("七宗宗主", characters);

        Assert.Contains("太虚圣主", aliases);
        Assert.Contains("赤焰宗主", aliases);
    }

    [Fact]
    public void MatchSkillsToText_UsesProjectAliases()
    {
        var qiyao = Skill(61, "七曜诛圣阵", "七宗宗主", "镇世武圣,七曜,诛圣阵,合击,封天");
        qiyao.Tier = 5;
        var characters = new List<CharacterAsset>
        {
            new()
            {
                Name = "七宗宗主",
                Description = "角色描述：七位宗门之主，太虚圣主亦在其中。"
            }
        };

        var unitText = """
        类型：打斗/动作
        时长：11秒
        核心动作/情绪：太虚圣主催动七曜诛圣阵，七宗合力封天锁地
        关键元素：太虚圣主；七曜诛圣阵
        """;

        var matched = CombatPlanSelector.MatchSkillsToText(new List<SkillLibraryItem> { qiyao }, unitText, new List<string> { "太虚圣主" }, characters);

        Assert.Contains(matched, s => s.Name == "七曜诛圣阵");
    }
}
