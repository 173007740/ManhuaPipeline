using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class SeedancePromptParserDurationTests
{
    [Fact]
    public void AtImageTimeBlocks_InferDurationFromLastBlock()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [前世岳沉天战斗态]战斗态；@图3 [葬天台]场景；@图4 [光影质感]光影质感。
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台夜晚。
        [0-3s]大远景固定机位，七宗宗主合围逼近。
        [3-7s]镜头急推至中近景，岳沉天双拳紧握。
        [7-11s]持续仰拍，拳锋爆发刺目金光。
        灯光：冷月光+惨白天光。
        约束：宗主持续向前逼近。
        """;

        var parsed = SeedancePromptParser.Parse(
            prompt,
            30,
            new List<string> { "前世岳沉天", "前世岳沉天战斗态" },
            new List<string>(),
            new List<string> { "葬天台" },
            new List<string>());

        var one = Assert.Single(parsed);
        Assert.Equal(11, one.Duration);
        Assert.Equal("高潮/对决", one.ShotType);
        Assert.Contains("@图1 [前世岳沉天]人物", one.PromptText);
        Assert.Contains("[0-3s]大远景固定机位", one.PromptText);
        Assert.DoesNotContain("类型:", one.PromptText);
        Assert.DoesNotContain("【第1集】【单元1.1】【镜头1.1-1】", one.PromptText);
        Assert.DoesNotContain("【镜头1.1-1】", one.PromptText);
    }

    [Fact]
    public void HeaderOnlyTemplate_IsNotSavedAsPrompt()
    {
        var parsed = SeedancePromptParser.Parse(
            "# Seedance 2.0 视频生成提示词",
            30,
            new List<string>(),
            new List<string>(),
            new List<string>(),
            new List<string>());

        Assert.Empty(parsed);
    }
}
