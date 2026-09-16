using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptInnerMonologueGuardTests
{
    [Fact]
    public void NarrationSpeaker_IsConvertedToInnerMonologue_AndMouthClosed()
    {
        var prompt = """
        【第1集】【单元1.8】【镜头1.8-1】
        类型:文戏/情感
        @图1 [岳沉天]人物；@图2 [荒郊破庙]场景；@图3 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，荒郊破庙+夜。
        [0-3s]固定机位近景：岳沉天低头缠绕拳带，旁白说"能放，也能收。"
        [3-5s]特写：他缓缓抬头，目光坚毅。
        灯光：冷月光。
        约束：语速从容自然。
        """;

        var result = PromptInnerMonologueGuard.Apply(prompt);

        Assert.Contains("内心独白-岳沉天：“能放，也能收。”", result);
        Assert.DoesNotContain("旁白说", result);
        Assert.Contains("岳沉天嘴巴闭合、嘴唇紧抿、未开口", result);
    }

    [Fact]
    public void ExistingMouthClosedKeyword_IsNotDuplicated()
    {
        var prompt = """
        【第1集】【单元1.8】【镜头1.8-1】
        @图1 [岳沉天]人物；@图2 [光影质感]光影质感
        [0-5s]近景锁焦：岳沉天嘴唇紧抿，内心独白-岳沉天："能放，也能收。"
        """;

        var result = PromptInnerMonologueGuard.Apply(prompt);

        Assert.Contains("内心独白-岳沉天", result);
        Assert.DoesNotContain("嘴巴闭合、嘴唇紧抿、未开口", result);
    }

    [Fact]
    public void NormalDialogue_IsUnchanged()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        [0-3s]高空俯拍：太虚圣主说"岳沉天，你一人再强，也强不过天下法统。"
        """;

        var result = PromptInnerMonologueGuard.Apply(prompt);

        Assert.Contains("太虚圣主说", result);
        Assert.DoesNotContain("内心独白", result);
        Assert.Equal(prompt, result);
    }
}
