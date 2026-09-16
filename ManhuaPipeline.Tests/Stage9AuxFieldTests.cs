using System.Reflection;
using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class Stage9AuxFieldTests
{
    private static string CallEnsureNegativePromptLines(string prompt)
    {
        var method = typeof(AgentService).GetMethod("EnsureNegativePromptLines", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { prompt })!;
    }

    [Fact]
    public void MissingNegativeLine_IsNotInserted()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+冷月夜。
        [0-3s]大远景固定机位：七宗宗主合围。
        [3-7s]镜头急推：岳沉天握拳。
        [7-11s]持续仰拍：气血汇聚拳锋。
        灯光：冷月光。
        约束：禁止站桩。
        """;

        var result = CallEnsureNegativePromptLines(prompt);

        Assert.DoesNotContain("负向提示词：", result);
        Assert.Contains("约束：禁止站桩。", result);
    }

    [Fact]
    public void ExistingNegativeLine_IsRemoved()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        [0-3s]大远景固定机位。
        约束：禁止站桩。
        负向提示词：人物变形，手指畸形，画面闪烁，色彩混乱，字幕，文字，口型错误
        """;

        var result = CallEnsureNegativePromptLines(prompt);

        Assert.DoesNotContain("负向提示词：", result);
        Assert.Contains("约束：禁止站桩。", result);
    }

    [Fact]
    public void StandaloneSoundLines_AreRemoved()
    {
        var prompt = """
        【第1集】【单元1.11】【镜头1.11-2】
        类型:文戏/情感
        @图1 [少年岳沉天]人物；@图2 [光影质感]光影质感
        [0-3s]近景缓推，留音石波动。
        约束：语速从容自然。
        音效：无
        3-6s 无；6-9s 苍老回声、地宫空旷回响；9-11s 衣料轻拂声。
        """;

        var result = CallEnsureNegativePromptLines(prompt);

        Assert.DoesNotContain("音效：", result);
        Assert.DoesNotContain("3-6s 无", result);
        Assert.DoesNotContain("负向提示词：", result);
    }

    [Fact]
    public void Parser_ExtractsNegativePromptAndKeepsPromptText()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 [前世岳沉天]人物；@图2 [光影质感]光影质感
        写实3D动画电影，4K，24fps，浅景深，无字幕无BGM，葬天台+冷月夜。
        [0-3s]大远景固定机位：七宗宗主合围，法阵轰鸣声。
        灯光：冷月光。
        约束：禁止站桩。
        负向提示词：人物变形，手指畸形，画面闪烁，色彩混乱，字幕，文字，口型错误
        """;

        var parsed = SeedancePromptParser.Parse(
            prompt,
            30,
            new List<string> { "前世岳沉天" },
            new List<string>(),
            new List<string>(),
            new List<string>());

        var one = Assert.Single(parsed);
        Assert.Contains("人物变形", one.NegativePrompt ?? "");
        Assert.Contains("负向提示词：", one.PromptText);
    }

    [Fact]
    public void BuildSeedanceSystemPrompt_IncludesAuxFieldRules()
    {
        var method = typeof(AgentService).GetMethod("BuildSeedanceSystemPrompt", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        var prompt = (string)method.Invoke(null, new object?[] { "风格", "技能", "打斗", "角色" })!;

        Assert.Contains("禁止输出「负向提示词：」行", prompt);
        Assert.Contains("4K，24fps，浅景深，无字幕无BGM，人物比例自然、肢体完整", prompt);
        Assert.Contains("内心独白与旁白规则", prompt);
        Assert.Contains("禁止输出独立的「音效：」字段行", prompt);
    }
}
