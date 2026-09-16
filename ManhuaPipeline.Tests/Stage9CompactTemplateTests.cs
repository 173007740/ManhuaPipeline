using System.Reflection;
using ManhuaPipeline.Models;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class Stage9CompactTemplateTests
{
    [Fact]
    public void CompactPrompt_PassesPostGuards_AndParsesDuration()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [七宗宗主]群像；@图3 [葬天台]场景；@图4 [光影质感]光影质感。
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台夜晚。
        [0-3s]大远景固定机位，七宗宗主环形合围逼近，七色法光交织封天。
        [3-7s]镜头急推至中近景，低机位仰拍，岳沉天立于阵心，赤金气血在皮下涌动。
        [7-11s]持续仰拍，赤金气血汇聚拳锋，拳锋爆发刺目金光。
        灯光：冷月光+惨白天光。
        约束：宗主持续向前逼近，禁止原地站桩。
        """;

        var skills = new List<SkillLibraryItem>
        {
            new SkillLibraryItem { Name = "七曜诛圣阵", Tier = 4, OwnerCharacter = "七宗宗主" },
            new SkillLibraryItem { Name = "赤金气血", Tier = 2, OwnerCharacter = "前世岳沉天", Tags = "状态", PromptVideo = "气血涌动状态" }
        };
        var locked = new List<SkillLibraryItem> { skills[0] };

        var ensureSkill = typeof(AgentService).GetMethod("EnsureSkillRefImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ensureSkill);
        var afterSkill = (string)ensureSkill.Invoke(null, new object?[] { prompt, skills, locked, new List<string> { "赤金气血" }, null })!;

        var afterCombat = PromptCombatStateGuard.Apply(afterSkill, "【单元1.1】七曜诛圣阵", new[] { "前世岳沉天", "前世岳沉天战斗态", "七宗宗主" });
        var ensureNine = typeof(AgentService).GetMethod("EnsureMaxNineRefImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ensureNine);
        var afterTrim = (string)ensureNine.Invoke(null, new object?[] { afterCombat })!;

        var parsed = SeedancePromptParser.Parse(
            afterTrim,
            30,
            new List<string> { "前世岳沉天", "前世岳沉天战斗态", "七宗宗主" },
            new List<string>(),
            new List<string> { "葬天台" },
            new List<string>());

        var one = Assert.Single(parsed);
        Assert.Equal(11, one.Duration);
        Assert.Contains("@图2 [前世岳沉天战斗态]战斗姿态参考", one.PromptText);
        Assert.Contains("[赤金气血]特效参考", one.PromptText);
        Assert.Contains("@图6 [光影质感]光影质感参考", one.PromptText);
        Assert.Contains("[0-3s]大远景固定机位", one.PromptText);
        Assert.Contains("[7-11s]持续仰拍", one.PromptText);
        Assert.DoesNotContain("类型:", one.PromptText);
        Assert.DoesNotContain("【第1集】【单元1.1】【镜头1.1-1】", one.PromptText);
        Assert.DoesNotContain("【镜头1.1-1】", one.PromptText);
    }

    [Fact]
    public void CombatStateGuard_ThenStateSkillRef_AddsOwnerStateSkillToBattleCard()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [太虚圣主]人物；@图3 [葬天台]场景；@图4 [光影质感]光影质感。
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+冷月夜。
        [0-2s]太虚圣主居高临下开口宣判，前世岳沉天立于阵心。
        [2-4s]阵纹亮起，生机流向天门，前世岳沉天眉头紧锁。
        [4-5s]前世岳沉天握拳怒视，眼神燃起赤金怒焰。
        灯光：冷月光从云隙倾泻。
        约束：语速从容自然，禁止七色法光细节。
        """;

        var skills = new List<SkillLibraryItem>
        {
            new SkillLibraryItem { Name = "赤金气血", Tier = 2, OwnerCharacter = "前世岳沉天", Tags = "状态", PromptVideo = "状态爆发", ImageUrl = "http://img/skill/flag.png" }
        };

        var afterCombat = PromptCombatStateGuard.Apply(prompt, "【单元1.2】", new[] { "前世岳沉天", "前世岳沉天战斗态" });

        var ensureSkill = typeof(AgentService).GetMethod("EnsureSkillRefImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ensureSkill);
        var afterSkill = (string)ensureSkill.Invoke(null, new object?[] { afterCombat, skills, new List<SkillLibraryItem>(), new List<string>(), null })!;

        var ensureNine = typeof(AgentService).GetMethod("EnsureMaxNineRefImages", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(ensureNine);
        var afterTrim = (string)ensureNine.Invoke(null, new object?[] { afterSkill })!;

        Assert.Contains("@图1 [前世岳沉天]人物形象参考", afterTrim);
        Assert.Contains("@图2 [前世岳沉天战斗态]战斗姿态参考", afterTrim);
        Assert.Contains("[赤金气血]特效参考", afterTrim);
    }
}
