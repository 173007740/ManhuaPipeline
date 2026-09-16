using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptCharacterRefGuardTests
{
    private static readonly string[] Assets =
    {
        "前世岳沉天", "前世岳沉天战斗态", "太虚圣主", "七宗宗主", "顾残山"
    };

    [Fact]
    public void NonCombatShot_AddsMissingVisibleCharacterCard()
    {
        var prompt = """
        【第1集】【单元1.5】【镜头1.5-1】
        类型:文戏/情感
        @图1 顾残山人物；@图2 葬天台场景；@图3 光影质感。
        [0-3s]低机位横移，前世岳沉天卧于碎石。
        """;

        var result = PromptCharacterRefGuard.Apply(prompt, Assets);

        Assert.Contains("@图1 [顾残山]人物形象参考", result);
        Assert.Contains("@图2 [前世岳沉天]人物形象参考", result);
        Assert.Contains("@图3 [葬天台]场景参考", result);
        Assert.Contains("@图4 [光影质感]光影质感参考", result);
    }

    [Fact]
    public void CombatShot_AddsAllMissingVisibleCharacterCards()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        @图1 葬天台场景；@图2 光影质感。
        [0-3s]太虚圣主立于云端。
        [3-5s]前世岳沉天握拳怒视阵纹。
        """;

        var result = PromptCharacterRefGuard.Apply(prompt, Assets);

        Assert.Contains("@图1 [前世岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [太虚圣主]人物形象参考", result);
        Assert.Contains("@图3 [葬天台]场景参考", result);
        Assert.Contains("@图4 [光影质感]光影质感参考", result);
    }

    [Fact]
    public void DialogueOnlyMention_DoesNotAddCard()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        @图1 太虚圣主人物；@图2 葬天台场景；@图3 光影质感。
        [0-3s]太虚圣主说"前世岳沉天，你一人再强，也强不过天下法统。"
        """;

        var result = PromptCharacterRefGuard.Apply(prompt, Assets);

        Assert.DoesNotContain("[前世岳沉天]人物", result);
    }

    [Fact]
    public void GroupName_IsAddedAsGroupCard()
    {
        var prompt = """
        【第1集】【单元1.3】【镜头1.3-1】
        类型:高潮/对决
        @图1 前世岳沉天人物；@图2 葬天台场景；@图3 光影质感。
        [0-3s]七宗宗主自四面合围。
        """;

        var result = PromptCharacterRefGuard.Apply(prompt, Assets);

        Assert.Contains("@图2 [七宗宗主]群像参考", result);
        Assert.Contains("@图3 [葬天台]场景参考", result);
    }

    [Fact]
    public void CombatGuard_AddsBattleCard_AfterCharacterRefGuard()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        @图1 太虚圣主人物；@图2 葬天台场景；@图3 光影质感。
        [3-5s]前世岳沉天握拳怒视阵纹。
        """;

        var withRefs = PromptCharacterRefGuard.Apply(prompt, Assets);
        var result = PromptCombatStateGuard.Apply(withRefs, "【单元1.2】控制模式: 打斗模板", Assets);

        Assert.Contains("@图1 [前世岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [前世岳沉天战斗态]战斗姿态参考", result);
        Assert.Contains("@图3 [太虚圣主]人物形象参考", result);
    }
}
