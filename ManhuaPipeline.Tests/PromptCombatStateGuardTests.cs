using ManhuaPipeline.Services;
using Xunit;

namespace ManhuaPipeline.Tests;

public class PromptCombatStateGuardTests
{
    private static readonly string[] Assets =
    {
        "前世岳沉天", "前世岳沉天战斗态", "少年岳沉天", "少年岳沉天战斗态"
    };

    [Fact]
    public void CombatShot_AddsMissingBattleCard()
    {
        var prompt = """
        【第1集】【单元1.9】【镜头1.9-1】
        类型:打斗/动作
        参考图:第一张(@图1)为[少年岳沉天]形象参考，保持外貌、发型、服装、气质一致；倒数第二张(@图2)为[乱葬岗]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        @角色引用:[少年岳沉天]
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.9】踏天步", Assets);

        Assert.Contains("[少年岳沉天]形象参考", result);
        Assert.Contains("[少年岳沉天战斗态]形象参考", result);
        Assert.Contains("@角色引用:[少年岳沉天][少年岳沉天战斗态]", result);
        Assert.Contains("最后一张(@图4)为画面风格参考", result);
    }

    [Fact]
    public void CombatShot_AddsMissingBaseCard_WhenOnlyBattleCardExists()
    {
        var prompt = """
        【第1集】【单元1.9】【镜头1.9-1】
        类型:打斗/动作
        参考图:第一张(@图1)为[少年岳沉天战斗态]形象参考，保持外貌、发型、服装与战损气血状态一致；倒数第二张(@图2)为[乱葬岗]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        @角色引用:[少年岳沉天战斗态]
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.9】踏天步", Assets);

        Assert.Contains("[少年岳沉天]形象参考", result);
        Assert.Contains("[少年岳沉天战斗态]形象参考", result);
        Assert.Contains("@角色引用:[少年岳沉天][少年岳沉天战斗态]", result);
    }

    [Fact]
    public void NonCombatShot_RemovesBattleCard()
    {
        var prompt = """
        【第1集】【单元1.5】【镜头1.5-1】
        类型:文戏/情感
        参考图:第一张(@图1)为[少年岳沉天]形象参考，保持外貌、发型、服装、气质一致；第二张(@图2)为[少年岳沉天战斗态]形象参考，保持外貌、发型、服装与战损气血状态一致；倒数第二张(@图3)为[荒郊破庙]场景参考，保持空间布局、色调、氛围一致；最后一张(@图4)为画面风格参考（光影、色调、材质质感）
        @角色引用:[少年岳沉天][少年岳沉天战斗态]
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.5】破庙旧带", Assets);

        Assert.Contains("[少年岳沉天]形象参考", result);
        Assert.DoesNotContain("[少年岳沉天战斗态]形象参考", result);
        Assert.Contains("@角色引用:[少年岳沉天]", result);
        Assert.Contains("倒数第二张(@图2)为[荒郊破庙]场景参考", result);
        Assert.Contains("最后一张(@图3)为画面风格参考", result);
    }

    [Fact]
    public void CombatShot_AtImageLine_AddsMissingBattleCard()
    {
        var prompt = """
        【第1集】【单元1.9】【镜头1.9-1】
        类型:打斗/动作
        @图1 少年岳沉天人物；@图2 乱葬岗场景；@图3 光影质感。
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.9】踏天步", Assets);

        Assert.Contains("@图1 [少年岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [少年岳沉天战斗态]战斗姿态参考", result);
        Assert.Contains("@图4 [光影质感]光影质感参考", result);
    }

    [Fact]
    public void CombatShot_AtImageLine_AddsBothCards_WhenFighterOnlyInBody()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        @图1 太虚圣主人物；@图2 七宗宗主群像；@图3 葬天台场景；@图4 光影质感。
        [2-5s]前世岳沉天握拳怒视阵纹。
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.2】控制模式: 打斗模板", Assets);

        Assert.Contains("@图1 [前世岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [前世岳沉天战斗态]战斗姿态参考", result);
        Assert.Contains("@图3 [太虚圣主]人物形象参考", result);
        Assert.Contains("@图5 [葬天台]场景参考", result);
        Assert.Contains("@图6 [光影质感]光影质感参考", result);
    }

    [Fact]
    public void CombatShot_ReferenceLine_AddsBothCards_WhenFighterOnlyInBody()
    {
        var prompt = """
        【第1集】【单元1.2】【镜头1.2-1】
        类型:高潮/对决
        参考图:第一张(@图1)为[太虚圣主]形象参考，保持外貌、发型、服装、气质一致；倒数第二张(@图2)为[葬天台]场景参考，保持空间布局、色调、氛围一致；最后一张(@图3)为画面风格参考（光影、色调、材质质感）
        [2-5s]前世岳沉天握拳怒视阵纹。
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.2】控制模式: 打斗模板", Assets);

        Assert.Contains("[前世岳沉天]形象参考", result);
        Assert.Contains("[前世岳沉天战斗态]形象参考", result);
        Assert.Contains("倒数第二张(@图4)为[葬天台]场景参考", result);
        Assert.Contains("最后一张(@图5)为画面风格参考", result);
    }

    [Fact]
    public void NonCombatShot_AtImageLine_RemovesBattleCard()
    {
        var prompt = """
        【第1集】【单元1.5】【镜头1.5-1】
        类型:文戏/情感
        @图1 少年岳沉天人物；@图2 少年岳沉天战斗态；@图3 荒郊破庙场景；@图4 光影质感。
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.5】破庙旧带", Assets);

        Assert.DoesNotContain("[少年岳沉天战斗态]", result);
        Assert.Contains("@图1 [少年岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [荒郊破庙]场景参考", result);
        Assert.Contains("@图3 [光影质感]光影质感参考", result);
    }

    [Fact]
    public void Parser_NormalizesRepeatedBattleSuffix()
    {
        var segments = SeedanceAtImageLine.Parse("@图1 [前世岳沉天]人物；@图2 [前世岳沉天战斗态战斗态]战斗态；@图3 [前世岳沉天战斗态]战斗态；@图4 [七宗宗主]群像；@图5 [葬天台]场景；@图6 [光影质感]光影质感。");

        Assert.Equal(5, segments.Count);
        Assert.Equal("前世岳沉天", segments[1].Name);
        Assert.Equal("战斗态", segments[1].Category);
        Assert.Single(segments, s => s.Category == "战斗态");
    }

    [Fact]
    public void CombatShot_AtImageLine_DeduplicatesMalformedBattleCard()
    {
        var prompt = """
        【第1集】【单元1.1】【镜头1.1-1】
        类型:高潮/对决
        @图1 前世岳沉天人物；@图2 前世岳沉天战斗态战斗态；@图3 前世岳沉天战斗态；@图4 七宗宗主群像；@图5 葬天台场景；@图6 光影质感。
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.1】七曜诛圣阵", Assets);

        Assert.DoesNotContain("战斗态战斗态", result);
        Assert.Contains("@图1 [前世岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [前世岳沉天战斗态]战斗姿态参考", result);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(result, @"@图\d+ \[前世岳沉天战斗态\]战斗姿态参考"));
    }

    [Fact]
    public void NonCombatShot_AtImageLine_RenumbersSkippedIndices()
    {
        var prompt = """
        【第1集】【单元1.6】【镜头1.6-1】
        类型:文戏/情感
        @图1 少年岳沉天人物；@图5 荒郊破庙场景；@图6 光影质感。
        """;

        var result = PromptCombatStateGuard.Apply(prompt, "【单元1.6】破庙旧带", Assets);

        Assert.Contains("@图1 [少年岳沉天]人物形象参考", result);
        Assert.Contains("@图2 [荒郊破庙]场景参考", result);
        Assert.Contains("@图3 [光影质感]光影质感参考", result);
        Assert.DoesNotContain("@图5", result);
    }

    [Fact]
    public void StyleSegment_NeverUsesBattleCardName()
    {
        var line = "@图1 [前世岳沉天]人物；@图2 [前世岳沉天战斗态]战斗态；@图3 [前世岳沉天战斗态]光影质感";
        var rebuilt = SeedanceAtImageLine.Rebuild(SeedanceAtImageLine.Parse(line));

        Assert.Contains("@图3 [光影质感]光影质感参考", rebuilt);
        Assert.DoesNotContain("[前世岳沉天战斗态]光影质感", rebuilt);
    }
}
