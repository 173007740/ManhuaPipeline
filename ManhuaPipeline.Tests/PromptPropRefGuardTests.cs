using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptPropRefGuardTests
{
    private static readonly string[] Props = { "镇岳拳带", "碎金拳谱", "七宗血契", "武圣遗骨" };

    [Fact]
    public void BodyMentionedProp_IsAddedAndRenumbered()
    {
        var prompt = """
        【第1集】【单元1.11】【镜头1.11-3】
        类型:文戏/情感
        @图1 [少年岳沉天]人物；@图2 [旧灯]人物；@图3 [赤金气血]特效；@图4 [枯井遗府]场景；@图5 [光影质感]光影质感
        [0-2s]少年岳沉天侧脸近景，赤金气血在掌心微凝。
        [2-4s]目光沉定落向石台武圣遗骨，镇岳拳带随气流轻轻一动。
        """;

        var result = PromptPropRefGuard.Apply(prompt, null, Props);

        Assert.Contains("[镇岳拳带]道具参考", result);
        Assert.Contains("[武圣遗骨]道具参考", result);
        Assert.DoesNotContain("[碎金拳谱]道具参考", result);
        var numbers = System.Text.RegularExpressions.Regex.Matches(result, @"@图(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();
        Assert.Equal(7, numbers.Count);
        for (var i = 1; i < numbers.Count; i++)
            Assert.Equal(numbers[i - 1] + 1, numbers[i]);
    }

    [Fact]
    public void UnitContextProps_AreAdded_WhenBodyUsesGenericPhrase()
    {
        var prompt = """
        【第1集】【单元1.11】【镜头1.11-1】
        类型:文戏/情感
        @图1 [少年岳沉天]人物；@图2 [枯井遗府]场景；@图3 [光影质感]光影质感
        [3-5s]少年岳沉天目光平稳扫过石台端坐的武圣遗骨与木案三件遗物。
        """;

        var result = PromptPropRefGuard.Apply(
            prompt,
            "镇岳拳带、碎金拳谱、七宗血契依序陈列，武圣遗骨端坐",
            Props);

        Assert.Contains("[武圣遗骨]道具参考", result);
        Assert.Contains("[镇岳拳带]道具参考", result);
        Assert.Contains("[碎金拳谱]道具参考", result);
        Assert.Contains("[七宗血契]道具参考", result);
        Assert.Contains("@图6 [枯井遗府]场景参考", result);
        Assert.Contains("@图7 [光影质感]光影质感参考", result);
    }

    [Fact]
    public void AlreadyBoundProp_IsNotDuplicated()
    {
        var prompt = """
        【第1集】【单元1.12】【镜头1.12-1】
        类型:文戏/情感
        @图1 [少年岳沉天]人物；@图2 [七宗血契]道具；@图3 [枯井遗府]场景；@图4 [光影质感]光影质感
        [0-3s]木案上摊开的七宗血契。
        """;

        var result = PromptPropRefGuard.Apply(prompt, "七宗血契", Props);

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, @"\[七宗血契\]道具"));
    }

    [Fact]
    public void PromptWithoutAtImageLine_IsUnchanged()
    {
        var prompt = """
        【第1集】【单元1.11】【镜头1.11-1】
        类型:文戏/情感
        [3-5s]少年岳沉天扫过武圣遗骨与木案三件遗物。
        """;

        var result = PromptPropRefGuard.Apply(prompt, "镇岳拳带、碎金拳谱、七宗血契", Props);

        Assert.Equal(prompt, result);
    }
}
